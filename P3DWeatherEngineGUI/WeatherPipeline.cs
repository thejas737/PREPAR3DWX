using System;
using System.Collections.Generic;
using System.Linq;
namespace P3DWeatherEngineGUI
{


public class WeatherModel
    {
        // Raw Data
        public string RawMetar { get; set; } = "";
        public string Timestamp { get; set; } = "";

        // Station Telemetry (Grid Prep)
        public string ICAO { get; set; } = "GLOB";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double StationElevation { get; set; }

        // Barometrics
        public double NumericPressure { get; set; }
        public string AltimeterToken { get; set; } = "A2992";
        public string PressureTrend { get; set; } = "STABLE";

        // Thermodynamics
        public int TempC { get; set; } = 15;
        public int DewpointC { get; set; } = 10;
        public double RelativeHumidity { get; set; }
        public int DensityAltitude { get; set; } 

        // Surface Wind
        public string WindToken { get; set; } = "00000KT";
        public int SurfaceWindDir { get; set; }
        public int SurfaceWindSpd { get; set; }
        public int WindGust { get; set; }
        public string WindUnit { get; set; } = "KT";
        public string VariableWindRange { get; set; } = "";

        // Visibility
        public int VisibilityMeters { get; set; }
        public int VisibilitySM { get; set; }
        public int PrevailingVisibilitySM { get; set; }
        public int MaxVisibility { get; set; }

        // Meteorological Restrictions
        public bool Fog { get; set; }
        public bool Mist { get; set; }
        public bool Smoke { get; set; }
        public bool Dust { get; set; }
        public bool Sand { get; set; }
        public bool Haze { get; set; }
        public bool VolcanicAsh { get; set; }

        // 3D Environment
        public List<WeatherPhenomenon> WeatherPhenomena { get; set; } = new List<WeatherPhenomenon>();
        public List<CloudLayer> CloudLayers { get; set; } = new List<CloudLayer>();
        public bool IsClearSkies { get; set; }
        
        // Vertical Atmosphere (Replaces WindsAloft Dictionary)
        public AtmosphericProfile Atmosphere { get; set; } = new AtmosphericProfile();
        
        // Aviation Indices
        public int ConvectiveIndex { get; set; }
        public int TurbulenceIndex { get; set; }
        public string TurbulenceOutlook { get; set; } = "SMOOTH";
    }

// --- 1. ENUMS & STRUCTURAL DEFINITIONS ---
    public enum CloudCoverage { NSC, CLR, SKC, CAVOK, FEW, SCT, BKN, OVC, VV }
    public enum ConvectiveType { None, TCU, CB }

    public class WeatherPhenomenon
    {
        public string RawToken { get; set; } = "";
        public string Type { get; set; } = "";
        public string Intensity { get; set; } = "";
        public bool Vicinity { get; set; }
        public bool Freezing { get; set; }
        public bool Shower { get; set; }
    }

    public class WeatherQuality
    {
        public int ConfidenceScore { get; set; } = 100;
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> UnsupportedTokens { get; set; } = new List<string>();
        public List<string> MissingFields { get; set; } = new List<string>();
        public List<string> ParserNotes { get; set; } = new List<string>();
    }

    public class WeatherDecodeResult
    {
        public WeatherModel Model { get; set; } = new WeatherModel();
        public WeatherQuality Quality { get; set; } = new WeatherQuality();
    }

    // --- 2. ATMOSPHERIC PHYSICS PROFILES ---
    public class WindLayer
    {
        public int Direction { get; set; }
        public int Speed { get; set; }
        public int Temperature { get; set; }
        public double Humidity { get; set; }
        public int Turbulence { get; set; }
        public int VerticalVelocity { get; set; }
    }

    public class AtmosphericProfile
    {
        // Dictionary Key = Flight Level (e.g., 0 for Surf, 100 for FL100, 360 for FL360)
        public Dictionary<int, WindLayer> Layers { get; set; } = new Dictionary<int, WindLayer>();
    }

    public class CloudLayer
    {
        public CloudCoverage Coverage { get; set; } = CloudCoverage.CLR;
        public int BaseElevationMSL { get; set; }
        public int EstimatedTopMSL { get; set; }
        public ConvectiveType CloudType { get; set; } = ConvectiveType.None; 
        public int Thickness { get; set; }
        public bool IsConvective => CloudType == ConvectiveType.CB || CloudType == ConvectiveType.TCU;
    }

public class RendererProfile
{
    public int MaxCloudLayers { get; set; } = 3;
    public bool EnforceSingleConvectiveLayer { get; set; } = true;
}

public class MetarDecoder
{
    public WeatherDecodeResult Decode(string rawMetar, double elevation, AtmosphericProfile localWinds, AtmosphericProfile globalCache)
    {
        var result = new WeatherDecodeResult();
        var model = result.Model;
        var quality = result.Quality;

        model.RawMetar = rawMetar;
        model.StationElevation = elevation;
        if (string.IsNullOrWhiteSpace(rawMetar)) 
        {
            quality.ConfidenceScore = 0;
            quality.MissingFields.Add("Empty METAR string provided.");
            return result;
        }

        string clean = rawMetar.Replace("\r", " ").Replace("\n", " ").Replace("=", "").Replace(";", "");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();

        // 1. Timestamp & Wind (Expanded for Gusts)
        model.Timestamp = DateTime.UtcNow.ToString("ddHHmm") + "Z";
        var timeMatch = System.Text.RegularExpressions.Regex.Match(clean, @"\b(\d{6}Z)\b");
        if (timeMatch.Success) model.Timestamp = timeMatch.Groups[1].Value;

        var windMatch = System.Text.RegularExpressions.Regex.Match(clean, @"\b(\d{3}|VRB)(\d{2,3})(?:G(\d{2,3}))?(KT|MPS|KMH)\b");
        if (windMatch.Success)
        {
            model.WindUnit = windMatch.Groups[4].Value;
            string dirStr = windMatch.Groups[1].Value;
            model.SurfaceWindDir = dirStr == "VRB" ? 0 : int.Parse(dirStr);
            
            int rawSpd = int.Parse(windMatch.Groups[2].Value);
            if (windMatch.Groups[3].Success) model.WindGust = int.Parse(windMatch.Groups[3].Value);
            
            if (model.WindUnit == "MPS") model.SurfaceWindSpd = (int)Math.Round(rawSpd * 1.94384);
            else if (model.WindUnit == "KMH") model.SurfaceWindSpd = (int)Math.Round(rawSpd * 0.539957);
            else model.SurfaceWindSpd = rawSpd;

            model.WindToken = $"{(dirStr == "VRB" ? "VRB" : dirStr)}{model.SurfaceWindSpd:D2}KT";
        }
        else { quality.ConfidenceScore -= 10; quality.MissingFields.Add("Wind Data"); }

        // 2. Temp & Dewpoint (with Magnus Formula RH)
        var tempMatch = System.Text.RegularExpressions.Regex.Match(clean, @"(?:^|\s)(M?\d{2})/(M?\d{2}|XX|///?)?(?:\s|$)");
        if (tempMatch.Success)
        {
            model.TempC = tempMatch.Groups[1].Value.StartsWith("M") ? -int.Parse(tempMatch.Groups[1].Value.Substring(1)) : int.Parse(tempMatch.Groups[1].Value);
            
            string dewStr = tempMatch.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(dewStr) || dewStr == "XX" || dewStr.StartsWith("/")) {
                model.DewpointC = model.TempC - 5; 
                quality.Warnings.Add("Dewpoint missing. Approximated standard atmospheric spread.");
            } 
            else model.DewpointC = dewStr.StartsWith("M") ? -int.Parse(dewStr.Substring(1)) : int.Parse(dewStr);
            
            // Advanced Atmospheric Physics: The Magnus Formula for Relative Humidity
            double betaTemp = (17.625 * model.TempC) / (243.04 + model.TempC);
            double betaDew = (17.625 * model.DewpointC) / (243.04 + model.DewpointC);
            model.RelativeHumidity = 100.0 * Math.Exp(betaDew - betaTemp);
        }
        else { quality.ConfidenceScore -= 15; quality.MissingFields.Add("Temperature/Dewpoint Data"); }

        // 3. Altimeter
        var altMatch = System.Text.RegularExpressions.Regex.Match(clean, @"\b([AQ])(\d{4})\b");
        if (altMatch.Success)
        {
            if (altMatch.Groups[1].Value == "Q")
            {
                model.NumericPressure = double.Parse(altMatch.Groups[2].Value);
                int p3dAlt = (int)Math.Round(model.NumericPressure * 0.029530 * 100);
                model.AltimeterToken = $"A{p3dAlt:D4}";
            }
            else {
                model.AltimeterToken = altMatch.Value;
                model.NumericPressure = double.Parse(altMatch.Groups[2].Value) / 100.0 * 33.8639; // Convert inHg to hPa
            }
        }

        // 4. Visibility Engine
        var visMatchFrac = System.Text.RegularExpressions.Regex.Match(clean, @"\b(?:(\d+)\s+)?(\d+)/(\d+)SM\b");
        var visMatchSM = System.Text.RegularExpressions.Regex.Match(clean, @"\b(\d+)SM\b");
        var visMatchM = System.Text.RegularExpressions.Regex.Match(clean, @"(?<=\s|^)(\d{4})(?=\s|NDV|$)");

        if (visMatchFrac.Success) 
        {
            double whole = visMatchFrac.Groups[1].Success ? double.Parse(visMatchFrac.Groups[1].Value) : 0;
            model.VisibilitySM = (int)Math.Max(1, Math.Round(whole + (double.Parse(visMatchFrac.Groups[2].Value) / double.Parse(visMatchFrac.Groups[3].Value))));
            model.VisibilityMeters = (int)Math.Round(model.VisibilitySM * 1609.34);
        }
        else if (visMatchSM.Success) {
            model.VisibilitySM = int.Parse(visMatchSM.Groups[1].Value);
            model.VisibilityMeters = (int)Math.Round(model.VisibilitySM * 1609.34);
        }
        else if (visMatchM.Success)
        {
            model.VisibilityMeters = int.Parse(visMatchM.Groups[1].Value);
            model.VisibilitySM = model.VisibilityMeters >= 9999 ? 10 : (int)Math.Round(model.VisibilityMeters / 1609.34);
        }

        model.IsClearSkies = clean.Contains("NSC") || clean.Contains("CLR") || clean.Contains("SKC") || clean.Contains("CAVOK");
        
        model.PrevailingVisibilitySM = model.VisibilitySM;
        if (model.VisibilitySM >= 10 || model.IsClearSkies)
        {
            if (model.RelativeHumidity < 40) model.PrevailingVisibilitySM = 40;
            else if (model.RelativeHumidity < 60) model.PrevailingVisibilitySM = 30;
            else if (model.RelativeHumidity < 80) model.PrevailingVisibilitySM = 20;
            else model.PrevailingVisibilitySM = 12;
        }
        model.MaxVisibility = model.PrevailingVisibilitySM + 10;
        if (model.PrevailingVisibilitySM < 1) model.PrevailingVisibilitySM = 1;

        // 5. Phenomenon Object Mapping
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(clean, @"\b(-|\+|VC)?(TS|SH|FZ|PR)?(RA|SN|DZ|SG|GR|GS|UP|BR|FG|FU|VA|DU|SA|HZ|PY|PO|SQ|FC|SS|DS)\b"))
        {
            var phenom = new WeatherPhenomenon
            {
                RawToken = m.Value,
                Intensity = m.Groups[1].Value,
                Type = m.Groups[3].Value,
                Vicinity = m.Groups[1].Value == "VC",
                Freezing = m.Groups[2].Value == "FZ",
                Shower = m.Groups[2].Value == "SH"
            };
            model.WeatherPhenomena.Add(phenom);

            if (phenom.Type == "FG") model.Fog = true;
            if (phenom.Type == "BR") model.Mist = true;
            if (phenom.Type == "FU") model.Smoke = true;
            if (phenom.Type == "DU") model.Dust = true;
            if (phenom.Type == "SA") model.Sand = true;
            if (phenom.Type == "HZ") model.Haze = true;
            if (phenom.Type == "VA") model.VolcanicAsh = true;

            if (phenom.RawToken.Contains("TS")) model.ConvectiveIndex += 50;
        }

        // 6. Dynamic Cloud Modeling (Enums & Physics Top Estimator)
        int elevFL = (int)Math.Round(elevation / 100.0);
        if (!model.IsClearSkies)
        {
            var rawCloudMatches = System.Text.RegularExpressions.Regex.Matches(clean, @"(FEW|SCT|BKN|OVC|VV)(\d{3}|///)(CB|TCU)?");
            foreach (System.Text.RegularExpressions.Match m in rawCloudMatches)
            {
                string rawType = m.Groups[1].Value;
                CloudCoverage coverageEnum = Enum.TryParse(rawType, out CloudCoverage p1) ? p1 : CloudCoverage.OVC;
                
                string altToken = m.Groups[2].Value;
                int hMSL = elevFL + (altToken == "///" ? 2 : int.Parse(altToken)); // Obscured vertical limit default
                
                string rawModifier = m.Groups[3].Value;
                ConvectiveType convEnum = Enum.TryParse(rawModifier, out ConvectiveType p2) ? p2 : ConvectiveType.None;

                // Dynamic Top Estimation based on coverage, convection, temperature, and RH
                int baseThickness = coverageEnum switch { CloudCoverage.FEW => 20, CloudCoverage.SCT => 40, CloudCoverage.BKN => 80, CloudCoverage.OVC => 150, _ => 50 };
                if (convEnum == ConvectiveType.TCU) baseThickness = 180;
                if (convEnum == ConvectiveType.CB) baseThickness = 350;

                int dynamicThickness = baseThickness + (int)(model.TempC * 0.5) + (int)(model.RelativeHumidity * 0.2) + (model.ConvectiveIndex / 10);
                if (dynamicThickness < 10) dynamicThickness = 10;

                model.CloudLayers.Add(new CloudLayer
                {
                    Coverage = coverageEnum,
                    BaseElevationMSL = hMSL,
                    CloudType = convEnum,
                    Thickness = dynamicThickness,
                    EstimatedTopMSL = hMSL + dynamicThickness
                });
                
                if (convEnum == ConvectiveType.CB) model.ConvectiveIndex += 40;
            }

            if (model.WeatherPhenomena.Count > 0 && !model.CloudLayers.Any(c => c.Coverage == CloudCoverage.BKN || c.Coverage == CloudCoverage.OVC))
            {
                bool isTS = model.WeatherPhenomena.Any(p => p.RawToken.Contains("TS"));
                model.CloudLayers.Add(new CloudLayer
                {
                    Coverage = CloudCoverage.BKN,
                    BaseElevationMSL = elevFL + 40,
                    CloudType = isTS && !model.CloudLayers.Any(c => c.IsConvective) ? ConvectiveType.CB : ConvectiveType.None,
                    Thickness = isTS ? 300 : 80
                });
            }

            // Layer Merging System
            model.CloudLayers = model.CloudLayers.OrderBy(c => c.BaseElevationMSL).ToList();
            for (int i = 0; i < model.CloudLayers.Count - 1; i++)
            {
                if (Math.Abs(model.CloudLayers[i + 1].BaseElevationMSL - model.CloudLayers[i].BaseElevationMSL) <= 3) 
                {
                    var c1 = model.CloudLayers[i];
                    var c2 = model.CloudLayers[i + 1];
                    c1.Coverage = c1.Coverage >= c2.Coverage ? c1.Coverage : c2.Coverage;
                    if (c1.CloudType == ConvectiveType.None && c2.CloudType != ConvectiveType.None) c1.CloudType = c2.CloudType;
                    model.CloudLayers.RemoveAt(i + 1);
                    i--;
                    quality.ParserNotes.Add("Merged overlapping thin cloud layers.");
                }
            }
        }

        // 7. Extractable Atmospheric Profile mapping
        int dir10k = 0, spd10k = 0, dir24k = 0, spd24k = 0, dir36k = 0, spd36k = 0;
        if (localWinds != null && localWinds.Layers.ContainsKey(100) && localWinds.Layers[100].Speed > 0) {
            dir10k = localWinds.Layers[100].Direction; spd10k = localWinds.Layers[100].Speed;
        } else if (globalCache != null && globalCache.Layers.ContainsKey(100)) {
            dir10k = globalCache.Layers[100].Direction; spd10k = globalCache.Layers[100].Speed;
        }
        
        if (localWinds != null && localWinds.Layers.ContainsKey(240) && localWinds.Layers[240].Speed > 0) {
            dir24k = localWinds.Layers[240].Direction; spd24k = localWinds.Layers[240].Speed;
        } else if (globalCache != null && globalCache.Layers.ContainsKey(240)) {
            dir24k = globalCache.Layers[240].Direction; spd24k = globalCache.Layers[240].Speed;
        }

        if (localWinds != null && localWinds.Layers.ContainsKey(360) && localWinds.Layers[360].Speed > 0) {
            dir36k = localWinds.Layers[360].Direction; spd36k = localWinds.Layers[360].Speed;
        } else if (globalCache != null && globalCache.Layers.ContainsKey(360)) {
            dir36k = globalCache.Layers[360].Direction; spd36k = globalCache.Layers[360].Speed;
        }

        model.Atmosphere.Layers[100] = new WindLayer { Direction = dir10k, Speed = spd10k };
        model.Atmosphere.Layers[240] = new WindLayer { Direction = dir24k, Speed = spd24k };
        model.Atmosphere.Layers[360] = new WindLayer { Direction = dir36k, Speed = spd36k };

        // 8. Universal Turbulence Indexing
        int shearLow = Math.Abs(spd10k - model.SurfaceWindSpd);
        int shearHigh = Math.Abs(spd36k - spd24k);
        model.TurbulenceIndex = shearHigh + (model.ConvectiveIndex / 2);
        model.TurbulenceOutlook = (shearHigh > 40 || spd36k > 100 || model.ConvectiveIndex > 60) ? "MODERATE/SEVERE" : (shearHigh > 20 || model.ConvectiveIndex > 30 ? "LIGHT" : "SMOOTH");

        if (model.IsClearSkies || model.CloudLayers.Count == 0)
        {
            model.IsClearSkies = true;
            model.CloudLayers.Clear();
            model.WeatherPhenomena.Clear(); 
        }

        return result;
    }
}

public class TrueSkyWeatherEncoder
{
    public string Encode(WeatherModel model, RendererProfile profile)
    {
        var tokens = new List<string> { "GLOB", model.Timestamp, model.WindToken };

        foreach (var kvp in model.Atmosphere.Layers)
        {
            if (kvp.Value.Speed > 0)
            {
                int altMeters = (int)Math.Round((kvp.Key * 100) * 0.3048);
                tokens.Add($"{kvp.Value.Direction:D3}{kvp.Value.Speed:D2}KT&A{altMeters}");
            }
        }

        int simConnectVis = Math.Min(10, model.PrevailingVisibilitySM);
        tokens.Add($"{simConnectVis}SM");

        foreach (var phenom in model.WeatherPhenomena)
        {
            string safePhenom = phenom.RawToken;
            if (phenom.Type == "DZ") safePhenom = safePhenom.Replace("DZ", "RA");
            
            if (safePhenom.Contains("TS") || safePhenom.Contains("RA") || safePhenom.Contains("SN") || safePhenom.Contains("FG"))
            {
                tokens.Add(safePhenom);
            }
        }

        if (model.IsClearSkies || model.CloudLayers.Count == 0)
        {
            tokens.Add("CLR");
        }
        else
        {
            bool cbUsed = false;
            var layers = model.CloudLayers.Take(profile.MaxCloudLayers).ToList();
            foreach (var layer in layers)
            {
                string modifier = layer.CloudType == ConvectiveType.None ? "" : layer.CloudType.ToString();
                if (profile.EnforceSingleConvectiveLayer && layer.IsConvective)
                {
                    if (!cbUsed) cbUsed = true;
                    else modifier = "";
                }
                
                string coverageStr = layer.Coverage == CloudCoverage.VV ? "OVC" : layer.Coverage.ToString();
                tokens.Add($"{coverageStr}{layer.BaseElevationMSL:D3}{modifier}");
            }
        }

        tokens.Add($"{model.TempC.ToString("D2").Replace("-", "M")}/{model.DewpointC.ToString("D2").Replace("-", "M")}");
        tokens.Add(model.AltimeterToken);

        string final = string.Join(" ", tokens);
        if (final.Contains(" RMK ")) final = final.Substring(0, final.IndexOf(" RMK "));
        return System.Text.RegularExpressions.Regex.Replace(final, @"\s+", " ").Trim();
    }
}

public class LegacyWeatherEncoder
{
    public string Encode(WeatherModel model, RendererProfile profile)
    {
        // Generates the exact same base string but strictly limits CB modifiers for old sprite clouds if desired.
        // For now, it delegates identically to ensure parity, but is fully isolated for future legacy tweaks.
        var trueSkyFallback = new TrueSkyWeatherEncoder();
        return trueSkyFallback.Encode(model, profile);
    }
}
}