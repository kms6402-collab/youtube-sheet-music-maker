using OpenCvSharp;

namespace ScoreCap.Services.Capture;

/// <summary>Heuristic (non-OCR) detector for small clustered glyph-like regions, used by the image editor's
/// "자동 인식" tool to find and erase likely Korean captions/labels. This is image-processing pattern matching,
/// not real text recognition — it flags dense clusters of small connected components that look like glyphs rather
/// than staff lines or notation, so it can miss text or occasionally flag notation by mistake.</summary>
public static class TextRegionDetector
{
    /// <param name="bgrOrGray">The image (or crop of one) to scan.</param>
    /// <param name="referenceWidth">Width used for the "is this a staff/bar line" and glyph-size heuristics —
    /// defaults to <paramref name="bgrOrGray"/>'s own width. Pass the *full* source image's width when scanning a
    /// small crop (e.g. a lasso selection): the thresholds are relative fractions, so deriving them from a small
    /// crop instead of the original image would reject ordinary-sized text as "too big for this tiny region".</param>
    /// <param name="referenceHeight">Same idea as <paramref name="referenceWidth"/>, for height.</param>
    public static List<Rect> FindTextRegions(Mat bgrOrGray, int? referenceWidth = null, int? referenceHeight = null)
    {
        using var gray = new Mat();
        if (bgrOrGray.Channels() == 1)
            bgrOrGray.CopyTo(gray);
        else
            Cv2.CvtColor(bgrOrGray, gray, ColorConversionCodes.BGR2GRAY);

        using var binary = new Mat();
        Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 25, 10);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var labelCount = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids);

        var w = referenceWidth ?? bgrOrGray.Width;
        var h = referenceHeight ?? bgrOrGray.Height;
        var glyphBoxes = new List<Rect>();
        for (var i = 1; i < labelCount; i++) // label 0 is the background
        {
            var x = stats.At<int>(i, 0);
            var y = stats.At<int>(i, 1);
            var cw = stats.At<int>(i, 2);
            var ch = stats.At<int>(i, 3);
            var area = stats.At<int>(i, 4);

            if (cw >= w * 0.4) continue; // spans most of the width — a staff/bar line, not a glyph
            if (ch < h * 0.008 || ch > h * 0.09) continue; // outside the plausible character-height range
            if (cw < 2 || ch < 2) continue;
            if ((double)cw / ch > 3.0) continue; // too flat/wide for a character (beam, slur, stem group)
            if (area < ch * cw * 0.12) continue; // too sparse to be solid glyph ink

            glyphBoxes.Add(new Rect(x, y, cw, ch));
        }

        return ClusterIntoRegions(glyphBoxes);
    }

    /// <summary>Merges nearby glyph-sized boxes into line-level text regions. Requires at least two glyphs close
    /// together (same rough line, small horizontal gap) to avoid flagging a single stray notehead or accidental.</summary>
    private static List<Rect> ClusterIntoRegions(List<Rect> glyphBoxes)
    {
        var used = new bool[glyphBoxes.Count];
        var regions = new List<Rect>();

        for (var i = 0; i < glyphBoxes.Count; i++)
        {
            if (used[i]) continue;

            var cluster = new List<int> { i };
            used[i] = true;

            bool grew;
            do
            {
                grew = false;
                var avgHeight = cluster.Average(idx => (double)glyphBoxes[idx].Height);
                var hGap = avgHeight * 1.6;
                var vTolerance = avgHeight * 0.7;

                for (var j = 0; j < glyphBoxes.Count; j++)
                {
                    if (used[j]) continue;
                    var candidate = glyphBoxes[j];
                    var candidateCenterY = candidate.Y + candidate.Height / 2.0;

                    var nearAny = cluster.Any(idx =>
                    {
                        var box = glyphBoxes[idx];
                        var boxCenterY = box.Y + box.Height / 2.0;
                        var horizontalGap = Math.Max(0, Math.Max(candidate.X - (box.X + box.Width), box.X - (candidate.X + candidate.Width)));
                        return horizontalGap <= hGap && Math.Abs(candidateCenterY - boxCenterY) <= vTolerance;
                    });

                    if (nearAny)
                    {
                        used[j] = true;
                        cluster.Add(j);
                        grew = true;
                    }
                }
            } while (grew);

            if (cluster.Count < 2) continue; // a lone blob is more likely a notehead/accidental than text

            var rx0 = cluster.Min(idx => glyphBoxes[idx].X);
            var ry0 = cluster.Min(idx => glyphBoxes[idx].Y);
            var rx1 = cluster.Max(idx => glyphBoxes[idx].X + glyphBoxes[idx].Width);
            var ry1 = cluster.Max(idx => glyphBoxes[idx].Y + glyphBoxes[idx].Height);

            const int pad = 3;
            regions.Add(new Rect(rx0 - pad, ry0 - pad, rx1 - rx0 + pad * 2, ry1 - ry0 + pad * 2));
        }

        return regions;
    }
}
