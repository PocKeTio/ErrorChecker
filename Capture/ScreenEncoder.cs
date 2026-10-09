using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using ErrorChecker.Core;

namespace ErrorChecker.Capture
{
    // Partie Windows de l'encodage : copie de la capture GDI, et JPEG/PNG (GDI+) pour les zones
    // trop riches en couleurs pour la palette. Le reste (diff, défilement, affinage) est dans FrameEncoder.
    public sealed class ScreenEncoder
    {
        private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        private readonly FrameEncoder frames = new(EncodeRichArea);

        public void Reset() => frames.Reset();

        public ScreenFrame? Encode(Bitmap screenshot, Point cursor, int quality, bool reduceColors, int seq, int lagMs)
        {
            var data = screenshot.LockBits(new Rectangle(0, 0, screenshot.Width, screenshot.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                return frames.Encode(screenshot.Width, screenshot.Height, data.Stride,
                    buffer => Marshal.Copy(data.Scan0, buffer, 0, buffer.Length), cursor.X, cursor.Y, quality, reduceColors, seq, lagMs);
            }
            finally { screenshot.UnlockBits(data); }
        }

        private static unsafe (byte[], bool) EncodeRichArea(byte[] pixels, int stride, Area a, int quality)
        {
            using var ms = new MemoryStream();
            fixed (byte* p = pixels)
            {
                using var bitmap = new Bitmap(a.W, a.H, stride, PixelFormat.Format32bppRgb, (IntPtr)(p + a.Y * stride + a.X * 4));
                if (quality <= 0)
                {
                    using var rgb = bitmap.Clone(new Rectangle(0, 0, a.W, a.H), PixelFormat.Format24bppRgb);
                    rgb.Save(ms, ImageFormat.Png);
                }
                else
                {
                    using var parameters = new EncoderParameters(1);
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                    bitmap.Save(ms, Jpeg, parameters);
                }
            }
            return (ms.ToArray(), quality > 0);
        }
    }
}
