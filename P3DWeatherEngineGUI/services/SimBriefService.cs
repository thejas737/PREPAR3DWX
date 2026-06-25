using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace P3DWeatherEngineGUI.Services
{
    public class SimBriefService
    {
        private readonly HttpClient _client = new HttpClient();

        public async Task<SimBriefData> FetchLatestOFPAsync(string username)
        {
            try
            {
                // Determine if the user typed a numeric Pilot ID or a text-based Username
                bool isNumeric = int.TryParse(username, out _);
                string queryParam = isNumeric ? "userid" : "username";

                // The '&json=1' flag ensures SimBrief returns modern JSON
                string url = $"https://www.simbrief.com/api/xml.fetcher.php?{queryParam}={Uri.EscapeDataString(username)}&json=1";
                string json = await _client.GetStringAsync(url);

                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    // 1. Check if SimBrief returned an explicit error (e.g. No active flight plan)
                    if (root.TryGetProperty("fetch", out var fetchNode) && fetchNode.TryGetProperty("status", out var statusNode))
                    {
                        string status = statusNode.GetString() ?? "";
                        if (status.Contains("Error"))
                        {
                            throw new Exception($"SimBrief API: {status}");
                        }
                    }

                    // 2. Safely grab the main property blocks (if they exist)
                    var general = root.TryGetProperty("general", out var g) ? g : default;
                    var origin = root.TryGetProperty("origin", out var o) ? o : default;
                    var dest = root.TryGetProperty("destination", out var d) ? d : default;
                    var alt = root.TryGetProperty("alternate", out var a) ? a : default;
                    var times = root.TryGetProperty("times", out var t) ? t : default;
                    var navlog = root.TryGetProperty("navlog", out var n) ? n : default; // NEW

                    // 3. Helper function to safely extract strings without throwing dictionary errors
                    // 3. Helper function to safely extract strings AND numbers without crashing
                    string SafeGetString(JsonElement element, string propertyName)
                    {
                        if (element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(propertyName, out var prop))
                        {
                            if (prop.ValueKind == JsonValueKind.String) return prop.GetString() ?? "";
                            if (prop.ValueKind == JsonValueKind.Number) return prop.GetRawText(); // Grabs the raw number safely
                        }
                        return "";
                    }

                    // 5. Parse the Navlog Array for exact routing coordinates
                    var waypoints = new System.Collections.Generic.List<Waypoint>();
                    
                    try
                    {
                        if (navlog.ValueKind != JsonValueKind.Undefined && navlog.TryGetProperty("fix", out var fixArray))
                        {
                            if (fixArray.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var fix in fixArray.EnumerateArray())
                                {
                                    string ident = SafeGetString(fix, "ident");
                                    string latStr = SafeGetString(fix, "pos_lat");
                                    string lonStr = SafeGetString(fix, "pos_long");

                                    if (double.TryParse(latStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double lat) &&
                                        double.TryParse(lonStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double lon))
                                    {
                                        waypoints.Add(new Waypoint { Ident = ident, Latitude = lat, Longitude = lon });
                                    }
                                }
                            }
                        }
                    }
                    catch { /* Ignore parsing errors and trigger fallback */ }

                    // EMERGENCY FALLBACK & DIAGNOSTIC REPORTER
                    if (waypoints.Count == 0)
                    {
                        // 1. Grab Origin and Destination coordinates so the Map and Winds Aloft ALWAYS work
                        string depLat = SafeGetString(origin, "pos_lat");
                        string depLon = SafeGetString(origin, "pos_long");
                        string arrLat = SafeGetString(dest, "pos_lat");
                        string arrLon = SafeGetString(dest, "pos_long");

                        if (double.TryParse(depLat, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dLat) &&
                            double.TryParse(depLon, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dLon))
                        {
                            waypoints.Add(new Waypoint { Ident = SafeGetString(origin, "icao_code"), Latitude = dLat, Longitude = dLon });
                        }

                        if (double.TryParse(arrLat, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double aLat) &&
                            double.TryParse(arrLon, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double aLon))
                        {
                            waypoints.Add(new Waypoint { Ident = SafeGetString(dest, "icao_code"), Latitude = aLat, Longitude = aLon });
                        }

                        // 2. Output the exact API structure to help us debug the Navlog
                        string rawJson = navlog.ValueKind != JsonValueKind.Undefined ? navlog.GetRawText() : "NAVLOG MISSING";
                        string preview = rawJson.Length > 300 ? rawJson.Substring(0, 300) + "..." : rawJson;
                        System.Windows.MessageBox.Show($"Waypoint Count = {waypoints.Count}","Route Debug");
                    }

                    // --- NEW: PARSE ALTERNATE ROUTE WAYPOINTS ---
                    var altWaypoints = new System.Collections.Generic.List<Waypoint>();
                    try
                    {
                        if (alt.ValueKind != JsonValueKind.Undefined && alt.TryGetProperty("navlog", out var altNavlog))
                        {
                            if (altNavlog.TryGetProperty("fix", out var altFixArray) && altFixArray.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var fix in altFixArray.EnumerateArray())
                                {
                                    string ident = SafeGetString(fix, "ident");
                                    string latStr = SafeGetString(fix, "pos_lat");
                                    string lonStr = SafeGetString(fix, "pos_long");

                                    if (double.TryParse(latStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double lat) &&
                                        double.TryParse(lonStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double lon))
                                    {
                                        altWaypoints.Add(new Waypoint { Ident = ident, Latitude = lat, Longitude = lon });
                                    }
                                }
                            }
                        }
                    }
                    catch { /* Ignore parsing errors for the alternate route */ }

                    // 4. Construct the data model
                    return new SimBriefData
                    {
                        FlightNumber = SafeGetString(general, "flight_number"),
                        Airline = SafeGetString(general, "icao_airline"), 
                        DepartureICAO = SafeGetString(origin, "icao_code"),
                        ArrivalICAO = SafeGetString(dest, "icao_code"),
                        AlternateICAO = SafeGetString(alt, "icao_code"),
                        Route = SafeGetString(general, "route"),
                        CruiseAltitude = SafeGetString(general, "initial_altitude"),
                        BlockTime = SafeGetString(times, "est_block"),
                        DistanceNM = SafeGetString(general, "route_distance"),
                        RouteWaypoints = waypoints,
                        AlternateWaypoints = altWaypoints // Map the new alternate list!
                    };
                }
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }
        }
    }

    public class SimBriefData
    {
        public string? FlightNumber { get; set; }
        public string? Airline { get; set; }
        public string? DepartureICAO { get; set; }
        public string? ArrivalICAO { get; set; }
        public string? AlternateICAO { get; set; }
        public string? Route { get; set; }
        public string? CruiseAltitude { get; set; }
        public string? BlockTime { get; set; }
        public string? DistanceNM { get; set; }
        
        // NEW: The list of geographical coordinates for the flight path
        public System.Collections.Generic.List<Waypoint> RouteWaypoints { get; set; } = new System.Collections.Generic.List<Waypoint>();

        public List<Waypoint> AlternateWaypoints { get; set; } = new List<Waypoint>();
    }

    public class Waypoint
    {
        public string? Ident { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}