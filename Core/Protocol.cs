using System.IO;
using System.Text;

namespace ErrorChecker.Core
{
    // Messages échangés entre l'utilisateur (canal u2h) et le dépanneur (canal h2u).
    public abstract record Msg;
    public sealed record Ping(long Ticks) : Msg;
    public sealed record Pong(long Ticks) : Msg;
    public sealed record Bye(string Reason) : Msg;
    public sealed record Join(string Helper) : Msg;
    public sealed record Accept(int Primary, string[] Screens) : Msg;
    public sealed record Refuse : Msg;
    // Quality : 0 = PNG (net, sans perte), sinon qualité JPEG 1-100.
    public sealed record Settings(int Fps, int Quality, int Screen) : Msg;
    // Seq : numéro acquitté par le dépanneur (Ack) une fois l'image affichée.
    // LagMs : délai capture -> affichage mesuré côté utilisateur sur les images précédentes.
    // Moves (défilements) s'appliquent à l'image précédente, avant les Patches.
    public sealed record ScreenFrame(int Seq, int Width, int Height, int CursorX, int CursorY, int LagMs, Move[] Moves, Patch[] Patches) : Msg;
    public sealed record Patch(int X, int Y, int W, int H, byte[] Data);
    // Lignes [Y - Dy, Y - Dy + H) recopiées en [Y, Y + H), entre les colonnes X et X + W.
    public readonly record struct Move(int X, int Y, int W, int H, int Dy);
    public sealed record Ack(int Seq) : Msg;
    // Modifiers : valeur de System.Windows.Input.ModifierKeys.
    public sealed record KeyStroke(int Vk, int Modifiers) : Msg;
    public sealed record TextInput(string Text) : Msg;
    // Value : bouton (0 gauche, 1 droit, 2 milieu) ou delta de molette.
    // Modifiers (ModifierKeys) : Ctrl+clic, Maj+clic, Ctrl+molette...
    public sealed record MouseInput(MouseKind Action, int X, int Y, int Value, int Modifiers) : Msg;
    public enum MouseKind : byte { Move, Down, Up, Wheel }

    public static class Protocol
    {
        private enum T : byte { Ping = 1, Pong, Bye, Join, Accept, Refuse, Settings, ScreenFrame, Key, Text, Mouse, Ack }

        public static byte[] Encode(Msg msg)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            switch (msg)
            {
                case Ping p: w.Write((byte)T.Ping); w.Write(p.Ticks); break;
                case Pong p: w.Write((byte)T.Pong); w.Write(p.Ticks); break;
                case Bye b: w.Write((byte)T.Bye); w.Write(b.Reason); break;
                case Join j: w.Write((byte)T.Join); w.Write(j.Helper); break;
                case Accept a:
                    w.Write((byte)T.Accept); w.Write(a.Primary); w.Write(a.Screens.Length);
                    foreach (var s in a.Screens) w.Write(s);
                    break;
                case Refuse: w.Write((byte)T.Refuse); break;
                case Settings s: w.Write((byte)T.Settings); w.Write(s.Fps); w.Write(s.Quality); w.Write(s.Screen); break;
                case ScreenFrame f:
                    w.Write((byte)T.ScreenFrame); w.Write(f.Seq); w.Write(f.Width); w.Write(f.Height); w.Write(f.CursorX); w.Write(f.CursorY); w.Write(f.LagMs);
                    w.Write(f.Moves.Length);
                    foreach (var m in f.Moves) { w.Write(m.X); w.Write(m.Y); w.Write(m.W); w.Write(m.H); w.Write(m.Dy); }
                    w.Write(f.Patches.Length);
                    foreach (var r in f.Patches)
                    {
                        w.Write(r.X); w.Write(r.Y); w.Write(r.W); w.Write(r.H);
                        w.Write(r.Data.Length); w.Write(r.Data);
                    }
                    break;
                case KeyStroke k: w.Write((byte)T.Key); w.Write(k.Vk); w.Write(k.Modifiers); break;
                case TextInput t: w.Write((byte)T.Text); w.Write(t.Text); break;
                case MouseInput m: w.Write((byte)T.Mouse); w.Write((byte)m.Action); w.Write(m.X); w.Write(m.Y); w.Write(m.Value); w.Write(m.Modifiers); break;
                case Ack a: w.Write((byte)T.Ack); w.Write(a.Seq); break;
                default: throw new ArgumentException($"Message inconnu : {msg.GetType().Name}");
            }
            w.Flush();
            return ms.ToArray();
        }

        public static Msg Decode(byte[] data)
        {
            using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            return (T)r.ReadByte() switch
            {
                T.Ping => new Ping(r.ReadInt64()),
                T.Pong => new Pong(r.ReadInt64()),
                T.Bye => new Bye(r.ReadString()),
                T.Join => new Join(r.ReadString()),
                T.Accept => new Accept(r.ReadInt32(), ReadStrings(r)),
                T.Refuse => new Refuse(),
                T.Settings => new Settings(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
                T.ScreenFrame => new ScreenFrame(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), ReadMoves(r), ReadPatches(r)),
                T.Key => new KeyStroke(r.ReadInt32(), r.ReadInt32()),
                T.Text => new TextInput(r.ReadString()),
                T.Mouse => new MouseInput((MouseKind)r.ReadByte(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
                T.Ack => new Ack(r.ReadInt32()),
                var t => throw new InvalidDataException($"Type de message inconnu : {t}")
            };
        }

        private static string[] ReadStrings(BinaryReader r)
        {
            var result = new string[r.ReadInt32()];
            for (int i = 0; i < result.Length; i++) result[i] = r.ReadString();
            return result;
        }

        private static Move[] ReadMoves(BinaryReader r)
        {
            var result = new Move[r.ReadInt32()];
            for (int i = 0; i < result.Length; i++) result[i] = new Move(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            return result;
        }

        private static Patch[] ReadPatches(BinaryReader r)
        {
            var result = new Patch[r.ReadInt32()];
            for (int i = 0; i < result.Length; i++)
                result[i] = new Patch(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadBytes(r.ReadInt32()));
            return result;
        }
    }
}
