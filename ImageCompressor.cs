using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ErrorChecker
{
    public class ImageCompressor
    {
        private const long MAX_IMAGE_SIZE = 1024 * 1024; // 1MB
        private const int JPEG_QUALITY = 75;

        public async Task CompressAndSaveAsync(Bitmap image, Stream outputStream)
        {
            await Task.Run(() =>
            {
                using var ms = new MemoryStream();
                SaveWithQuality(image, ms, JPEG_QUALITY);
                
                // Si l'image est trop grande, réduire la qualité jusqu'à atteindre la taille cible
                int quality = JPEG_QUALITY;
                while (ms.Length > MAX_IMAGE_SIZE && quality > 10)
                {
                    quality -= 5;
                    ms.SetLength(0);
                    SaveWithQuality(image, ms, quality);
                }

                ms.Position = 0;
                ms.CopyTo(outputStream);
            });
        }

        public async Task<BitmapImage> LoadCompressedImageAsync(Stream imageStream)
        {
            return await Task.Run(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = imageStream;
                bitmap.EndInit();
                bitmap.Freeze(); // Important pour les performances
                return bitmap;
            });
        }

        private void SaveWithQuality(Bitmap image, Stream stream, int quality)
        {
            var encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            
            // Trouver l'encodeur JPEG
            var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
            if (jpegEncoder == null)
            {
                // Si pas d'encodeur JPEG, utiliser PNG par défaut
                image.Save(stream, ImageFormat.Png);
                return;
            }

            image.Save(stream, jpegEncoder, encoderParameters);
        }

        private ImageCodecInfo GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }
    }
}
