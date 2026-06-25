using System;
using System.IO;
using System.Xml.Linq;

namespace P3DWeatherEngineGUI.Services
{
    public class RouteParserService
    {
        public ParsedRoute ParseFile(string filePath)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException("Route file not found.");

            string extension = Path.GetExtension(filePath).ToLower();

            if (extension == ".pln") return ParseStandardPln(filePath);
            if (extension == ".rte") return ParseProprietaryRte(filePath);
            
            throw new NotSupportedException("Unsupported flight plan format.");
        }

        private ParsedRoute ParseStandardPln(string filePath)
        {
            // Standard MSFS/P3D XML parsing logic
            XDocument doc = XDocument.Load(filePath);
            // TODO: Extract Waypoints, Dep, Arr from XML nodes
            return new ParsedRoute { /* Populated Data */ };
        }

        private ParsedRoute ParseProprietaryRte(string filePath)
        {
            // Custom parser for PMDG/iFly .rte text structures
            string[] lines = File.ReadAllLines(filePath);
            // TODO: Extract route string via line-by-line regex
            return new ParsedRoute { /* Populated Data */ };
        }
    }

    public class ParsedRoute
    {
        public string? DepartureICAO { get; set; }
        public string? ArrivalICAO { get; set; }
        public string? RouteString { get; set; }
    }
}