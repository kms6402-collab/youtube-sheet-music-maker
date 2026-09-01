using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using ScoreCap.Models;

namespace ScoreCap.Services.Capture;

public static class ImageProcessor
{
    public static Mat Crop(Mat src, CropRegion region)
    {
        var clamped = region.Clamp(src.Width, src.Height);
        return new Mat(src, new Rect(clamped.X, clamped.Y, clamped.Width, clamped.Height));
    }

    /// <summary>Cuts a fixed number of pixels off the bottom edge — used to drop a thin artifact/UI-bar line some
    /// sources show right at the frame boundary.</summary>
    public static Mat TrimBottom(Mat src, int px)
    {
        if (px <= 0 || px >= src.Height)
            return src.Clone();
        return new Mat(src, new Rect(0, 0, src.Width, src.Height - px));
    }

    /// <summary>Upscales (Lanczos4, high-quality resize) and applies unsharp-mask sharpening — crops taken from a
    /// 720p stream are otherwise fairly low-resolution once printed at PDF page size.</summary>
    public static Mat UpscaleAndSharpen(Mat src, double factor = 2.0)
    {
        var upscaled = new Mat();
        Cv2.Resize(src, upscaled, new Size(), factor, factor, InterpolationFlags.Lanczos4);

        using var blurred = new Mat();
        Cv2.GaussianBlur(upscaled, blurred, new Size(0, 0), 3);

        var sharpened = new Mat();
        Cv2.AddWeighted(upscaled, 1.5, blurred, -0.5, 0, sharpened);
        upscaled.Dispose();
        return sharpened;
    }

    /// <summary>Corrects small rotation using the dominant angle of near-horizontal lines (staff lines), then
    /// improves readability with local contrast enhancement (CLAHE).</summary>
    public static Mat EnhanceForSheetMusic(Mat src)
    {
        using var deskewed = Deskew(src);
        return EnhanceContrast(deskewed);
    }

    public static Mat Deskew(Mat src)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150);

        var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, threshold: 120, minLineLength: src.Width * 0.25, maxLineGap: 20);
        var angles = new List<double>();
        foreach (var line in lines)
        {
            var dx = line.P2.X - line.P1.X;
            var dy = line.P2.Y - line.P1.Y;
            var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (Math.Abs(angle) < 10) // keep only near-horizontal candidates (staff lines / barlines context)
                angles.Add(angle);
        }

        if (angles.Count < 5)
            return src.Clone();

        angles.Sort();
        var median = angles[angles.Count / 2];
        if (Math.Abs(median) < 0.3)
            return src.Clone();

        var center = new Point2f(src.Width / 2f, src.Height / 2f);
        using var rotationMatrix = Cv2.GetRotationMatrix2D(center, median, 1.0);
        using var rotated = new Mat();
        Cv2.WarpAffine(src, rotated, rotationMatrix, src.Size(), InterpolationFlags.Cubic, BorderTypes.Replicate);

        // WarpAffine replicates edge pixels into the corners the rotation exposes, which can smear into a visible
        // line along the boundary once contrast is enhanced. Trim a small margin so it never reaches the output.
        var marginX = Math.Max(1, (int)(src.Width * 0.015));
        var marginY = Math.Max(1, (int)(src.Height * 0.015));
        var trimRect = new Rect(marginX, marginY, Math.Max(1, src.Width - marginX * 2), Math.Max(1, src.Height - marginY * 2));
        return new Mat(rotated, trimRect).Clone();
    }

    public static Mat EnhanceContrast(Mat src)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

        using var clahe = Cv2.CreateCLAHE(clipLimit: 2.5, tileGridSize: new Size(8, 8));
        using var equalized = new Mat();
        clahe.Apply(gray, equalized);

        var result = new Mat();
        Cv2.CvtColor(equalized, result, ColorConversionCodes.GRAY2BGR);
        return result;
    }

    /// <summary>Copies raw pixel data into a frozen WriteableBitmap (no System.Drawing dependency).</summary>
    public static BitmapSource ToBitmapSource(Mat mat)
    {
        var needsDispose = !mat.IsContinuous();
        var src = needsDispose ? mat.Clone() : mat;
        try
        {
            var format = src.Channels() switch
            {
                1 => PixelFormats.Gray8,
                3 => PixelFormats.Bgr24,
                4 => PixelFormats.Bgra32,
                var c => throw new NotSupportedException($"지원하지 않는 채널 수입니다: {c}"),
            };

            var stride = (int)src.Step();
            var buffer = new byte[stride * src.Height];
            Marshal.Copy(src.Data, buffer, 0, buffer.Length);

            var bitmap = BitmapSource.Create(src.Width, src.Height, 96, 96, format, null, buffer, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (needsDispose) src.Dispose();
        }
    }

    public static void SaveAsPng(Mat mat, string path, int dpi = 300)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Cv2.ImWrite(path, mat, new ImageEncodingParam(ImwriteFlags.PngCompression, 6));
    }
}
