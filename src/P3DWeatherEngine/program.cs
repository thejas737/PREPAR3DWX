using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Speech.Synthesis; 
using LockheedMartin.Prepar3D.SimConnect;

namespace P3DWeatherEngine
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

    class Program
    {
        const double VISIBILITY_MULTIPLIER = 1.5; 
        const int IDLE_UPDATE_MINUTES = 5;
        const double ATIS_FREQUENCY = 122.00;

        static SimConnect simconnect = null;
        static StationLocator locator = new StationLocator();
        static readonly HttpClient client = new HttpClient();
        static SpeechSynthesizer speechEngine = new SpeechSynthesizer();

        static string currentIcao = "";
        static DateTime lastFetchTime = DateTime.MinValue;
        static bool isAtisPlaying = false; 
        
        static string atisAirportName = "";
        static int atisRawVisibility = 10;
        static int atisRawTemp = 15;
        static int atisRawDew = 10;
        static string atisRawAltStr = "2992";
        static string atisCloudString = "clear";
        static string atisWindString = "Wind calm";

        static readonly string[] PhoneticAlphabet = { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliett", "Kilo", "Lima", "Mike", "November", "Oscar", "Papa", "Quebec", "Romeo", "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "X-ray", "Yankee", "Zulu" };

        static void Main(string[] args)
        {
            Console.WriteLine("=== P3Dv5 Premium Engine (v17.0 - Optimized Surface & Approach) ===");

            speechEngine.Volume = 100;
            speechEngine.Rate = -1; 
            speechEngine.SelectVoiceByHints(VoiceGender.Male, VoiceAge.Adult);
            
            string csvPath = "Data/airports.csv"; 
            try { locator.LoadStations(csvPath); }
            catch (Exception ex) { Console.WriteLine($"[ERROR] {ex.Message}"); return; }

            try
            {
                simconnect = new SimConnect("P3DWeatherEngine", IntPtr.Zero, 0, null, 0);
                Console.WriteLine("Connected to P3Dv5 SimConnect!");

                simconnect.WeatherSetModeCustom();
                Console.WriteLine("Simulator locked to Custom Weather mode.");

                simconnect.RegisterDataDefineStruct<PositionData>(DEFINITIONS.AircraftPosition);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Latitude", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Longitude", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Altitude", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "COM ACTIVE FREQUENCY:1", "Megahertz", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);

                simconnect.OnRecvSimobjectData += Simconnect_OnRecvSimobjectData;
                simconnect.OnRecvException += Simconnect_OnRecvException; 

                simconnect.RequestDataOnSimObject(DATA_REQUESTS.ContinuousPositionRequest, DEFINITIONS.AircraftPosition, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

                while (true)
                {
                    if (simconnect != null) simconnect.ReceiveMessage();
                    System.Threading.Thread.Sleep(50);
                }
            }
            catch (Exception ex) { Console.WriteLine("[FATAL ERROR] SimConnect Error: " + ex.Message); }
        }

        private static void Simconnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            Console.WriteLine($"\n[SIMCONNECT EXCEPTION] P3D Rejected a command! Error Code: {data.dwException}");
        }

        private static async void Simconnect_OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            if (data.dwRequestID == (uint)DATA_REQUESTS.ContinuousPositionRequest)
            {
                PositionData pos = (PositionData)data.dwData[0];
                
                bool isTunedToAtis = Math.Abs(pos.Com1Frequency - ATIS_FREQUENCY) < 0.01;
                if (isTunedToAtis)
                {
                    if (!isAtisPlaying && !string.IsNullOrEmpty(currentIcao))
                    {
                        isAtisPlaying = true; 
                        string infoLetter = PhoneticAlphabet[DateTime.UtcNow.Hour % 26];
                        string zuluTime = DateTime.UtcNow.ToString("HHmm");
                        string voiceScript = $"{atisAirportName} airport information {infoLetter}, {zuluTime} zulu. " +
                                             $"{atisWindString}. Visibility: {atisRawVisibility}. Sky condition: {atisCloudString}. " +
                                             $"Temperature: {atisRawTemp}. Dewpoint: {atisRawDew}. Altimeter {ToAviationDigits(atisRawAltStr)}. " +
                                             $"ILS runway 24 in use. Landing and departing runway 24. VFR aircraft say direction of flight. All aircraft read back hold short instructions. Advise controller on initial contact you have {infoLetter}.";
                        
                        Task.Run(() => {
                            Console.Beep(800, 200); 
                            System.Threading.Thread.Sleep(200);
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

                    // Only process heavy logic if the station changes or 5 minutes pass
                    if (isNewStation || isTimeForUpdate)
                    {
                        currentIcao = primaryStation.ICAO;
                        lastFetchTime = DateTime.Now;
                        await UpdateInterpolatedWeatherAsync(nearestStations);
                    }
                }
            }
        }

        static async Task UpdateInterpolatedWeatherAsync(List<(WeatherStation Station, double Distance)> stations)
        {
            double totalWeight = 0, interpTemp = 0, interpDew = 0, interpAlt = 0;
            int validReadings = 0;
            string baseMetar = "";
            WeatherStation primaryStation = stations[0].Station;

            foreach (var item in stations)
            {
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
                atisAirportName = primaryStation.ICAO;
                atisRawAltStr = ((int)Math.Round(interpAlt * 100)).ToString("D4");

                var windMatch = Regex.Match(baseMetar, @"(\d{3}|VRB)(\d{2,3})(?:G\d{2,3})?KT");
                if (windMatch.Success)
                {
                    string dir = windMatch.Groups[1].Value;
                    int spd = int.Parse(windMatch.Groups[2].Value);
                    if (dir == "000" && spd == 0) atisWindString = "Wind calm";
                    else if (dir == "VRB") atisWindString = $"Wind variable at {spd}";
                    else {
                        string dirSpelled = string.Join(" ", dir.ToCharArray()).Replace("9", "niner");
                        atisWindString = $"Wind {dirSpelled} at {spd}";
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
                        string hStr = h.ToString("#,##0"); 
                        
                        if (type == "FEW") clouds.Add($"few clouds at {hStr}");
                        else if (type == "SCT") clouds.Add($"{hStr} scattered");
                        else if (type == "BKN" && !hasCeiling) { clouds.Add($"ceiling {hStr} broken"); hasCeiling = true; }
                        else if (type == "BKN") clouds.Add($"{hStr} broken");
                        else if (type == "OVC" && !hasCeiling) { clouds.Add($"ceiling {hStr} overcast"); hasCeiling = true; }
                        else if (type == "OVC") clouds.Add($"{hStr} overcast");
                    }
                    atisCloudString = clouds.Count > 0 ? string.Join(" ", clouds) : "clear";
                }

                string cleanMetar = baseMetar;
                if (cleanMetar.StartsWith("METAR ")) cleanMetar = cleanMetar.Substring(6);
                if (cleanMetar.StartsWith("SPECI ")) cleanMetar = cleanMetar.Substring(6);
                string[] trends = { " RMK", " NOSIG", " BECMG", " TEMPO" };
                foreach (string trend in trends)
                {
                    int idx = cleanMetar.IndexOf(trend);
                    if (idx > 0) cleanMetar = cleanMetar.Substring(0, idx);
                }
                string[] badWords = { "AUTO ", "COR ", "$ " };
                foreach (string word in badWords) cleanMetar = cleanMetar.Replace(word, "");

                if (cleanMetar.Contains("CAVOK")) cleanMetar = cleanMetar.Replace("CAVOK", "10SM CLR");
                if (cleanMetar.Contains("NSC")) cleanMetar = cleanMetar.Replace("NSC", "CLR");
                if (cleanMetar.Contains("NCD")) cleanMetar = cleanMetar.Replace("NCD", "CLR");

                cleanMetar = Regex.Replace(cleanMetar, @"\s\d+/\d+SM", "SM");
                cleanMetar = Regex.Replace(cleanMetar, @"\d+/\d+SM", "1SM");
                cleanMetar = cleanMetar.Replace(" 9999 ", " 10SM ");

                var visMatch = Regex.Match(cleanMetar, @"\s(\d+)SM");
                if (visMatch.Success) atisRawVisibility = int.Parse(visMatch.Groups[1].Value);
                else {
                    var meterMatch = Regex.Match(cleanMetar, @"\s(\d{4})\s");
                    if (meterMatch.Success && int.TryParse(meterMatch.Groups[1].Value, out int m))
                        atisRawVisibility = (int)Math.Round(m / 1609.34);
                    else atisRawVisibility = 10;
                }

                cleanMetar = Regex.Replace(cleanMetar, @"\s(\d{4})\s", m => {
                    int sm = (int)Math.Round(int.Parse(m.Groups[1].Value) / 1609.34);
                    return $" {(sm == 0 ? 1 : sm)}SM ";
                });

                cleanMetar = Regex.Replace(cleanMetar, @"\b(\d+)SM\b", m => {
                    return $"{(int)Math.Round(int.Parse(m.Groups[1].Value) * VISIBILITY_MULTIPLIER)}SM";
                });

                string newTStr = atisRawTemp < 0 ? "M" + Math.Abs(atisRawTemp).ToString("D2") : atisRawTemp.ToString("D2");
                string newDStr = atisRawDew < 0 ? "M" + Math.Abs(atisRawDew).ToString("D2") : atisRawDew.ToString("D2");
                cleanMetar = Regex.Replace(cleanMetar, @"\sM?\d{2}/M?\d{2}", $" {newTStr}/{newDStr}");
                cleanMetar = Regex.Replace(cleanMetar, @"[AQ]\d{4}", $"A{atisRawAltStr}");

                int cloudLayerCount = 0;
                cleanMetar = Regex.Replace(cleanMetar, @"(FEW|SCT|BKN|OVC|VV)(\d{3})(CB|TCU)?", m => {
                    if (cloudLayerCount >= 2) return ""; 
                    cloudLayerCount++;
                    string type = m.Groups[1].Value;
                    if (type == "OVC") type = "BKN";
                    else if (type == "BKN") type = "SCT";
                    else if (type == "SCT") type = "FEW";

                    int h = int.Parse(m.Groups[2].Value) + (int)Math.Round(primaryStation.Elevation / 100.0);
                    string modifier = m.Groups[3].Success ? m.Groups[3].Value : "";
                    return $"{type}{h:D3}{modifier}";
                });
                
                cleanMetar = Regex.Replace(cleanMetar, @"\s+", " ").Trim();

                string globalMetar = Regex.Replace(cleanMetar, @"^[A-Z]{4}\s", "GLOB ");
                
                Console.WriteLine($"[INJECTING GLOBAL (VATSIM)]: {globalMetar}");
                
                // Fire and forget injection
                simconnect.WeatherSetObservation(0, globalMetar);
            }
        }

        private static string ToAviationDigits(string input)
        {
            string result = "";
            foreach (char c in input) result += (c == '9' ? "niner " : c + " ");
            return result.Trim();
        }
    }
}