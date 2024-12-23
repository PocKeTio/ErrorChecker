using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace ErrorChecker
{
    public class WindowInfo
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; }
        public string ProcessName { get; set; }

        public override string ToString()
        {
            return $"{Title} ({ProcessName})";
        }
    }

    public class WindowManager
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public List<WindowInfo> GetWindows()
        {
            var windows = new List<WindowInfo>();
            EnumWindows((hWnd, lParam) =>
            {
                // Vérifier si la fenêtre est valide
                if (!IsWindowValid(hWnd)) 
                    return true;

                var builder = new StringBuilder(256);
                GetWindowText(hWnd, builder, 256);
                string title = builder.ToString().Trim();

                if (string.IsNullOrWhiteSpace(title)) return true;

                try
                {
                    uint processId;
                    GetWindowThreadProcessId(hWnd, out processId);
                    var process = System.Diagnostics.Process.GetProcessById((int)processId);

                    // Vérifier si la fenêtre est réellement visible et utilisable
                    RECT rect;
                    GetWindowRect(hWnd, out rect);
                    if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return true;

                    // Ne pas inclure les fenêtres système ou masquées
                    if (!string.IsNullOrWhiteSpace(process.ProcessName) && 
                        !title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase) &&
                        !title.Equals("Windows Input Experience", StringComparison.OrdinalIgnoreCase))
                    {
                        windows.Add(new WindowInfo
                        {
                            Handle = hWnd,
                            Title = title,
                            ProcessName = process.ProcessName
                        });

                        System.Diagnostics.Debug.WriteLine($"Fenêtre trouvée : {title} ({process.ProcessName})");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Erreur lors de l'énumération de la fenêtre : {ex.Message}");
                }

                return true;
            }, IntPtr.Zero);

            System.Diagnostics.Debug.WriteLine($"Nombre total de fenêtres trouvées : {windows.Count}");
            return windows;
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public bool IsWindowValid(IntPtr handle)
        {
            return IsWindow(handle) && IsWindowVisible(handle) && IsWindowEnabled(handle);
        }

        public Bitmap CaptureWindow(IntPtr handle)
        {
            // Obtenir la taille de la zone client
            RECT clientRect;
            GetClientRect(handle, out clientRect);
            int width = clientRect.Right - clientRect.Left;
            int height = clientRect.Bottom - clientRect.Top;

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Fenêtre invalide");

            // Obtenir la position de la zone client à l'écran
            POINT clientPoint = new POINT { X = clientRect.Left, Y = clientRect.Top };
            ClientToScreen(handle, ref clientPoint);

            // Créer le bitmap de sortie
            var bitmap = new Bitmap(width, height);
            IntPtr dc = IntPtr.Zero;

            try
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    dc = graphics.GetHdc();
                    PrintWindow(handle, dc, 2); // 2 = PW_CLIENTONLY
                    graphics.ReleaseHdc(dc);
                }

                return bitmap;
            }
            catch
            {
                bitmap?.Dispose();
                throw;
            }
        }

        public Rectangle GetWindowBounds(IntPtr handle)
        {
            RECT rect;
            GetWindowRect(handle, out rect);
            return new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        public Point GetRelativeMousePosition(IntPtr handle, Point screenPoint)
        {
            RECT rect;
            GetWindowRect(handle, out rect);
            return new Point(screenPoint.X - rect.Left, screenPoint.Y - rect.Top);
        }

        public Point GetClientPoint(IntPtr handle)
        {
            POINT point = new POINT { X = 0, Y = 0 };
            ClientToScreen(handle, ref point);
            return new Point(point.X, point.Y);
        }

        public bool FocusWindow(IntPtr handle)
        {
            return SetForegroundWindow(handle);
        }
    }
}