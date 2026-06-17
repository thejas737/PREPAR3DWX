using System;
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
        static SimConnect simconnect = null;
        static StationLocator locator = new StationLocator();
        static readonly HttpClient client = new HttpClient();

        static string currentIcao = "";
        static DateTime lastFetchTime = DateTime.MinValue;

        static void Main(string[] args)
        {
            Console.WriteLine("=== P3Dv5 Real-Time Weather Engine ===");

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

                simconnect.RequestDataOnSimObject(
                    DATA_REQUESTS.ContinuousPositionRequest,
                    DEFINITIONS.AircraftPosition,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.SECOND,
                    SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                    0, 0, 0);

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
                
                // Get the FULL station object now
                WeatherStation nearest = locator.GetNearestStation(pos.Latitude, pos.Longitude);

                if (nearest != null)
                {
                    bool isNewStation = nearest.ICAO != currentIcao;
                    bool isTimeForUpdate = (DateTime.Now - lastFetchTime).TotalMinutes >= 15;

                    if (isNewStation || isTimeForUpdate)
                    {
                        Console.WriteLine($"\nUpdate Triggered! Nearest: {nearest.ICAO} (Station Elev: {nearest.Elevation}ft)");
                        currentIcao = nearest.ICAO;
                        lastFetchTime = DateTime.Now;

                        await UpdateWeatherAsync(nearest);
                    }
                }
            }
        }

        static async Task UpdateWeatherAsync(WeatherStation station)
        {
            Console.WriteLine($"Fetching METAR for {station.ICAO} from NOAA...");
            string url = $"https://aviationweather.gov/api/data/metar?ids={station.ICAO}&format=raw&hours=2";

            try
            {
                HttpResponseMessage response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                string rawMetar = await response.Content.ReadAsStringAsync();

                if (!string.IsNullOrWhiteSpace(rawMetar))
                {
                    string[] lines = rawMetar.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    string cleanMetar = lines[0].Trim();

                    // 1. Remove prefixes
                    if (cleanMetar.StartsWith("METAR ")) cleanMetar = cleanMetar.Substring(6);
                    if (cleanMetar.StartsWith("SPECI ")) cleanMetar = cleanMetar.Substring(6);

                    // 2. Cut off Remarks and Trends (P3D hates these)
                    string[] trends = { " RMK", " NOSIG", " BECMG", " TEMPO" };
                    foreach (string trend in trends)
                    {
                        int idx = cleanMetar.IndexOf(trend);
                        if (idx > 0) cleanMetar = cleanMetar.Substring(0, idx);
                    }

                    // 3. Remove standard bad words
                    string[] badWords = { "AUTO ", "COR ", "$ " };
                    foreach (string word in badWords) cleanMetar = cleanMetar.Replace(word, "");

                    // 4. Fix Fractional Visibilities
                    cleanMetar = Regex.Replace(cleanMetar, @"\s\d+/\d+SM", "SM");
                    cleanMetar = Regex.Replace(cleanMetar, @"\d+/\d+SM", "1SM");

                    // 5. Fix Metric Visibility
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

                    // 6. THE VIRTUAL SEA LEVEL TRANSLATION (Physics Fix)
                    // Adjust Temperature & Dewpoint based on lapse rate (1.98C per 1000ft)
                    cleanMetar = Regex.Replace(cleanMetar, @"\s(M?\d{2})/(M?\d{2})", match =>
                    {
                        string tStr = match.Groups[1].Value;
                        int temp = tStr.StartsWith("M") ? -int.Parse(tStr.Substring(1)) : int.Parse(tStr);

                        string dStr = match.Groups[2].Value;
                        int dew = dStr.StartsWith("M") ? -int.Parse(dStr.Substring(1)) : int.Parse(dStr);

                        double offset = (station.Elevation / 1000.0) * 1.98;
                        int newTemp = (int)Math.Round(temp + offset);
                        int newDew = (int)Math.Round(dew + offset); 

                        string newTStr = newTemp < 0 ? "M" + Math.Abs(newTemp).ToString("D2") : newTemp.ToString("D2");
                        string newDStr = newDew < 0 ? "M" + Math.Abs(newDew).ToString("D2") : newDew.ToString("D2");

                        return $" {newTStr}/{newDStr}";
                    });

                    // Adjust Cloud Heights from AGL to ASL
                    cleanMetar = Regex.Replace(cleanMetar, @"(FEW|SCT|BKN|OVC|VV)(\d{3})", match =>
                    {
                        string type = match.Groups[1].Value;
                        int heightHundreds = int.Parse(match.Groups[2].Value);
                        
                        int elevHundreds = (int)Math.Round(station.Elevation / 100.0);
                        int newHeight = heightHundreds + elevHundreds;
                        
                        return $"{type}{newHeight:D3}";
                    });

                    // 7. RESTORE GLOB
                    cleanMetar = Regex.Replace(cleanMetar, @"^[A-Z]{4}\s", "GLOB ");

                    Console.WriteLine($"Aggressively Cleaned METAR: {cleanMetar}");
                    simconnect.WeatherSetObservation(0, cleanMetar);
                    Console.WriteLine("-> Weather injected into P3Dv5.");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"-> Error fetching weather: {e.Message}");
            }
        }
    }
}