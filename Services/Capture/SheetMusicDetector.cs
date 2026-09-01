using OpenCvSharp;
using ScoreCap.Models;

namespace ScoreCap.Services.Capture;

/// <summary>Heuristically locates the sheet-music panel inside a video frame by finding the region with the
/// strongest concentration of long horizontal strokes (staff lines), and suggests a crop rectangle for it.</summary>
public static class SheetMusicDetector
{
    public static CropRegion Detect(Mat frame, bool excludeSubtitleAndPlaybackUi)
    {
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

        using var binary = new Mat();
        Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.MeanC, ThresholdTypes.BinaryInv, 25, 10);

        var kernelWidth = Math.Max(15, frame.Width / 30);
        using var horizontalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(kernelWidth, 1));
        using var staffLineMask = new Mat();
        Cv2.MorphologyEx(binary, staffLineMask, MorphTypes.Open, horizontalKernel);

        var top = 0;
        var bottom = frame.Height;
        if (excludeSubtitleAndPlaybackUi)
        {
            // YouTube subtitles / playback controls typically live in the bottom ~16% of the player.
            bottom = (int)(frame.Height * 0.84);
            top = (int)(frame.Height * 0.04);
        }

        var rowScores = new int[frame.Height];
        for (var y = top; y < bottom; y++)
            rowScores[y] = Cv2.CountNonZero(staffLineMask.Row(y));

        var maxRowScore = rowScores.Length == 0 ? 0 : rowScores.Max();
        if (maxRowScore < kernelWidth) // essentially no long horizontal strokes found; bail out to full frame
            return CropRegion.FullFrame(frame.Width, frame.Height);

        // A grand staff has a visible gap between the treble and bass staves — much bigger than the gap between
        // the five lines *within* one staff, but still part of the same system. Rather than chase a single gap
        // size that reliably tells "within a system" apart from "between systems" (fragile — real sheet music
        // varies), find every band of genuine staff-line content, then take the union of all of them: this
        // naturally keeps a full grand staff together and still ignores incidental single-row noise elsewhere.
        var rowThreshold = Math.Max(kernelWidth, maxRowScore * 0.2);
        var rowGap = Math.Max(6, (int)(frame.Height * 0.025));
        var rowBands = FindQualifyingBands(rowScores, top, bottom, rowThreshold, rowGap);
        if (rowBands.Count == 0)
            return CropRegion.FullFrame(frame.Width, frame.Height);

        var rowMin = rowBands.Min(b => b.start);
        var rowMax = rowBands.Max(b => b.end);

        using var rowsMask = new Mat(staffLineMask, new Rect(0, rowMin, frame.Width, rowMax - rowMin + 1));
        var colScores = new int[frame.Width];
        for (var x = 0; x < frame.Width; x++)
            colScores[x] = Cv2.CountNonZero(rowsMask.Col(x));

        var maxColScore = colScores.Max();
        var colThreshold = Math.Max(1, maxColScore * 0.15);
        var colGap = Math.Max(4, (int)(frame.Width * 0.01));
        var colBands = FindQualifyingBands(colScores, 0, frame.Width, colThreshold, colGap);
        var colMin = colBands.Count > 0 ? colBands.Min(b => b.start) : 0;
        var colMax = colBands.Count > 0 ? colBands.Max(b => b.end) : frame.Width - 1;

        var padX = (int)(frame.Width * 0.015);
        var padY = (int)(frame.Height * 0.03);

        var x0 = Math.Max(0, colMin - padX);
        var y0 = Math.Max(0, rowMin - padY);
        var x1 = Math.Min(frame.Width - 1, colMax + padX);
        var y1 = Math.Min(frame.Height - 1, rowMax + padY);

        return new CropRegion { X = x0, Y = y0, Width = x1 - x0 + 1, Height = y1 - y0 + 1 };
    }

    /// <summary>Scans <paramref name="scores"/> in [from, to) for runs at/above <paramref name="threshold"/>,
    /// bridging gaps up to <paramref name="maxGap"/> indices apart (enough to merge one staff's five lines into a
    /// single band). Bands whose total score is a small fraction of the strongest band are dropped as noise;
    /// everything else is kept — the caller unions them, rather than assuming only one band is "the" content.</summary>
    private static List<(int start, int end, long score)> FindQualifyingBands(int[] scores, int from, int to, double threshold, int maxGap)
    {
        var bands = new List<(int start, int end, long score)>();
        int? bandStart = null;
        var lastAbove = -1;
        var gapRun = 0;
        long bandScore = 0;

        for (var i = from; i < to; i++)
        {
            if (scores[i] >= threshold)
            {
                bandStart ??= i;
                bandScore += scores[i];
                lastAbove = i;
                gapRun = 0;
            }
            else if (bandStart is not null)
            {
                gapRun++;
                if (gapRun > maxGap)
                {
                    bands.Add((bandStart.Value, lastAbove, bandScore));
                    bandStart = null;
                    bandScore = 0;
                }
            }
        }
        if (bandStart is not null)
            bands.Add((bandStart.Value, lastAbove, bandScore));

        if (bands.Count == 0)
            return bands;

        var maxScore = bands.Max(b => b.score);
        return bands.Where(b => b.score >= maxScore * 0.12).ToList();
    }
}
