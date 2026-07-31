using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Speech.Synthesis; 
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using LockheedMartin.Prepar3D.SimConnect;
using Microsoft.Web.WebView2.Core; 
using P3DWeatherEngine; 
using System.IO;
using System.Diagnostics;
using P3DWeatherEngineGUI.Services;


namespace P3DWeatherEngineGUI
{
    public partial class MainWindow : Window
    {
        // --- LOGGING & MAINTENANCE ENGINE ---

        // 0 = Minimal, 1 = Normal, 2 = Debug
        public enum LogLevel { Minimal = 0, Normal = 1, Debug = 2 }
        
        // Throttles the SimConnect position loop to prevent CPU microstutters
        private DateTime _lastPositionTick = DateTime.MinValue;

        private System.Collections.Generic.List<Waypoint> _currentFlightPlanWaypoints;

        // Global storage for the latest SimBrief data so lazy-loaded tabs can access it
        private SimBriefData _currentOfp;
        private string logDirectory;
        private string currentLogFile;
        private readonly object logLock = new object(); // Prevents thread collisions if multiple async tasks log at once
        private string cacheDirectory;
        private string settingsFile;
        private bool isInitializing = true;

        private bool _isFetchingAtis = false; // Prevents overlapping API calls

        // ROUTE-BASED POLLING (PLAN MODE)
        private System.Collections.Generic.HashSet<string> _activeRouteStations = new System.Collections.Generic.HashSet<string>();

        public List<Waypoint> AlternateWaypoints { get; set; } = new List<Waypoint>();
        private System.Windows.Threading.DispatcherTimer _vatsimSyncTimer;

        
        
        #region DIRECTORIES & SETTINGS PERSISTENCE
        private void InitializeDirectories()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            
            // Create dedicated folders that survive published builds
            logDirectory = Path.Combine(baseDir, "Logs");
            cacheDirectory = Path.Combine(baseDir, "Cache");
            string configDirectory = Path.Combine(baseDir, "Config");

            if (!Directory.Exists(logDirectory)) Directory.CreateDirectory(logDirectory);
            if (!Directory.Exists(cacheDirectory)) Directory.CreateDirectory(cacheDirectory);
            if (!Directory.Exists(configDirectory)) Directory.CreateDirectory(configDirectory);

            settingsFile = Path.Combine(configDirectory, "appsettings.json");
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFile))
                {
                    string json = File.ReadAllText(settingsFile);
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("LogLevel", out var lvl)) cmbLogLevel.SelectedIndex = lvl.GetInt32();
                        if (root.TryGetProperty("SmoothWind", out var sw)) chkSmoothWind.IsChecked = sw.GetBoolean();
                        if (root.TryGetProperty("JetStream", out var js)) chkJetStream.IsChecked = js.GetBoolean();
                        if (root.TryGetProperty("AutoConnect", out var ac)) chkAutoConnect.IsChecked = ac.GetBoolean();
                        if (root.TryGetProperty("TurbModel", out var tm)) cmbTurbulenceModel.SelectedIndex = tm.GetInt32();
                    }
                }
            }
            catch { /* If settings file is corrupted, fallback to UI defaults */ }
        }

        private void SaveSettings()
        {
            if (isInitializing) return;

            try
            {
                var settings = new
                {
                    LogLevel = cmbLogLevel.SelectedIndex,
                    SmoothWind = chkSmoothWind.IsChecked ?? false,
                    JetStream = chkJetStream.IsChecked ?? false,
                    AutoConnect = chkAutoConnect.IsChecked ?? false,
                    TurbModel = cmbTurbulenceModel.SelectedIndex
                };

                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(settingsFile, json);
            }
            catch (Exception ex) { LogEngineEvent($"Failed to save settings: {ex.Message}", LogLevel.Minimal); }
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            LogEngineEvent("Application configuration updated by user.", LogLevel.Debug);
        }
        #endregion

        private void InitializeLogging()
        {
            // Create the Logs folder next to the .exe
            logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // Create a daily log file
            currentLogFile = Path.Combine(logDirectory, $"SkyNexus_Log_{DateTime.Now:yyyy-MM-dd}.txt");
            
            LogEngineEvent("=== SkyNexus Engine Session Started ===", LogLevel.Minimal);
        }

        public void LogEngineEvent(string message, LogLevel eventLevel)
        {
            LogLevel userSelectedLevel = LogLevel.Normal;
            Dispatcher.Invoke(() => {
                if (cmbLogLevel != null) userSelectedLevel = (LogLevel)cmbLogLevel.SelectedIndex;
            });

            if (eventLevel <= userSelectedLevel)
            {
                string logEntry = $"[{DateTime.UtcNow:HH:mm:ssZ}] [{eventLevel.ToString().ToUpper()}] {message}";
                
                // OFF-LOAD FILE WRITING: Prevents Windows from freezing the app to scan the .txt file!
                System.Threading.Tasks.Task.Run(() => 
                {
                    lock (logLock)
                    {
                        try { File.AppendAllText(currentLogFile, logEntry + Environment.NewLine); }
                        catch { }
                    }
                });

                Dispatcher.Invoke(() => 
                {
                    if (txtConsole != null)
                    {
                        txtConsole.AppendText(logEntry + Environment.NewLine);
                        
                        // PREVENT WPF MEMORY LEAK: Keep the UI console clean and lightning fast
                        if (txtConsole.Text.Length > 10000)
                        {
                            txtConsole.Text = txtConsole.Text.Substring(txtConsole.Text.Length - 5000);
                        }
                        txtConsole.ScrollToEnd(); 
                    }
                });
            }
        }

        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            // Open the Logs folder in Windows Explorer
            if (Directory.Exists(logDirectory))
            {
                Process.Start(new ProcessStartInfo()
                {
                    FileName = logDirectory,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
        }

        private void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Now safely targets ONLY runtime generated data in the Cache folder
                ScrubDirectorySafely(cacheDirectory);
                
                LogEngineEvent("Maintenance: Application runtime cache cleared by user.", LogLevel.Minimal);
                MessageBox.Show("Runtime cache cleared successfully.\nSettings and Airport Databases were preserved.", "Maintenance Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogEngineEvent($"Failed to clear cache: {ex.Message}", LogLevel.Minimal);
            }
        }

        private void ScrubDirectorySafely(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return;

            DirectoryInfo di = new DirectoryInfo(folderPath);
            
            // Delete loose files
            foreach (FileInfo file in di.GetFiles())
            {
                try { file.Delete(); } catch { /* Ignore locked files */ }
            }
            // Delete sub-folders
            foreach (DirectoryInfo dir in di.GetDirectories())
            {
                try { dir.Delete(true); } catch { /* Ignore locked active folders */ }
            }
        }
        const int WM_USER_SIMCONNECT = 0x0402;
        private static readonly string[] PhoneticAlphabet = { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliett", "Kilo", "Lima", "Mike", "November", "Oscar", "Papa", "Quebec", "Romeo", "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "X-ray", "Yankee", "Zulu" };

        enum DEFINITIONS { AircraftPosition }
        enum DATA_REQUESTS { ContinuousPositionRequest }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct PositionData
        {
            public double Latitude;
            public double Longitude;
            public double Altitude;
            public double Com1Frequency; 
        }

        // --- WINDS ALOFT CACHE STRUCTURE ---
        private AtmosphericProfile _windsCache = new AtmosphericProfile();
        private DateTime _lastWindsFetchTime = DateTime.MinValue;
        private bool _isFetchingWinds = false;

        // --- SIMCONNECT CONNECTION MANAGER FIELDS ---
        private IntPtr _windowHandle;
        private bool _isSimConnected = false;
        private System.Windows.Threading.DispatcherTimer _reconnectTimer = new System.Windows.Threading.DispatcherTimer();

        const double VISIBILITY_MULTIPLIER = 1.5; 
        const int IDLE_UPDATE_MINUTES = 5;
        const double ATIS_FREQUENCY = 122.00;

        SimConnect? simconnect = null;
        StationLocator locator = new StationLocator();
        readonly HttpClient client = new HttpClient();
        SpeechSynthesizer speechEngine = new SpeechSynthesizer();

        string currentIcao = "";
        DateTime lastFetchTime = DateTime.MinValue;
        bool isAtisPlaying = false; 
        bool isFetchingWeather = false; 
        
        string atisAirportName = "";
        int atisRawVisibility = 10;
        int atisRawTemp = 15;
        int atisRawDew = 10;
        string atisRawAltStr = "2992";
        string atisCloudString = "clear";
        string atisWindString = "Wind calm";
        double surfWindDir = 0;
        int surfWindSpd = 0;

        public MainWindow()
        {
            InitializeComponent();
    
            InitializeDirectories();
            LoadSettings();      // Load JSON settings before UI reacts
            InitializeLogging(); // Turn on the logging engine
    
            isInitializing = false;

            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            
            speechEngine.Volume = 100;
            speechEngine.Rate = -1; 
            speechEngine.SelectVoiceByHints(VoiceGender.Male, VoiceAge.Adult);
            speechEngine.SpeakCompleted += (s, e) => { isAtisPlaying = false; };
            
            try 
            { 
                locator.LoadStations("Data/airports.csv");
                Log("Navigation database loaded successfully.");
            }
            catch (Exception ex) {
                MessageBox.Show("SkyNexus Critical Startup Error:\n\n" + ex.Message + "\n\n" + ex.StackTrace, "Startup Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }

            Log("Initializing WebView2 Map Engine...");
            InitializeAsync();
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                string time = DateTime.UtcNow.ToString("HH:mm:ss");
                txtConsole.AppendText($"[{time}Z] {message}\n");
                txtConsole.ScrollToEnd();
            });
        }

        async void InitializeAsync()
        {
            await mapBrowser.EnsureCoreWebView2Async(null);
            
            // Forces the mini-map to load the simple global view
            UpdateMap(0, 0, true); 
            
            Log("Conditions mini-map rendered.");
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _windowHandle = new WindowInteropHelper(this).Handle;
            HwndSource source = HwndSource.FromHwnd(_windowHandle);
            source.AddHook(new HwndSourceHook(WndProc));

            // Start the automatic connection polling timer
            _reconnectTimer.Interval = TimeSpan.FromSeconds(5);
            _reconnectTimer.Tick += ReconnectTimer_Tick;
            _reconnectTimer.Start();

            ConnectToSim();
        }

        private void ReconnectTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isSimConnected) ConnectToSim();
        }

        // Add this timer variable right above your method
        private System.Windows.Threading.DispatcherTimer _simPollTimer;

        private void ConnectToSim()
        {
            if (_simPollTimer == null)
            {
                _simPollTimer = new System.Windows.Threading.DispatcherTimer();
                _simPollTimer.Interval = TimeSpan.FromSeconds(3); // Try connecting every 3 seconds
                _simPollTimer.Tick += (s, e) => { if (!_isSimConnected) ConnectToSim(); };
                _simPollTimer.Start();
            }

            if (_isSimConnected) return;

            // --- THE SILENT KILLER FIX ---
            // Check if the simulator is actually running BEFORE trying to connect.
            // If it's closed, SimConnect hangs for 2 seconds before failing, freezing the UI!
            bool isSimRunning = System.Diagnostics.Process.GetProcessesByName("Prepar3D").Length > 0 || 
                                System.Diagnostics.Process.GetProcessesByName("fsx").Length > 0;
            
            if (!isSimRunning) 
            {
                return; // Instantly skip without freezing!
            }

            try
            {
                simconnect = new SimConnect("SkyNexus_Engine", _windowHandle, WM_USER_SIMCONNECT, null, 0);
                _isSimConnected = true;
                
                Dispatcher.Invoke(() => {
                    lblStatus.Text = "Sim Connection: CONNECTED TO P3D";
                    InjectBaselineAtmosphere();
                    lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen);
                });
                
                Log("Connection established. Enforcing Custom Weather Mode.");
                simconnect.WeatherSetModeCustom();

                simconnect.RegisterDataDefineStruct<PositionData>(DEFINITIONS.AircraftPosition);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Latitude", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Longitude", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Altitude", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "COM ACTIVE FREQUENCY:1", "Megahertz", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);

                simconnect.OnRecvQuit += Simconnect_OnRecvQuit; 
                simconnect.OnRecvSimobjectData += Simconnect_OnRecvSimobjectData;
                simconnect.OnRecvException += Simconnect_OnRecvException;
                simconnect.RequestDataOnSimObject(DATA_REQUESTS.ContinuousPositionRequest, DEFINITIONS.AircraftPosition, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            }
            catch (Exception) 
            { 
                simconnect = null;
                _isSimConnected = false;

                Dispatcher.Invoke(() => {
                    if (!lblStatus.Text.Contains("Searching"))
                    {
                        lblStatus.Text = "Sim Connection: Searching for Simulator...";
                        lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightCoral);
                    }
                });
            }
        }

        private void Simconnect_OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            // Force the disconnect sequence onto the main UI thread to prevent silent cross-thread crashes!
            Dispatcher.Invoke(() => 
            {
                Log("Simulator closed by user. Detaching engine...");
                DisconnectSim();
            });
        }

        private async System.Threading.Tasks.Task CalculateEnrouteStationsAsync(System.Collections.Generic.List<Waypoint> route)
        {
            if (route == null || route.Count == 0) return;

            LogEngineEvent($"[DISPATCH] Pre-calculating enroute weather stations for {route.Count} waypoints...", LogLevel.Debug);
            _activeRouteStations.Clear();

            // Run the heavy database looping on a background thread to prevent UI freezing
            await System.Threading.Tasks.Task.Run(() => 
            {
                var depIcao = route[0].Ident;
                var arrIcao = route[route.Count - 1].Ident;
                if (!string.IsNullOrEmpty(depIcao)) _activeRouteStations.Add(depIcao);
                if (!string.IsNullOrEmpty(arrIcao)) _activeRouteStations.Add(arrIcao);

                foreach (var waypoint in route)
                {
                    var nearestStations = locator.GetNearestStations(waypoint.Latitude, waypoint.Longitude, 1);
                    
                    if (nearestStations != null && nearestStations.Count > 0)
                    {
                        var nearest = nearestStations[0];
                        double distanceToStation = CalculateDistanceNM(waypoint.Latitude, waypoint.Longitude, nearest.Station.Latitude, nearest.Station.Longitude);
                        
                        if (distanceToStation <= 50 && nearest.Station.ICAO != null)
                        {
                            _activeRouteStations.Add(nearest.Station.ICAO);
                        }
                    }
                }
            });
            
            LogEngineEvent($"[DISPATCH] Found {_activeRouteStations.Count} real weather stations along the route.", LogLevel.Normal);
        }
        private void DisconnectSim()
        {
            // 1. Force EVERYTHING onto the main UI thread. 
            // Disposing a COM object on a background thread will cause a silent deadlock!
            Dispatcher.Invoke(() => 
            {
                if (simconnect != null)
                {
                    try { simconnect.Dispose(); } catch { }
                    simconnect = null;
                }
                
                _isSimConnected = false;

                // Stop the background weather injector so it doesn't hammer a dead COM object
                try { _vatsimSyncTimer?.Stop(); } catch { }
                
                lblStatus.Text = "Sim Connection: Searching for Simulator...";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightCoral);
                lblSimTime.Text = "Active Time: --:--Z";

                lblIata.Text = "---";
                lblStationName.Text = "WAITING FOR SIMCONNECT...";
                lblAirportName.Text = "Simulator Connection Pending";
                lblLastUpdate.Text = "--:--Z";
                lblCoords.Text = "-- / --";
                lblElevation.Text = "-- ft";

                lblTemp.Text = "--°C"; lblTempImp.Text = "--°F";
                lblDew.Text = "--°C"; lblDewImp.Text = "--°F";
                lblWind.Text = "--- @ -- kts"; lblWindImp.Text = "-- mph";
                lblVis.Text = "-- km"; lblVisImp.Text = "-- SM";
                lblPressure.Text = "---- hPa"; lblPressureImp.Text = "--.-- inHg";
                lblConditions.Text = "--";

                txtMetar.Text = ""; txtTaf.Text = "";

                lblWind36k.Text = "-- @ -- kts";
                lblWind24k.Text = "-- @ -- kts";
                lblWind10k.Text = "-- @ -- kts";
                lblWindSurf_Aloft.Text = "-- @ -- kts";
                
                currentIcao = "";
                lastFetchTime = DateTime.MinValue;
                _lastWindsFetchTime = DateTime.MinValue;

                UpdateMap(0, 0, true); 
            });
            
            Log("SimConnect disconnected. UI cleared. Listening for simulator reboot...");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_USER_SIMCONNECT && simconnect != null && _isSimConnected)
            {
                // 2. CRITICAL: Tell the Windows Message Pump we handled this!
                // If we don't, WPF can start throttling or dropping the connection pipe.
                handled = true; 
                
                try 
                { 
                    simconnect.ReceiveMessage(); 
                }
                catch 
                { 
                    // The pipe broke! (P3D crashed or was closed via Task Manager)
                    DisconnectSim(); 
                }
            }
            return IntPtr.Zero;
        }
        private void Simconnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            // Error 14 is SIMCONNECT_EXCEPTION_DATA_ERROR.
            // This happens natively when injecting a perfect METAR for a modern airport that P3D's 2018 database doesn't recognize.
            if (data.dwException == 14)
            {
                LogEngineEvent($"[SIMCONNECT] Station skipped (ICAO not found in P3D internal database). TrueSky will interpolate the gap.", LogLevel.Debug);
                return;
            }

            Log($"[SIMCONNECT EXCEPTION] Error Code: {data.dwException}");
        }

        // 1. Add this variable right above the method to control the background search throttle
        private DateTime _lastStationSearchTick = DateTime.MinValue;

        private async void Simconnect_OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            if (data.dwRequestID == (uint)DATA_REQUESTS.ContinuousPositionRequest)
            {
                PositionData pos = (PositionData)data.dwData[0];
                
                if ((DateTime.UtcNow - _lastPositionTick).TotalMilliseconds < 1000) return; 
                _lastPositionTick = DateTime.UtcNow;

                lblSimTime.Text = $"Active Time: {DateTime.UtcNow:HH:mm}Z";

                UpdateFullMap(pos.Latitude, pos.Longitude, 0, 0, pos.Altitude);

                double maxAlt = 40000.0;
                double currentAlt = pos.Altitude;
                if (currentAlt < 0) currentAlt = 0;
                if (currentAlt > maxAlt) currentAlt = maxAlt;
                
                if (altCanvas.ActualHeight > 0)
                {
                    double h = altCanvas.ActualHeight;
                    double surfY = h * 0.125;
                    double fl100Y = h * 0.375;
                    double fl240Y = h * 0.625;
                    double fl360Y = h * 0.875;
                    double bottomPos = 0;

                    if (currentAlt <= 10000) bottomPos = surfY + (currentAlt / 10000.0) * (fl100Y - surfY);
                    else if (currentAlt <= 24000) bottomPos = fl100Y + ((currentAlt - 10000) / 14000.0) * (fl240Y - fl100Y);
                    else if (currentAlt <= 36000) bottomPos = fl240Y + ((currentAlt - 24000) / 12000.0) * (fl360Y - fl240Y);
                    else bottomPos = fl360Y + ((currentAlt - 36000) / 4000.0) * (h - fl360Y);

                    System.Windows.Controls.Canvas.SetBottom(planeIcon, bottomPos - 7);
                }

                if ((DateTime.Now - _lastWindsFetchTime).TotalMinutes >= 15 && !_isFetchingWinds)
                {
                    _ = FetchWindsAloftAsync(pos.Latitude, pos.Longitude);
                }

                bool isTunedToAtis = Math.Abs(pos.Com1Frequency - ATIS_FREQUENCY) < 0.01;
                if (isTunedToAtis)
                {
                    if (!isAtisPlaying && !string.IsNullOrEmpty(currentIcao) && !_isFetchingAtis)
                    {
                        _isFetchingAtis = true; 
                        try
                        {
                            var wx = await FetchMetarDataAsync(currentIcao);
                            string voiceScript = GenerateSmartAtis(currentIcao, wx.raw, ""); 
                            
                            isAtisPlaying = true; 
                            Log($"Broadcasting ATIS on {ATIS_FREQUENCY} MHz.");
                            speechEngine.SpeakAsync(voiceScript);
                        }
                        finally { _isFetchingAtis = false; }
                    }
                }
                else if (isAtisPlaying)
                {
                    speechEngine.SpeakAsyncCancelAll();
                    isAtisPlaying = false;
                }

                if ((DateTime.UtcNow - _lastStationSearchTick).TotalSeconds >= 10 && !isFetchingWeather)
                {
                    _lastStationSearchTick = DateTime.UtcNow;

                    var nearestStations = await System.Threading.Tasks.Task.Run(async () => 
                    {
                        if (_skyGridManager != null)
                        {
                            await _skyGridManager.UpdateStreamingWindowAsync(pos.Latitude, pos.Longitude, async (lat, lon, id) => {
                                string baseMetar = await FetchGridMetarAsync(lat, lon, id);
                                AtmosphericProfile aloft = await FetchWindsForCoordinateAsync(lat, lon);
                                
                                // --- THE BUG FIX: ENSURE STREAMED CELLS DON'T INJECT INTO "GLOB"! ---
                                string p3dString = ParseAndSanitizeMetar(baseMetar, 0, aloft);
                                return p3dString.Replace("GLOB", id);
                            });
                        }
                        return locator.GetNearestStations(pos.Latitude, pos.Longitude, 3);
                    });

                    PushSkyGridToMap();

                    if (nearestStations.Count > 0)
                    {
                        WeatherStation primaryStation = nearestStations[0].Station;
                        string stationIdentifier = primaryStation.ICAO ?? "GLOB";
                        bool isNewStation = stationIdentifier != currentIcao;
                        bool isTimeForUpdate = (DateTime.Now - lastFetchTime).TotalMinutes >= IDLE_UPDATE_MINUTES;

                        if (isNewStation || isTimeForUpdate)
                        {
                            currentIcao = stationIdentifier;
                            lastFetchTime = DateTime.Now;
                            
                            UpdateMap(primaryStation.Latitude, primaryStation.Longitude, false);

                            bool isAutoModeEnabled = chkAutoMode.IsChecked ?? false;
                            if (isAutoModeEnabled)
                            {
                                await UpdateInterpolatedWeatherAsync(nearestStations);
                            }
                        }
                    }
                }
            }
        }
        private string GenerateSmartAtis(string airportIdent, string rawMetar, string rawTaf)
        {
            // 1. Assign Information Letter (Assuming PhoneticAlphabet is defined in your class)
            string infoLetter = PhoneticAlphabet[DateTime.UtcNow.Hour % 26].ToUpper();
            
            // 2. Decode Wind (SayIntentions Style: "WIND 260 AT 8")
            string windSpoken = "CALM";
            var windMatch = System.Text.RegularExpressions.Regex.Match(rawMetar, @"(\d{3}|VRB)(\d{2,3})(?:G\d{2,3})?KT");
            if (windMatch.Success)
            {
                if (windMatch.Groups[1].Value == "000" && windMatch.Groups[2].Value == "00") {
                    windSpoken = "CALM";
                } else {
                    string dir = windMatch.Groups[1].Value;
                    int spd = int.Parse(windMatch.Groups[2].Value); // int.Parse removes leading zeros (08 -> 8)
                    windSpoken = $"{dir} AT {spd}";
                }
            }

            // 3 & 4. Decode Visibility & Clouds (With CAVOK Override)
            string visCloudSpoken = "";
            if (rawMetar.Contains("CAVOK"))
            {
                visCloudSpoken = "CAVOK.";
            }
            else
            {
                // Visibility Handling (Meters and Statute Miles)
                string visSpoken = "";
                var visMatchSM = System.Text.RegularExpressions.Regex.Match(rawMetar, @"\b(\d+)SM\b");
                var visMatchM = System.Text.RegularExpressions.Regex.Match(rawMetar, @"(?<=\s|^)(\d{4})(?=\s|NDV|$)");
                
                if (visMatchSM.Success) visSpoken = $"VISIBILITY {visMatchSM.Groups[1].Value}.";
                else if (visMatchM.Success) visSpoken = $"VISIBILITY {int.Parse(visMatchM.Groups[1].Value)}."; // Drops leading zeros

                // Cloud Mapper
                var cloudMap = new Dictionary<string, string> { { "FEW", "FEW" }, { "SCT", "SCATTERED" }, { "BKN", "BROKEN" }, { "OVC", "OVERCAST" } };
                var cloudLayers = new List<string>();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(rawMetar, @"(FEW|SCT|BKN|OVC)(\d{3})"))
                {
                    int height = int.Parse(m.Groups[2].Value) * 100;
                    cloudLayers.Add($"{cloudMap[m.Groups[1].Value]} CLOUDS AT {height}");
                }
                string cloudsSpoken = cloudLayers.Count > 0 ? string.Join(", ", cloudLayers) + "." : "SKY CLEAR.";
                
                visCloudSpoken = $"{visSpoken} {cloudsSpoken}".Trim();
            }

            // 5. Decode Temp/Dew/Alt (SayIntentions Style: "TEMPERATURE 35, DEWPOINT 25.")
            var tempMatch = System.Text.RegularExpressions.Regex.Match(rawMetar, @"(?:^|\s)(M?\d{2})/(M?\d{2})(?:\s|$)");
            string tempSpoken = "";
            if (tempMatch.Success) 
            {
                // int.Parse cleans up negative "M" values nicely (e.g. "M02" -> "-02" -> "-2")
                int t = int.Parse(tempMatch.Groups[1].Value.Replace("M", "-"));
                int d = int.Parse(tempMatch.Groups[2].Value.Replace("M", "-"));
                tempSpoken = $"TEMPERATURE {t}, DEWPOINT {d}.";
            }
            
            // Pressure Handling (QNH vs Altimeter)
            string altSpoken = "";
            var qnhMatch = System.Text.RegularExpressions.Regex.Match(rawMetar, @"Q(\d{4})");
            var altMatch = System.Text.RegularExpressions.Regex.Match(rawMetar, @"[A](\d{4})");
            
            if (qnhMatch.Success) altSpoken = $"QNH {int.Parse(qnhMatch.Groups[1].Value)}.";
            else if (altMatch.Success) altSpoken = $"ALTIMETER {altMatch.Groups[1].Value.Insert(2, ".")}."; // e.g. 2992 -> 29.92

            // 6. Integrate Trend Analysis
            string trend = AnalyzeWeatherTrend(rawMetar, rawTaf);
            string trendSpoken = trend == "Stable" ? "EXPECT STABLE CONDITIONS." : $"EXPECT {trend.ToUpper()}.";

            // 7. Assemble the final spoken script
            // Using a clean, contiguous string builder to prevent double-spaces
            var finalAtisParts = new List<string> 
            { 
                $"{airportIdent.ToUpper()} AIRPORT, INFORMATION {infoLetter}.",
                $"{DateTime.UtcNow:HHmm} ZULU.",
                "ARRIVING AND DEPARTING RUNWAYS IN USE.",
                $"WIND {windSpoken}.",
                visCloudSpoken,
                tempSpoken,
                altSpoken,
                trendSpoken,
                $"ADVISE ON INITIAL CONTACT YOU HAVE INFORMATION {infoLetter}."
            };

            // Remove any empty strings (like if temp/dew was missing) and join with a space
            return string.Join(" ", finalAtisParts.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        // --- NEW REAL-WORLD FORECAST FETCH METHOD ---
        private async Task FetchWindsAloftAsync(double lat, double lon)
        {
            _isFetchingWinds = true;
            try
            {
                string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}&hourly=wind_speed_250hPa,wind_direction_250hPa,wind_speed_500hPa,wind_direction_500hPa,wind_speed_700hPa,wind_direction_700hPa&wind_speed_unit=kn&forecast_days=1";
                HttpResponseMessage response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                    {
                        var hourly = doc.RootElement.GetProperty("hourly");
                        int currentHour = DateTime.UtcNow.Hour; // Maps exactly to 0-23 array index

                        // Create and assign new WindLayers directly into the Dictionary
                        _windsCache.Layers[360] = new WindLayer 
                        {
                            Speed = (int)Math.Round(hourly.GetProperty("wind_speed_250hPa")[currentHour].GetDouble()),
                            Direction = (int)Math.Round(hourly.GetProperty("wind_direction_250hPa")[currentHour].GetDouble())
                        };
                        _windsCache.Layers[240] = new WindLayer 
                        {
                            Speed = (int)Math.Round(hourly.GetProperty("wind_speed_500hPa")[currentHour].GetDouble()),
                            Direction = (int)Math.Round(hourly.GetProperty("wind_direction_500hPa")[currentHour].GetDouble())
                        };
                        _windsCache.Layers[100] = new WindLayer 
                        {
                            Speed = (int)Math.Round(hourly.GetProperty("wind_speed_700hPa")[currentHour].GetDouble()),
                            Direction = (int)Math.Round(hourly.GetProperty("wind_direction_700hPa")[currentHour].GetDouble())
                        };

                        _lastWindsFetchTime = DateTime.Now;
                        Log($"[NOAA GRIB] Real-World Winds Aloft updated. Core Jetstream (FL360): {_windsCache.Layers[360].Direction:D3} @ {_windsCache.Layers[360].Speed}kts");

                        // Update the UI since we know the layers exist now
                        Dispatcher.Invoke(() => {
                            lblWind36k.Text = $"{_windsCache.Layers[360].Direction:D3} @ {_windsCache.Layers[360].Speed} kts";
                            lblWind24k.Text = $"{_windsCache.Layers[240].Direction:D3} @ {_windsCache.Layers[240].Speed} kts";
                            lblWind10k.Text = $"{_windsCache.Layers[100].Direction:D3} @ {_windsCache.Layers[100].Speed} kts";
                        });

                        // --- THE FIX: INJECT CONTINUOUS GLOBAL WINDS ALOFT ---
                        if (simconnect != null)
                        {
                            string globWinds = $"GLOB {DateTime.UtcNow:ddHHmm}Z 00000KT " +
                                               $"{_windsCache.Layers[100].Direction:D3}{_windsCache.Layers[100].Speed:D2}KT&A3048 " +
                                               $"{_windsCache.Layers[240].Direction:D3}{_windsCache.Layers[240].Speed:D2}KT&A7315 " +
                                               $"{_windsCache.Layers[360].Direction:D3}{_windsCache.Layers[360].Speed:D2}KT&A10973 " +
                                               "40SM CLR 15/10 A2992";

                            Dispatcher.Invoke(() => {
                                try { simconnect.WeatherSetObservation(0, globWinds); } catch { }
                            });
                            LogEngineEvent($"[ENGINE] Global background winds aloft synchronized with aircraft position.", LogLevel.Debug);
                        }
                    }
                }
                else
                {
                    Log($"[API ERROR] Open-Meteo returned status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Log($"[API WARNING] Real-World Winds Aloft fetch failed: {ex.Message}");
            }
            finally
            {
                _isFetchingWinds = false;
            }
        }

        private async System.Threading.Tasks.Task<string> FetchWindsGridAsync(System.Collections.Generic.List<Waypoint> route)
        {
            if (route == null || route.Count < 2) return "";

            // 1. Find the Bounding Box of the flight plan
            double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
            foreach (var wp in route)
            {
                if (wp.Latitude < minLat) minLat = wp.Latitude;
                if (wp.Latitude > maxLat) maxLat = wp.Latitude;
                if (wp.Longitude < minLon) minLon = wp.Longitude;
                if (wp.Longitude > maxLon) maxLon = wp.Longitude;
            }

            // Expand the box slightly to give map context around the route
            minLat -= 2; maxLat += 2; minLon -= 2; maxLon += 2;

            // 2. Build a Dense 35-Point Grid (Safe for URL length and API limits!)
            var lats = new System.Collections.Generic.List<double>();
            var lons = new System.Collections.Generic.List<double>();
            
            // INCREASED DENSITY: Divided latitude by 4 and longitude by 6
            for (double lat = minLat; lat <= maxLat; lat += (maxLat - minLat) / 9)
            {
                for (double lon = minLon; lon <= maxLon; lon += (maxLon - minLon) / 9)
                {
                    lats.Add(Math.Round(lat, 2));
                    lons.Add(Math.Round(lon, 2));
                }
            }

            // 3. Batch Request to Open-Meteo
            string latString = string.Join(",", lats);
            string lonString = string.Join(",", lons);
            
            // This URL will now be roughly 650 characters—well under the 2048 limit.
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={latString}&longitude={lonString}&hourly=wind_speed_250hPa,wind_direction_250hPa,wind_speed_500hPa,wind_direction_500hPa,wind_speed_700hPa,wind_direction_700hPa&wind_speed_unit=kn&forecast_days=1";

            try
            {
                HttpResponseMessage response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }
            }
            catch { }
            return "";
        }
        // --- BRIDGE METHOD ---
        // Catch legacy map calls (like UI clicks) and forward them safely to the new engine
        private void UpdateMap(double lat, double lon, bool showGlobe)
        {
            if (mapBrowser == null || mapBrowser.CoreWebView2 == null) return;

            int zoom = showGlobe ? 2 : 13;
            double centerLat = showGlobe ? 20.0 : lat;
            double centerLon = showGlobe ? 0.0 : lon;

            string html = $@"<!DOCTYPE html>
            <html>
            <head>
                <link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css' />
                <script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
                <style>
                    body {{ padding: 0; margin: 0; background-color: #333333; }}
                    #map {{ height: 100vh; width: 100vw; }}
                    .leaflet-control-attribution {{ display: none !important; }}
                </style>
            </head>
            <body>
                <div id='map'></div>
                <script>
                    var map = L.map('map', {{ zoomControl: false }}).setView([{centerLat}, {centerLon}], {zoom});
                    
                    L.tileLayer('https://{{s}}.basemaps.cartocdn.com/dark_all/{{z}}/{{x}}/{{y}}{{r}}.png', {{
                        subdomains: 'abcd',
                        maxZoom: 20
                    }}).addTo(map);

                    if (!{showGlobe.ToString().ToLower()}) {{
                        var markerIcon = L.divIcon({{
                            className: 'custom-div-icon',
                            html: ""<div style='background-color:#D87A1E; width:14px; height:14px; border-radius:50%; border:2px solid white;'></div>"",
                            iconSize: [14, 14],
                            iconAnchor: [7, 7]
                        }});
                        L.marker([{lat}, {lon}], {{icon: markerIcon}}).addTo(map);
                    }}
                </script>
            </body>
            </html>";

            // Targets ONLY the mini-map
            mapBrowser.NavigateToString(html); 
        }
        private void UpdateMap(double lat, double lon, double hdg, double gs, double alt, bool showGlobe)
        {
            if (mapBrowser == null || mapBrowser.CoreWebView2 == null) return;

            // Execute raw JavaScript to update the marker position dynamically without reloading the page!
            string jsCommand = $"updateAircraft({lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {hdg.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {gs.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {alt.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {(!showGlobe).ToString().ToLower()});";
            
            mapBrowser.CoreWebView2.ExecuteScriptAsync(jsCommand);
        }

        // ==========================================
        // WEATHER ENGINE STUBS & LOGGING EXAMPLES
        // ==========================================
        private async void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            string station = txtSearchIcao.Text.ToUpper();
            if (string.IsNullOrWhiteSpace(station)) return;

            LogEngineEvent($"Station lookup initiated for: {station}", LogLevel.Normal);
            lblStationName.Text = "FETCHING DATA...";

            try
            {
                // 1. Fetch Bulletproof Weather using our new dual-source helper!
                var wxData = await FetchMetarDataAsync(station);
                
                // If the raw METAR is completely empty, the station truly does not exist
                if (string.IsNullOrEmpty(wxData.raw))
                {
                    HandleAirportNotFound(station);
                    return;
                }

                // 2. Fetch Station Metadata (Lat, Lon, Elevation, Name) independent of weather reports
                // 2. Fetch Station Metadata (Lat, Lon, Elevation, Name) independent of weather reports
                double lat = 0, lon = 0, elevMeters = 0;
                string stationName = station; // Fallback to the ICAO code (e.g. VOML) instead of a weird string
                try
                {
                    string infoUrl = $"https://aviationweather.gov/api/data/stationinfo?ids={station}&format=json";
                    string infoJson = await client.GetStringAsync(infoUrl);
                    using (JsonDocument doc = JsonDocument.Parse(infoJson))
                    {
                        if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                        {
                            var info = doc.RootElement[0];
                            lat = info.TryGetProperty("lat", out var la) ? la.GetDouble() : 0;
                            lon = info.TryGetProperty("lon", out var lo) ? lo.GetDouble() : 0;
                            elevMeters = info.TryGetProperty("elev", out var el) ? el.GetDouble() : 0;
                            
                            // Smart checker that looks for multiple possible name keys
                            if (info.TryGetProperty("name", out var n) && n.ValueKind != JsonValueKind.Null) 
                            {
                                stationName = n.GetString() ?? station;
                            }
                            else if (info.TryGetProperty("site", out var s) && s.ValueKind != JsonValueKind.Null) 
                            {
                                stationName = s.GetString() ?? station;
                            }
                        }
                    }
                }
                catch { LogEngineEvent($"[API] Station info fallback failed for {station}.", LogLevel.Debug); }

                // 3. Process Visibility (With Deterministic Temp/Dew Spread)
                int atisRawVisibility = 10;
                var visMatch = System.Text.RegularExpressions.Regex.Match(wxData.raw, @"\b(\d+)SM\b|\b(\d{4})\b");
                if (visMatch.Success) 
                {
                    if (visMatch.Groups[1].Success) atisRawVisibility = int.Parse(visMatch.Groups[1].Value);
                    else {
                        int m = int.Parse(visMatch.Groups[2].Value);
                        atisRawVisibility = m >= 9999 ? 10 : (int)Math.Round(m / 1609.34);
                    }
                }

                if (atisRawVisibility >= 10)
                {
                    int visSpread = (int)wxData.temp - (int)wxData.dew;
                    if (visSpread >= 15) atisRawVisibility = 40;
                    else if (visSpread >= 10) atisRawVisibility = 30;
                    else if (visSpread >= 5) atisRawVisibility = 20;
                    else atisRawVisibility = 12;
                }

                // 4. Process Clouds for UI
                string cloudStr = "CLEAR";
                if (!wxData.raw.Contains("CAVOK") && !wxData.raw.Contains("SKC") && !wxData.raw.Contains("CLR"))
                {
                    System.Collections.Generic.List<string> clouds = new System.Collections.Generic.List<string>();
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(wxData.raw, @"(FEW|SCT|BKN|OVC|VV)(\d{3})"))
                    {
                        int h = int.Parse(m.Groups[2].Value) * 100;
                        clouds.Add($"{m.Groups[1].Value} {h}");
                    }
                    if (clouds.Count > 0) cloudStr = string.Join(" / ", clouds);
                }

                // ==========================================
                // UPDATE FULL UI DASHBOARD
                // ==========================================
                
                // Headers
                lblIata.Text = station;
                lblStationName.Text = string.IsNullOrEmpty(stationName) ? station : stationName.ToUpper();
                lblAirportName.Text = "Manual Station Search";
                lblLastUpdate.Text = DateTime.UtcNow.ToString("HH:mm") + "Z";
                
                // Location Data
                lblCoords.Text = $"{lat:F3}° / {lon:F3}°";
                lblElevation.Text = $"{(int)Math.Round(elevMeters * 3.28084)} ft";

                // Temperatures
                lblTemp.Text = $"{wxData.temp}°C";
                lblTempImp.Text = $"{Math.Round(wxData.temp * 9.0 / 5.0 + 32)}°F";
                lblDew.Text = $"{wxData.dew}°C";
                lblDewImp.Text = $"{Math.Round(wxData.dew * 9.0 / 5.0 + 32)}°F";

                // Wind
                lblWind.Text = $"{wxData.wdir:D3} @ {wxData.wspd} kts";
                lblWindImp.Text = $"{Math.Round(wxData.wspd * 1.15078)} mph";

                // Visibility
                lblVis.Text = $"{Math.Round(atisRawVisibility * 1.60934, 1)} km";
                lblVisImp.Text = $"{atisRawVisibility} SM";

                // Pressure (Smart Magnitude Check)
                double hPa, inHg;
                if (wxData.alt > 200) 
                {
                    hPa = wxData.alt;
                    inHg = wxData.alt * 0.029530;
                }
                else 
                {
                    inHg = wxData.alt;
                    hPa = wxData.alt * 33.8639;
                }

                lblPressure.Text = $"{(int)Math.Round(hPa)} hPa";
                lblPressureImp.Text = $"{inHg:F2} inHg";
                
                // Conditions & Text
                lblConditions.Text = cloudStr;
                txtMetar.Text = wxData.raw;

                // Teleport the Map instantly to the searched airport
                UpdateMap(lat, lon, false);
                
                LogEngineEvent($"[API] Successfully loaded bulletproof profile for {station}.", LogLevel.Normal);

                // Fetch TAF
                try {
                    string tafUrl = $"https://aviationweather.gov/api/data/taf?ids={station}&format=raw";
                    System.Net.Http.HttpResponseMessage tafResp = await client.GetAsync(tafUrl);
                    if (tafResp.IsSuccessStatusCode) txtTaf.Text = await tafResp.Content.ReadAsStringAsync();
                    else txtTaf.Text = "No TAF available for this station.";
                } catch { txtTaf.Text = "Failed to fetch TAF."; }

                // RUN TURBULENCE ENGINE
                RunTurbulencePrediction(wxData.wspd, wxData.raw);
            }
            catch (Exception ex)
            {
                LogEngineEvent($"[API ERROR] {ex.Message}", LogLevel.Minimal);
                HandleAirportNotFound(station);
            }
        }
        private void HandleAirportNotFound(string station)
        {
            lblIata.Text = station;
            lblStationName.Text = "AIRPORT NOT FOUND";
            lblAirportName.Text = "Check ICAO code and try again.";
            txtMetar.Text = "NO DATA AVAILABLE.";
            txtTaf.Text = "";
            lblConditions.Text = "--";
            
            lblTurbScore.Text = "-- / 100";
            lblTurbSource.Text = "--";
            lblTurbClass.Text = "UNKNOWN";
            lblTurbClass.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);

            LogEngineEvent($"Lookup failed: {station} not found in live database.", LogLevel.Normal);
        }

        private void RunTurbulencePrediction(int surfaceWindKts, string rawMetar)
        {
            LogEngineEvent("[ENGINE] Initiating Turbulence Prediction algorithms...", LogLevel.Debug);
            int score = 0;
            string sources = "";

            // 1. Convective Activity
            if (rawMetar.Contains("TS") || rawMetar.Contains("CB") || rawMetar.Contains("TCU") || rawMetar.Contains("VCTS"))
            {
                score += 35;
                sources += "Convective Weather, ";
                LogEngineEvent("[TURB] Convective indicators found (+35).", LogLevel.Debug);
            }

            // 2. Wind Shear & Mountain Wave Potential
            if (surfaceWindKts > 20)
            {
                score += 25;
                sources += "Surface Shear / Terrain, ";
                LogEngineEvent($"[TURB] High surface winds ({surfaceWindKts}kts) detected (+25).", LogLevel.Debug);
            }

            // 3. Jet Stream / CAT (Simulated upper gradient difference)
            bool isJetStreamEnabled = false;
            Dispatcher.Invoke(() => isJetStreamEnabled = chkJetStream.IsChecked ?? false);

            // SAFELY EXTRACT FROM THE NEW PHYSICS DICTIONARY
            int spd36k = _windsCache.Layers.ContainsKey(360) ? _windsCache.Layers[360].Speed : 0;
            int spd24k = _windsCache.Layers.ContainsKey(240) ? _windsCache.Layers[240].Speed : 0;

            if (isJetStreamEnabled && spd36k > 80 && Math.Abs(spd36k - spd24k) > 40)
            {
                score += 20;
                sources += "CAT / Jet Stream, ";
                LogEngineEvent($"[TURB] Massive upper-level speed gradient ({spd36k}kt vs {spd24k}kt) detected (+20).", LogLevel.Debug);
            }

            // Clean up string formatting
            if (sources.EndsWith(", ")) sources = sources.Substring(0, sources.Length - 2);
            if (score == 0) sources = "None Detected";

            // Classify
            string classification;
            if (score <= 20) classification = "SMOOTH";
            else if (score <= 40) classification = "LIGHT";
            else if (score <= 60) classification = "MODERATE";
            else if (score <= 80) classification = "HEAVY";
            else classification = "SEVERE";

            // Update UI
            Dispatcher.Invoke(() => {
                lblTurbScore.Text = $"{score} / 100";
                lblTurbSource.Text = sources;
                lblTurbClass.Text = classification;

                // Shift color based on severity
                if (score <= 20) lblTurbClass.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981")); // Green
                else if (score <= 60) lblTurbClass.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D87A1E")); // Orange
                else lblTurbClass.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E0443E")); // Red
            });

            LogEngineEvent($"[TURB] Master Assessment: {classification} (Score: {score}). Sources: {sources}", LogLevel.Normal);
        }

        private void BtnInjectCustom_Click(object sender, RoutedEventArgs e)
        {
            string rawMetar = txtCustomMetar.Text;

            if (string.IsNullOrWhiteSpace(rawMetar))
            {
                MessageBox.Show("Please paste a valid METAR string to inject.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 1. Write to the Log
            LogEngineEvent("Custom weather injection requested by user.", LogLevel.Normal);
            LogEngineEvent($"[INJECT] Parsing raw custom string: {rawMetar}", LogLevel.Debug);
            LogEngineEvent("[SIMCONNECT] Transmitting custom weather layer to Prepar3D...", LogLevel.Debug);
            
            // 2. Update the UI to show a custom profile is active
            lblIata.Text = "CUST";
            lblStationName.Text = "CUSTOM WEATHER PROFILE";
            lblAirportName.Text = "Manual Injection Active";
            lblLastUpdate.Text = DateTime.UtcNow.ToString("HH:mm") + "Z";
            
            lblTemp.Text = "--°C";
            lblDew.Text = "--°C";
            lblWind.Text = "--- @ -- kts";
            lblConditions.Text = "Custom Data Applied";
            
            // Clear the text box so it feels like the data was "sent" into the engine
            txtCustomMetar.Text = "";
        }

        private async void BtnForceUpdate_Click(object sender, RoutedEventArgs e)
        {
            // 1. Write to the log
            LogEngineEvent("[NORMAL] Manual live weather refresh triggered.", LogLevel.Normal);

            // Temporarily update the UI to show we are working
            lblStatus.Text = "Sim Connection: Fetching latest data...";
            lblStatus.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B")); // Turns amber/yellow

            // Grab the ICAO currently shown on the Conditions page
            string currentIcao = lblStationName.Text.Trim().ToUpper();

            if (!string.IsNullOrEmpty(currentIcao) && currentIcao != "---")
            {
                var station = locator.GetStationByIcao(currentIcao);
                if (station != null)
                {
                    try
                    {
                        // Fire off the actual weather engine injection pipeline!
                        var list = new System.Collections.Generic.List<(WeatherStation Station, double Distance)> { (station, 0.0) };
                        await UpdateInterpolatedWeatherAsync(list);

                        // 2. Update the UI on success
                        lblLastUpdate.Text = DateTime.UtcNow.ToString("HH:mm") + "Z";
                        lblStatus.Text = "Sim Connection: Weather Refreshed Successfully.";
                        lblStatus.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981")); // Turns green
                    }
                    catch (Exception ex)
                    {
                        LogEngineEvent($"[ERROR] Manual refresh failed: {ex.Message}", LogLevel.Normal);
                        lblStatus.Text = "Sim Connection: Injection Failed.";
                        lblStatus.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444")); // Turns red
                    }
                }
                else
                {
                    lblStatus.Text = "Sim Connection: Station Not Found.";
                    lblStatus.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444")); // Turns red
                }
            }
        }

        private void BtnToggleView_Click(object sender, RoutedEventArgs e)
{
    // Debug Log
    LogEngineEvent("UI state changed: User toggled Live Tracking / Winds Aloft VSD.", LogLevel.Debug);

    if (mapBrowser.Visibility == Visibility.Visible)
    {
        mapBrowser.Visibility = Visibility.Hidden;
        windsAloftPanel.Visibility = Visibility.Visible;
        lblViewTitle.Text = "Winds Aloft Metrics";
    }
    else
    {
        windsAloftPanel.Visibility = Visibility.Hidden;
        mapBrowser.Visibility = Visibility.Visible;
        lblViewTitle.Text = "Live Tracking";
    }
}       

        private void FullMapBrowser_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(message)) return;

            if (message.StartsWith("INJECT_METAR|"))
            {
                // Parse the ICAO, Latitude, and Longitude sent from the JavaScript map
                string[] parts = message.Split('|');
                if (parts.Length < 4) return;

                string icao = parts[1];
                double lat = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                double lon = double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);

                LogEngineEvent($"[MAP] Manual injection requested for {icao}", LogLevel.Normal);
                
                System.Threading.Tasks.Task.Run(async () =>
                {
                    // Use the existing GetNearestStations method with the exact coordinates!
                    var nearestList = locator.GetNearestStations(lat, lon, 1);
                    
                    if (nearestList != null && nearestList.Count > 0)
                    {
                        var station = nearestList[0].Station;
                        // Use 0.0 to explicitly tell C# this is a double, satisfying the tuple type
                        var list = new System.Collections.Generic.List<(WeatherStation Station, double Distance)> { (station, 0.0) };
                        await UpdateInterpolatedWeatherAsync(list);
                    }
                });
            }
        }
        private async Task UpdateInterpolatedWeatherAsync(List<(WeatherStation Station, double Distance)> stations)
        {
            isFetchingWeather = true; 
            try
            {
                double totalWeight = 0, interpTemp = 0, interpDew = 0, interpAlt = 0;
                int validReadings = 0;
                string baseMetar = "";
                WeatherStation primaryStation = stations[0].Station;

                Log($"Fetching latest METAR for {primaryStation.ICAO} from VATSIM API...");

                foreach (var item in stations)
                {
                    if (item.Station.ICAO == null) continue;
                    string url = $"https://metar.vatsim.net/{item.Station.ICAO}";
                    try
                    {
                        HttpResponseMessage response = await client.GetAsync(url);
                        if (response.IsSuccessStatusCode)
                        {
                            string rawMetar = await response.Content.ReadAsStringAsync();
                            if (!string.IsNullOrWhiteSpace(rawMetar))
                            {
                                string metar = rawMetar.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                                if (item.Station.ICAO == primaryStation.ICAO) baseMetar = metar;

                                var tempMatch = System.Text.RegularExpressions.Regex.Match(metar, @"\s(M?\d{2})/(M?\d{2})");
                                var altMatch = System.Text.RegularExpressions.Regex.Match(metar, @"A(\d{4})|Q(\d{4})");

                                if (tempMatch.Success && altMatch.Success)
                                {
                                    double temp = double.Parse(tempMatch.Groups[1].Value.Replace("M", "-"));
                                    double dew = double.Parse(tempMatch.Groups[2].Value.Replace("M", "-"));
                                    double alt = altMatch.Groups[1].Success ? double.Parse(altMatch.Groups[1].Value) / 100.0 : double.Parse(altMatch.Groups[2].Value) * 0.02953;

                                    double weight = 1.0 / (item.Distance + 0.1); 
                                    totalWeight += weight;
                                    interpTemp += temp * weight;
                                    interpDew += dew * weight;
                                    interpAlt += alt * weight;
                                    validReadings++;
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (validReadings > 0 && !string.IsNullOrEmpty(baseMetar))
                {
                    interpTemp /= totalWeight;
                    interpDew /= totalWeight;
                    interpAlt /= totalWeight;

                    Log($"Data retrieved. Executing interpolation across {validReadings} stations...");
                    
                    int uiTemp = (int)Math.Round(interpTemp);
                    int uiDew = (int)Math.Round(interpDew);
                    double inHg = interpAlt;
                    double hPa = inHg * 33.8639;

                    int uiWindSpd = 0;
                    double uiWindDir = 0;
                    var windMatch = System.Text.RegularExpressions.Regex.Match(baseMetar, @"(\d{3}|VRB)(\d{2,3})(?:G\d{2,3})?KT");
                    if (windMatch.Success)
                    {
                        string dir = windMatch.Groups[1].Value;
                        uiWindSpd = int.Parse(windMatch.Groups[2].Value);
                        uiWindDir = (dir == "VRB" || dir == "000") ? 0 : double.Parse(dir);
                    }

                    int uiVis = 10;
                    var visMatch = System.Text.RegularExpressions.Regex.Match(baseMetar, @"\s(\d+)SM");
                    if (visMatch.Success) uiVis = int.Parse(visMatch.Groups[1].Value);
                    else {
                        var meterMatch = System.Text.RegularExpressions.Regex.Match(baseMetar, @"\s(\d{4})\s");
                        if (meterMatch.Success && int.TryParse(meterMatch.Groups[1].Value, out int m)) {
                            uiVis = m >= 9999 ? 10 : (int)Math.Round(m / 1609.34);
                        }
                    }

                    string cloudString = "CLEAR";
                    if (!baseMetar.Contains("CAVOK") && !baseMetar.Contains("SKC") && !baseMetar.Contains("CLR")) 
                    {
                        var clouds = new System.Collections.Generic.List<string>();
                        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(baseMetar, @"(FEW|SCT|BKN|OVC|VV)(\d{3})"))
                        {
                            clouds.Add($"{m.Groups[1].Value} {int.Parse(m.Groups[2].Value) * 100}");
                        }
                        if (clouds.Count > 0) cloudString = string.Join(" / ", clouds);
                    }

                    string iata = string.IsNullOrWhiteSpace(primaryStation.IATA) ? null : primaryStation.IATA.Trim();
                    string name = string.IsNullOrWhiteSpace(primaryStation.Name) ? null : primaryStation.Name.Trim();

                    Dispatcher.Invoke(() => 
                    {
                        lblIata.Text = string.IsNullOrEmpty(iata) ? "---" : iata;
                        lblAirportName.Text = string.IsNullOrEmpty(name) ? "Airport Data Available" : name;
                        lblStationName.Text = primaryStation.ICAO ?? "GLOB";
                        
                        lblLastUpdate.Text = $"{DateTime.UtcNow:HH:mm}Z";
                        lblCoords.Text = $"{primaryStation.Latitude:F3}° / {primaryStation.Longitude:F3}°";
                        lblElevation.Text = $"{primaryStation.Elevation} ft";

                        lblTemp.Text = $"{uiTemp}°C";
                        lblTempImp.Text = $"{Math.Round(uiTemp * 9.0 / 5.0 + 32)}°F";

                        lblDew.Text = $"{uiDew}°C";
                        lblDewImp.Text = $"{Math.Round(uiDew * 9.0 / 5.0 + 32)}°F";

                        lblWind.Text = $"{(int)uiWindDir:D3} @ {uiWindSpd} kts";
                        lblWindImp.Text = $"{Math.Round(uiWindSpd * 1.15078)} mph";

                        lblVis.Text = $"{Math.Round(uiVis * 1.60934, 1)} km";
                        lblVisImp.Text = $"{uiVis} SM";

                        lblPressure.Text = $"{(int)Math.Round(hPa)} hPa";
                        lblPressureImp.Text = $"{inHg:F2} inHg";

                        lblConditions.Text = cloudString;
                        txtMetar.Text = baseMetar;
                    });

                    try {
                        string tafUrl = $"https://aviationweather.gov/api/data/taf?ids={primaryStation.ICAO}&format=raw";
                        HttpResponseMessage tafResp = await client.GetAsync(tafUrl);
                        if (tafResp.IsSuccessStatusCode) {
                            string taf = await tafResp.Content.ReadAsStringAsync();
                            Dispatcher.Invoke(() => txtTaf.Text = taf);
                        }
                    } catch { }

                    AtmosphericProfile localWinds = null;
                    if (primaryStation.Latitude != 0 && primaryStation.Longitude != 0)
                    {
                        localWinds = await FetchWindsForCoordinateAsync(primaryStation.Latitude, primaryStation.Longitude);
                    }

                    string safeP3DMetar = ParseAndSanitizeMetar(baseMetar, primaryStation.Elevation, localWinds);
                    
                    if (!string.IsNullOrEmpty(primaryStation.ICAO) && primaryStation.ICAO.Length == 4)
                    {
                        string injectionIcao = primaryStation.ICAO;
                        
                        // --- THE COMPILER FIX: Call the Async version and await it! ---
                        await EnsureWeatherStationExistsAsync(injectionIcao, primaryStation.Latitude, primaryStation.Longitude, primaryStation.Elevation);
                        
                        safeP3DMetar = safeP3DMetar.Replace("GLOB", injectionIcao);
                    }
                    
                    RunTurbulencePrediction(uiWindSpd, baseMetar);
                    
                    if (simconnect != null) 
                    {
                        Log($"[INJECT] -> {safeP3DMetar}");
                        Dispatcher.Invoke(() => 
                        {
                            try { simconnect.WeatherSetObservation(0, safeP3DMetar); }
                            catch (Exception ex) { Log($"[SIMCONNECT] Injection failed: {ex.Message}"); }
                        });
                    }
                }
            }
            finally
            {
                isFetchingWeather = false; 
            }
        }
        // =========================================================================================
        // 3D HYBRID PARSING ALGORITHM (Sanitizer -> Lexer -> Volumetric Simplifier)
        // =========================================================================================

        private async Task<AtmosphericProfile> FetchWindsForCoordinateAsync(double lat, double lon)
        {
            var profile = new AtmosphericProfile();
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&hourly=wind_speed_250hPa,wind_direction_250hPa,wind_speed_500hPa,wind_direction_500hPa,wind_speed_700hPa,wind_direction_700hPa&wind_speed_unit=kn&forecast_days=1";
        
            try
            {
                 HttpResponseMessage response = await client.GetAsync(url);
                 if (response.IsSuccessStatusCode)
                 {
                    string json = await response.Content.ReadAsStringAsync();
                    using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                    {
                        var hourly = doc.RootElement.GetProperty("hourly");
                        int currentHour = DateTime.UtcNow.Hour;
                        
                        // Parse the JSON and map directly to the flight levels
                        profile.Layers[360] = new WindLayer 
                        {
                            Speed = (int)Math.Round(hourly.GetProperty("wind_speed_250hPa")[currentHour].GetDouble()),
                            Direction = (int)Math.Round(hourly.GetProperty("wind_direction_250hPa")[currentHour].GetDouble())
                        };
                        profile.Layers[240] = new WindLayer 
                        {
                            Speed = (int)Math.Round(hourly.GetProperty("wind_speed_500hPa")[currentHour].GetDouble()),
                            Direction = (int)Math.Round(hourly.GetProperty("wind_direction_500hPa")[currentHour].GetDouble())
                        };
                        profile.Layers[100] = new WindLayer 
                        {
                            Speed = (int)Math.Round(hourly.GetProperty("wind_speed_700hPa")[currentHour].GetDouble()),
                            Direction = (int)Math.Round(hourly.GetProperty("wind_direction_700hPa")[currentHour].GetDouble())
                        };
                    }
                 }
            }
            catch { }
            
            return profile;
        }

        private async Task<string> FetchGridMetarAsync(double lat, double lon, string cellId)
        {
            // THE BUG FIX: Added &timezone=GMT to satisfy Open-Meteo's strict parameter requirements
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&current=temperature_2m,relative_humidity_2m,surface_pressure,wind_speed_10m,wind_direction_10m,cloud_cover,weather_code&wind_speed_unit=kn&timezone=GMT";
            try
            {
                 HttpResponseMessage response = await client.GetAsync(url);
                 if (response.IsSuccessStatusCode)
                 {
                     string json = await response.Content.ReadAsStringAsync();
                     using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                     {
                         var current = doc.RootElement.GetProperty("current");
                         double t = current.GetProperty("temperature_2m").GetDouble();
                         double rh = current.GetProperty("relative_humidity_2m").GetDouble();
                         double p = current.GetProperty("surface_pressure").GetDouble();
                         int ws = (int)Math.Round(current.GetProperty("wind_speed_10m").GetDouble());
                         int wd = (int)Math.Round(current.GetProperty("wind_direction_10m").GetDouble());
                         int clouds = current.GetProperty("cloud_cover").GetInt32();
                         int wxCode = current.GetProperty("weather_code").GetInt32();
                         
                         // Calculate true dewpoint and convert hPa to inches of mercury
                         double dew = t - ((100.0 - rh) / 5.0);
                         double inHg = p * 0.02953;

                         string tempStr = t < 0 ? $"M{Math.Abs((int)t):D2}" : $"{(int)t:D2}";
                         string dewStr = dew < 0 ? $"M{Math.Abs((int)dew):D2}" : $"{(int)dew:D2}";
                         string windStr = $"{wd:D3}{ws:D2}KT";
                         string altStr = $"A{(int)(inHg * 100):D4}";
                         
                         // Map WMO Weather Codes to Aviation Precipitation
                         string wxStr = "";
                         if (wxCode >= 51 && wxCode <= 55) wxStr = "-RA ";
                         else if (wxCode >= 61 && wxCode <= 65) wxStr = "RA ";
                         else if (wxCode >= 71 && wxCode <= 77) wxStr = "SN ";
                         else if (wxCode >= 95 && wxCode <= 99) wxStr = "TSRA ";

                         // Dynamically build the cloud layers
                         string cloudStr = "CLR";
                         if (clouds > 85) cloudStr = "OVC040";
                         else if (clouds > 60) cloudStr = "BKN040";
                         else if (clouds > 25) cloudStr = "SCT040";
                         else if (clouds > 10) cloudStr = "FEW040";

                         return $"{cellId} {DateTime.UtcNow:ddHHmm}Z {windStr} 10SM {wxStr}{cloudStr} {tempStr}/{dewStr} {altStr}";
                     }
                 }
                 else
                 {
                     // Explicit logging for future API debugs
                     LogEngineEvent($"[SKYGRID API ERROR] Open-Meteo rejected request for {cellId}. Status: {response.StatusCode}", LogLevel.Debug);
                 }
            } 
            catch (Exception ex) 
            {
                 LogEngineEvent($"[SKYGRID API ERROR] Failed to parse data for {cellId}: {ex.Message}", LogLevel.Debug);
            }
            
            return $"{cellId} {DateTime.UtcNow:ddHHmm}Z 00000KT 10SM CLR 15/10 A2992"; 
        }

        private async Task EnsureWeatherStationExistsAsync(string icao, double lat, double lon, double elevation = 0)
        {
            if (simconnect == null) return;
            try 
            {
                var method = simconnect.GetType().GetMethods().FirstOrDefault(m => m.Name == "WeatherCreateStation");
                if (method != null)
                {
                    var pInfos = method.GetParameters();
                    object[] args = new object[pInfos.Length];
                    int floatIndex = 0, stringIndex = 0;
                    
                    double elevationMeters = elevation / 3.28084;

                    for (int i = 0; i < pInfos.Length; i++)
                    {
                        var pType = pInfos[i].ParameterType;
                        if (pType == typeof(System.Enum)) args[i] = System.DayOfWeek.Monday;
                        else if (pType == typeof(uint) || pType == typeof(int)) args[i] = Convert.ChangeType(0, pType);
                        else if (pType == typeof(string)) { args[i] = (stringIndex == 0) ? icao : $"Dynamic_{icao}"; stringIndex++; }
                        else if (pType == typeof(float)) 
                        { 
                            args[i] = floatIndex == 0 ? (float)lat : floatIndex == 1 ? (float)lon : floatIndex == 2 ? (float)elevationMeters : 0f; 
                            floatIndex++; 
                        }
                        else if (pType == typeof(double)) 
                        { 
                            args[i] = floatIndex == 0 ? lat : floatIndex == 1 ? lon : floatIndex == 2 ? elevationMeters : 0.0; 
                            floatIndex++; 
                        }
                        else args[i] = pType.IsValueType ? Activator.CreateInstance(pType) : null;
                    }
                    
                    Dispatcher.Invoke(() => method.Invoke(simconnect, args));
                    
                    // --- CRITICAL FIX: THE RACE CONDITION ---
                    // Give Prepar3D time to build the station before we hammer it with weather!
                    await Task.Delay(500);
                }
            } 
            catch { }
        }
        private string ParseAndSanitizeMetar(string rawMetar, double stationElevation, AtmosphericProfile? winds = null)
        {
            if (string.IsNullOrWhiteSpace(rawMetar)) return "";
            var decoder = new MetarDecoder();
            
            var decodeResult = decoder.Decode(rawMetar, stationElevation, winds, _windsCache);
            var model = decodeResult.Model;
            var quality = decodeResult.Quality;

            Dispatcher.Invoke(() => {
                var wMatch = System.Text.RegularExpressions.Regex.Match(model.WindToken, @"^(\d{3}|VRB)(\d{2,3})");
                if (wMatch.Success) {
                    string dirStr = wMatch.Groups[1].Value;
                    lblWindSurf_Aloft.Text = $"{(dirStr == "VRB" ? "VRB" : int.Parse(dirStr).ToString("D3"))} @ {int.Parse(wMatch.Groups[2].Value)} kts";
                }
            });

            var profile = new RendererProfile { MaxCloudLayers = 3, EnforceSingleConvectiveLayer = true };
            var encoder = new TrueSkyWeatherEncoder();
            string finalString = encoder.Encode(model, profile);
            
            if (quality.ConfidenceScore < 100) 
            {
                LogEngineEvent($"[WX DIAGNOSTICS] Quality Score: {quality.ConfidenceScore}/100. Issues: {string.Join(", ", quality.MissingFields)}", LogLevel.Debug);
            }
            
            LogEngineEvent($"[WX MODEL] RH={model.RelativeHumidity:F1}%, ConvectiveIdx={model.ConvectiveIndex}", LogLevel.Debug);
            LogEngineEvent($"[WX] Temp={model.TempC}°C, Dewpoint={model.DewpointC}°C, Visibility={model.PrevailingVisibilitySM}SM", LogLevel.Debug);
            LogEngineEvent($"[WX] Cloud Layers={model.CloudLayers.Count}, Aloft Tiers={model.Atmosphere.Layers.Count}", LogLevel.Debug);
            LogEngineEvent($"[WX] Wind={model.WindToken}, Altimeter={model.AltimeterToken}", LogLevel.Debug);
            LogEngineEvent($"[WX] Diagnostics: Turbulence={model.TurbulenceOutlook} (Score: {model.TurbulenceIndex})", LogLevel.Debug);
            
            // --- THE FIX: SIMCONNECT ERROR 16 SANITIZATION BLOCK ---
            
            // 1. Strip the '=' sign if it bled through
            finalString = finalString.Replace("=", "");

            // 2. Strip European trend forecasts and remarks (P3D crashes on these)
            string[] badTags = { " NOSIG", " TEMPO", " BECMG", " RMK", " PROB" };
            foreach (string tag in badTags)
            {
                int idx = finalString.IndexOf(tag);
                if (idx > 0) finalString = finalString.Substring(0, idx);
            }

            // 3. Convert VRB winds to 000 degrees (P3D math engine crashes on "VRB")
            finalString = System.Text.RegularExpressions.Regex.Replace(finalString, @"\bVRB(\d{2,3}KT)\b", "000$1");

            // 4. Force Altimeter to standard FAA format (Axxxx) if it generated QNH (Qxxxx)
            finalString = System.Text.RegularExpressions.Regex.Replace(finalString, @"\bQ(\d{4})\b", m => 
            {
                if (double.TryParse(m.Groups[1].Value, out double qnh))
                {
                    return $"A{(int)(qnh * 0.02953 * 100):D4}";
                }
                return m.Value;
            });

            // 5. Remove duplicate precipitation modifiers that choke the tokenizer
            finalString = finalString.Replace("RA RA", "RA").Replace("TSRA TSRA", "TSRA").Replace("SHRA RA", "SHRA");
            finalString = System.Text.RegularExpressions.Regex.Replace(finalString, @"\b([A-Z]{2,5})\s+\1\b", "$1");

            // 6. Enforce strict single spacing (Double spaces cause Error 16)
            finalString = System.Text.RegularExpressions.Regex.Replace(finalString, @"\s+", " ").Trim();
            
            return finalString;
        }
        private string ToAviationDigits(string input)
        {
            string result = "";
            foreach (char c in input) result += (c == '9' ? "niner " : c + " ");
            return result.Trim();
        }
        // --- CUSTOM TITLE BAR CONTROLS ---
        private void TitleBarClose_Click(object sender, RoutedEventArgs e) => Close();
        private void TitleBarMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void TitleBarMaximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        // --- TAB NAVIGATION LOGIC ---
        // --- TAB NAVIGATION LOGIC ---
        // --- TAB NAVIGATION LOGIC ---
        // Flag to prevent the heavy map HTML from reloading every time you switch tabs
        private bool _isFullMapInitialized = false; 

        private void TabButton_Click(object sender, RoutedEventArgs e)
        {
            Button clickedBtn = (Button)sender;

            // 1. Reset all tabs to the inactive gray style
            btnTabWxConfig.Style = (Style)FindResource("TabButton");
            btnTabMap.Style = (Style)FindResource("TabButton");
            btnTabConditions.Style = (Style)FindResource("TabButton");
            btnTabFlightPlan.Style = (Style)FindResource("TabButton");
            btnTabBriefing.Style = (Style)FindResource("TabButton");
            btnTabSettings.Style = (Style)FindResource("TabButton");

            // 2. Highlight the tab the user just clicked orange
            clickedBtn.Style = (Style)FindResource("ActiveTabButton");

            // 3. Toggle View Visibility - HIDE EVERYTHING FIRST
            ConditionsView.Visibility = Visibility.Collapsed;
            ComingSoonView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed; 
            FlightPlanView.Visibility = Visibility.Collapsed;
            if (this.FindName("MapView") != null) ((System.Windows.Controls.Grid)this.FindName("MapView")).Visibility = Visibility.Collapsed;

            // 4. Show only the requested view
            if (clickedBtn == btnTabConditions)
            {
                ConditionsView.Visibility = Visibility.Visible;
            }
            else if (clickedBtn == btnTabMap) // <-- NEW MAP TAB LOGIC
            {
                MapView.Visibility = Visibility.Visible;
                if (!_isFullMapInitialized)
                {
                    _isFullMapInitialized = true;
                    InitializeFullMapAsync();
                }
            }
            else if (clickedBtn == btnTabSettings)
            {
                SettingsView.Visibility = Visibility.Visible; 
            }
            else if (clickedBtn == btnTabFlightPlan)
            {
                FlightPlanView.Visibility = Visibility.Visible;
            }
            else
            {
                ComingSoonView.Visibility = Visibility.Visible; 
            }
        }

        private async void InitializeFullMapAsync()
        {
            await fullMapBrowser.EnsureCoreWebView2Async(null);
            
            // --- ADD THIS LINE: Listen for messages from the map ---
            fullMapBrowser.WebMessageReceived += FullMapBrowser_WebMessageReceived;

            // Wait for the HTML and Leaflet JS to physically finish loading in the browser!
            fullMapBrowser.NavigationCompleted += async (sender, args) =>
            {
                // If a flight plan was already downloaded before this tab was opened, draw it now!
                if (_currentOfp != null)
                {
                    await PushLiveAirportsToMapAsync(_currentOfp);
                }
            };
            
            fullMapBrowser.NavigateToString(GenerateFullMapHtml());
            Log("Dedicated Layered Map Engine initialized.");
        }

        public void UpdateFullMap(double lat, double lon, double hdg, double gs, double alt)
        {
            if (fullMapBrowser == null || fullMapBrowser.CoreWebView2 == null) return;

            // Injects live telemetry into the JS layer manager without reloading the page
            string jsCommand = $"updateAircraft({lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                               $"{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                               $"{hdg.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                               $"{gs.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                               $"{alt.ToString(System.Globalization.CultureInfo.InvariantCulture)});";
            
            fullMapBrowser.CoreWebView2.ExecuteScriptAsync(jsCommand);
        }

        private void PushSkyGridToMap()
        {
            // Abort safely if the map hasn't loaded or the grid hasn't been generated
            if (fullMapBrowser == null || fullMapBrowser.CoreWebView2 == null || _skyGridManager == null) return;

            // Strip down the complex objects into a lightweight JSON payload for the browser
            var simplifiedCells = _skyGridManager.Cells.Select(c => new {
                ID = c.CellID,
                Lat = c.CenterLat,
                Lon = c.CenterLon,
                Pri = (int)c.Priority,
                State = (int)c.State
            }).ToList();

            string json = System.Text.Json.JsonSerializer.Serialize(simplifiedCells);
            
            // Fire the javascript function without reloading the page!
            _ = fullMapBrowser.CoreWebView2.ExecuteScriptAsync($"if(typeof updateSkyGrid === 'function') {{ updateSkyGrid('{json}'); }}");
        }

        private string GenerateFullMapHtml()
        {
            return @"<!DOCTYPE html>
            <html>
            <head>
                <link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css' />
                <script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
                <style>
                    body { padding: 0; margin: 0; background-color: #111; color: #E0E0E0; font-family: 'Product Sans', 'Segoe UI', Tahoma, sans-serif; overflow: hidden; }
                    #map { height: 100vh; width: 100vw; z-index: 1; }
                    .leaflet-control-attribution, .leaflet-control-zoom { display: none !important; }
                    .glass-panel { position: absolute; background: rgba(25, 25, 25, 0.85); border: 1px solid #444; border-radius: 8px; z-index: 1000; backdrop-filter: blur(8px); }
                    #toolbar { top: 20px; left: 20px; display: flex; flex-direction: column; padding: 5px; gap: 5px; }
                    .tool-btn { width: 36px; height: 36px; background: #333; border: 1px solid #555; color: #aaa; cursor: pointer; border-radius: 6px; font-size: 14px; }
                    .tool-btn.active { background: #D87A1E; color: #fff; border-color: #D87A1E; }
                    #layers-panel { top: 20px; right: 20px; width: 220px; padding: 15px; }
                    .layer-header { font-size: 11px; font-weight: bold; color: #D87A1E; margin: 10px 0 5px 0; text-transform: uppercase; border-bottom: 1px solid #444; }
                    .layer-toggle { display: flex; align-items: center; margin-bottom: 6px; font-size: 13px; cursor: pointer; }
                    .layer-toggle input { margin-right: 8px; accent-color: #D87A1E; }
                    
                    #winds-selector { top: 20px; right: 290px; display: flex; padding: 5px; gap: 2px; display: none; }
                    .alt-btn { background: transparent; color: #aaa; border: none; padding: 6px 10px; border-radius: 4px; font-size: 12px; cursor: pointer; font-weight: bold; font-family: 'Product Sans', 'Segoe UI', sans-serif; }
                    .alt-btn.active { background: #555; color: #D87A1E; }
                    
                    #status-bar { bottom: 0; left: 0; width: 100%; height: 30px; background: rgba(15, 15, 15, 0.95); border-top: 1px solid #333; z-index: 1000; position: absolute; display: flex; align-items: center; padding: 0 15px; font-size: 12px; color: #888; }
                    .status-item { margin-right: 25px; }
                    .status-val { color: #fff; font-weight: bold; margin-left: 5px; }
                    .leaflet-popup-content-wrapper { background: #222; color: #E0E0E0; border: 1px solid #444; border-radius: 8px; font-family: 'Product Sans', 'Segoe UI', sans-serif; }
                    .leaflet-popup-tip { background: #222; }
                    .pop-title { font-size: 18px; font-weight: bold; color: #D87A1E; margin: 0 0 10px 0; border-bottom: 1px solid #444; padding-bottom: 5px; }
                    .pop-row { margin-bottom: 5px; font-size: 12px; }
                </style>
            </head>
            <body>
                <div id='map'></div>
                
                <div id='toolbar' class='glass-panel'>
                    <button class='tool-btn active'>🗺️</button>
                    <button class='tool-btn' onclick='document.getElementById(""layers-panel"").style.display = document.getElementById(""layers-panel"").style.display === ""none"" ? ""block"" : ""none"";'>⚙️</button>
                </div>
                
                <div id='winds-selector' class='glass-panel'>
                    <button class='alt-btn' data-lvl='10k' onclick='changeWindAlt(this)'>FL100</button>
                    <button class='alt-btn' data-lvl='24k' onclick='changeWindAlt(this)'>FL240</button>
                    <button class='alt-btn active' data-lvl='36k' onclick='changeWindAlt(this)'>FL360</button>
                </div>
                
                <div id='layers-panel' class='glass-panel'>
                    <div class='layer-header'>Aviation</div>
                    <label class='layer-toggle'><input type='checkbox' checked onchange='toggleLayer(this, aircraftLayer)'> Aircraft Position</label>
                    <label class='layer-toggle'><input type='checkbox' checked onchange='toggleLayer(this, routeLayer)'> Flight Route</label>
                    <label class='layer-toggle'><input type='checkbox' onchange='toggleLayer(this, airportsLayer)'> Airports (METAR)</label>
                    
                    <div class='layer-header'>Meteorology</div>
                    <label class='layer-toggle'><input type='checkbox' onchange='toggleWinds(this)'> Winds Aloft</label>
                    
                    <div class='layer-header' style='color:#10B981;'>Developer / Debug</div>
                    <label class='layer-toggle'><input type='checkbox' id='chkSkyGrid' onchange='toggleLayer(this, skyGridLayer)'> SkyGrid Engine</label>
                </div>

                <div id='status-bar'>
                    <div class='status-item'>Cursor: <span class='status-val' id='sb-coords'>-- / --</span></div>
                    <div class='status-item'>Elev: <span class='status-val' id='sb-elev'>-- ft</span></div>
                    <div class='status-item'>Wind: <span class='status-val' id='sb-wind'>-- / --</span></div>
                </div>

                <script>
                    var map = L.map('map', { zoomControl: false }).setView([25, 55], 4);
                    L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', { maxZoom: 19 }).addTo(map);

                    var aircraftLayer = L.layerGroup().addTo(map);
                    var routeLayer = L.layerGroup().addTo(map);
                    var airportsLayer = L.layerGroup();
                    var windsLayer = L.layerGroup();
                    var skyGridLayer = L.layerGroup();

                    var activeWindData = null;
                    var activeUtcHour = 0;
                    var activeFlightLevel = '36k';

                    function toggleLayer(cb, layer) { cb.checked ? map.addLayer(layer) : map.removeLayer(layer); }
                    function toggleWinds(cb) {
                        toggleLayer(cb, windsLayer);
                        document.getElementById('winds-selector').style.display = cb.checked ? 'flex' : 'none';
                    }

                    // --- SVG WIND BARB MATH ---
                    function getWindBarbIcon(speed, dir) {
                        let svg = `<svg width=""40"" height=""40"" viewBox=""0 0 40 40"" style=""transform: rotate(${dir}deg); transform-origin: 20px 20px;"">`;
                        svg += `<circle cx=""20"" cy=""20"" r=""2.5"" fill=""#D87A1E""/>`; 
                        svg += `<line x1=""20"" y1=""20"" x2=""20"" y2=""2"" stroke=""#E0E0E0"" stroke-width=""1.5""/>`;
                        let y = 2; let s = speed;
                        while(s >= 50) { svg += `<polygon points=""20,${y} 20,${y+5} 28,${y+2}"" fill=""#E0E0E0""/>`; s -= 50; y += 6; }
                        while(s >= 10) { svg += `<line x1=""20"" y1=""${y}"" x2=""28"" y2=""${y-4}"" stroke=""#E0E0E0"" stroke-width=""1.5""/>`; s -= 10; y += 4; }
                        if(s >= 5) { svg += `<line x1=""20"" y1=""${y}"" x2=""24"" y2=""${y-2}"" stroke=""#E0E0E0"" stroke-width=""1.5""/>`; }
                        svg += `</svg>`;
                        return L.divIcon({ className: '', html: svg, iconSize: [40, 40], iconAnchor: [20, 20] });
                    }

                    function buildRealWindsGrid(apiData, currentHour) {
                        activeWindData = apiData;
                        activeUtcHour = currentHour;
                        drawWinds();
                    }

                    function changeWindAlt(btn) {
                        document.querySelectorAll('.alt-btn').forEach(b => b.classList.remove('active'));
                        btn.classList.add('active');
                        activeFlightLevel = btn.getAttribute('data-lvl');
                        drawWinds(); 
                    }

                    function drawWinds() {
                        if (!activeWindData) return;
                        windsLayer.clearLayers();
                        
                        let locations = Array.isArray(activeWindData) ? activeWindData : [activeWindData];
                        
                        locations.forEach((loc) => {
                            let spdKey = activeFlightLevel === '36k' ? 'wind_speed_250hPa' : (activeFlightLevel === '24k' ? 'wind_speed_500hPa' : 'wind_speed_700hPa');
                            let dirKey = activeFlightLevel === '36k' ? 'wind_direction_250hPa' : (activeFlightLevel === '24k' ? 'wind_direction_500hPa' : 'wind_direction_700hPa');
        
                            let spd = Math.round(loc.hourly[spdKey][activeUtcHour]);
                            let dir = Math.round(loc.hourly[dirKey][activeUtcHour]);
                  
                            let marker = L.marker([loc.latitude, loc.longitude], {icon: getWindBarbIcon(spd, dir), interactive: true}).addTo(windsLayer);
                            
                            marker.on('mouseover', function() { document.getElementById('sb-wind').innerText = `${dir}° @ ${spd} kt`; document.getElementById('sb-wind').style.color = '#D87A1E'; });
                            marker.on('mouseout', function() { document.getElementById('sb-wind').innerText = '-- / --'; document.getElementById('sb-wind').style.color = '#888'; });
                        });
                    }

                    function updateSkyGrid(gridDataJson) {
                        if(typeof skyGridLayer === 'undefined') return;
                        skyGridLayer.clearLayers();
                        var chk = document.getElementById('chkSkyGrid');
                        if (!chk || !chk.checked) return;

                        var cells = JSON.parse(gridDataJson);
                        var halfSp = 0.35; 

                        cells.forEach(function(c) {
                            var color = '#333'; var op = 0.1;
                            if (c.State === 1) { color = '#F59E0B'; op = 0.3; } 
                            if (c.State === 2) { color = '#3B82F6'; op = 0.4; } 
                            if (c.State === 3) { color = '#10B981'; op = 0.5; } 
                            if (c.State === 4) { color = '#D87A1E'; op = 0.4; } 
                            if (c.State === 5) { color = '#EF4444'; op = 0.2; } 

                            if (c.State > 0) { 
                                var bounds = [[c.Lat - halfSp, c.Lon - halfSp], [c.Lat + halfSp, c.Lon + halfSp]];
                                var rect = L.rectangle(bounds, {color: color, weight: 1, fillOpacity: op, dashArray: '4,4'}).addTo(skyGridLayer);
                                var marker = L.circleMarker([c.Lat, c.Lon], {radius: 3, color: color, fillOpacity: 1}).addTo(skyGridLayer);

                                var stateNames = ['UNLOADED','REQUESTING','READY','INJECTED','STALE','DISCARDED'];
                                var tipHtml = '<b>' + c.ID + '</b><br>State: ' + stateNames[c.State] + '<br>Priority: P' + (c.Pri + 1);
                                rect.bindTooltip(tipHtml);
                            }
                        });
                    }

                    var planeMarker;
                    function updateAircraft(lat, lon, hdg, gs, alt) {
                        if(!planeMarker) {
                            var icon = L.divIcon({ className: 'ac-icon', html: ""<div style='background-color:#D87A1E; width:16px; height:16px; border-radius:50%; border:2px solid white;'></div>"", iconSize: [16, 16], iconAnchor: [8, 8] });
                            planeMarker = L.marker([lat, lon], {icon: icon}).addTo(aircraftLayer);
                        } else planeMarker.setLatLng([lat, lon]);
                    }

                    map.on('mousemove', function(e) { document.getElementById('sb-coords').innerText = e.latlng.lat.toFixed(4) + '° / ' + e.latlng.lng.toFixed(4) + '°'; });
                </script>
            </body>
            </html>";
        }

        #region FLIGHT PLAN & DISPATCH CENTER
        // Calculates the distance in Nautical Miles between two GPS coordinates
        private double CalculateDistanceNM(double lat1, double lon1, double lat2, double lon2)
        {
            var d1 = lat1 * (Math.PI / 180.0);
            var num1 = lon1 * (Math.PI / 180.0);
            var d2 = lat2 * (Math.PI / 180.0);
            var num2 = lon2 * (Math.PI / 180.0) - num1;
            var d3 = Math.Pow(Math.Sin((d2 - d1) / 2.0), 2.0) + Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin(num2 / 2.0), 2.0);
            return 3440.065 * (2.0 * Math.Atan2(Math.Sqrt(d3), Math.Sqrt(1.0 - d3))); // 3440.065 is Earth's radius in NM
        }
        private async System.Threading.Tasks.Task<string> GenerateSyntheticOceanicMetarAsync(string waypointIdent, double lat, double lon)
        {
            try
            {
                // We ask Open-Meteo for the current surface conditions exactly at this GPS coordinate
                string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&current=temperature_2m,relative_humidity_2m,precipitation,cloud_cover,surface_pressure,wind_speed_10m,wind_direction_10m&wind_speed_unit=kn";
                
                string json = await client.GetStringAsync(url);
                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var current = doc.RootElement.GetProperty("current");

                    // 1. Wind
                    int windSpd = (int)Math.Round(current.GetProperty("wind_speed_10m").GetDouble());
                    int windDir = (int)Math.Round(current.GetProperty("wind_direction_10m").GetDouble());
                    string windString = $"{windDir:D3}{windSpd:D2}KT";

                    // 2. Temp & Dewpoint (Approximating dewpoint from relative humidity)
                    int temp = (int)Math.Round(current.GetProperty("temperature_2m").GetDouble());
                    int humidity = (int)Math.Round(current.GetProperty("relative_humidity_2m").GetDouble());
                    int dew = (int)Math.Round(temp - ((100.0 - humidity) / 5.0)); // Simple marine dewpoint approximation
                    string tempString = $"{(temp < 0 ? "M" + Math.Abs(temp).ToString("D2") : temp.ToString("D2"))}/{(dew < 0 ? "M" + Math.Abs(dew).ToString("D2") : dew.ToString("D2"))}";

                    // 3. Altimeter (Surface Pressure in hPa to inHg)
                    double hpa = current.GetProperty("surface_pressure").GetDouble();
                    int altimeter = (int)Math.Round((hpa * 0.029530) * 100);
                    string altString = $"A{altimeter:D4}";

                    // 4. Clouds (Translating percentage to aviation terms)
                    int cloudCover = (int)Math.Round(current.GetProperty("cloud_cover").GetDouble());
                    string cloudString = "CAVOK";
                    
                    if (cloudCover > 85) cloudString = "OVC025";
                    else if (cloudCover > 50) cloudString = "BKN030";
                    else if (cloudCover > 25) cloudString = "SCT035";
                    else if (cloudCover > 5) cloudString = "FEW040";

                    // 5. Precipitation
                    double precip = current.GetProperty("precipitation").GetDouble();
                    string wxString = "";
                    if (precip > 2.0) wxString = "+RA ";
                    else if (precip > 0.5) wxString = "RA ";
                    else if (precip > 0.1) wxString = "-RA ";

                    // Assemble the fake METAR!
                    string timestamp = DateTime.UtcNow.ToString("ddHHmm") + "Z";
                    string syntheticMetar = $"{waypointIdent} {timestamp} {windString} 10SM {wxString}{cloudString} {tempString} {altString}";
                    
                    LogEngineEvent($"[OCEANIC FORGER] Generated synthetic marine weather for {waypointIdent}: {syntheticMetar}", LogLevel.Debug);
                    return syntheticMetar;
                }
            }
            catch (Exception ex)
            {
                LogEngineEvent($"[OCEANIC FORGER] Failed to generate marine weather for {waypointIdent}: {ex.Message}", LogLevel.Debug);
                // Safe fallback if API fails
                return $"{waypointIdent} {DateTime.UtcNow:ddHHmm}Z 00000KT 10SM CAVOK 15/10 A2992";
            }
        }

        private async void InjectBaselineAtmosphere()
        {
            try
            {
                // Wait 3 seconds to ensure P3D's environment is fully loaded before painting the canvas
                await System.Threading.Tasks.Task.Delay(3000);
                
                string baselineMetar = $"GLOB {DateTime.UtcNow:ddHHmm}Z 00000KT";

                // SHIELD: Prevent overwriting the winds aloft if they were already fetched!
                if (_windsCache.Layers.ContainsKey(360))
                {
                    baselineMetar += $" {_windsCache.Layers[100].Direction:D3}{_windsCache.Layers[100].Speed:D2}KT&A3048";
                    baselineMetar += $" {_windsCache.Layers[240].Direction:D3}{_windsCache.Layers[240].Speed:D2}KT&A7315";
                    baselineMetar += $" {_windsCache.Layers[360].Direction:D3}{_windsCache.Layers[360].Speed:D2}KT&A10973";
                }

                baselineMetar += " 100SM CLR 15/10 A2992";

                if (simconnect != null)
                {
                    simconnect.WeatherSetObservation(0, baselineMetar);
                    LogEngineEvent("[ENGINE] Global Atmospheric Baseline injected successfully.", LogLevel.Normal);
                }
            }
            catch (Exception ex)
            {
                LogEngineEvent($"[ENGINE] Failed to inject baseline atmosphere: {ex.Message}", LogLevel.Debug);
            }
        }
        private async System.Threading.Tasks.Task InjectFlightPlanWeatherAsync(System.Collections.Generic.List<Waypoint> route)
        {
            if (route == null || route.Count == 0) return;
            LogEngineEvent($"[DISPATCH] Initiating Local Route Injection for {route.Count} waypoints...", LogLevel.Normal);
            
            _activeRouteStations.Clear();
            System.Collections.Generic.HashSet<string> injectedThisRun = new System.Collections.Generic.HashSet<string>();

            foreach (var waypoint in route)
            {
                var nearestStations = await System.Threading.Tasks.Task.Run(() => locator.GetNearestStations(waypoint.Latitude, waypoint.Longitude, 1));
                
                if (nearestStations != null && nearestStations.Count > 0)
                {
                    var nearest = nearestStations[0];
                    double distanceToStation = CalculateDistanceNM(waypoint.Latitude, waypoint.Longitude, nearest.Station.Latitude, nearest.Station.Longitude);
                    
                    if (distanceToStation <= 250 && !string.IsNullOrEmpty(nearest.Station.ICAO) && nearest.Station.ICAO.Length == 4)
                    {
                        string injectionIcao = nearest.Station.ICAO;

                        if (injectedThisRun.Add(injectionIcao))
                        {
                            _activeRouteStations.Add(injectionIcao);
                            string rawMetar = await FetchVatsimMetarAsync(injectionIcao);
                            
                            if (!string.IsNullOrEmpty(rawMetar))
                            {
                                // --- THE COMPILER FIX: Call the Async version and await it! ---
                                await EnsureWeatherStationExistsAsync(injectionIcao, nearest.Station.Latitude, nearest.Station.Longitude, nearest.Station.Elevation);

                                AtmosphericProfile localWinds = await FetchWindsForCoordinateAsync(nearest.Station.Latitude, nearest.Station.Longitude);
                                string baseP3dString = ParseAndSanitizeMetar(rawMetar, nearest.Station.Elevation, localWinds);
                                string localP3dString = baseP3dString.Replace("GLOB", injectionIcao);

                                if (simconnect != null)
                                {
                                    LogEngineEvent($"[INJECT LOCAL] -> {localP3dString}", LogLevel.Normal);
                                    Dispatcher.Invoke(() => 
                                    {
                                        try { simconnect.WeatherSetObservation(0, localP3dString); }
                                        catch (Exception ex) { LogEngineEvent($"[SIMCONNECT] Local injection failed: {ex.Message}", LogLevel.Normal); }
                                    });
                                }
                            }
                        }
                    }
                }
                await System.Threading.Tasks.Task.Delay(50);
            }

            LogEngineEvent($"[DISPATCH] Local Injection Complete. {_activeRouteStations.Count} stations added to VATSIM sync corridor.", LogLevel.Normal);
            
            if (_skyGridManager != null)
            {
                var activeCells = _skyGridManager.Cells.Where(c => c.State == CellState.READY || c.State == CellState.INJECTED).ToList();
                if (activeCells.Count > 0)
                {
                    LogEngineEvent($"[SKYGRID] Updating weather for {activeCells.Count} active grid cells from Open-Meteo...", LogLevel.Normal);
                    foreach (var cell in activeCells)
                    {
                        string rawMetar = await FetchGridMetarAsync(cell.CenterLat, cell.CenterLon, cell.CellID);
                        AtmosphericProfile localWinds = await FetchWindsForCoordinateAsync(cell.CenterLat, cell.CenterLon);
                        string safeP3dString = ParseAndSanitizeMetar(rawMetar, 0, localWinds);

                        safeP3dString = safeP3dString.Replace("GLOB", cell.CellID);

                        if (simconnect != null)
                        {
                            Dispatcher.Invoke(() => 
                            {
                                try { simconnect.WeatherSetObservation(0, safeP3dString); }
                                catch (Exception ex) { LogEngineEvent($"[SIMCONNECT] Grid injection failed: {ex.Message}", LogLevel.Debug); }
                            });
                        }
                        await System.Threading.Tasks.Task.Delay(50);
                    }
                    LogEngineEvent($"[SKYGRID] Spatial weather update complete.", LogLevel.Normal);
                }
            }

            InitializeVatsimRouteSync();
            _vatsimSyncTimer.Start();
        }
        private void InitializeVatsimRouteSync()
        {
            if (_vatsimSyncTimer == null)
            {
                _vatsimSyncTimer = new System.Windows.Threading.DispatcherTimer();
                _vatsimSyncTimer.Interval = TimeSpan.FromMinutes(5);
                _vatsimSyncTimer.Tick += async (s, e) => await SyncRouteWithVatsimAsync();
            }
        }

        private async Task<string> FetchVatsimMetarAsync(string icao)
        {
            try 
            {
                // VATSIM's ultra-lightweight raw text endpoint
                string rawText = await client.GetStringAsync($"https://metar.vatsim.net/metar.php?id={icao}");
                return string.IsNullOrWhiteSpace(rawText) ? "" : rawText.Trim();
            } 
            catch { return ""; }
        }

        private async System.Threading.Tasks.Task SyncRouteWithVatsimAsync()
        {
            if (_activeRouteStations.Count == 0) return;
            LogEngineEvent($"[CORRIDOR SYNC] Polling VATSIM for {_activeRouteStations.Count} enroute stations...", LogLevel.Normal);

            foreach (var icao in _activeRouteStations)
            {
                string vatsimMetar = await FetchVatsimMetarAsync(icao);
                if (!string.IsNullOrEmpty(vatsimMetar))
                {
                    var station = locator.GetStationByIcao(icao);
                    double elevation = station != null ? station.Elevation : 0;
                    AtmosphericProfile localWinds = null;
                    if (station != null)
                    {
                        localWinds = await FetchWindsForCoordinateAsync(station.Latitude, station.Longitude);
                        
                        // --- THE COMPILER FIX: Call the Async version and await it! ---
                        await EnsureWeatherStationExistsAsync(icao, station.Latitude, station.Longitude, station.Elevation);
                    }

                    string safeP3dString = ParseAndSanitizeMetar(vatsimMetar, elevation, localWinds);
                    if (!string.IsNullOrEmpty(safeP3dString))
                    {
                        safeP3dString = safeP3dString.Replace("GLOB", icao);

                        if (simconnect != null)
                        {
                            Dispatcher.Invoke(() => 
                            {
                                try { simconnect.WeatherSetObservation(0, safeP3dString); }
                                catch (Exception ex) { LogEngineEvent($"[SIMCONNECT] Background injection failed: {ex.Message}", LogLevel.Debug); }
                            });
                        }
                    }

                    LogEngineEvent($"[VATSIM SYNC] Updated {icao} locally.", LogLevel.Debug);
                }
                await System.Threading.Tasks.Task.Delay(300);
            }
            
            LogEngineEvent($"[CORRIDOR SYNC] Background weather refresh complete.", LogLevel.Debug);

            string currentlyDisplayedIcao = "";
            Dispatcher.Invoke(() => {
                currentlyDisplayedIcao = lblStationName.Text.Trim().ToUpper();
            });

            if (!string.IsNullOrEmpty(currentlyDisplayedIcao) && _activeRouteStations.Contains(currentlyDisplayedIcao))
            {
                var station = locator.GetStationByIcao(currentlyDisplayedIcao);
                if (station != null)
                {
                    LogEngineEvent($"[UI SYNC] Refreshing UI for active station: {currentlyDisplayedIcao}.", LogLevel.Normal);
                    var list = new System.Collections.Generic.List<(WeatherStation Station, double Distance)> { (station, 0.0) };
                    await Dispatcher.InvokeAsync(async () => {
                        await UpdateInterpolatedWeatherAsync(list); 
                    });
                }
            }
        }
        private SkyGridManager _skyGridManager;      
        private async void BtnImportSimbrief_Click(object sender, RoutedEventArgs e)    
        {
            string username = txtSimbriefId.Text.Trim();
            if (string.IsNullOrEmpty(username))
            {
                MessageBox.Show("Please enter a SimBrief Username or Pilot ID.", "Missing Information", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LogEngineEvent($"[DISPATCH] Initiating SimBrief OFP download for user: {username}", LogLevel.Normal);
            btnImportSimbrief.Content = "Loading...";

            try
            {
                SimBriefService sbService = new SimBriefService();
                SimBriefData ofp = await sbService.FetchLatestOFPAsync(username);

                lblBriefFlight.Text = $"{ofp.Airline}{ofp.FlightNumber}";
                lblBriefRoute.Text = $"{ofp.DepartureICAO} → {ofp.ArrivalICAO}";
                lblBriefDist.Text = $"{ofp.DistanceNM} NM";
                
                if (int.TryParse(ofp.BlockTime, out int seconds))
                {
                    TimeSpan time = TimeSpan.FromSeconds(seconds);
                    lblBriefTime.Text = $"{(int)time.TotalHours:D2}:{time.Minutes:D2}";
                }

                string rawAlt = ofp.CruiseAltitude ?? "";
                if (int.TryParse(rawAlt, out int altFeet) && altFeet >= 1000) {
                    lblBriefCruise.Text = $"FL{altFeet / 100}";
                }
                else {
                    lblBriefCruise.Text = rawAlt.StartsWith("FL") ? rawAlt : (rawAlt.StartsWith("F") ? $"FL{rawAlt.Substring(1)}" : $"FL{rawAlt}");
                }

                LogEngineEvent($"[DISPATCH] Successfully parsed SimBrief OFP for {lblBriefFlight.Text}.", LogLevel.Normal);
                if (ofp.RouteWaypoints != null && ofp.RouteWaypoints.Count > 0) {
                    await DrawFlightPlanMapAsync(ofp.RouteWaypoints);
                }

                await GenerateFlightBriefingAsync(ofp.DepartureICAO, ofp.ArrivalICAO, ofp.RouteWaypoints);
                var combinedRoute = new System.Collections.Generic.List<Waypoint>(ofp.RouteWaypoints);
                if (ofp.AlternateWaypoints != null && ofp.AlternateWaypoints.Count > 0)
                {
                    combinedRoute.AddRange(ofp.AlternateWaypoints);
                    LogEngineEvent($"[DISPATCH] Added {ofp.AlternateWaypoints.Count} alternate waypoints to the weather grid.", LogLevel.Debug);
                }

                _currentFlightPlanWaypoints = combinedRoute;
                
                // THE FIX: PASSED SIMCONNECT DIRECTLY INTO THE MANAGER!
                if (_skyGridManager == null) _skyGridManager = new SkyGridManager(this.LogEngineEvent, this.simconnect);
                _skyGridManager.GenerateGridFromRoute(_currentFlightPlanWaypoints);

                await CalculateEnrouteStationsAsync(_currentFlightPlanWaypoints);
                BtnInjectPlanWx.IsEnabled = true;
                _currentOfp = ofp; 
                await PushLiveAirportsToMapAsync(ofp);
                PushSkyGridToMap();
            }
            catch (Exception ex)
            {
                LogEngineEvent($"[DISPATCH ERROR] SimBrief Fetch Failed: {ex.Message}", LogLevel.Minimal);
                MessageBox.Show($"Could not retrieve SimBrief data.\nEnsure your username is correct and an OFP has been generated.\n\nError: {ex.Message}", "SimBrief Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnImportSimbrief.Content = "Import OFP";
            }
        }

        private async void BtnInjectPlanWx_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_currentFlightPlanWaypoints != null && _currentFlightPlanWaypoints.Count > 0)
            {
                try
                {
                    BtnInjectPlanWx.IsEnabled = false;
                    BtnInjectPlanWx.Content = "Injecting...";
                    
                    LogEngineEvent("[DISPATCH] Manual route weather injection triggered.", LogLevel.Normal);
                    await InjectFlightPlanWeatherAsync(_currentFlightPlanWaypoints);
                }
                catch (Exception ex)
                {
                    LogEngineEvent($"[FATAL] Injection process encountered an error: {ex.Message}", LogLevel.Normal);
                    System.Windows.MessageBox.Show($"Weather Injection Error:\n{ex.Message}", "SkyNexus Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                finally
                {
                    // This block GUARANTEES the button resets, even if the app fails
                    BtnInjectPlanWx.Content = "Inject Route WX";
                    BtnInjectPlanWx.IsEnabled = true;
                }
            }
        }
        private void BtnImportFile_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement Microsoft.Win32.OpenFileDialog
            // Call RouteParserService.ParseFile(dialog.FileName)
            LogEngineEvent("[DISPATCH] Local .PLN / .RTE parsing requested (Pending Implementation).", LogLevel.Debug);
            MessageBox.Show("Local file parsing for .PLN and .RTE files is ready to be implemented in the RouteParserService.", "Coming Soon");
        }

        private async Task<(string raw, int wdir, int wspd, double temp, double dew, double alt)> FetchMetarDataAsync(string icao)
        {
            // 1. Primary Attempt: NOAA JSON API
            try
            {
                string json = await client.GetStringAsync($"https://aviationweather.gov/api/data/metar?ids={icao}&format=json&hours=6");
                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    if (doc.RootElement.GetArrayLength() > 0)
                    {
                        var wx = doc.RootElement[0]; // Gets the most recent
                        return (
                            wx.GetProperty("rawOb").GetString() ?? "",
                            wx.TryGetProperty("wdir", out var wd) ? wd.GetInt32() : 0,
                            wx.TryGetProperty("wspd", out var ws) ? ws.GetInt32() : 0,
                            wx.TryGetProperty("temp", out var t) ? t.GetDouble() : 0,
                            wx.TryGetProperty("dewp", out var d) ? d.GetDouble() : 0,
                            wx.TryGetProperty("altim", out var a) ? a.GetDouble() : 1013.25
                        );
                    }
                }
            }
            catch { LogEngineEvent($"[DISPATCH] JSON failed for {icao}. Pivoting to raw NWS text servers...", LogLevel.Debug); }

            // 2. Secondary Fallback: NWS Raw Text Servers
            try
            {
                string rawText = await client.GetStringAsync($"https://tgftp.nws.noaa.gov/data/observations/metar/stations/{icao}.TXT");
                
                string[] lines = rawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string rawMetar = lines.Length > 1 ? string.Join(" ", lines.Skip(1)) : lines.FirstOrDefault() ?? "";
                
                int pDir = 0, pSpd = 0; double pTemp = 0, pDew = 0, pAlt = 1013.25;

                var windMatch = System.Text.RegularExpressions.Regex.Match(rawMetar, @"\b(\d{3}|VRB)(\d{2,3})(G\d{2,3})?KT\b");
                if (windMatch.Success) { int.TryParse(windMatch.Groups[1].Value, out pDir); int.TryParse(windMatch.Groups[2].Value, out pSpd); }

                var tempMatch = System.Text.RegularExpressions.Regex.Match(rawMetar, @"\b(M?\d{2})/(M?\d{2})\b");
                if (tempMatch.Success) { pTemp = int.Parse(tempMatch.Groups[1].Value.Replace("M", "-")); pDew = int.Parse(tempMatch.Groups[2].Value.Replace("M", "-")); }

                var altMatch = System.Text.RegularExpressions.Regex.Match(rawMetar, @"\b([AQ])(\d{4})\b");
                if (altMatch.Success) { double aVal = double.Parse(altMatch.Groups[2].Value); pAlt = altMatch.Groups[1].Value == "A" ? aVal / 100.0 : aVal; }

                return (rawMetar, pDir, pSpd, pTemp, pDew, pAlt);
            }
            catch
            {
                LogEngineEvent($"[DISPATCH] Both NOAA and NWS fallback failed for {icao}. Skipping station.", LogLevel.Debug);
                return ("", 0, 0, 0, 0, 1013.25); // Return safe empty data to prevent crashes
            }
        }
        
        private string GetFlightCategory(string metar)
        {
            if (string.IsNullOrEmpty(metar)) return "VFR";
            
            bool isLifr = false, isIfr = false, isMvfr = false;
            
            // Visibility parsing (SM or Meters)
            var visMatchSM = System.Text.RegularExpressions.Regex.Match(metar, @"\b(\d+)(/\d+)?SM\b");
            var visMatchM = System.Text.RegularExpressions.Regex.Match(metar, @"(?<=\s|^)(\d{4})(?=\s|NDV|$)");
            
            double visSM = 10;
            if (visMatchSM.Success) {
                double.TryParse(visMatchSM.Groups[1].Value, out visSM);
            } else if (visMatchM.Success) {
                if (double.TryParse(visMatchM.Groups[1].Value, out double visM))
                    visSM = visM >= 9999 ? 10 : visM / 1609.34;
            }

            // Ceiling parsing (Looking for Broken, Overcast, or Vertical Visibility)
            var cloudMatches = System.Text.RegularExpressions.Regex.Matches(metar, @"(BKN|OVC|VV)(\d{3})");
            int lowestCeiling = 999;
            foreach (System.Text.RegularExpressions.Match m in cloudMatches)
            {
                if (int.TryParse(m.Groups[2].Value, out int cld))
                    if (cld < lowestCeiling) lowestCeiling = cld;
            }

            // Standard Aviation Minimums
            if (visSM < 1 || lowestCeiling < 5) isLifr = true;
            else if (visSM < 3 || lowestCeiling < 10) isIfr = true;
            else if (visSM <= 5 || lowestCeiling <= 30) isMvfr = true;

            if (isLifr) return "LIFR";
            if (isIfr) return "IFR";
            if (isMvfr) return "MVFR";
            return "VFR";
        }

        private async System.Threading.Tasks.Task PushLiveAirportsToMapAsync(SimBriefData ofp)
        {
            if (fullMapBrowser == null || fullMapBrowser.CoreWebView2 == null) return;
            
            // 1. NO-OFP FALLBACK PROMPT
            if (ofp == null || ofp.RouteWaypoints == null || ofp.RouteWaypoints.Count < 2)
            {
                string jsPrompt = "airportsLayer.clearLayers(); routeLayer.clearLayers(); " +
                                  "L.popup().setLatLng(map.getCenter()).setContent(\"<div class='pop-title'>NO OFP LOADED</div><div class='pop-row'>Please import a SimBrief Flight Plan first from the Flight Plan page to enable live airport data without overloading the API servers.</div>\").openOn(map);";
                _ = fullMapBrowser.CoreWebView2.ExecuteScriptAsync(jsPrompt);
                return;
            }

            // 2. CLEAR DUMMY DATA & DRAW ROUTE
            string jsCommand = "airportsLayer.clearLayers();\nrouteLayer.clearLayers();\n";
            var coordsList = new System.Collections.Generic.List<string>();
            foreach (var wp in ofp.RouteWaypoints)
            {
                coordsList.Add($"[{wp.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {wp.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}]");
            }
            string jsCoords = string.Join(",", coordsList);
            jsCommand += $@"
                var routeCoords = [{jsCoords}];
                var routeLine = L.polyline(routeCoords, {{ color: '#FF00FF', weight: 3, opacity: 0.9, lineJoin: 'round' }}).addTo(routeLayer);
                routeCoords.forEach(function(coord) {{ L.circleMarker(coord, {{ radius: 2, color: '#FFFFFF', weight: 1, fillOpacity: 1 }}).addTo(routeLayer); }});
                map.fitBounds(routeLine.getBounds(), {{ padding: [30, 30] }});
            ";
            
            // 3. EXTRACT CRITICAL OFP STATIONS SAFELY
            var stationsToPlot = new System.Collections.Generic.Dictionary<string, (double lat, double lon)>();
            if (!string.IsNullOrEmpty(ofp.DepartureICAO)) stationsToPlot[ofp.DepartureICAO] = (ofp.RouteWaypoints[0].Latitude, ofp.RouteWaypoints[0].Longitude);
            if (!string.IsNullOrEmpty(ofp.ArrivalICAO)) stationsToPlot[ofp.ArrivalICAO] = (ofp.RouteWaypoints[ofp.RouteWaypoints.Count - 1].Latitude, ofp.RouteWaypoints[ofp.RouteWaypoints.Count - 1].Longitude);
            
            if (ofp.AlternateWaypoints != null && ofp.AlternateWaypoints.Count > 0)
            {
                var altn = ofp.AlternateWaypoints[ofp.AlternateWaypoints.Count - 1];
                if (!string.IsNullOrEmpty(altn.Ident)) stationsToPlot[altn.Ident] = (altn.Latitude, altn.Longitude);
            }

            // THE LAG FIX: Background thread the heavy 43,000 airport sorting!
            foreach (var wp in ofp.RouteWaypoints)
            {
                var nearestList = await System.Threading.Tasks.Task.Run(() => locator.GetNearestStations(wp.Latitude, wp.Longitude, 1));
                if (nearestList != null && nearestList.Count > 0)
                {
                    var stn = nearestList[0].Station;
                    if (stn.ICAO != null && _activeRouteStations.Contains(stn.ICAO))
                    {
                        stationsToPlot[stn.ICAO] = (stn.Latitude, stn.Longitude);
                    }
                }
            }

            // 4. CONTROLLED ASYNC FETCHING
            var fetchTasks = new System.Collections.Generic.List<System.Threading.Tasks.Task<(string icao, double lat, double lon, string raw, int wdir, int wspd, double temp, double dew, double alt)>>();
            using var semaphore = new System.Threading.SemaphoreSlim(5);
            foreach (var kvp in stationsToPlot)
            {
                string icao = kvp.Key;
                double lat = kvp.Value.lat;
                double lon = kvp.Value.lon;

                fetchTasks.Add(System.Threading.Tasks.Task.Run(async () => 
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var wx = await FetchMetarDataAsync(icao);
                        return (icao, lat, lon, wx.raw, wx.wdir, wx.wspd, wx.temp, wx.dew, wx.alt);
                    }
                    finally { semaphore.Release(); }
                }));
            }

            var completedStations = await System.Threading.Tasks.Task.WhenAll(fetchTasks);
            
            // 5. BUILD UI
            int stationsPlotted = 0;
            foreach (var stn in completedStations)
            {
                if (string.IsNullOrEmpty(stn.raw)) continue;
                string cat = GetFlightCategory(stn.raw);
                string catColor = cat == "VFR" ? "#10B981" : cat == "MVFR" ? "#F59E0B" : cat == "IFR" ? "#EF4444" : "#8B5CF6";
                string roleTag = "";
                if (stn.icao == ofp.DepartureICAO) roleTag = " - DEPARTURE";
                else if (stn.icao == ofp.ArrivalICAO) roleTag = " - ARRIVAL";
                else if (ofp.AlternateWaypoints != null && ofp.AlternateWaypoints.Any(a => a.Ident == stn.icao)) roleTag = " - ALTERNATE";
                
                string popupHtml = $@"
                    <div class='pop-title'>{stn.icao}{roleTag}</div>
                    <div class='pop-row'><span>Cat:</span> <b style='color:{catColor}'>{cat}</b></div>
                    <div class='pop-row'><span>METAR:</span> {stn.raw}</div>
                    <div class='pop-row'><span>Wind:</span> {stn.wdir:D3}° @ {stn.wspd} kts</div>
                    <div class='pop-row'><span>Temp:</span> {stn.temp}°C (Dew: {stn.dew}°C)</div>
                    <div class='pop-row'><span>Altimeter:</span> {stn.alt:F2}</div>
                    <button class='pop-btn primary' onclick='window.chrome.webview.postMessage(""INJECT_METAR|{stn.icao}|{stn.lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{stn.lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}"");'>Inject WX</button> 
                    <button class='pop-btn' style='float:right;'>Refresh</button>
                ";
                jsCommand += $@"
                    var icon_{stn.icao} = L.divIcon({{ className: '', html: ""<div style='background:{catColor}; width:12px; height:12px; border-radius:50%; border:2px solid #000; box-shadow:0 0 4px #000;'></div>"", iconSize:[12,12], iconAnchor:[6,6] }});
                    var marker_{stn.icao} = L.marker([{stn.lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {stn.lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}], {{icon: icon_{stn.icao}}}).addTo(airportsLayer);
                    marker_{stn.icao}.bindPopup(`{popupHtml}`, {{maxWidth: 300}});
                ";
                stationsPlotted++;
            }

            string gridJson = await FetchWindsGridAsync(ofp.RouteWaypoints);
            if (!string.IsNullOrEmpty(gridJson))
            {
                int currentHour = DateTime.UtcNow.Hour;
                jsCommand += $@"
                    var windApiData = {gridJson};
                    var currentUtcHour = {currentHour};
                    if(typeof buildRealWindsGrid === 'function') {{
                        buildRealWindsGrid(windApiData, currentUtcHour);
                    }}
                ";
            }

            _ = fullMapBrowser.CoreWebView2.ExecuteScriptAsync(jsCommand);
            LogEngineEvent($"[MAP] Drew flight route and pushed {stationsPlotted} live OFP airports to the map layer.", LogLevel.Normal);
        }
        private async Task GenerateFlightBriefingAsync(string depIcao, string arrIcao, System.Collections.Generic.List<Waypoint> waypoints)
        {
            LogEngineEvent($"[DISPATCH] Generating comprehensive weather briefing for {depIcao} -> {arrIcao}", LogLevel.Debug);
            
            listSigWx.Items.Clear(); 
            int totalRiskScore = 0;

            // --- DEPARTURE WEATHER ---
            try
            {
                var depWx = await FetchMetarDataAsync(depIcao);
                
                lblFpDepName.Text = depIcao;
                lblFpDepWind.Text = $"{depWx.wdir:D3} @ {depWx.wspd} kt";
                lblFpDepCond.Text = depWx.raw.Contains("TS") ? "THUNDERSTORMS" : (depWx.raw.Contains("RA") ? "RAIN" : "NORMAL");
                lblBriefDepWx.Text = lblFpDepCond.Text;
                lblFpDepTemp.Text = $"{depWx.temp}°C / {depWx.dew}°C";
                lblFpDepAltim.Text = depWx.alt > 200 ? $"{(int)Math.Round(depWx.alt)} hPa" : $"{(int)Math.Round(depWx.alt * 33.8639)} hPa";
                lblFpDepRaw.Text = depWx.raw;

                // Vis and Turb
                int vis = 10;
                var visMatch = System.Text.RegularExpressions.Regex.Match(depWx.raw, @"\b(\d+)SM\b|\b(\d{4})\b");
                if (visMatch.Success) vis = visMatch.Groups[1].Success ? int.Parse(visMatch.Groups[1].Value) : (int.Parse(visMatch.Groups[2].Value) >= 9999 ? 10 : (int)Math.Round(int.Parse(visMatch.Groups[2].Value) / 1609.34));
                lblFpDepVis.Text = $"{vis} SM";

                if (depWx.raw.Contains("TS") || depWx.raw.Contains("CB")) lblFpTurbDep.Text = "MODERATE / SEVERE (CONVECTIVE)";
                else if (depWx.wspd >= 20 || depWx.raw.Contains("G")) lblFpTurbDep.Text = "MODERATE (SURFACE SHEAR)";
                else lblFpTurbDep.Text = "LIGHT / SMOOTH";

                if (depWx.wspd > 25 || depWx.raw.Contains("TS")) 
                {
                    totalRiskScore += 40;
                    listSigWx.Items.Add(new System.Windows.Controls.ListBoxItem { Content = $"⚠ Departure ({depIcao}): Hazardous wind/convective activity detected." });
                }
            }
            catch { lblFpDepName.Text = depIcao; lblFpDepRaw.Text = "DATA UNAVAILABLE (NOAA/NWS UNREACHABLE)"; }

            // --- ARRIVAL WEATHER ---
            try
            {
                var arrWx = await FetchMetarDataAsync(arrIcao);
                
                lblFpArrName.Text = arrIcao;
                lblFpArrWind.Text = $"{arrWx.wdir:D3} @ {arrWx.wspd} kt";
                lblFpArrCond.Text = arrWx.raw.Contains("TS") ? "THUNDERSTORMS" : (arrWx.raw.Contains("RA") ? "RAIN" : "NORMAL");
                lblBriefArrWx.Text = lblFpArrCond.Text;
                lblFpArrTemp.Text = $"{arrWx.temp}°C / {arrWx.dew}°C";
                lblFpArrAltim.Text = arrWx.alt > 200 ? $"{(int)Math.Round(arrWx.alt)} hPa" : $"{(int)Math.Round(arrWx.alt * 33.8639)} hPa";
                lblFpArrRaw.Text = arrWx.raw;

                // Vis and Turb
                int vis = 10;
                var visMatch = System.Text.RegularExpressions.Regex.Match(arrWx.raw, @"\b(\d+)SM\b|\b(\d{4})\b");
                if (visMatch.Success) vis = visMatch.Groups[1].Success ? int.Parse(visMatch.Groups[1].Value) : (int.Parse(visMatch.Groups[2].Value) >= 9999 ? 10 : (int)Math.Round(int.Parse(visMatch.Groups[2].Value) / 1609.34));
                lblFpArrVis.Text = $"{vis} SM";

                if (arrWx.raw.Contains("TS") || arrWx.raw.Contains("CB")) lblFpTurbArr.Text = "MODERATE / SEVERE (CONVECTIVE)";
                else if (arrWx.wspd >= 20 || arrWx.raw.Contains("G")) lblFpTurbArr.Text = "MODERATE (SURFACE SHEAR)";
                else lblFpTurbArr.Text = "LIGHT / SMOOTH";

                if (arrWx.wspd > 25 || arrWx.raw.Contains("TS"))
                {
                    totalRiskScore += 40;
                    listSigWx.Items.Add(new System.Windows.Controls.ListBoxItem { Content = $"⚠ Arrival ({arrIcao}): Hazardous weather detected." });
                }
            }
            catch { lblFpArrName.Text = arrIcao; lblFpArrRaw.Text = "DATA UNAVAILABLE (NOAA/NWS UNREACHABLE)"; }

            // --- PHASE 3: CONTINUOUS ROUTE SAMPLING & MAPPING ---
            var dispatchBriefing = new Models.SkyNexusBriefingModel { DepartureIcao = depIcao, ArrivalIcao = arrIcao };
            int highestCruiseSpd = 0;

            // SAFELY EXTRACT DICTIONARY VARIABLES
            int spd36k = 0, dir36k = 0, spd24k = 0, dir24k = 0, spd10k = 0, dir10k = 0;

            if (waypoints != null && waypoints.Count > 0)
            {
                var midPoint = waypoints[waypoints.Count / 2];
                await FetchWindsAloftAsync(midPoint.Latitude, midPoint.Longitude);

                // Populate extracted variables after await
                spd36k = _windsCache.Layers.ContainsKey(360) ? _windsCache.Layers[360].Speed : 0;
                dir36k = _windsCache.Layers.ContainsKey(360) ? _windsCache.Layers[360].Direction : 0;
                spd24k = _windsCache.Layers.ContainsKey(240) ? _windsCache.Layers[240].Speed : 0;
                dir24k = _windsCache.Layers.ContainsKey(240) ? _windsCache.Layers[240].Direction : 0;
                spd10k = _windsCache.Layers.ContainsKey(100) ? _windsCache.Layers[100].Speed : 0;
                dir10k = _windsCache.Layers.ContainsKey(100) ? _windsCache.Layers[100].Direction : 0;

                double accumulatedDistance = 0;
                System.Windows.Point previousPoint = new System.Windows.Point(waypoints[0].Latitude, waypoints[0].Longitude);

                for (int i = 0; i < waypoints.Count; i++)
                {
                    var wp = waypoints[i];
                    if (i > 0) 
                    {
                        accumulatedDistance += CalculateDistanceNM(previousPoint.X, previousPoint.Y, wp.Latitude, wp.Longitude);
                        previousPoint = new System.Windows.Point(wp.Latitude, wp.Longitude);
                    }

                    int jetSpd = spd36k;
                    if (jetSpd > highestCruiseSpd) highestCruiseSpd = jetSpd; 

                    int shear = Math.Abs(spd36k - spd24k);
                    string turbLevel = "Smooth";
                    System.Windows.Media.Brush segmentColor = System.Windows.Media.Brushes.LimeGreen;

                    if (shear > 40 || jetSpd > 100) { turbLevel = "Severe"; segmentColor = System.Windows.Media.Brushes.Red; }
                    else if (shear > 25 || jetSpd > 75) { turbLevel = "Moderate"; segmentColor = System.Windows.Media.Brushes.DarkOrange; }
                    else if (shear > 15) { turbLevel = "Light"; segmentColor = System.Windows.Media.Brushes.Yellow; }

                    if (i == 0 || i == waypoints.Count - 1 || accumulatedDistance % 100 < 20)
                    {
                        string phase = i == 0 ? "Departure" : (i == waypoints.Count - 1 ? "Arrival" : "Enroute");
                        dispatchBriefing.RouteTimeline.Add(new Models.RouteWeatherEvent
                        {
                            Phase = phase,
                            LocationIdent = wp.Ident ?? "WPT",
                            TurbulenceLevel = turbLevel,
                            DistanceFromDep = Math.Round(accumulatedDistance)
                        });
                    }
                }
            }

            lblFpWind36k.Text = $"{dir36k:D3} @ {spd36k} kt";
            lblFpWind24k.Text = $"{dir24k:D3} @ {spd24k} kt";
            lblFpWind10k.Text = $"{dir10k:D3} @ {spd10k} kt";
            
            if (highestCruiseSpd > 80)
            {
                lblFpTurbEnroute.Text = "MODERATE CAT (JET STREAM)";
                lblBriefTurb.Text = "MODERATE";
                listSigWx.Items.Add(new System.Windows.Controls.ListBoxItem { Content = $"⚠ Enroute: Strong Jet Stream detected ({highestCruiseSpd} kt). Expect CAT." });
                totalRiskScore += 20;
            }
            else { lblFpTurbEnroute.Text = "LIGHT / SMOOTH"; lblBriefTurb.Text = "LIGHT"; }

            // --- FINAL RISK SCORE ---
            if (totalRiskScore >= 60) { lblRiskScore.Text = "HIGH"; lblRiskScore.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E0443E")); }
            else if (totalRiskScore >= 20) { lblRiskScore.Text = "MODERATE"; lblRiskScore.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D87A1E")); }
            else { lblRiskScore.Text = "LOW"; lblRiskScore.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981")); listSigWx.Items.Add(new System.Windows.Controls.ListBoxItem { Content = "✔ No significant weather hazards detected for this route." }); }
        }

        private string AnalyzeWeatherTrend(string metar, string taf)
        {
            if (string.IsNullOrEmpty(metar) || string.IsNullOrEmpty(taf)) return "Stable";

            bool hasMetarRain = metar.Contains("RA") || metar.Contains("TS");
            bool hasTafRain = taf.Contains("RA") || taf.Contains("TS");
            
            bool metarLowVis = System.Text.RegularExpressions.Regex.IsMatch(metar, @"\b([1-4])SM\b") || System.Text.RegularExpressions.Regex.IsMatch(metar, @"\b([0-4]\d{3})\b");
            bool tafLowVis = System.Text.RegularExpressions.Regex.IsMatch(taf, @"\b([1-4])SM\b") || System.Text.RegularExpressions.Regex.IsMatch(taf, @"\b([0-4]\d{3})\b");

            if (!hasMetarRain && hasTafRain) return "Deteriorating: Rain Expected";
            if (hasMetarRain && !hasTafRain) return "Improving: Rain Clearing";
            if (!metarLowVis && tafLowVis) return "Deteriorating: Visibility Dropping";
            if (metarLowVis && !tafLowVis) return "Improving: Visibility Increasing";

            return "Stable";
        }

        private string GenerateMapHtml()
        {
            return @"<!DOCTYPE html>
            <html>
            <head>
                <link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css' />
                <script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
                <style>
                    body { padding: 0; margin: 0; background-color: #111; color: #E0E0E0; font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; overflow: hidden; }
                    #map { height: 100vh; width: 100vw; z-index: 1; }
                    .leaflet-control-attribution { display: none !important; }
                    
                    /* Floating UI Base Styles */
                    .floating-panel { position: absolute; background: rgba(30, 30, 30, 0.9); border: 1px solid #444; border-radius: 8px; z-index: 1000; box-shadow: 0 4px 6px rgba(0,0,0,0.5); backdrop-filter: blur(5px); }
                    
                    /* Left Toolbar */
                    #toolbar { top: 20px; left: 20px; width: 45px; display: flex; flex-direction: column; padding: 5px 0; }
                    .tool-btn { width: 100%; height: 40px; background: transparent; border: none; color: #aaa; cursor: pointer; border-left: 3px solid transparent; transition: 0.2s; font-size: 18px; }
                    .tool-btn:hover { color: #fff; background: rgba(255,255,255,0.1); }
                    .tool-btn.active { color: #D87A1E; border-left: 3px solid #D87A1E; background: rgba(216, 122, 30, 0.1); }
                    
                    /* Right Layers Panel */
                    #layers-panel { top: 20px; right: 20px; width: 220px; padding: 10px; }
                    .layer-header { font-size: 12px; font-weight: bold; color: #D87A1E; margin-bottom: 8px; text-transform: uppercase; letter-spacing: 1px; border-bottom: 1px solid #444; padding-bottom: 4px; }
                    .layer-toggle { display: flex; align-items: center; margin-bottom: 6px; font-size: 13px; cursor: pointer; }
                    .layer-toggle input { margin-right: 8px; cursor: pointer; accent-color: #D87A1E; }
                    
                    /* Winds Aloft Altitude Slider */
                    #winds-selector { top: 20px; right: 260px; display: flex; gap: 5px; background: rgba(30,30,30,0.9); padding: 5px; border-radius: 6px; border: 1px solid #444; z-index: 1000; position: absolute; }
                    .alt-btn { background: #333; color: #aaa; border: none; padding: 4px 10px; border-radius: 4px; font-size: 11px; cursor: pointer; }
                    .alt-btn.active { background: #D87A1E; color: #fff; font-weight: bold; }
                    
                    /* Bottom Status Bar */
                    #status-bar { bottom: 0; left: 0; width: 100%; height: 28px; background: rgba(20, 20, 20, 0.95); border-top: 1px solid #333; z-index: 1000; position: absolute; display: flex; align-items: center; padding: 0 15px; font-size: 12px; color: #aaa; gap: 20px; }
                    .status-item span { color: #fff; font-weight: bold; margin-left: 5px; }
                    
                    /* Aircraft Icon */
                    .aircraft-icon { transition: all 1s linear; }
                </style>
            </head>
            <body>
                <div id='map'></div>
                
                <div id='toolbar' class='floating-panel'>
                    <button class='tool-btn active' title='Standard Map'>🗺️</button>
                    <button class='tool-btn' title='Radar'>📡</button>
                    <button class='tool-btn' title='Clouds'>☁️</button>
                    <button class='tool-btn' title='Winds'>💨</button>
                </div>
                
                <div id='winds-selector'>
                    <button class='alt-btn active'>SFC</button>
                    <button class='alt-btn'>FL100</button>
                    <button class='alt-btn'>FL180</button>
                    <button class='alt-btn'>FL240</button>
                    <button class='alt-btn'>FL300</button>
                    <button class='alt-btn'>FL360</button>
                    <button class='alt-btn'>FL450</button>
                </div>
                
                <div id='layers-panel' class='floating-panel'>
                    <div class='layer-header'>Aviation</div>
                    <label class='layer-toggle'><input type='checkbox' id='chkAircraft' checked onchange='toggleLayer(this, aircraftLayer)'> Aircraft Position</label>
                    <label class='layer-toggle'><input type='checkbox' id='chkRoute' checked onchange='toggleLayer(this, routeLayer)'> Flight Route</label>
                    <label class='layer-toggle'><input type='checkbox' id='chkAirports' onchange='toggleLayer(this, airportsLayer)'> Airports (METAR)</label>
                    
                    <div class='layer-header' style='margin-top:10px;'>Meteorology</div>
                    <label class='layer-toggle'><input type='checkbox' id='chkRadar' onchange='toggleLayer(this, radarLayer)'> Weather Radar</label>
                    <label class='layer-toggle'><input type='checkbox' id='chkClouds' onchange='toggleLayer(this, cloudsLayer)'> Cloud Coverage</label>
                    <label class='layer-toggle'><input type='checkbox' id='chkWinds' onchange='toggleLayer(this, windsLayer)'> Winds Aloft</label>
                    <label class='layer-toggle'><input type='checkbox' id='chkTurb' onchange='toggleLayer(this, turbulenceLayer)'> Turbulence</label>
                    <label class='layer-toggle'><input type='checkbox' id='chkLightning' onchange='toggleLayer(this, lightningLayer)'> Lightning</label>
                    
                    <div class='layer-header' style='margin-top:10px;'>Advisories</div>
                    <label class='layer-toggle'><input type='checkbox' id='chkSigmet' onchange='toggleLayer(this, sigmetLayer)'> SIGMETs</label>
                    <label class='layer-toggle'><input type='checkbox' id='chkAirmet' onchange='toggleLayer(this, airmetLayer)'> AIRMETs</label>
                    <label class='layer-toggle'><input type='checkbox' id='chkTaf' onchange='toggleLayer(this, tafLayer)'> TAF Reports</label>
                </div>

                <div id='status-bar'>
                    <div class='status-item'>Cursor: <span id='statCoords'>-- / --</span></div>
                    <div class='status-item'>Elev: <span id='statElev'>-- ft</span></div>
                    <div class='status-item'>Wind: <span id='statWind'>-- kt</span></div>
                    <div class='status-item'>Temp: <span id='statTemp'>-- °C</span></div>
                </div>

                <script>
                    var map = L.map('map', { zoomControl: false }).setView([20, 0], 2);
                    
                    // BASE MAP LAYER
                    var baseMap = L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', {
                        subdomains: 'abcd', maxZoom: 20
                    }).addTo(map);

                    // INITIALIZE INDEPENDENT LAYER GROUPS
                    var aircraftLayer = L.layerGroup().addTo(map);
                    var routeLayer = L.layerGroup().addTo(map);
                    var airportsLayer = L.layerGroup();
                    var radarLayer = L.layerGroup();
                    var cloudsLayer = L.layerGroup();
                    var windsLayer = L.layerGroup();
                    var turbulenceLayer = L.layerGroup();
                    var lightningLayer = L.layerGroup();
                    var sigmetLayer = L.layerGroup();
                    var airmetLayer = L.layerGroup();
                    var tafLayer = L.layerGroup();

                    // Aircraft Marker Variable
                    var planeMarker = null;

                    // Expose toggle logic
                    function toggleLayer(checkbox, layer) {
                        if(checkbox.checked) map.addLayer(layer);
                        else map.removeLayer(layer);
                    }

                    // Expose Aircraft Update Logic to C#
                    // Now handles lat, lon, heading, groundspeed, and altitude!
                    function updateAircraft(lat, lon, hdg, gs, alt, doPan) {
                        if(!planeMarker) {
                            var icon = L.divIcon({
                                className: 'aircraft-icon',
                                html: ""<div style='background-color:#D87A1E; width:14px; height:14px; border-radius:50%; border:2px solid white;'></div>"",
                                iconSize: [14, 14], iconAnchor: [7, 7]
                            });
                            planeMarker = L.marker([lat, lon], {icon: icon}).addTo(aircraftLayer);
                        } else {
                            planeMarker.setLatLng([lat, lon]);
                        }
                        
                        if(doPan) map.panTo([lat, lon]);
                    }

                    // Live Status Bar Mouse Tracker
                    map.on('mousemove', function(e) {
                        document.getElementById('statCoords').innerText = e.latlng.lat.toFixed(4) + '° / ' + e.latlng.lng.toFixed(4) + '°';
                        // Placeholders for future APIs
                        document.getElementById('statElev').innerText = '120 ft';
                        document.getElementById('statWind').innerText = '270/15';
                        document.getElementById('statTemp').innerText = '15 °C';
                    });
                </script>
            </body>
            </html>";
        }

        private async Task DrawFlightPlanMapAsync(System.Collections.Generic.List<Waypoint> waypoints)
        {
            if (fpMapBrowser.CoreWebView2 == null)
            {
                await fpMapBrowser.EnsureCoreWebView2Async();
            }
            
            // Force initialization NOW, while the tab is actually visible on the screen!
            await fpMapBrowser.EnsureCoreWebView2Async(null);

            if (waypoints == null || waypoints.Count == 0) return;

            // 1. Convert C# Waypoints into a JavaScript coordinate array
            var coordsList = new System.Collections.Generic.List<string>();
            foreach (var wp in waypoints)
            {
                coordsList.Add($"[{wp.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {wp.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}]");
            }
            string jsCoords = string.Join(",", coordsList);

            // 2. Build the HTML/JS for the Leaflet Map
            string html = $@"<!DOCTYPE html>
            <html>
            <head>
                <link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css' />
                <script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
                <style>
                    body {{ padding: 0; margin: 0; background-color: #111111; }}
                    #map {{ height: 100vh; width: 100vw; }}
                    .leaflet-control-attribution {{ display: none !important; }}
                </style>
            </head>
            <body>
                <div id='map'></div>
                <script>
                    var map = L.map('map', {{ zoomControl: false }}).setView([0,0], 2);
                    
                    L.tileLayer('https://{{s}}.basemaps.cartocdn.com/dark_all/{{z}}/{{x}}/{{y}}{{r}}.png', {{
                        subdomains: 'abcd',
                        maxZoom: 20
                    }}).addTo(map);

                    var routeCoords = [{jsCoords}];
                    
                    // Draw the Boeing 737 ND Magenta Route Line
                    var routeLine = L.polyline(routeCoords, {{ 
                        color: '#FF00FF', 
                        weight: 3, 
                        opacity: 0.9,
                        lineJoin: 'round'
                    }}).addTo(map);

                    // Draw tiny white dots for every waypoint/fix
                    routeCoords.forEach(function(coord) {{
                        L.circleMarker(coord, {{ radius: 2, color: '#FFFFFF', weight: 1, fillOpacity: 1 }}).addTo(map);
                    }});

                    // Automatically pan and zoom the map to fit the entire route beautifully
                    map.fitBounds(routeLine.getBounds(), {{ padding: [30, 30] }});
                </script>
            </body>
            </html>";

            fpMapBrowser.NavigateToString(html);
        }
        #endregion

        // ==============================================================================================
        // --- EXPERIMENTAL WEATHER GRID PROOF-OF-CONCEPT ---
        // Completely isolated. Memory-only stations. Does not modify global injection or database.
        // ==============================================================================================
        private async void BtnRunGridPoC_Click(object sender, RoutedEventArgs e)
        {
            if (simconnect == null)
            {
                LogEngineEvent("[PoC] SimConnect not connected. Cannot run grid experiment.", LogLevel.Normal);
                return;
            }

            LogEngineEvent("[PoC] === STARTING WEATHER GRID EXPERIMENT ===", LogLevel.Normal);
            
            // VOML is roughly 12.96N, 74.89E. 
            // We start the grid offshore at 13.5N, 73.0E and generate South-East.
            double startLat = 13.5;
            double startLon = 73.0;
            
            // 0.7 degrees of Lat/Lon is roughly 42 Nautical Miles, perfect for the transitions.
            double spacing = 0.7; 

            // 16 distinct weather profiles to force obvious interpolation testing
            string[] testMetars = new string[]
            {
                "240600Z 09010KT 10SM CLR 30/20 A2992",                   // P001: Clear, East Wind
                "240600Z 18018KT 10SM BKN030 28/22 A2980",                // P002: Broken, South Wind
                "240600Z 27030KT 2SM TSRA BKN020CB 25/24 A2960",          // P003: Thunderstorm, West Wind
                "240600Z 34008KT 1SM FG OVC002 22/21 A2990",              // P004: Fog, NW Wind
                
                "240600Z 04515KT 5SM HZ SCT040 32/18 A3000",              // P005: Haze/Scattered
                "240600Z 12025KT 3SM +RA OVC015 24/23 A2975",             // P006: Heavy Rain
                "240600Z 22005KT 10SM FEW250 33/20 A2995",                // P007: High Cirrus
                "240600Z 31012KT 4SM SHRA SCT020TCU BKN040 26/24 A2985",  // P008: Showers

                "240600Z 36020KT 10SM SCT025 29/19 A3005",                // P009: Scattered
                "240600Z 08010KT 6SM BR OVC010 23/22 A2990",              // P010: Mist/Overcast
                "240600Z 16035KT 1SM +TSRA OVC010CB 21/20 A2950",         // P011: Severe Storm
                "240600Z 25015KT 10SM CLR 34/15 A2980",                   // P012: Clear

                "240600Z 02008KT 10SM FEW030 31/22 A2992",                // P013: Few
                "240600Z 10022KT 2SM RA BKN015 OVC030 25/24 A2970",       // P014: Rain/Broken
                "240600Z 19014KT 10SM SCT030 BKN080 28/23 A2988",         // P015: Layered Clouds
                "240600Z 28005KT 0SM FG VV001 20/20 A2995"                // P016: Dense Fog
            };

            // Prepare Javascript for Map injection (creates a dedicated cleanable layer)
            string jsCommand = "if(typeof pocLayer !== 'undefined') { pocLayer.clearLayers(); } else { window.pocLayer = L.layerGroup().addTo(map); }\n";

            int index = 0;
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    index++;
                    string icao = $"P{index:D3}"; // Formats as P001, P002...
                    double lat = startLat - (row * spacing);
                    double lon = startLon + (col * spacing);
                    float elevation = 0f; // Sea level over the ocean

                    LogEngineEvent($"[PoC] Creating Station {icao} at Lat: {lat:F4}, Lon: {lon:F4}, Elev: {elevation}ft", LogLevel.Normal);

                    // 1. Create the temporary station dynamically using C# Reflection
                    try 
                    {
                        var method = simconnect.GetType().GetMethods().FirstOrDefault(m => m.Name == "WeatherCreateStation");
                        if (method != null)
                        {
                            var pInfos = method.GetParameters();
                            object[] args = new object[pInfos.Length];
                            int floatIndex = 0;
                            int stringIndex = 0;
                            
                            for (int i = 0; i < pInfos.Length; i++)
                            {
                                var pType = pInfos[i].ParameterType;
                                
                                if (pType == typeof(System.Enum)) 
                                    args[i] = System.DayOfWeek.Monday; // Dummy valid enum to satisfy RequestID
                                else if (pType == typeof(uint) || pType == typeof(int)) 
                                    args[i] = Convert.ChangeType(0, pType);
                                else if (pType == typeof(string)) 
                                {
                                    args[i] = (stringIndex == 0) ? icao : $"PoC_Grid_{icao}";
                                    stringIndex++;
                                }
                                else if (pType == typeof(float)) 
                                {
                                    if (floatIndex == 0) args[i] = (float)lat;
                                    else if (floatIndex == 1) args[i] = (float)lon;
                                    else if (floatIndex == 2) args[i] = (float)elevation;
                                    else args[i] = 0f;
                                    floatIndex++;
                                }
                                else if (pType == typeof(double)) 
                                {
                                    if (floatIndex == 0) args[i] = lat;
                                    else if (floatIndex == 1) args[i] = lon;
                                    else if (floatIndex == 2) args[i] = (double)elevation;
                                    else args[i] = 0.0;
                                    floatIndex++;
                                }
                                else 
                                {
                                    args[i] = pType.IsValueType ? Activator.CreateInstance(pType) : null;
                                }
                            }
                            method.Invoke(simconnect, args);
                        }
                        else
                        {
                            LogEngineEvent($"[PoC ERROR] Method WeatherCreateStation not found in your DLL.", LogLevel.Debug);
                        }
                    } 
                    catch (Exception ex) 
                    {
                        LogEngineEvent($"[PoC ERROR] Failed to create station {icao}: {ex.Message}", LogLevel.Debug);
                    }

                    // 2. Inject Weather immediately using standard pipeline
                    string baseMetar = testMetars[index - 1];
                    string fullMetar = $"{icao} {baseMetar}";
                    
                    LogEngineEvent($"[PoC] Injecting Weather {icao} -> {fullMetar}", LogLevel.Normal);
                    
                    try {
                        // 0 second blend time is vital here to establish the static geographic cell immediately
                        simconnect.WeatherSetObservation(0, fullMetar);
                    }
                    catch (Exception ex) {
                        LogEngineEvent($"[PoC ERROR] Failed to inject weather for {icao}: {ex.Message}", LogLevel.Debug);
                    }

                    // 3. Draw bounding square and center dot on the map
                    string popupHtml = $"<div class='pop-title'>{icao} - GRID TEST</div><div class='pop-row'>{baseMetar}</div>";
                    jsCommand += $@"
                        var bounds_{icao} = [[{lat - (spacing/2)}, {lon - (spacing/2)}], [{lat + (spacing/2)}, {lon + (spacing/2)}]];
                        L.rectangle(bounds_{icao}, {{color: '#10B981', weight: 1, fillOpacity: 0.05, dashArray: '4,4'}}).addTo(window.pocLayer);
                        
                        var marker_{icao} = L.circleMarker([{lat}, {lon}], {{ radius: 4, color: '#10B981', weight: 2, fillOpacity: 1 }}).addTo(window.pocLayer);
                        marker_{icao}.bindPopup(`{popupHtml}`);
                        marker_{icao}.bindTooltip('{icao}', {{permanent: true, direction: 'right', className: 'poc-label', offset: [5,0]}});
                    ";

                    await Task.Delay(150); // Prevents flooding the COM pipeline
                }
            }

            // 4. Push Map Updates to UI
            if (fullMapBrowser != null && fullMapBrowser.CoreWebView2 != null)
            {
                // Injects a CSS class to make the station text labels clean and transparent on the map
                string styleInjection = "if(!document.getElementById('pocStyles')) { var style = document.createElement('style'); style.id='pocStyles'; style.innerHTML = '.poc-label { background: transparent; border: none; color: #10B981; font-weight: bold; box-shadow: none; text-shadow: 1px 1px 2px black; }'; document.head.appendChild(style); }";
                _ = fullMapBrowser.CoreWebView2.ExecuteScriptAsync(styleInjection + jsCommand);
                LogEngineEvent("[PoC] Rendered 16 weather cells onto the Live Tracking map.", LogLevel.Normal);
            }

            LogEngineEvent("[PoC] === WEATHER GRID EXPERIMENT COMPLETE ===", LogLevel.Normal);
        }
    }
    // ==============================================================================================
    // --- SKYGRID: ROUTE-CENTRIC WEATHER CORRIDOR ARCHITECTURE ---
    // ==============================================================================================
    public enum CellState { UNLOADED, REQUESTING, READY, INJECTED, STALE, DISCARDED }
    public enum CellPriority { Priority1, Priority2, Priority3 }

    public class SkyGridCell
    {
        public string CellID { get; set; }
        public double CenterLat { get; set; }
        public double CenterLon { get; set; }
        public CellPriority Priority { get; set; }
        public double DistanceAlongRoute { get; set; }
        public CellState State { get; set; } = CellState.UNLOADED;
    }

    public class SkyGridManager
    {
        public System.Collections.Generic.List<SkyGridCell> Cells { get; private set; } = new System.Collections.Generic.List<SkyGridCell>();
        private Action<string, MainWindow.LogLevel> _logEngineEvent;
        private SimConnect _simconnect;

        public SkyGridManager(Action<string, MainWindow.LogLevel> logger, SimConnect simconnect)
        {
            _logEngineEvent = logger;
            _simconnect = simconnect;
        }

        public void GenerateGridFromRoute(System.Collections.Generic.List<Waypoint> route)
        {
            Cells.Clear();
            if (route == null || route.Count < 2) return;

            _logEngineEvent("[SKYGRID] Architecting route-centric weather corridor...", MainWindow.LogLevel.Normal);

            double accumulatedDistance = 0;
            double cellSpacingNm = 40.0; 
            int cellIndex = 1;

            for (int i = 0; i < route.Count - 1; i++)
            {
                var wp1 = route[i];
                var wp2 = route[i + 1];
                double segmentDist = CalculateDistanceNM(wp1.Latitude, wp1.Longitude, wp2.Latitude, wp2.Longitude);
                double bearing = CalculateBearing(wp1.Latitude, wp1.Longitude, wp2.Latitude, wp2.Longitude);

                for (double d = 0; d < segmentDist; d += cellSpacingNm)
                {
                    var center = PointFromDistanceAndBearing(wp1.Latitude, wp1.Longitude, d, bearing);
                    double cellRouteDist = accumulatedDistance + d;
                    
                    Cells.Add(new SkyGridCell { CellID = $"C{cellIndex:D3}", CenterLat = center.Lat, CenterLon = center.Lon, Priority = CellPriority.Priority1, DistanceAlongRoute = cellRouteDist });
                    
                    var left = PointFromDistanceAndBearing(center.Lat, center.Lon, cellSpacingNm, bearing - 90);
                    var right = PointFromDistanceAndBearing(center.Lat, center.Lon, cellSpacingNm, bearing + 90);

                    Cells.Add(new SkyGridCell { CellID = $"L{cellIndex:D3}", CenterLat = left.Lat, CenterLon = left.Lon, Priority = CellPriority.Priority2, DistanceAlongRoute = cellRouteDist });
                    Cells.Add(new SkyGridCell { CellID = $"R{cellIndex:D3}", CenterLat = right.Lat, CenterLon = right.Lon, Priority = CellPriority.Priority2, DistanceAlongRoute = cellRouteDist });
                    cellIndex++;
                }
                accumulatedDistance += segmentDist;
            }

            _logEngineEvent($"[SKYGRID] Spatial Hashing Complete: Generated {Cells.Count} inactive grid cells spanning a {accumulatedDistance:F0} NM corridor.", MainWindow.LogLevel.Normal);
            // Revert to non-async dummy generation so the Map UI renders instantly without waiting for API calls!
            _ = UpdateStreamingWindowAsync(route[0].Latitude, route[0].Longitude, null);
        }

        public void UpdateStreamingWindow(double acftLat, double acftLon)
        {
            _ = UpdateStreamingWindowAsync(acftLat, acftLon, null);
        }

        public async Task UpdateStreamingWindowAsync(double acftLat, double acftLon, Func<double, double, string, Task<string>> fetchMetarCallback)
        {
            if (Cells.Count == 0) return;

            var closest = Cells.Where(c => c.Priority == CellPriority.Priority1)
                               .OrderBy(c => CalculateDistanceNM(acftLat, acftLon, c.CenterLat, c.CenterLon))
                               .FirstOrDefault();

            if (closest == null) return;

            double acftRouteDist = closest.DistanceAlongRoute;
            double windowStart = acftRouteDist - 300;
            double windowEnd = acftRouteDist + 600;

            int newlyLoaded = 0;
            int discarded = 0;

            foreach (var cell in Cells)
            {
                if (cell.DistanceAlongRoute >= windowStart && cell.DistanceAlongRoute <= windowEnd)
                {
                    if (cell.State == CellState.UNLOADED)
                    {
                        cell.State = CellState.REQUESTING; 

                        if (_simconnect != null)
                        {
                            try 
                            {
                                var method = _simconnect.GetType().GetMethods().FirstOrDefault(m => m.Name == "WeatherCreateStation");
                                if (method != null)
                                {
                                    var pInfos = method.GetParameters();
                                    object[] args = new object[pInfos.Length];
                                    int floatIndex = 0, stringIndex = 0;
                                    
                                    for (int i = 0; i < pInfos.Length; i++)
                                    {
                                        var pType = pInfos[i].ParameterType;
                                        if (pType == typeof(System.Enum)) args[i] = System.DayOfWeek.Monday;
                                        else if (pType == typeof(uint) || pType == typeof(int)) args[i] = Convert.ChangeType(0, pType);
                                        else if (pType == typeof(string)) { args[i] = (stringIndex == 0) ? cell.CellID : $"SkyGrid_{cell.CellID}"; stringIndex++; }
                                        else if (pType == typeof(float)) { args[i] = floatIndex == 0 ? (float)cell.CenterLat : floatIndex == 1 ? (float)cell.CenterLon : 0f; floatIndex++; }
                                        else if (pType == typeof(double)) { args[i] = floatIndex == 0 ? cell.CenterLat : floatIndex == 1 ? cell.CenterLon : 0.0; floatIndex++; }
                                        else args[i] = pType.IsValueType ? Activator.CreateInstance(pType) : null;
                                    }
                                    method.Invoke(_simconnect, args);
                                }
                            } catch { }
                        }

                        // CRITICAL FIX: Give P3D time to build the SkyGrid cell!
                        await Task.Delay(500);

                        // Use the injected C# Delegate to pull the real Open-Meteo weather!
                        string fullP3dMetar = null;
                        if (fetchMetarCallback != null)
                        {
                            fullP3dMetar = await fetchMetarCallback(cell.CenterLat, cell.CenterLon, cell.CellID);
                        }
                        if (fullP3dMetar == null) fullP3dMetar = $"{cell.CellID} 240600Z 00000KT 10SM CLR 15/10 A2992";

                        cell.State = CellState.READY; 
                        
                        if (_simconnect != null) {
                            try {
                                _simconnect.WeatherSetObservation(0, fullP3dMetar);
                                cell.State = CellState.INJECTED;
                            } catch { }
                        }
                        newlyLoaded++;
                    }
                }
                else if (cell.DistanceAlongRoute < windowStart - 100)
                {
                    if (cell.State == CellState.READY || cell.State == CellState.INJECTED)
                    {
                        cell.State = CellState.DISCARDED;
                        discarded++;
                    }
                }
            }

            if (newlyLoaded > 0 || discarded > 0)
            {
                _logEngineEvent($"[SKYGRID] Streaming Pipeline: Processed {newlyLoaded} new cells ahead. Discarded {discarded} stale cells behind.", MainWindow.LogLevel.Debug);
            }
        }

        private double CalculateDistanceNM(double lat1, double lon1, double lat2, double lon2)
        {
            var d1 = lat1 * (Math.PI / 180.0); var num1 = lon1 * (Math.PI / 180.0);
            var d2 = lat2 * (Math.PI / 180.0); var num2 = lon2 * (Math.PI / 180.0) - num1;
            var d3 = Math.Pow(Math.Sin((d2 - d1) / 2.0), 2.0) + Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin(num2 / 2.0), 2.0);
            return 3440.065 * (2.0 * Math.Atan2(Math.Sqrt(d3), Math.Sqrt(1.0 - d3)));
        }

        private double CalculateBearing(double lat1, double lon1, double lat2, double lon2)
        {
            double dL = (lon2 - lon1) * Math.PI / 180.0;
            lat1 = lat1 * Math.PI / 180.0; lat2 = lat2 * Math.PI / 180.0;
            double x = Math.Sin(dL) * Math.Cos(lat2);
            double y = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dL);
            return ((Math.Atan2(x, y) * 180.0 / Math.PI) + 360) % 360;
        }

        private (double Lat, double Lon) PointFromDistanceAndBearing(double lat, double lon, double distNm, double bearingDeg)
        {
            double dR = distNm / 3440.065; 
            double bR = bearingDeg * Math.PI / 180.0;
            double lat1 = lat * Math.PI / 180.0; double lon1 = lon * Math.PI / 180.0;

            double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(dR) + Math.Cos(lat1) * Math.Sin(dR) * Math.Cos(bR));
            double lon2 = lon1 + Math.Atan2(Math.Sin(bR) * Math.Sin(dR) * Math.Cos(lat1), Math.Cos(dR) - Math.Sin(lat1) * Math.Sin(lat2));

            return (lat2 * 180.0 / Math.PI, lon2 * 180.0 / Math.PI);
        }
    }
}