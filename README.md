# SkyNexus 2.6 | P3D Real-Time Weather Engine

SkyNexus is an advanced, high-performance real-time weather injection engine engineered for Prepar3D (P3D). Designed with a sleek dark-theme user interface and sophisticated data routing, it seamlessly bridges live terrestrial and atmospheric weather data into the simulator via SimConnect.

By aggregating data from standard aviation channels (NOAA/VATSIM) and upper-level GRIB models (Open-Meteo), SkyNexus acts as a fully-fledged virtual dispatch and meteorology center for flight simulation.

##  Project Status: WIP

SkyNexus is currently under active development. The core engine routing, flight plan parsing, and atmospheric modeling are highly functional, but the application remains a work in progress.

With the recent introduction of the SkyGrid spatial hashing architecture and dynamic station generation, the internal data pipeline has undergone significant upgrades. Users may encounter edge-case bugs, unhandled exceptions, or unoptimized code paths. The feature set is subject to rapid iteration.

##  Key Features

SkyGrid Spatial Hashing Architecture: Utilizes a progressive streaming window that dynamically generates a route-centric weather corridor. The engine continuously loads active weather cells up to 600 NM ahead of the aircraft and automatically discards stale cells 300 NM behind, ensuring a minimal memory footprint even on ultra-long-haul flights.

Dynamic Station Generation: Automatically builds missing regional airports and custom oceanic weather cells directly within Prepar3D's memory using C# Reflection and the WeatherCreateStation API.

Advanced Meteorological Parsing: Intelligently merges terrestrial METAR data with Open-Meteo API forecasts. The engine calculates precise relative humidity, enforces accurate MSL/AGL cloud layer conversions, maps WMO weather codes to aviation precipitation, and generates heavily sanitized injection strings to prevent TrueSky rendering errors.

SimBrief Integration: One-click import of operational flight plans (OFP). Automatically parses the primary route, cruise altitudes, and alternate waypoints to feed the spatial grid.

Interactive Meteorology Map: A dedicated, Leaflet-powered layered map engine that renders the flight route, tracks the aircraft position in real-time, and dynamically plots both established METAR stations and actively streaming SkyGrid pseudo-cells.

Dynamic Winds Aloft Engine: Generates a dense, multi-level atmospheric profile (FL100, FL240, FL360) directly mapped to the localized weather cells.
---

## Important Notice: Source Code Only

This repository contains the raw source code for SkyNexus. There is no pre-compiled executable included. To use this application, you must run it via the .NET CLI or compile it into a standalone executable. 

## -------------------------------------------------------- INSTALLATION INSTRUCTIONS ----------------------------------------------------------------------------
To download the source code, make sure to install the 'P3DWeatherEngineGUI' as a .zip file then follow the instructions listed below.

## Prerequisites

To run or build this project, you will need the following installed on your machine:
* [.NET SDK](https://dotnet.microsoft.com/download) (Version 6.0 or higher)
* [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (Required for the interactive map UI)
* Prepar3D V5(.1 to .4)(for SimConnect integration) NOTE: OTHER VERSIONS OF P3D ARE NOT TESTED IN ANY WAY, FEEL FREE TO DO SO AT YOUR OWN RISK.

---
##  Publishing the Application (Creating the .exe)
To compile the source code into a standalone, runnable executable file (.exe), follow these steps:

-Open your terminal or command prompt and navigate to the project directory as shown above.

-Run the following publish command to bundle the application for 64-bit Windows:
*dotnet publish -c Release -r win-x64 --self-contained false*
(Note: Change --self-contained false to true if you wish to bundle the .NET runtime directly into the application. This increases the total file size but eliminates the need for the end-user to install .NET separately).

-Once the build process completes successfully, navigate to the output directory to locate your executable:

*C:\Users\(User)\Documents\(User)\P3DWeatherEngine\P3DWeatherEngineGUI\bin\Release\net8.0-windows\win-x64\publish*
(NOTE: If u cant find it in the location given above, the donet run publish command will return the final build path, just copy paste it onto your windows explorer)
Inside this folder, you will find SkyNexus.exe along with its required .dll files. You can move this entire folder to any location on your system to use the weather engine.
