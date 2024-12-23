using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;

namespace ErrorChecker
{
    public class ScreenInfo
    {
        public Screen Screen { get; set; }
        public string Name { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }

    public class ScreenManager
    {
        private Screen currentScreen;

        public ScreenManager()
        {
            currentScreen = Screen.PrimaryScreen;
        }

        public List<ScreenInfo> GetScreens()
        {
            return Screen.AllScreens.Select((screen, index) => new ScreenInfo 
            { 
                Screen = screen,
                Name = $"Écran {index + 1} ({screen.Bounds.Width}x{screen.Bounds.Height})"
            }).ToList();
        }

        public void SelectScreen(ScreenInfo screenInfo)
        {
            currentScreen = screenInfo?.Screen ?? Screen.PrimaryScreen;
        }

        public Rectangle GetCurrentScreenBounds()
        {
            return currentScreen.Bounds;
        }

        public Bitmap CaptureScreen()
        {
            Rectangle bounds = currentScreen.Bounds;
            Bitmap screenshot = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics g = Graphics.FromImage(screenshot))
            {
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
            }
            return screenshot;
        }
    }
}
