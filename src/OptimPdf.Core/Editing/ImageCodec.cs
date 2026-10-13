using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OptimPdf.Core.Rendering;
using PdfSharp.Drawing;

namespace OptimPdf.Core.Editing;

public enum ImageFileFormat { Png, Jpeg, Tiff, Bmp }

/// <summary>Image encoding/decoding through Windows Imaging (GDI+).</summary>
public static class ImageCodec
{
    /// <summary>
    /// Converts image bytes into something PDFsharp embeds well: JPEG data passes through unchanged
    /// (no generation loss); everything else is re-encoded losslessly as PNG (keeping transparency).
    /// </summary>
    public static XImage ToXImage(byte[] data)
    {
        if (IsJpeg(data)) return XImage.FromStream(new MemoryStream(data, writable: false));
        using var bitmap = Decode(data);
        return XImage.FromStream(new MemoryStream(Encode(bitmap, ImageFileFormat.Png)));
    }

    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static Bitmap Decode(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            using var image = Image.FromStream(ms);
            return Copy(image); // Detach from the stream.
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException("The image format is not supported or the file is damaged.");
        }
    }

    /// <summary>All frames of a multi-page image (TIFF/GIF); a single frame for other formats.</summary>
    public static List<Bitmap> DecodeFrames(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        Image image;
        try { image = Image.FromStream(ms); }
        catch (ArgumentException) { throw new InvalidDataException("The image format is not supported or the file is damaged."); }
        using (image)
        {
            var frames = new List<Bitmap>();
            var dimension = image.FrameDimensionsList.Length > 0 ? new FrameDimension(image.FrameDimensionsList[0]) : FrameDimension.Page;
            int count = image.GetFrameCount(dimension);
            for (int i = 0; i < count; i++)
            {
                image.SelectActiveFrame(dimension, i);
                frames.Add(Copy(image));
            }
            return frames;
        }
    }

    /// <summary>A detached copy that keeps the image's DPI (the Bitmap copy constructor resets it to 96).</summary>
    static Bitmap Copy(Image image)
    {
        var copy = new Bitmap(image);
        if (image.HorizontalResolution > 0 && image.VerticalResolution > 0)
            copy.SetResolution(image.HorizontalResolution, image.VerticalResolution);
        return copy;
    }

    public static byte[] Encode(Image image, ImageFileFormat format, int jpegQuality = 85)
    {
        using var ms = new MemoryStream();
        if (format == ImageFileFormat.Jpeg)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(jpegQuality, 1, 100));
            image.Save(ms, codec, parameters);
        }
        else
        {
            image.Save(ms, format switch
            {
                ImageFileFormat.Tiff => ImageFormat.Tiff,
                ImageFileFormat.Bmp => ImageFormat.Bmp,
                _ => ImageFormat.Png,
            });
        }
        return ms.ToArray();
    }

    /// <summary>Saves several bitmaps as one multi-page TIFF (LZW compressed).</summary>
    public static byte[] EncodeMultiPageTiff(IReadOnlyList<Bitmap> pages)
    {
        if (pages.Count == 0) throw new ArgumentException("No pages.", nameof(pages));
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Tiff.Guid);
        using var ms = new MemoryStream();
        using var parameters = new EncoderParameters(2);
        parameters.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
        parameters.Param[1] = new EncoderParameter(Encoder.Compression, (long)EncoderValue.CompressionLZW);
        pages[0].Save(ms, codec, parameters);
        using var next = new EncoderParameters(1);
        for (int i = 1; i < pages.Count; i++)
        {
            next.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
            pages[0].SaveAdd(pages[i], next);
        }
        next.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);
        pages[0].SaveAdd(next);
        return ms.ToArray();
    }

    /// <summary>Wraps a PDFium render (BGRA, opaque) in a GDI+ bitmap with the given DPI.</summary>
    public static Bitmap FromRendered(RenderedPage page, double dpi)
    {
        var bitmap = new Bitmap(page.PixelWidth, page.PixelHeight, PixelFormat.Format24bppRgb);
        bitmap.SetResolution((float)dpi, (float)dpi);
        var data = bitmap.LockBits(new Rectangle(0, 0, page.PixelWidth, page.PixelHeight), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < page.PixelHeight; y++)
            {
                int src = y * page.Stride;
                for (int x = 0; x < page.PixelWidth; x++)
                {
                    row[x * 3] = page.Pixels[src + x * 4];
                    row[x * 3 + 1] = page.Pixels[src + x * 4 + 1];
                    row[x * 3 + 2] = page.Pixels[src + x * 4 + 2];
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    /// <summary>Builds a bitmap from raw 8-bit RGB or gray samples (as stored in a PDF image).</summary>
    public static Bitmap FromRawSamples(byte[] samples, int width, int height, int components)
    {
        if (components is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(components));
        if (samples.Length < width * height * components) throw new InvalidDataException("Image data is shorter than its dimensions.");
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < height; y++)
            {
                int src = y * width * components;
                for (int x = 0; x < width; x++)
                {
                    byte r, g, b;
                    if (components == 1) r = g = b = samples[src + x];
                    else { r = samples[src + x * 3]; g = samples[src + x * 3 + 1]; b = samples[src + x * 3 + 2]; }
                    row[x * 3] = b; row[x * 3 + 1] = g; row[x * 3 + 2] = r;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    public static Bitmap Resize(Image source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        result.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY); // No dark edge from bicubic sampling.
        g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return result;
    }
}
