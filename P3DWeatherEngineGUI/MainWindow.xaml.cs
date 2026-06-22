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
using System.Text.Json; // ADDED FOR OPEN-METEO PARSING
using LockheedMartin.Prepar3D.SimConnect;
using Microsoft.Web.WebView2.Core; 
using P3DWeatherEngine; 
using System.IO;
using System.Diagnostics;


namespace P3DWeatherEngineGUI
{
    public partial class MainWindow : Window
    {
        // --- LOGGING & MAINTENANCE ENGINE ---

        // 0 = Minimal, 1 = Normal, 2 = Debug
        public enum LogLevel { Minimal = 0, Normal = 1, Debug = 2 }
        
        private string logDirectory;
        private string currentLogFile;
        private readonly object logLock = new object(); // Prevents thread collisions if multiple async tasks log at once
        private string cacheDirectory;
        private string settingsFile;
        private bool isInitializing = true;

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
            // Default to Normal if accessed before UI loads
            LogLevel userSelectedLevel = LogLevel.Normal;

            // Safely read the UI ComboBox 
            Dispatcher.Invoke(() => {
                if (cmbLogLevel != null)
                {
                    userSelectedLevel = (LogLevel)cmbLogLevel.SelectedIndex;
                }
            });

            // Only log if the event is important enough based on the user's setting
            if (eventLevel <= userSelectedLevel)
            {
                string logEntry = $"[{DateTime.UtcNow:HH:mm:ssZ}] [{eventLevel.ToString().ToUpper()}] {message}";
                
                // 1. Write to the .txt log file safely
                lock (logLock)
                {
                    try
                    {
                        File.AppendAllText(currentLogFile, logEntry + Environment.NewLine);
                    }
                    catch { /* Fail silently to prevent weather engine crashes over file-locks */ }
                }

                // 2. Write to the UI Engine Console safely
                Dispatcher.Invoke(() => {
                    if (txtConsole != null)
                    {
                        txtConsole.AppendText(logEntry + Environment.NewLine);
                        txtConsole.ScrollToEnd(); // Automatically scroll down to the newest message
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
        private struct WindsAloftCache
        {
            public int Dir10k; public int Spd10k; // 700 hPa
            public int Dir24k; public int Spd24k; // 500 hPa
            public int Dir36k; public int Spd36k; // 250 hPa
        }
        private WindsAloftCache _windsCache;
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
            UpdateMap(0, 0, true); 
            Log("WebView2 map rendered.");
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

        private void ConnectToSim()
        {
            if (_isSimConnected) return; 

            try
            {
                simconnect = new SimConnect("SkyNexus_Engine", _windowHandle, WM_USER_SIMCONNECT, null, 0);
                _isSimConnected = true;
                
                Dispatcher.Invoke(() => {
                    lblStatus.Text = "Sim Connection: CONNECTED TO P3D";
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
            Log("Simulator closed by user. Detaching engine...");
            DisconnectSim();
        }

        private void DisconnectSim()
        {
            if (simconnect != null)
            {
                try { simconnect.Dispose(); } catch { }
                simconnect = null;
            }
            
            _isSimConnected = false;
            
            Dispatcher.Invoke(() => {
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
                try { simconnect.ReceiveMessage(); }
                catch { DisconnectSim(); }
            }
            return IntPtr.Zero;
        }

        private void Simconnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            Log($"[SIMCONNECT EXCEPTION] Error Code: {data.dwException}");
        }

        private async void Simconnect_OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            if (data.dwRequestID == (uint)DATA_REQUESTS.ContinuousPositionRequest)
            {
                PositionData pos = (PositionData)data.dwData[0];
                
                lblSimTime.Text = $"Active Time: {DateTime.UtcNow:HH:mm}Z";
                
                // --- TRIGGER ASYNC REAL-WORLD WINDS ALOFT FETCH (15 Min Interval) ---
                if ((DateTime.Now - _lastWindsFetchTime).TotalMinutes >= 15 && !_isFetchingWinds)
                {
                    _ = FetchWindsAloftAsync(pos.Latitude, pos.Longitude);
                }

                // --- UPDATE DYNAMIC AIRCRAFT ICON ---
                Dispatcher.Invoke(() => {
                    double maxAlt = 40000.0;
                    double currentAlt = pos.Altitude;
                    if (currentAlt < 0) currentAlt = 0;
                    if (currentAlt > maxAlt) currentAlt = maxAlt;
                    
                    if (altCanvas.ActualHeight > 0)
                    {
                        double bottomPos = (currentAlt / maxAlt) * (altCanvas.ActualHeight - 30); 
                        Canvas.SetBottom(planeIcon, bottomPos);
                    }
                });

                bool isTunedToAtis = Math.Abs(pos.Com1Frequency - ATIS_FREQUENCY) < 0.01;
                if (isTunedToAtis)
                {
                    if (!isAtisPlaying && !string.IsNullOrEmpty(currentIcao))
                    {
                        isAtisPlaying = true; 
                        string infoLetter = PhoneticAlphabet[DateTime.UtcNow.Hour % 26];
                        string voiceScript = $"{atisAirportName} airport information {infoLetter}, {DateTime.UtcNow:HHmm} zulu. " +
                                             $"{atisWindString}. Visibility: {atisRawVisibility}. Sky condition: {atisCloudString}. " +
                                             $"Temperature: {atisRawTemp}. Dewpoint: {atisRawDew}. Altimeter {ToAviationDigits(atisRawAltStr)}. " +
                                             $"Advise controller on initial contact you have {infoLetter}.";
                        
                        Log($"Broadcasting Synthetic ATIS on {ATIS_FREQUENCY} MHz.");
                        speechEngine.SpeakAsync(voiceScript);
                    }
                }
                else if (isAtisPlaying)
                {
                    speechEngine.SpeakAsyncCancelAll();
                    isAtisPlaying = false;
                }

                var nearestStations = locator.GetNearestStations(pos.Latitude, pos.Longitude, 3);
                if (nearestStations.Count > 0 && !isFetchingWeather)
                {
                    WeatherStation primaryStation = nearestStations[0].Station;
                    bool isNewStation = primaryStation.ICAO != currentIcao;
                    bool isTimeForUpdate = (DateTime.Now - lastFetchTime).TotalMinutes >= IDLE_UPDATE_MINUTES;

                    if (isNewStation || isTimeForUpdate)
                    {
                        currentIcao = primaryStation.ICAO ?? "GLOB";
                        lastFetchTime = DateTime.Now;
                        
                        UpdateMap(primaryStation.Latitude, primaryStation.Longitude, false);
                        await UpdateInterpolatedWeatherAsync(nearestStations);
                    }
                }
            }
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
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        var hourly = doc.RootElement.GetProperty("hourly");
                        int currentHour = DateTime.UtcNow.Hour; // Maps exactly to 0-23 array index

                        _windsCache.Spd36k = (int)Math.Round(hourly.GetProperty("wind_speed_250hPa")[currentHour].GetDouble());
                        _windsCache.Dir36k = (int)Math.Round(hourly.GetProperty("wind_direction_250hPa")[currentHour].GetDouble());
                        
                        _windsCache.Spd24k = (int)Math.Round(hourly.GetProperty("wind_speed_500hPa")[currentHour].GetDouble());
                        _windsCache.Dir24k = (int)Math.Round(hourly.GetProperty("wind_direction_500hPa")[currentHour].GetDouble());
                        
                        _windsCache.Spd10k = (int)Math.Round(hourly.GetProperty("wind_speed_700hPa")[currentHour].GetDouble());
                        _windsCache.Dir10k = (int)Math.Round(hourly.GetProperty("wind_direction_700hPa")[currentHour].GetDouble());

                        _lastWindsFetchTime = DateTime.Now;
                        Log($"[NOAA GRIB] Real-World Winds Aloft updated. Core Jetstream (FL360): {_windsCache.Dir36k:D3} @ {_windsCache.Spd36k}kts");

                        Dispatcher.Invoke(() => {
                            lblWind36k.Text = $"{_windsCache.Dir36k:D3} @ {_windsCache.Spd36k} kts";
                            lblWind24k.Text = $"{_windsCache.Dir24k:D3} @ {_windsCache.Spd24k} kts";
                            lblWind10k.Text = $"{_windsCache.Dir10k:D3} @ {_windsCache.Spd10k} kts";
                        });
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

            mapBrowser.NavigateToString(html);
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
                // LIVE NOAA API LOOKUP (Independent of SimConnect)
                LogEngineEvent($"[API] Transmitting GET Request -> NOAA Aviation Weather for {station}", LogLevel.Debug);
                string url = $"https://aviationweather.gov/api/data/metar?ids={station}&format=json";
                
                string jsonResponse = await client.GetStringAsync(url);
                
                using (JsonDocument doc = JsonDocument.Parse(jsonResponse))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    {
                        var wx = root[0];
                        string rawMetar = wx.GetProperty("rawOb").GetString();
                        
                        // Extract Core Data
                        double temp = wx.TryGetProperty("temp", out var t) ? t.GetDouble() : 0;
                        double dew = wx.TryGetProperty("dewp", out var d) ? d.GetDouble() : 0;
                        int wspd = wx.TryGetProperty("wspd", out var ws) ? ws.GetInt32() : 0;
                        int wdir = wx.TryGetProperty("wdir", out var wd) ? wd.GetInt32() : 0;
                        double alt = wx.TryGetProperty("altim", out var a) ? a.GetDouble() : 29.92;
                        
                        // Extract Nav Data
                        double lat = wx.TryGetProperty("lat", out var la) ? la.GetDouble() : 0;
                        double lon = wx.TryGetProperty("lon", out var lo) ? lo.GetDouble() : 0;
                        double elevMeters = wx.TryGetProperty("elev", out var el) ? el.GetDouble() : 0;
                        string stationName = wx.TryGetProperty("name", out var n) ? n.GetString() : "NOAA Database";
                        
                        // 1. Process Visibility (With Deterministic Temp/Dew Spread)
                        int atisRawVisibility = 10;
                        var visMatch = Regex.Match(rawMetar, @"\s(\d+)SM");
                        if (visMatch.Success) atisRawVisibility = int.Parse(visMatch.Groups[1].Value);
                        else {
                            var meterMatch = Regex.Match(rawMetar, @"\s(\d{4})\s");
                            if (meterMatch.Success && int.TryParse(meterMatch.Groups[1].Value, out int m)) {
                                if (m >= 9999) atisRawVisibility = 10;
                                else atisRawVisibility = (int)Math.Round(m / 1609.34);
                            }
                        }

                        if (atisRawVisibility >= 10)
                        {
                            int visSpread = (int)temp - (int)dew;
                            if (visSpread >= 15) atisRawVisibility = 40;
                            else if (visSpread >= 10) atisRawVisibility = 30;
                            else if (visSpread >= 5) atisRawVisibility = 20;
                            else atisRawVisibility = 12;
                        }

                        // 2. Process Clouds for UI
                        string cloudStr = "CLEAR";
                        if (!rawMetar.Contains("CAVOK") && !rawMetar.Contains("SKC") && !rawMetar.Contains("CLR"))
                        {
                            List<string> clouds = new List<string>();
                            foreach (Match m in Regex.Matches(rawMetar, @"(FEW|SCT|BKN|OVC|VV)(\d{3})"))
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
                        lblTemp.Text = $"{temp}°C";
                        lblTempImp.Text = $"{Math.Round(temp * 9.0 / 5.0 + 32)}°F";
                        lblDew.Text = $"{dew}°C";
                        lblDewImp.Text = $"{Math.Round(dew * 9.0 / 5.0 + 32)}°F";

                        // Wind
                        lblWind.Text = $"{wdir:D3} @ {wspd} kts";
                        lblWindImp.Text = $"{Math.Round(wspd * 1.15078)} mph";

                        // Visibility
                        lblVis.Text = $"{Math.Round(atisRawVisibility * 1.60934, 1)} km";
                        lblVisImp.Text = $"{atisRawVisibility} SM";

                        // Pressure
                        // Pressure (Smart Magnitude Check)
                        double hPa, inHg;
                        if (alt > 200) 
                        {
                            // API returned hPa (e.g., 1014)
                            hPa = alt;
                            inHg = alt * 0.029530;
                        }
                        else 
                        {
                            // API returned inHg (e.g., 29.92)
                            inHg = alt;
                            hPa = alt * 33.8639;
                        }

                        lblPressure.Text = $"{(int)Math.Round(hPa)} hPa";
                        lblPressureImp.Text = $"{inHg:F2} inHg";
                        
                        // Conditions & Text
                        lblConditions.Text = cloudStr;
                        txtMetar.Text = rawMetar;

                        // Teleport the Map instantly to the searched airport
                        UpdateMap(lat, lon, false);
                        
                        LogEngineEvent($"Successfully loaded full live NOAA profile for {station}.", LogLevel.Normal);

                        // Fetch TAF
                        try {
                            string tafUrl = $"https://aviationweather.gov/api/data/taf?ids={station}&format=raw";
                            HttpResponseMessage tafResp = await client.GetAsync(tafUrl);
                            if (tafResp.IsSuccessStatusCode) txtTaf.Text = await tafResp.Content.ReadAsStringAsync();
                            else txtTaf.Text = "No TAF available for this station.";
                        } catch { txtTaf.Text = "Failed to fetch TAF."; }

                        // RUN TURBULENCE ENGINE
                        RunTurbulencePrediction(wspd, rawMetar);
                    }
                    else
                    {
                        HandleAirportNotFound(station);
                    }
                }
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

            if (isJetStreamEnabled && _windsCache.Spd36k > 80 && Math.Abs(_windsCache.Spd36k - _windsCache.Spd24k) > 40)
            {
                score += 20;
                sources += "CAT / Jet Stream, ";
                LogEngineEvent($"[TURB] Massive upper-level speed gradient ({_windsCache.Spd36k}kt vs {_windsCache.Spd24k}kt) detected (+20).", LogLevel.Debug);
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

        private void BtnForceUpdate_Click(object sender, RoutedEventArgs e)
        {
            // 1. Write to the log
            LogEngineEvent("Manual live weather refresh triggered.", LogLevel.Normal);
            
            // 2. Update the UI
            lblLastUpdate.Text = DateTime.UtcNow.ToString("HH:mm") + "Z";
            lblStatus.Text = "Sim Connection: Weather Refreshed Successfully.";
            lblStatus.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981")); // Turns green
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

                                var tempMatch = Regex.Match(metar, @"\s(M?\d{2})/(M?\d{2})");
                                var altMatch = Regex.Match(metar, @"[AQ](\d{4})");

                                if (tempMatch.Success && altMatch.Success)
                                {
                                    string tStr = tempMatch.Groups[1].Value;
                                    double temp = tStr.StartsWith("M") ? -int.Parse(tStr.Substring(1)) : int.Parse(tStr);
                                    string dStr = tempMatch.Groups[2].Value;
                                    double dew = dStr.StartsWith("M") ? -int.Parse(dStr.Substring(1)) : int.Parse(dStr);
                                    
                                    double alt;
                                    if (altMatch.Value.StartsWith("Q")) {
                                        alt = double.Parse(altMatch.Groups[1].Value) * 0.029530; 
                                    } else {
                                        alt = double.Parse(altMatch.Groups[1].Value) / 100.0; 
                                    }

                                    double offset = (item.Station.Elevation / 100.0) * 0.198; 
                                    temp += offset; dew += offset;

                                    double dist = item.Distance < 0.1 ? 0.1 : item.Distance; 
                                    double weight = 1.0 / (dist * dist);
                                    interpTemp += temp * weight; interpDew += dew * weight; interpAlt += alt * weight;
                                    totalWeight += weight; validReadings++;
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (validReadings > 0 && !string.IsNullOrEmpty(baseMetar))
                {
                    Log($"Data retrieved. Executing interpolation across {validReadings} stations...");
                    interpTemp /= totalWeight; interpDew /= totalWeight; interpAlt /= totalWeight;

                    atisRawTemp = (int)Math.Round(interpTemp);
                    atisRawDew = (int)Math.Round(interpDew);
                    
                    // --- IATA AND NAME EXTRACTION ---
                    string? iata = string.IsNullOrWhiteSpace(primaryStation.IATA) ? null : primaryStation.IATA.Trim();
                    string? name = string.IsNullOrWhiteSpace(primaryStation.Name) ? null : primaryStation.Name.Trim();
                    
                    lblIata.Text = string.IsNullOrEmpty(iata) ? "---" : iata;
                    lblAirportName.Text = string.IsNullOrEmpty(name) ? "Airport Data Available" : name;
                    atisAirportName = string.IsNullOrEmpty(name) ? (primaryStation.ICAO ?? "Airport") : name;
                    
                    atisRawAltStr = ((int)Math.Round(interpAlt * 100)).ToString("D4");

                    var windMatch = Regex.Match(baseMetar, @"(\d{3}|VRB)(\d{2,3})(?:G\d{2,3})?KT");
                    if (windMatch.Success)
                    {
                        string dir = windMatch.Groups[1].Value;
                        surfWindSpd = int.Parse(windMatch.Groups[2].Value);
                        surfWindDir = (dir == "VRB" || dir == "000") ? 0 : double.Parse(dir);

                        if (dir == "000" && surfWindSpd == 0) atisWindString = "Wind calm";
                        else if (dir == "VRB") atisWindString = $"Wind variable at {surfWindSpd}";
                        else {
                            string dirSpelled = string.Join(" ", dir.ToCharArray()).Replace("9", "niner");
                            atisWindString = $"Wind {dirSpelled} at {surfWindSpd}";
                        }
                    }

                    if (baseMetar.Contains("CAVOK") || baseMetar.Contains("SKC") || baseMetar.Contains("CLR")) { atisCloudString = "clear"; }
                    else 
                    {
                        List<string> clouds = new List<string>();
                        bool hasCeiling = false;
                        foreach (Match m in Regex.Matches(baseMetar, @"(FEW|SCT|BKN|OVC|VV)(\d{3})"))
                        {
                            string typeCloud = m.Groups[1].Value;
                            int h = int.Parse(m.Groups[2].Value) * 100;
                            if (typeCloud == "FEW") clouds.Add($"few clouds at {h}");
                            else if (typeCloud == "SCT") clouds.Add($"{h} scattered");
                            else if (typeCloud == "BKN" && !hasCeiling) { clouds.Add($"ceiling {h} broken"); hasCeiling = true; }
                            else if (typeCloud == "BKN") clouds.Add($"{h} broken");
                            else if (typeCloud == "OVC" && !hasCeiling) { clouds.Add($"ceiling {h} overcast"); hasCeiling = true; }
                            else if (typeCloud == "OVC") clouds.Add($"{h} overcast");
                        }
                        atisCloudString = clouds.Count > 0 ? string.Join(" ", clouds) : "clear";
                    }

                    lblStationName.Text = primaryStation.ICAO;
                    lblLastUpdate.Text = $"{DateTime.UtcNow:HH:mm}Z";
                    lblCoords.Text = $"{primaryStation.Latitude:F3}° / {primaryStation.Longitude:F3}°";
                    lblElevation.Text = $"{primaryStation.Elevation} ft";
                    
                    lblTemp.Text = $"{atisRawTemp}°C";
                    lblTempImp.Text = $"{Math.Round(atisRawTemp * 9.0 / 5.0 + 32)}°F";

                    lblDew.Text = $"{atisRawDew}°C";
                    lblDewImp.Text = $"{Math.Round(atisRawDew * 9.0 / 5.0 + 32)}°F";

                    lblWind.Text = $"{(int)surfWindDir:D3} @ {surfWindSpd} kts";
                    lblWindImp.Text = $"{Math.Round(surfWindSpd * 1.15078)} mph";
                    
                    var visMatch = Regex.Match(baseMetar, @"\s(\d+)SM");
                    if (visMatch.Success) atisRawVisibility = int.Parse(visMatch.Groups[1].Value);
                    else {
                        var meterMatch = Regex.Match(baseMetar, @"\s(\d{4})\s");
                        if (meterMatch.Success && int.TryParse(meterMatch.Groups[1].Value, out int m)) {
                            if (m >= 9999) atisRawVisibility = 10;
                            else atisRawVisibility = (int)Math.Round(m / 1609.34);
                        }
                        else atisRawVisibility = 10;
                    }

                    // --- NEW DETERMINISTIC VISIBILITY MODEL ---
                    if (atisRawVisibility >= 10)
                    {
                        // Calculate relative humidity profile using temp/dew spread
                        int spread = atisRawTemp - atisRawDew;
                        
                        if (spread >= 15) atisRawVisibility = 40;      // Very dry desert/arctic air = crystal clear
                        else if (spread >= 10) atisRawVisibility = 30; // Dry air = excellent visibility
                        else if (spread >= 5) atisRawVisibility = 20;  // Moderate humidity = slight haze
                        else atisRawVisibility = 12;                   // High humidity (close to saturation) = heavy haze

                        Log($"[3D ENGINE] Unrestricted visibility graded by Temp/Dew spread ({spread}°C). Ceiling set to: {atisRawVisibility} SM.");
                    }

                    lblVis.Text = $"{Math.Round(atisRawVisibility * 1.60934, 1)} km";
                    lblVisImp.Text = $"{atisRawVisibility} SM";

                    double inHg = interpAlt;
                    double hPa = inHg * 33.8639;
                    lblPressure.Text = $"{(int)Math.Round(hPa)} hPa";
                    lblPressureImp.Text = $"{inHg:F2} inHg";
                    
                    lblConditions.Text = atisCloudString.ToUpper();
                    txtMetar.Text = baseMetar;

                    try {
                        string tafUrl = $"https://aviationweather.gov/api/data/taf?ids={primaryStation.ICAO}&format=raw";
                        HttpResponseMessage tafResp = await client.GetAsync(tafUrl);
                        if (tafResp.IsSuccessStatusCode) {
                            txtTaf.Text = await tafResp.Content.ReadAsStringAsync();
                        } else { txtTaf.Text = "No TAF available for this station."; }
                    } catch { txtTaf.Text = "Failed to fetch TAF."; }

                    Log($"Engaging 3-Phase Parser on raw string...");
                    string safeP3DMetar = ParseAndSanitizeMetar(baseMetar, primaryStation.Elevation);
                    RunTurbulencePrediction(surfWindSpd, baseMetar);
                    
                    if (simconnect != null) 
                    {
                        Log($"[INJECT] -> {safeP3DMetar}");
                        simconnect.WeatherSetObservation(0, safeP3DMetar);
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
        private string ParseAndSanitizeMetar(string rawMetar, double stationElevation)
        {
            if (string.IsNullOrWhiteSpace(rawMetar)) return "";

            int localTemp = 15;
            int localDew = 10;
            var tempDewMatch = Regex.Match(rawMetar, @"(?:^|\s)(M?\d{2})/(M?\d{2})(?:\s|$)");
            if (tempDewMatch.Success)
            {
                string tStr = tempDewMatch.Groups[1].Value;
                localTemp = tStr.StartsWith("M") ? -int.Parse(tStr.Substring(1)) : int.Parse(tStr);
                string dStr = tempDewMatch.Groups[2].Value;
                localDew = dStr.StartsWith("M") ? -int.Parse(dStr.Substring(1)) : int.Parse(dStr);
            }

            int rmkIndex = rawMetar.IndexOf(" RMK ");
            if (rmkIndex != -1) rawMetar = rawMetar.Substring(0, rmkIndex);

            rawMetar = rawMetar.Replace("=", "").Replace(";", "");

            string[] headersToStrip = { "METAR ", "SPECI ", "AUTO ", "COR ", "$ " };
            foreach (string header in headersToStrip)
            {
                rawMetar = rawMetar.Replace(header, "");
            }

            if (rawMetar.Contains("CAVOK"))
            {
                rawMetar = rawMetar.Replace("CAVOK", "10SM CLR");
            }

            string[] tokens = rawMetar.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> safeTokens = new List<string>();

            safeTokens.Add("GLOB"); 
            
            bool trigger3DInversionLayer = false;
            int inversionTopFlightLevel = 0;

            for (int i = 1; i < tokens.Length; i++) 
            {
                string token = tokens[i];

                if (Regex.IsMatch(token, @"^\d{3}V\d{3}$")) continue;
                if (token == "NOSIG" || token == "BECMG" || token == "TEMPO" || token == "PROB") break; 
                if (token.StartsWith("R") && token.Contains("/") && (token.EndsWith("FT") || token.EndsWith("M") || Regex.IsMatch(token, @"\d{4}"))) continue;
                if (token.Contains("&A")) continue; 

                // --- 1. WINDS ALOFT & JETSTREAM INJECTOR (REAL DATA) ---
                if (Regex.IsMatch(token, @"^(\d{3}|VRB)(\d{2,3})(?:G\d{2,3})?KT$"))
                {
                    safeTokens.Add(token); 

                    Match wMatch = Regex.Match(token, @"^(\d{3}|VRB)(\d{2,3})");
                    if (wMatch.Success)
                    {
                        string dirStr = wMatch.Groups[1].Value;
                        int baseSpd = int.Parse(wMatch.Groups[2].Value);

                        Dispatcher.Invoke(() => {
                            lblWindSurf_Aloft.Text = $"{(dirStr == "VRB" ? "VRB" : int.Parse(dirStr).ToString("D3"))} @ {baseSpd} kts";
                        });

                        // Inject the cached real-world winds aloft
                        if (_windsCache.Spd10k > 0 || _windsCache.Spd24k > 0 || _windsCache.Spd36k > 0)
                        {
                            int alt10m = (int)Math.Round(10000 * 0.3048);
                            safeTokens.Add($"{_windsCache.Dir10k:D3}{_windsCache.Spd10k:D2}KT&A{alt10m}");

                            int alt24m = (int)Math.Round(24000 * 0.3048);
                            safeTokens.Add($"{_windsCache.Dir24k:D3}{_windsCache.Spd24k:D2}KT&A{alt24m}");

                            int alt36m = (int)Math.Round(36000 * 0.3048);
                            safeTokens.Add($"{_windsCache.Dir36k:D3}{_windsCache.Spd36k:D2}KT&A{alt36m}");
                            
                            Log($"[LEXER] Appending synchronized forecast winds-aloft layers.");
                        }
                    }
                    continue;
                }

                // --- 2. VISIBILITY PROCESSING & HAZE CURVE ---
                if (token.EndsWith("SM") || token.EndsWith("KM") || Regex.IsMatch(token, @"^\d{4}$"))
                {
                    double rawVisSM = -1;

                    if (token.EndsWith("SM"))
                    {
                        string numPart = token.Replace("SM", "");
                        if (numPart.Contains("/")) { safeTokens.Add(token); continue; }
                        if (double.TryParse(numPart, out double v)) rawVisSM = v;
                    }
                    else if (token.EndsWith("KM"))
                    {
                        if (double.TryParse(token.Replace("KM", ""), out double v)) rawVisSM = v / 1.60934;
                    }
                    else if (Regex.IsMatch(token, @"^\d{4}$"))
                    {
                        if (double.TryParse(token, out double v)) 
                        {
                            if (v >= 9999) rawVisSM = 10; 
                            else rawVisSM = v / 1609.34;
                        }
                    }

                    if (rawVisSM >= 0)
                    {
                        double finalVisSM;

                        if (rawVisSM >= 10)
                        {
                            // Renamed to visSpread to avoid conflicting with the 3D volumetric engine later in the method!
                            int visSpread = localTemp - localDew;
                            
                            if (visSpread >= 15) finalVisSM = 40;
                            else if (visSpread >= 10) finalVisSM = 30;
                            else if (visSpread >= 5) finalVisSM = 20;
                            else finalVisSM = 12;
                        }
                        else
                        {
                            if (rawVisSM <= 1.5) finalVisSM = rawVisSM * 1.0; 
                            else if (rawVisSM > 1.5 && rawVisSM <= 5.0) finalVisSM = rawVisSM * 2.5; 
                            else finalVisSM = rawVisSM * 1.5; 
                        }

                        int visOut = (int)Math.Round(finalVisSM);
                        if (visOut < 1) visOut = 1; 
                        if (visOut > 40) visOut = 40; 

                        safeTokens.Add($"{visOut}SM");
                        
                        if (visOut <= 6)
                        {
                            trigger3DInversionLayer = true;
                            inversionTopFlightLevel = new Random().Next(15, 26); 
                            Log($"[3D ENGINE] Haze detected. Generating volumetric inversion layer capped at {inversionTopFlightLevel}00 ft.");
                        }
                        continue;
                    }
                }

                // --- 3. ALTIMETER FORMATTING (QNH to inHg) ---
                var altMatch = Regex.Match(token, @"^([AQ])(\d{4})$");
                if (altMatch.Success)
                {
                    if (altMatch.Groups[1].Value == "Q")
                    {
                        double hpa = double.Parse(altMatch.Groups[2].Value);
                        double inHg = hpa * 0.029530;
                        int p3dAlt = (int)Math.Round(inHg * 100);
                        safeTokens.Add($"A{p3dAlt:D4}");
                        Log($"[LEXER] Converted QNH Altimeter for P3D: {token} -> A{p3dAlt:D4}");
                    }
                    else
                    {
                        safeTokens.Add(token); 
                    }
                    continue;
                }

                if (trigger3DInversionLayer && token.StartsWith("VV")) continue;
                safeTokens.Add(token);
            }

            // --- PHASE 3: THE 3D SIMPLIFIER & VOLUMETRIC EXPANSION ---
            string rebuiltMetar = string.Join(" ", safeTokens);

            int spread = localTemp - localDew;
            bool isHighEnergy = localTemp >= 24 && spread <= 4; 
            bool hasPrecipitation = rawMetar.Contains("RA") || rawMetar.Contains("TS") || rawMetar.Contains("SH");

            int cloudLayerCount = 0;
            rebuiltMetar = Regex.Replace(rebuiltMetar, @"(FEW|SCT|BKN|OVC|VV)(\d{3})(CB|TCU)?", m => {
                if (cloudLayerCount >= 2) return ""; 
                
                int h = int.Parse(m.Groups[2].Value);
                
                if (trigger3DInversionLayer && h <= inversionTopFlightLevel)
                {
                    Log($"[3D ENGINE] Swept conflicting cloud layer ({m.Value}) trapped inside inversion zone.");
                    return ""; 
                }
                
                cloudLayerCount++;
                string typeCloud = m.Groups[1].Value;
                string volumetricTag = m.Groups[3].Value; 
                
                if (typeCloud == "OVC") typeCloud = "BKN";
                else if (typeCloud == "BKN") typeCloud = "SCT";
                else if (typeCloud == "SCT") typeCloud = "FEW";

                if (string.IsNullOrEmpty(volumetricTag) && (typeCloud == "SCT" || typeCloud == "BKN" || typeCloud == "OVC" || typeCloud == "FEW"))
                {
                    if (isHighEnergy && hasPrecipitation) 
                    {
                        volumetricTag = "CB"; 
                        Log($"[3D ENGINE] Extreme instability detected. Upgrading {typeCloud}{h:D3} to Volumetric Storm (CB).");
                    }
                    else if (isHighEnergy) 
                    {
                        volumetricTag = "TCU"; 
                        Log($"[3D ENGINE] High heat/humidity detected. Upgrading {typeCloud}{h:D3} to Towering Cumulus (TCU).");
                    }
                }

                h += (int)Math.Round(stationElevation / 100.0); 
                return $"{typeCloud}{h:D3}{volumetricTag}";
            });

            rebuiltMetar = Regex.Replace(rebuiltMetar, @"\s+", " ").Trim();

            if (trigger3DInversionLayer)
            {
                rebuiltMetar = Regex.Replace(rebuiltMetar, @"(\d+SM)", $"$1 VV0{inversionTopFlightLevel}");
            }

            return rebuiltMetar;
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

            // 3. Toggle View Visibility
            ConditionsView.Visibility = Visibility.Collapsed;
            ComingSoonView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed; // Add new settings view to routing reset

            if (clickedBtn == btnTabConditions)
            {
                ConditionsView.Visibility = Visibility.Visible;
            }
            else if (clickedBtn == btnTabSettings)
            {
                SettingsView.Visibility = Visibility.Visible; // Show the new Settings UI
            }
            else
            {
                ComingSoonView.Visibility = Visibility.Visible; // All other unbuilt tabs
            }
        }
    }
}