using System;
using System.IO;
using System.Windows;

namespace P3DWeatherEngineGUI
{
    public partial class App : Application
    {
        static App()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                // This writes to a file in the folder where your .exe is located
                File.WriteAllText("crash_log.txt", e.ExceptionObject.ToString());
            };
        }
    }
}