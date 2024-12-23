using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ErrorChecker.Imaging
{
    public class ImageCompressor
    {
        private readonly ImageCodecInfo jpegEncoder;
        private readonly EncoderParameters encoderParams;

        public ImageCompressor(int quality = 50)
        {
            jpegEncoder = GetEncoder(ImageFormat.Jpeg);
            encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        }

        private ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            return Array.Find(codecs, codec => codec.FormatID == format.Guid)
                ?? throw new ArgumentException("JPEG encoder not found");
        }

        public byte[] CompressImage(Bitmap image)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                // Redimensionner l'image si elle est trop grande
                using (Bitmap resized = ResizeImage(image))
                {
                    // Convertir en niveaux de gris pour réduire la taille
                    using (Bitmap grayscale = ConvertToGrayscale(resized))
                    {
                        grayscale.Save(ms, jpegEncoder, encoderParams);
                    }
                }
                return ms.ToArray();
            }
        }

        private Bitmap ResizeImage(Bitmap image)
        {
            // Calculer les nouvelles dimensions en maintenant le ratio
            const int MAX_DIMENSION = 1024;
            double ratio = Math.Min((double)MAX_DIMENSION / image.Width, (double)MAX_DIMENSION / image.Height);
            
            if (ratio >= 1) return new Bitmap(image);

            int newWidth = (int)(image.Width * ratio);
            int newHeight = (int)(image.Height * ratio);

            Bitmap resized = new Bitmap(newWidth, newHeight);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, 0, 0, newWidth, newHeight);
            }

            return resized;
        }

        private Bitmap ConvertToGrayscale(Bitmap original)
        {
            Bitmap grayscale = new Bitmap(original.Width, original.Height);

            using (Graphics g = Graphics.FromImage(grayscale))
            {
                ColorMatrix colorMatrix = new ColorMatrix(
                    new float[][]
                    {
                        new float[] {.3f, .3f, .3f, 0, 0},
                        new float[] {.59f, .59f, .59f, 0, 0},
                        new float[] {.11f, .11f, .11f, 0, 0},
                        new float[] {0, 0, 0, 1, 0},
                        new float[] {0, 0, 0, 0, 1}
                    });

                using (ImageAttributes attributes = new ImageAttributes())
                {
                    attributes.SetColorMatrix(colorMatrix);
                    g.DrawImage(original,
                        new Rectangle(0, 0, original.Width, original.Height),
                        0, 0, original.Width, original.Height,
                        GraphicsUnit.Pixel,
                        attributes);
                }
            }

            return grayscale;
        }
    }
}
