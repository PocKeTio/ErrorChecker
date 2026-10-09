using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using ErrorChecker.Core;

namespace ErrorChecker.Capture
{
    // Transforme les captures successives en images différentielles : seules les zones modifiées
    // sont compressées (JPEG, ou PNG pour un texte parfaitement net) et envoyées.
    public sealed class ScreenEncoder
    {
        private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        private byte[]? previous;
        private byte[]? current;
        private Point lastCursor = new(int.MinValue, int.MinValue);

        // La prochaine image sera complète (dépanneur qui arrive, changement d'écran ou de qualité).
        public void Reset() => previous = null;

        // Renvoie null si rien n'a bougé depuis l'appel précédent.
        public ScreenFrame? Encode(Bitmap screenshot, Point cursor, int quality)
        {
            var bounds = new Rectangle(0, 0, screenshot.Width, screenshot.Height);
            var data = screenshot.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride, length = stride * data.Height;
            if (current?.Length != length) current = new byte[length];
            Marshal.Copy(data.Scan0, current, 0, length);
            screenshot.UnlockBits(data);

            var areas = TileDiff.DirtyAreas(previous, current, bounds.Width, bounds.Height, stride);
            (previous, current) = (current, previous);
            if (areas.Count == 0 && cursor == lastCursor) return null;
            lastCursor = cursor;

            var patches = areas.Select(a => EncodeArea(screenshot, a, quality)).ToArray();
            return new ScreenFrame(bounds.Width, bounds.Height, cursor.X, cursor.Y, patches);
        }

        private static Patch EncodeArea(Bitmap screenshot, Area a, int quality)
        {
            using var part = screenshot.Clone(new Rectangle(a.X, a.Y, a.W, a.H), PixelFormat.Format24bppRgb);
            using var ms = new MemoryStream();
            if (quality <= 0)
                part.Save(ms, ImageFormat.Png);
            else
            {
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                part.Save(ms, Jpeg, parameters);
            }
            return new Patch(a.X, a.Y, a.W, a.H, ms.ToArray());
        }
    }
}
