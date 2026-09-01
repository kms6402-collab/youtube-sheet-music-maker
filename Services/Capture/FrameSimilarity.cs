using OpenCvSharp;

namespace ScoreCap.Services.Capture;

/// <summary>Pixel-difference based similarity score between two frames, used to detect near-duplicate score pages.</summary>
public static class FrameSimilarity
{
    private const int CompareWidth = 320;

    /// <summary>Returns 0-100, where 100 means the frames are effectively identical.</summary>
    public static double ComputeSimilarityPercent(Mat a, Mat b)
    {
        using var grayA = ToComparableGray(a);
        using var grayB = ToComparableGray(b);

        if (grayA.Size() != grayB.Size())
            Cv2.Resize(grayB, grayB, grayA.Size());

        using var diff = new Mat();
        Cv2.Absdiff(grayA, grayB, diff);
        var meanDiff = Cv2.Mean(diff).Val0; // 0..255
        var similarity = 100.0 * (1.0 - meanDiff / 255.0);
        return Math.Clamp(similarity, 0, 100);
    }

    private static Mat ToComparableGray(Mat src)
    {
        var gray = new Mat();
        if (src.Channels() > 1)
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        else
            src.CopyTo(gray);

        var scale = CompareWidth / (double)gray.Width;
        var targetHeight = Math.Max(1, (int)(gray.Height * scale));
        Cv2.Resize(gray, gray, new Size(CompareWidth, targetHeight));
        Cv2.GaussianBlur(gray, gray, new Size(3, 3), 0);
        return gray;
    }
}
