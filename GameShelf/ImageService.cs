using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GameShelf;

public static class ImageService
{
    public const int Width = 480;
    public const int Height = 640;
    private const long MaxBytes = 20 * 1024 * 1024;
    private const int MaxPixels = 40_000_000;

    public static void ProcessToCard(string source, string destination)
    {
        var info = new FileInfo(source);
        if (!info.Exists || info.Length > MaxBytes) throw new InvalidOperationException("Image is missing or exceeds the 20 MB limit.");
        using var input = Image.FromFile(source);
        if (input.Width * input.Height > MaxPixels) throw new InvalidOperationException("Image dimensions exceed the allowed limit.");
        if (input.RawFormat.Guid != ImageFormat.Png.Guid && input.RawFormat.Guid != ImageFormat.Jpeg.Guid) throw new InvalidOperationException("Only PNG and JPEG images are accepted.");
        using var canvas = new Bitmap(Width, Height);
        using var graphics = Graphics.FromImage(canvas);
        graphics.Clear(Color.FromArgb(25, 35, 50));
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var scale = Math.Max((float)Width / input.Width, (float)Height / input.Height);
        var w = (int)Math.Ceiling(input.Width * scale); var h = (int)Math.Ceiling(input.Height * scale);
        graphics.DrawImage(input, (Width - w) / 2, (Height - h) / 2, w, h);
        canvas.Save(destination, ImageFormat.Png);
    }

    public static Image MissingImage()
    {
        var bitmap = new Bitmap(Width, Height);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(37, 50, 70));
        using var pen = new Pen(Color.FromArgb(160, 180, 200), 14);
        g.DrawLine(pen, 0, 0, Width, Height); g.DrawLine(pen, Width, 0, 0, Height);
        return bitmap;
    }
}
