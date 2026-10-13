using System.Drawing;
using OptimPdf.Core.Editing;

namespace OptimPdf.Core.Tests;

static class TestImages
{
    public static byte[] Solid(int width, int height, Color color, ImageFileFormat format = ImageFileFormat.Png, int quality = 90)
    {
        using var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp)) g.Clear(color);
        return ImageCodec.Encode(bmp, format, quality);
    }

    /// <summary>A photo-like image (smooth gradients plus fine noise) that compresses poorly as PNG.</summary>
    public static Bitmap Photo(int width, int height, int seed = 1)
    {
        var rng = new Random(seed);
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        var row = new byte[data.Stride];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int n = rng.Next(-24, 25);
                row[x * 3] = (byte)Math.Clamp(60 + 120 * x / width + n, 0, 255);
                row[x * 3 + 1] = (byte)Math.Clamp(90 + 100 * y / height + n, 0, 255);
                row[x * 3 + 2] = (byte)Math.Clamp(200 - 80 * (x + y) / (width + height) + n, 0, 255);
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
        }
        bmp.UnlockBits(data);
        return bmp;
    }

    public static byte[] PhotoBytes(int width, int height, ImageFileFormat format, int quality = 95)
    {
        using var bmp = Photo(width, height);
        return ImageCodec.Encode(bmp, format, quality);
    }
}
