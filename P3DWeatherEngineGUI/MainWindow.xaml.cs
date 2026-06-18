using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Speech.Synthesis; 
using System.Windows;
using System.Windows.Threading;
using LockheedMartin.Prepar3D.SimConnect;

namespace P3DWeatherEngine
{
    public partial class MainWindow : Window
    {
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

        public class AltitudeRow
        {
            public string? Alt { get; set; }
            public string? Dir { get; set; }
            public string? Spd { get; set; }
            public string? Temp { get; set; }
        }

        const double VISIBILITY_MULTIPLIER = 1.5; 
        const int IDLE_UPDATE_MINUTES = 5;
        const double ATIS_FREQUENCY = 122.00;

        SimConnect? simconnect = null;
        StationLocator locator = new StationLocator();
        readonly HttpClient client = new HttpClient();
        SpeechSynthesizer speechEngine = new SpeechSynthesizer();
        DispatcherTimer simTimer = new DispatcherTimer();

        string currentIcao = "";
        DateTime lastFetchTime = DateTime.MinValue;
        bool isAtisPlaying = false; 
        
        string atisAirportName = "";
        int atisRawVisibility = 10;
        int atisRawTemp = 15;
        int atisRawDew = 10;
        string atisRawAltStr = "2992";
        string atisCloudString = "clear";
        string atisWindString = "Wind calm";
        double surfWindDir = 0;
        int surfWindSpd = 0;

        readonly string[] PhoneticAlphabet = { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliett", "Kilo", "Lima", "Mike", "November", "Oscar", "Papa", "Quebec", "Romeo", "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "X-ray", "Yankee", "Zulu" };

        public MainWindow()
        {
            InitializeComponent();
            
            speechEngine.Volume = 100;
            speechEngine.Rate = -1; 
            speechEngine.SelectVoiceByHints(VoiceGender.Male, VoiceAge.Adult);
            
            try { locator.LoadStations("Data/airports.csv"); }
            catch (Exception ex) { MessageBox.Show($"Missing airports.csv data.\n{ex.Message}"); }

            ConnectToSim();

            simTimer.Interval = TimeSpan.FromMilliseconds(50);
            simTimer.Tick += SimTimer_Tick;
            simTimer.Start();
        }

        private void ConnectToSim()
        {
            try
            {
                // Fix: Pass IntPtr.Zero since we are using a DispatcherTimer to poll messages, 
                // avoiding the issue where the Window handle doesn't exist yet during the constructor.
                simconnect = new SimConnect("P3DWeatherEngine_GUI", IntPtr.Zero, 0, null, 0);
                
                lblStatus.Text = "Sim Connection: CONNECTED TO P3D";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen);

                simconnect.WeatherSetModeCustom();

                simconnect.RegisterDataDefineStruct<PositionData>(DEFINITIONS.AircraftPosition);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Latitude", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Longitude", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Altitude", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "COM ACTIVE FREQUENCY:1", "Megahertz", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);

                simconnect.OnRecvSimobjectData += Simconnect_OnRecvSimobjectData;
                simconnect.OnRecvException += Simconnect_OnRecvException; 

                simconnect.RequestDataOnSimObject(DATA_REQUESTS.ContinuousPositionRequest, DEFINITIONS.AircraftPosition, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SIMCONNECT INIT ERROR] {ex.Message}");
                lblStatus.Text = "Sim Connection: Disconnected / Waiting...";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightCoral);
            }
        }

        private void SimTimer_Tick(object? sender, EventArgs e)
        {
            if (simconnect != null)
            {
                try { simconnect.ReceiveMessage(); }
                catch { simconnect = null; ConnectToSim(); } 
            }
        }

        private void Simconnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            Console.WriteLine($"[SIMCONNECT EXCEPTION] Error Code: {data.dwException}");
        }

        private async void Simconnect_OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            if (data.dwRequestID == (uint)DATA_REQUESTS.ContinuousPositionRequest)
            {
                PositionData pos = (PositionData)data.dwData[0];
                
                lblSimTime.Text = $"Active Time: {DateTime.UtcNow:HH:mm}Z";
                
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
                        
                        _ = Task.Run(() => {
                            speechEngine.Speak(voiceScript);
                            isAtisPlaying = false; 
                        });
                    }
                }
                else if (isAtisPlaying)
                {
                    speechEngine.SpeakAsyncCancelAll();
                    isAtisPlaying = false;
                }

                var nearestStations = locator.GetNearestStations(pos.Latitude, pos.Longitude, 3);
                if (nearestStations.Count > 0)
                {
                    WeatherStation primaryStation = nearestStations[0].Station;
                    bool isNewStation = primaryStation.ICAO != currentIcao;
                    bool isTimeForUpdate = (DateTime.Now - lastFetchTime).TotalMinutes >= IDLE_UPDATE_MINUTES;

                    if (isNewStation || isTimeForUpdate)
                    {
                        currentIcao = primaryStation.ICAO ?? "GLOB";
                        lastFetchTime = DateTime.Now;
                        await UpdateInterpolatedWeatherAsync(nearestStations);
                    }
                }
            }
        }

        private async void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            string manualIcao = txtSearchIcao.Text.Trim().ToUpper();
            if (manualIcao.Length == 4)
            {
                currentIcao = manualIcao;
                var fakeStationList = new List<(WeatherStation, double)> { (new WeatherStation { ICAO = manualIcao, Elevation = 0, Latitude = 0, Longitude = 0 }, 0.0) };
                await UpdateInterpolatedWeatherAsync(fakeStationList);
            }
        }

        private void BtnForceUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(txtMetar.Text) && simconnect != null)
            {
                string globalMetar = Regex.Replace(txtMetar.Text, @"^[A-Z]{4}\s", "GLOB ");
                simconnect.WeatherSetObservation(0, globalMetar);
                MessageBox.Show("Weather injected forcefully to Simulator.", "Update Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async Task UpdateInterpolatedWeatherAsync(List<(WeatherStation Station, double Distance)> stations)
        {
            double totalWeight = 0, interpTemp = 0, interpDew = 0, interpAlt = 0;
            int validReadings = 0;
            string baseMetar = "";
            WeatherStation primaryStation = stations[0].Station;

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
                                double alt = double.Parse(altMatch.Groups[1].Value);
                                if (metar.Contains(" Q")) alt = alt * 0.029530; 

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
                interpTemp /= totalWeight; interpDew /= totalWeight; interpAlt /= totalWeight;

                atisRawTemp = (int)Math.Round(interpTemp);
                atisRawDew = (int)Math.Round(interpDew);
                atisAirportName = primaryStation.ICAO ?? "Airport";
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
                        string type = m.Groups[1].Value;
                        int h = int.Parse(m.Groups[2].Value) * 100;
                        if (type == "FEW") clouds.Add($"few clouds at {h}");
                        else if (type == "SCT") clouds.Add($"{h} scattered");
                        else if (type == "BKN" && !hasCeiling) { clouds.Add($"ceiling {h} broken"); hasCeiling = true; }
                        else if (type == "BKN") clouds.Add($"{h} broken");
                        else if (type == "OVC" && !hasCeiling) { clouds.Add($"ceiling {h} overcast"); hasCeiling = true; }
                        else if (type == "OVC") clouds.Add($"{h} overcast");
                    }
                    atisCloudString = clouds.Count > 0 ? string.Join(" ", clouds) : "clear";
                }

                lblStationName.Text = primaryStation.ICAO;
                lblLastUpdate.Text = $"{DateTime.UtcNow:HH:mm}Z";
                lblCoords.Text = $"{primaryStation.Latitude:F3}° / {primaryStation.Longitude:F3}°";
                lblElevation.Text = $"{primaryStation.Elevation} ft";
                
                lblTemp.Text = $"{atisRawTemp}°C";
                lblDew.Text = $"{atisRawDew}°C";
                lblWind.Text = $"{(int)surfWindDir:D3} @ {surfWindSpd} kts";
                
                var visMatch = Regex.Match(baseMetar, @"\s(\d+)SM");
                if (visMatch.Success) atisRawVisibility = int.Parse(visMatch.Groups[1].Value);
                else {
                    var meterMatch = Regex.Match(baseMetar, @"\s(\d{4})\s");
                    if (meterMatch.Success && int.TryParse(meterMatch.Groups[1].Value, out int m)) atisRawVisibility = (int)Math.Round(m / 1609.34);
                    else atisRawVisibility = 10;
                }
                lblVis.Text = $"{atisRawVisibility} SM";

                double inHg = interpAlt;
                double hPa = inHg * 33.8639;
                lblPressure.Text = $"{(int)Math.Round(hPa)} / {inHg:F2}";
                lblConditions.Text = atisCloudString.ToUpper();
                txtMetar.Text = baseMetar;

                List<AltitudeRow> altData = new List<AltitudeRow>
                {
                    new AltitudeRow { Alt = "Surface", Dir = ((int)surfWindDir).ToString("D3"), Spd = surfWindSpd.ToString(), Temp = atisRawTemp.ToString() },
                    new AltitudeRow { Alt = "3000", Dir = "-", Spd = "-", Temp = "-" },
                    new AltitudeRow { Alt = "6000", Dir = "-", Spd = "-", Temp = "-" },
                    new AltitudeRow { Alt = "9000", Dir = "-", Spd = "-", Temp = "-" },
                    new AltitudeRow { Alt = "12000", Dir = "-", Spd = "-", Temp = "-" }
                };
                lvAltitudes.ItemsSource = altData;

                try {
                    string tafUrl = $"https://aviationweather.gov/api/data/taf?ids={primaryStation.ICAO}&format=raw";
                    HttpResponseMessage tafResp = await client.GetAsync(tafUrl);
                    if (tafResp.IsSuccessStatusCode) {
                        txtTaf.Text = await tafResp.Content.ReadAsStringAsync();
                    } else { txtTaf.Text = "No TAF available for this station."; }
                } catch { txtTaf.Text = "Failed to fetch TAF."; }

                string cleanMetar = baseMetar;
                if (cleanMetar.StartsWith("METAR ")) cleanMetar = cleanMetar.Substring(6);
                string[] badWords = { "AUTO ", "COR ", "$ " };
                foreach (string word in badWords) cleanMetar = cleanMetar.Replace(word, "");

                if (cleanMetar.Contains("CAVOK")) cleanMetar = cleanMetar.Replace("CAVOK", "10SM CLR");

                cleanMetar = Regex.Replace(cleanMetar, @"\b(\d+)SM\b", m => {
                    return $"{(int)Math.Round(int.Parse(m.Groups[1].Value) * VISIBILITY_MULTIPLIER)}SM";
                });

                int cloudLayerCount = 0;
                cleanMetar = Regex.Replace(cleanMetar, @"(FEW|SCT|BKN|OVC|VV)(\d{3})(CB|TCU)?", m => {
                    if (cloudLayerCount >= 2) return ""; 
                    cloudLayerCount++;
                    string type = m.Groups[1].Value;
                    if (type == "OVC") type = "BKN";
                    else if (type == "BKN") type = "SCT";
                    else if (type == "SCT") type = "FEW";

                    int h = int.Parse(m.Groups[2].Value) + (int)Math.Round(primaryStation.Elevation / 100.0);
                    return $"{type}{h:D3}";
                });
                
                cleanMetar = Regex.Replace(cleanMetar, @"\s+", " ").Trim();
                string globalMetar = Regex.Replace(cleanMetar, @"^[A-Z]{4}\s", "GLOB ");

                if (simconnect != null) simconnect.WeatherSetObservation(0, globalMetar);
            }
        }

        private string ToAviationDigits(string input)
        {
            string result = "";
            foreach (char c in input) result += (c == '9' ? "niner " : c + " ");
            return result.Trim();
        }
    }
}