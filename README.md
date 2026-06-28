# SkyNexus | P3D Real-Time Weather Engine

SkyNexus is a modern, high-performance real-time weather injection engine built for Prepar3D (P3D). Designed with a sleek dark-theme UI and advanced data routing, it seamlessly bridges live terrestrial and atmospheric weather data into your simulator via SimConnect.

By pulling data from standard aviation channels (NOAA/VATSIM) and upper-level GRIB models (Open-Meteo), SkyNexus acts as a fully-fledged virtual dispatch and meteorology center for flight simulation.

## 🚧 Project Status: Massive Work in Progress

Please note that SkyNexus is currently under active, heavy development. While the core engine routing (live METARs, flight plan parsing, and Open-Meteo winds aloft grids) is highly functional, the application as a whole is still a **massive work in progress**. 

You may encounter bugs, unhandled exceptions, unoptimized code paths, or UI elements labeled "Coming Soon." The internal architecture and feature set are subject to rapid and significant changes. Please use this software at your own discretion, and feel free to open an issue if you spot something broken!

## ✨ Key Features

* **SimBrief Integration:** One-click import of your operational flight plan (OFP). Automatically parses your route, altitudes, and alternate waypoints.
* **Interactive Meteorology Map:** A dedicated, Leaflet-powered layered map engine that renders your magenta flight route and dynamically plots weather stations along your path.
* **Live METAR Tracking & VFR/IFR Symbology:** Automatically calculates flight categories (VFR, MVFR, IFR, LIFR) and color-codes enroute airport markers. 
* **Dynamic Winds Aloft Grid:** Generates a dense, multi-level atmospheric grid (FL100, FL240, FL360) projecting accurate wind barbs over your flight path using live Open-Meteo batch data.
* **Smart Injection Logic:** Prevents API spamming by evaluating terrestrial bounds (50 NM snap-to-station) and generates synthetic meteorological data for oceanic crossing segments.

---

## ⚠️ Important Notice: Source Code Only

This repository contains the **raw source code** for SkyNexus. 
There is no direct `.exe` file included in this repository. To use this application, you must either run it via the .NET CLI for development or compile/publish it into an executable yourself. 

## 🛠️ Prerequisites

To run or build this project, you will need the following installed on your machine:
* [.NET SDK](https://dotnet.microsoft.com/download) (Version 6.0 or higher)
* [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (Required for the interactive map UI)
* Prepar3D (for SimConnect integration)

---
## 📦 Publishing the Application (Creating the .exe)
To compile the source code into a standalone, runnable executable file (.exe) that you can double-click and use normally
Open your terminal or command prompt. Navigate to the P3DWeatherEngineGUI folder.

Run the following publish command. This bundles the app for 64-bit Windows:
**dotnet publish -c Release -r win-x64 --self-contained false**

(Note: Change --self-contained false to true if you want to bundle the .NET runtime with the app, which makes the file size larger but doesn't require users to install .NET).

Locate your .exe: Once the build completes successfully, navigate to:
*C:\Users\<User>\Documents\<User>\P3DWeatherEngine\P3DWeatherEngineGUI\bin\Release\net8.0-windows\win-x64\publish*
Inside this folder, you will find SkyNexus.exe (or your configured executable name) along with its required .dll files. You can move this entire folder anywhere on your PC to use the weather engine!
