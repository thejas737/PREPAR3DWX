using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace P3DWeatherEngine
{
    public class WeatherStation
    {
        public string ICAO { get; set; } = "";
        public string Name { get; set; } = "";
        public string IATA { get; set; } = "";

        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Elevation { get; set; }
    }

    public class StationLocator
    {
        private readonly List<WeatherStation> _stations = new();
        private const double EarthRadiusNm = 3440.065;

        public void LoadStations(string csvFilePath)
        {
            Console.WriteLine("Loading airport database...");

            if (!File.Exists(csvFilePath))
            {
                Console.WriteLine($"Airport database not found: {csvFilePath}");
                return;
            }

            var lines = File.ReadAllLines(csvFilePath);

            for (int i = 1; i < lines.Length; i++)
            {
                try
                {
                    var parts = lines[i].Split(',');

                    // Ensure sufficient columns exist
                    if (parts.Length < 14)
                        continue;

                    string icao = parts[1].Trim('"');
                    string airportName = parts[3].Trim('"');
                    string iata = parts[13].Trim('"');

                    if (icao.Length != 4)
                        continue;

                    if (!double.TryParse(parts[4], out double lat))
                        continue;

                    if (!double.TryParse(parts[5], out double lon))
                        continue;

                    double elev = 0;
                    double.TryParse(parts[6], out elev);

                    _stations.Add(new WeatherStation
                    {
                        ICAO = icao,
                        Name = airportName,
                        IATA = iata,
                        Latitude = lat,
                        Longitude = lon,
                        Elevation = elev
                    });
                }
                catch
                {
                    // Skip malformed rows
                    continue;
                }
            }

            Console.WriteLine($"Loaded {_stations.Count} weather stations.");
        }

        public List<(WeatherStation Station, double Distance)> GetNearestStations(
            double planeLat,
            double planeLon,
            int count = 3)
        {
            return _stations
                .Select(station => (
                    Station: station,
                    Distance: CalculateHaversineDistance(
                        planeLat,
                        planeLon,
                        station.Latitude,
                        station.Longitude)))
                .OrderBy(x => x.Distance)
                .Take(count)
                .ToList();
        }

        private double CalculateHaversineDistance(
            double lat1,
            double lon1,
            double lat2,
            double lon2)
        {
            double dLat = ToRadians(lat2 - lat1);
            double dLon = ToRadians(lon2 - lon1);

            lat1 = ToRadians(lat1);
            lat2 = ToRadians(lat2);

            double a =
                Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2) *
                Math.Cos(lat1) * Math.Cos(lat2);

            double c = 2 * Math.Asin(Math.Sqrt(a));

            return EarthRadiusNm * c;
        }

        private double ToRadians(double angle)
        {
            return Math.PI * angle / 180.0;
        }
    }
}