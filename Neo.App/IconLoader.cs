using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Neo.App
{
    public static class IconLoader
    {
        public static ImageSource LoadImageSourceFromBytes(byte[] data, string? originalFileNameOrExtensionHint = null)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Icon bytes are empty.", nameof(data));

            if (LooksLikeIco(data, originalFileNameOrExtensionHint))
            {
                var ico = TryLoadIco(data);
                if (ico != null) return ico;
            }

            var bmp = TryLoadBitmap(data);
            if (bmp != null) return bmp;

            var ico2 = TryLoadIco(data);
            if (ico2 != null) return ico2;

            throw new NotSupportedException("Unsupported image/ICO format.");
        }

        private static bool LooksLikeIco(byte[] data, string? hint)
        {
            if (!string.IsNullOrWhiteSpace(hint) &&
                hint.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                return true;

            if (data.Length >= 4)
            {
                ushort reserved = BitConverter.ToUInt16(data, 0);
                ushort type = BitConverter.ToUInt16(data, 2);
                if (reserved == 0 && type == 1)
                    return true;
            }
            return false;
        }

        private static ImageSource? TryLoadIco(byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data, writable: false);
                var decoder = new IconBitmapDecoder(
                    ms,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                if (decoder.Frames == null || decoder.Frames.Count == 0)
                    return null;

                var best = PickBestFrame(decoder.Frames);
                return best;
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource? TryLoadBitmap(byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data, writable: false);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        // FIX: akzeptiert IList<BitmapFrame> (ReadOnlyCollection<BitmapFrame> implementiert das)
        private static BitmapFrame PickBestFrame(IList<BitmapFrame> frames)
        {
            var best = frames
                .Select(f => new
                {
                    Frame = f,
                    SizeScore = (f.PixelWidth == f.PixelHeight) ? f.PixelWidth : 0,
                    Bpp = f.Format.BitsPerPixel
                })
                .OrderByDescending(x => x.SizeScore)
                .ThenByDescending(x => x.Bpp)
                .Select(x => x.Frame)
                .First();

            best.Freeze();
            return best;
        }
    }
}
