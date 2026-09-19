using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using WorkplaceSaver.Data;

namespace WorkplaceSaver.Services
{
    public class ScreenshotService
    {
        public static string? CaptureDesktopThumbnail(string workspaceId)
        {
            try
            {
                Directory.CreateDirectory(DatabaseContext.ThumbnailsFolder);
                string targetPath = Path.Combine(DatabaseContext.ThumbnailsFolder, $"{workspaceId}.jpg");

                // Calculate virtual screen bounds spanning all monitors using physical pixels
                var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                int left = vs.Left;
                int top = vs.Top;
                int width = vs.Width;
                int height = vs.Height;

                if (width <= 0 || height <= 0)
                {
                    var primary = System.Windows.Forms.Screen.PrimaryScreen?.Bounds 
                        ?? new Rectangle(0, 0, 1920, 1080);
                    width = primary.Width;
                    height = primary.Height;
                    left = primary.Left;
                    top = primary.Top;
                }

                using var screenBitmap = new Bitmap(width, height);

                using (var g = Graphics.FromImage(screenBitmap))
                {
                    g.CopyFromScreen(left, top, 0, 0, screenBitmap.Size, CopyPixelOperation.SourceCopy);
                }

                // Scale down to a crisp, high-DPI thumbnail (e.g. 640x360 or proportionate)
                int thumbWidth = 640;
                int thumbHeight = (int)(thumbWidth * ((double)height / width));
                if (thumbHeight <= 0) thumbHeight = 360;

                using var thumbBitmap = new Bitmap(thumbWidth, thumbHeight);
                using (var gThumb = Graphics.FromImage(thumbBitmap))
                {
                    gThumb.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gThumb.SmoothingMode = SmoothingMode.HighQuality;
                    gThumb.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    gThumb.DrawImage(screenBitmap, 0, 0, thumbWidth, thumbHeight);
                }

                // Save with 85% JPEG quality for small size and fast loading
                var encoder = GetEncoder(ImageFormat.Jpeg);
                if (encoder != null)
                {
                    var encParams = new EncoderParameters(1);
                    encParams.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
                    thumbBitmap.Save(targetPath, encoder, encParams);
                }
                else
                {
                    thumbBitmap.Save(targetPath, ImageFormat.Jpeg);
                }

                return targetPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error capturing thumbnail: {ex.Message}");
                return null;
            }
        }

        public static string? ExtractIconBase64(string executablePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                    return null;

                using var icon = Icon.ExtractAssociatedIcon(executablePath);
                if (icon == null) return null;

                using var bitmap = icon.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
            catch
            {
                return null;
            }
        }

        private static ImageCodecInfo? GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageDecoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                    return codec;
            }
            return null;
        }
    }
}
