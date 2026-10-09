using System.Runtime.InteropServices;
using System.Windows.Input;
using ErrorChecker.Core;

namespace ErrorChecker.Input
{
    // Rejoue chez l'utilisateur les actions du dépanneur, via SendInput.
    // Limites de Windows : Ctrl+Alt+Suppr, l'écran UAC et les fenêtres lancées en administrateur
    // ne peuvent pas être pilotés par une application normale.
    public static class InputInjector
    {
        private static readonly HashSet<int> pressedButtons = new();
        private static ModifierKeys heldModifiers; // tenus pendant un clic ou un glisser (Ctrl+clic, Maj+glisser...)

        // Touche avec ses modificateurs. Ex. Maj+F8 : Maj enfoncée, F8 enfoncée puis relâchée, Maj relâchée.
        public static void Key(int vk, ModifierKeys modifiers)
        {
            var mods = ModifierVks(modifiers);
            var inputs = mods.Select(m => KeyInput(m, up: false)).ToList();
            inputs.Add(KeyInput((ushort)vk, up: false));
            inputs.Add(KeyInput((ushort)vk, up: true));
            inputs.AddRange(Enumerable.Reverse(mods).Select(m => KeyInput(m, up: true)));
            Send(inputs);
        }

        // Texte tapé tel quel, quelle que soit la disposition du clavier (saisie Unicode).
        public static void Text(string text)
        {
            var inputs = new List<INPUT>();
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c is '\n' or '\t')
                {
                    ushort vk = c == '\n' ? VK_RETURN : VK_TAB;
                    inputs.Add(KeyInput(vk, up: false));
                    inputs.Add(KeyInput(vk, up: true));
                    continue;
                }
                inputs.Add(KeyEvent(0, c, KEYEVENTF_UNICODE));
                inputs.Add(KeyEvent(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }
            Send(inputs);
        }

        // x, y : coordonnées écran en pixels physiques. modifiers : tenus pendant le clic, le glisser ou la molette.
        public static void Mouse(MouseKind action, int x, int y, int value, ModifierKeys modifiers)
        {
            SetCursorPos(x, y);
            lock (pressedButtons)
            {
                bool dragging = pressedButtons.Count > 0;
                // Simple survol : rien à tenir (sinon Ctrl serait pressé/relâché à chaque mouvement).
                var inputs = action == MouseKind.Move && !dragging ? new List<INPUT>() : SyncModifiers(modifiers);
                switch (action)
                {
                    case MouseKind.Down:
                        pressedButtons.Add(value);
                        inputs.Add(MouseButton(value, down: true));
                        break;
                    case MouseKind.Up:
                        pressedButtons.Remove(value);
                        inputs.Add(MouseButton(value, down: false));
                        break;
                    case MouseKind.Wheel:
                        inputs.Add(MouseEvent(MOUSEEVENTF_WHEEL, unchecked((uint)value)));
                        break;
                }
                if (pressedButtons.Count == 0) inputs.AddRange(SyncModifiers(ModifierKeys.None));
                Send(inputs);
            }
        }

        // Fin de session : ne jamais laisser un bouton ni une touche enfoncés chez l'utilisateur.
        public static void ReleaseMouseButtons()
        {
            lock (pressedButtons)
            {
                var inputs = pressedButtons.Select(b => MouseButton(b, down: false)).ToList();
                pressedButtons.Clear();
                inputs.AddRange(SyncModifiers(ModifierKeys.None));
                Send(inputs);
            }
        }

        // Enfonce/relâche ce qui diffère entre les modificateurs tenus et ceux demandés.
        private static List<INPUT> SyncModifiers(ModifierKeys wanted)
        {
            var inputs = ModifierVks(wanted & ~heldModifiers).Select(vk => KeyInput(vk, up: false)).ToList();
            inputs.AddRange(ModifierVks(heldModifiers & ~wanted).Select(vk => KeyInput(vk, up: true)));
            heldModifiers = wanted;
            return inputs;
        }

        private static List<ushort> ModifierVks(ModifierKeys modifiers)
        {
            var vks = new List<ushort>();
            if (modifiers.HasFlag(ModifierKeys.Control)) vks.Add(VK_CONTROL);
            if (modifiers.HasFlag(ModifierKeys.Alt)) vks.Add(VK_MENU);
            if (modifiers.HasFlag(ModifierKeys.Shift)) vks.Add(VK_SHIFT);
            if (modifiers.HasFlag(ModifierKeys.Windows)) vks.Add(VK_LWIN);
            return vks;
        }

        // Boutons inversés (gaucher) : SendInput parle de boutons physiques, Windows applique l'inversion.
        private static INPUT MouseButton(int button, bool down)
        {
            if (GetSystemMetrics(SM_SWAPBUTTON) != 0 && button is 0 or 1) button = 1 - button;
            return MouseEvent(button switch
            {
                1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
                2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
                _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
            }, 0);
        }

        private static INPUT MouseEvent(uint flags, uint data) =>
            new() { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } } };

        private static INPUT KeyInput(ushort vk, bool up) =>
            KeyEvent(vk, (ushort)MapVirtualKey(vk, 0), (up ? KEYEVENTF_KEYUP : 0) | (ExtendedKeys.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0));

        private static INPUT KeyEvent(ushort vk, ushort scan, uint flags) =>
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };

        private static void Send(List<INPUT> inputs)
        {
            if (inputs.Count > 0) SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }

        // Touches « étendues » : sans ce drapeau, les flèches, Inser, Suppr... sont lues comme le pavé numérique.
        private static readonly HashSet<ushort> ExtendedKeys = new()
        {
            0x03, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5
        };

        private const ushort VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;
        private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_SWAPBUTTON = 23;
    }
}
