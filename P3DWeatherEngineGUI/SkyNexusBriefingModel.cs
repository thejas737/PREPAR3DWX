using System.Collections.Generic;

namespace P3DWeatherEngineGUI.Models
{
    public class SkyNexusBriefingModel
    {
        public string FlightNumber { get; set; } = "";
        public string DepartureIcao { get; set; } = "";
        public string ArrivalIcao { get; set; } = "";
        public string CruiseAltitude { get; set; } = "";
        public double TotalDistanceNm { get; set; }
        
        public string DepMetar { get; set; } = "";
        public string ArrMetar { get; set; } = "";
        public string WeatherTrendSummary { get; set; } = "";
        public string WeatherRiskLevel { get; set; } = "NORMAL";

        // Stores the chronological weather samples
        public List<RouteWeatherEvent> RouteTimeline { get; set; } = new List<RouteWeatherEvent>();
    }

    public class RouteWeatherEvent
    {
        public string Phase { get; set; } = ""; // e.g., "Climb", "Mid Route", "Descent"
        public string LocationIdent { get; set; } = ""; 
        public double DistanceFromDep { get; set; }
        public string TurbulenceLevel { get; set; } = "Smooth"; // Smooth, Light, Moderate, Severe
        public string SignificantWeather { get; set; } = "Clear";
        public string Winds { get; set; } = "Calm";
    }
}