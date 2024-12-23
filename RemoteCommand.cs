using System;
using System.Text.Json;
using System.Windows.Input;

namespace ErrorChecker
{
    public class RemoteCommand
    {
        public enum CommandType
        {
            MouseClick,
            KeyPress
        }

        public CommandType Type { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Button { get; set; }  // 0 = gauche, 1 = droit, 2 = milieu
        public bool IsDoubleClick { get; set; }
        public string KeyChar { get; set; }  // Le caractère à envoyer
        public Key SpecialKey { get; set; }  // Pour les touches spéciales
        public ModifierKeys Modifiers { get; set; }  // Modificateurs (Alt, Ctrl, Shift, Win)
    }
}
