using System;
using System.Collections.Generic;
using System.IO;
using System.Linq; // Added for sorting

namespace P3DWeatherEngine
{
    public class WeatherStation
    {
        public string ICAO { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Elevation { get; set; }
    }

    public class StationLocator
    {
        private List<WeatherStation> _stations = new List<WeatherStation>();
        private const double EarthRadiusNm = 3440.065;

        public void LoadStations(string csvFilePath)
        {
            Console.WriteLine("Loading airport database...");
            var lines = File.ReadAllLines(csvFilePath);
            
            for (int i = 1; i < lines.Length; i++) 
            {
                var parts = lines[i].Split(',');
                if (parts.Length > 6)
                {
                    string icao = parts[1].Trim('"'); 
                    
                    if (icao.Length == 4 && 
                        double.TryParse(parts[4], out double lat) && 
                        double.TryParse(parts[5], out double lon))
                    {
                        double elev = 0;
                        double.TryParse(parts[6], out elev);

                        _stations.Add(new WeatherStation { 
                            ICAO = icao, Latitude = lat, Longitude = lon, Elevation = elev 
                        });
                    }
                }
            }
            Console.WriteLine($"Loaded {_stations.Count} weather stations.");
        }

        // NEW: Returns the Top 3 closest stations along with their distances
        public List<(WeatherStation Station, double Distance)> GetNearestStations(double planeLat, double planeLon, int count = 3)
        {
            var distances = new List<(WeatherStation Station, double Distance)>();

            foreach (var station in _stations)
            {
                double d = CalculateHaversineDistance(planeLat, planeLon, station.Latitude, station.Longitude);
                distances.Add((station, d));
            }

            // Sort by distance (closest first) and take the top 3
            return distances.OrderBy(x => x.Distance).Take(count).ToList();
        }

        private double CalculateHaversineDistance(double lat1, double lon1, double lat2, double lon2)
        {
            double dLat = ToRadians(lat2 - lat1);
            double dLon = ToRadians(lon2 - lon1);
            lat1 = ToRadians(lat1);
            lat2 = ToRadians(lat2);
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Sin(dLon / 2) * Math.Sin(dLon / 2) * Math.Cos(lat1) * Math.Cos(lat2);
            double c = 2 * Math.Asin(Math.Sqrt(a));
            return EarthRadiusNm * c; 
        }

        private double ToRadians(double angle)
        {
            return Math.PI * angle / 180.0;
        }
    }
}