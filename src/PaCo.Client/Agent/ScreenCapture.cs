using System.Drawing.Imaging;

namespace PaCo.Client.Agent;

/// <summary>Captures all monitors of the child's session as a compressed JPEG.</summary>
internal static class ScreenCapture
{
    private const int MaxWidth = 1600;

    public static byte[] CaptureJpeg(long quality = 60)
    {
        var bounds = SystemInformation.VirtualScreen;
        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        var scale = Math.Min(1.0, (double)MaxWidth / bounds.Width);
        using var scaled = new Bitmap(Math.Max(1, (int)(bounds.Width * scale)), Math.Max(1, (int)(bounds.Height * scale)));
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, 0, 0, scaled.Width, scaled.Height);
        }

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var ms = new MemoryStream();
        scaled.Save(ms, codec, p);
        return ms.ToArray();
    }
}
