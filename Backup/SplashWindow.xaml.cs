using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace P3DWeatherEngineGUI
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            
            // Inject a random image before the window even renders
            LoadRandomBackground();
            
            Loaded += SplashWindow_Loaded;
        }

        private void LoadRandomBackground()
        {
            try
            {
                // Target the "SplashScreens" directory next to the compiled executable
                string splashDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SplashScreens");
                
                if (Directory.Exists(splashDir))
                {
                    // Grab all valid image files
                    var files = Directory.GetFiles(splashDir)
                                         .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || 
                                                     f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                         .ToList();

                    if (files.Count > 0)
                    {
                        // Pick one at random
                        Random rnd = new Random();
                        string selectedFile = files[rnd.Next(files.Count)];
                        
                        // Load image into memory safely
                        BitmapImage bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad; // Prevents file-locking
                        bitmap.UriSource = new Uri(selectedFile, UriKind.Absolute);
                        bitmap.EndInit();
                        
                        // Paint the background
                        SplashBackgroundBrush.ImageSource = bitmap;
                    }
                }
            }
            catch 
            { 
                // Silently ignore errors (e.g., folder is missing or empty). 
                // The splash screen will gracefully fall back to its default dark gray theme.
            }
        }

        private async void SplashWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Give the user 5 seconds to appreciate the screenshot
            await Task.Delay(5000); 

            // Boot up the main engine
            MainWindow mainWindow = new MainWindow();
            mainWindow.Show();

            // Close the splash window
            this.Close();
        }
    }
}