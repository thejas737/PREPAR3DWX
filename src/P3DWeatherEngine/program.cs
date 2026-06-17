using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
    }

    class Program
    {
        // ==========================================
        // ENGINE SETTINGS (Tweak these as needed!)
        // ==========================================
        
        // Multiplies the real-world visibility to fix P3D's unrealistic fog rendering.
        // 1.0 = strictly realistic math (looks too foggy). 
        // 1.5 = adds 50% more visibility distance (visually looks like real life).
        const double VISIBILITY_MULTIPLIER = 1.5; 
        
        // How many minutes to wait before fetching weather again if sitting idle.
        const int IDLE_UPDATE_MINUTES = 5;

        // ==========================================

        static SimConnect simconnect = null;
        static StationLocator locator = new StationLocator();
        static readonly HttpClient client = new HttpClient();

        static string currentIcao = "";
        static DateTime lastFetchTime = DateTime.MinValue;

        static void Main(string[] args)
        {
            Console.WriteLine("=== P3Dv5 Real-Time Weather Engine (v3.2 - Vis Hack) ===");

            string csvPath = "Data/airports.csv"; 
            try { locator.LoadStations(csvPath); }
            catch (Exception ex) { Console.WriteLine($"[ERROR] {ex.Message}"); return; }

            try
            {
                simconnect = new SimConnect("P3DWeatherEngine", IntPtr.Zero, 0, null, 0);
                Console.WriteLine("Connected to P3Dv5 SimConnect!");

                simconnect.RegisterDataDefineStruct<PositionData>(DEFINITIONS.AircraftPosition);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Latitude", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Longitude", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                simconnect.AddToDataDefinition(DEFINITIONS.AircraftPosition, "Plane Altitude", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);

                simconnect.OnRecvSimobjectData += Simconnect_OnRecvSimobjectData;
                simconnect.OnRecvException += Simconnect_OnRecvException; 

                simconnect.RequestDataOnSimObject(DATA_REQUESTS.ContinuousPositionRequest, DEFINITIONS.AircraftPosition, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

                Console.WriteLine("Engine running. Waiting for aircraft position...");

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
                
                var nearestStations = locator.GetNearestStations(pos.Latitude, pos.Longitude, 3);

                if (nearestStations.Count > 0)
                {
                    WeatherStation primaryStation = nearestStations[0].Station;
                    bool isNewStation = primaryStation.ICAO != currentIcao;
                    bool isTimeForUpdate = (DateTime.Now - lastFetchTime).TotalMinutes >= IDLE_UPDATE_MINUTES;

                    if (isNewStation || isTimeForUpdate)
                    {
                        Console.WriteLine($"\n--- Triggering Interpolation Update ---");
                        currentIcao = primaryStation.ICAO;
                        lastFetchTime = DateTime.Now;

                        await UpdateInterpolatedWeatherAsync(nearestStations);
                    }
                }
            }
        }

        static async Task UpdateInterpolatedWeatherAsync(List<(WeatherStation Station, double Distance)> stations)
        {
            double totalWeight = 0;
            double interpTemp = 0;
            double interpDew = 0;
            double interpAlt = 0;
            int validReadings = 0;

            string baseMetar = "";
            WeatherStation primaryStation = stations[0].Station;

            foreach (var item in stations)
            {
                Console.WriteLine($"Fetching {item.Station.ICAO} (Distance: {item.Distance:F1} NM)...");
                string url = $"https://aviationweather.gov/api/data/metar?ids={item.Station.ICAO}&format=raw&hours=2";
                
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

                                double offset = (item.Station.Elevation / 1000.0) * 1.98;
                                temp += offset;
                                dew += offset;

                                double dist = item.Distance < 0.1 ? 0.1 : item.Distance; 
                                double weight = 1.0 / (dist * dist);

                                interpTemp += temp * weight;
                                interpDew += dew * weight;
                                interpAlt += alt * weight;
                                totalWeight += weight;
                                validReadings++;
                            }
                        }
                    }
                }
                catch { /* Ignore failed fetches */ }
            }

            if (validReadings > 0 && !string.IsNullOrEmpty(baseMetar))
            {
                interpTemp /= totalWeight;
                interpDew /= totalWeight;
                interpAlt /= totalWeight;

                int finalTemp = (int)Math.Round(interpTemp);
                int finalDew = (int)Math.Round(interpDew);
                string newTStr = finalTemp < 0 ? "M" + Math.Abs(finalTemp).ToString("D2") : finalTemp.ToString("D2");
                string newDStr = finalDew < 0 ? "M" + Math.Abs(finalDew).ToString("D2") : finalDew.ToString("D2");
                string newAltStr = $"A{(int)Math.Round(interpAlt * 100):D4}";

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

                cleanMetar = Regex.Replace(cleanMetar, @"\s\d+/\d+SM", "SM");
                cleanMetar = Regex.Replace(cleanMetar, @"\d+/\d+SM", "1SM");

                cleanMetar = cleanMetar.Replace(" 9999 ", " 10SM ");
                cleanMetar = Regex.Replace(cleanMetar, @"\s(\d{4})\s", match =>
                {
                    if (int.TryParse(match.Groups[1].Value, out int meters))
                    {
                        int sm = (int)Math.Round(meters / 1609.34);
                        if (sm == 0) sm = 1;
                        return $" {sm}SM ";
                    }
                    return match.Value;
                });

                // ==========================================
                // APPLY VISIBILITY MULTIPLIER HACK
                // ==========================================
                cleanMetar = Regex.Replace(cleanMetar, @"\b(\d+)SM\b", match =>
                {
                    if (int.TryParse(match.Groups[1].Value, out int currentVis))
                    {
                        int newVis = (int)Math.Round(currentVis * VISIBILITY_MULTIPLIER);
                        return $"{newVis}SM";
                    }
                    return match.Value;
                });

                cleanMetar = Regex.Replace(cleanMetar, @"\sM?\d{2}/M?\d{2}", $" {newTStr}/{newDStr}");
                cleanMetar = Regex.Replace(cleanMetar, @"[AQ]\d{4}", newAltStr);

                cleanMetar = Regex.Replace(cleanMetar, @"(FEW|SCT|BKN|OVC|VV)(\d{3})", match =>
                {
                    string type = match.Groups[1].Value;
                    int heightHundreds = int.Parse(match.Groups[2].Value);
                    int elevHundreds = (int)Math.Round(primaryStation.Elevation / 100.0);
                    return $"{type}{(heightHundreds + elevHundreds):D3}";
                });

                cleanMetar = Regex.Replace(cleanMetar, @"^[A-Z]{4}\s", "GLOB ");

                Console.WriteLine($"VIRTUAL METAR: {cleanMetar}");
                simconnect.WeatherSetObservation(0, cleanMetar);
                Console.WriteLine("-> Interpolated Weather injected into P3Dv5.");
            }
        }
    }
}