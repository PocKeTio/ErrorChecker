using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Windows.Input;

namespace ErrorChecker
{
    public class InputSimulator
    {
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public static void SimulateMouseMove(int x, int y)
        {
            SetCursorPos(x, y);
        }

        public static void SimulateMouseDoubleClick(int button)
        {
            SimulateMouseClick(button);
            SimulateMouseClick(button);
        }

        public static void SimulateMouseClick(int button)
        {
            uint downFlag, upFlag;
            switch (button)
            {
                case 1: // Right click
                    downFlag = MOUSEEVENTF_RIGHTDOWN;
                    upFlag = MOUSEEVENTF_RIGHTUP;
                    break;
                case 2: // Middle click
                    downFlag = MOUSEEVENTF_MIDDLEDOWN;
                    upFlag = MOUSEEVENTF_MIDDLEUP;
                    break;
                default: // Left click (0) ou valeur par défaut
                    downFlag = MOUSEEVENTF_LEFTDOWN;
                    upFlag = MOUSEEVENTF_LEFTUP;
                    break;
            }

            mouse_event(downFlag, 0, 0, 0, 0);
            System.Threading.Thread.Sleep(50); // Délai naturel entre down et up
            mouse_event(upFlag, 0, 0, 0, 0);
        }

        public static void SimulateKeyPress(int keyCode)
        {
            keybd_event((byte)keyCode, 0, KEYEVENTF_KEYDOWN, 0);
            System.Threading.Thread.Sleep(50); // Délai naturel entre down et up
            keybd_event((byte)keyCode, 0, KEYEVENTF_KEYUP, 0);
        }

        public static void SimulateKeyChar(char c)
        {
            short vkey = VkKeyScan(c);
            byte scanCode = (byte)((vkey >> 8) & 0xFF);
            byte virtualKey = (byte)(vkey & 0xFF);

            if (vkey != -1)
            {
                // Appuyer sur la touche
                keybd_event(virtualKey, scanCode, 0, 0);
                // Relâcher la touche
                keybd_event(virtualKey, scanCode, KEYEVENTF_KEYUP, 0);
            }
        }

        public static void SimulateSpecialKey(Key key, ModifierKeys modifiers)
        {
            var keyCode = KeyInterop.VirtualKeyFromKey(key);
            var modifierKeys = new List<byte>();

            // Préparer les modificateurs
            if (modifiers.HasFlag(ModifierKeys.Alt))
                modifierKeys.Add(0x12); // VK_ALT
            if (modifiers.HasFlag(ModifierKeys.Control))
                modifierKeys.Add(0x11); // VK_CONTROL
            if (modifiers.HasFlag(ModifierKeys.Shift))
                modifierKeys.Add(0x10); // VK_SHIFT
            if (modifiers.HasFlag(ModifierKeys.Windows))
                modifierKeys.Add(0x5B); // VK_LWIN

            try
            {
                // Appuyer sur les modificateurs
                foreach (var mod in modifierKeys)
                {
                    keybd_event(mod, 0, 0, 0);
                }

                // Appuyer et relâcher la touche principale
                keybd_event((byte)keyCode, 0, 0, 0);
                keybd_event((byte)keyCode, 0, KEYEVENTF_KEYUP, 0);

                // Relâcher les modificateurs dans l'ordre inverse
                foreach (var mod in modifierKeys.AsEnumerable().Reverse())
                {
                    keybd_event(mod, 0, KEYEVENTF_KEYUP, 0);
                }
            }
            catch (Exception)
            {
                // En cas d'erreur, s'assurer que tous les modificateurs sont relâchés
                foreach (var mod in modifierKeys)
                {
                    keybd_event(mod, 0, KEYEVENTF_KEYUP, 0);
                }
            }
        }
    }
}
