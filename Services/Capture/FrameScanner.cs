using System.IO;
using OpenCvSharp;
using ScoreCap.Models;

namespace ScoreCap.Services.Capture;

public record ScanProgress(double Percent, int ScannedFrames, int KeptCount, int DuplicateCount);

/// <summary>Sequentially reads a video source (local file OR a direct streaming URL) frame-by-frame, sampling at a
/// fixed interval, cropping/enhancing each sampled frame, and flagging near-duplicates.
///
/// Reads sequentially (Grab/Retrieve) rather than seeking by frame index: seeking is unreliable and can hang on
/// network streams, so this scans through the video once from start to end — which also means it can begin
/// producing results immediately, without first downloading the whole file.</summary>
public class FrameScanner
{
    public Task<List<CaptureItem>> ScanAsync(
        string videoSource,
        CropRegion? crop,
        DetectionSettings settings,
        string cacheDir,
        TimeSpan? knownDuration,
        IProgress<ScanProgress>? progress,
        IProgress<CaptureItem>? itemFound,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var capture = new VideoCapture(videoSource);
            if (!capture.IsOpened())
                throw new InvalidOperationException("영상을 열 수 없습니다. (스트림 연결 실패)");

            var fps = capture.Fps > 0 ? capture.Fps : 30;
            var frameStep = Math.Max(1, (int)Math.Round(fps * settings.FrameIntervalSeconds));
            var durationMs = knownDuration is { } d && d > TimeSpan.Zero ? d.TotalMilliseconds : 0;

            Directory.CreateDirectory(cacheDir);

            var results = new List<CaptureItem>();
            Mat? lastKeptFrame = null;
            var frameIndex = 0;
            var order = 0;
            var keptCount = 0;
            var duplicateCount = 0;

            using var raw = new Mat();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!capture.Grab())
                    break; // stream ended (or connection dropped)

                if (frameIndex % frameStep == 0)
                {
                    if (!capture.Retrieve(raw) || raw.Empty())
                        break;

                    var posMs = capture.PosMsec;
                    var timestamp = posMs > 0 ? TimeSpan.FromMilliseconds(posMs) : TimeSpan.FromSeconds(frameIndex / fps);

                    using var cropped = crop is { IsEmpty: false } c ? ImageProcessor.Crop(raw, c) : raw.Clone();
                    using var trimmed = ImageProcessor.TrimBottom(cropped, settings.TrimBottomPx);
                    using var enhanced = settings.EnhanceContrastAndDeskew
                        ? ImageProcessor.EnhanceForSheetMusic(trimmed)
                        : trimmed.Clone();
                    var processed = settings.UpscaleForQuality
                        ? ImageProcessor.UpscaleAndSharpen(enhanced)
                        : enhanced.Clone();

                    double? similarity = null;
                    var isDuplicate = false;
                    if (lastKeptFrame is not null)
                    {
                        similarity = FrameSimilarity.ComputeSimilarityPercent(lastKeptFrame, processed);
                        isDuplicate = similarity.Value >= settings.DuplicateSimilarityThreshold;
                    }

                    var fileName = $"frame_{order:0000}_{(int)timestamp.TotalSeconds:00000}.png";
                    var path = Path.Combine(cacheDir, fileName);
                    ImageProcessor.SaveAsPng(processed, path);

                    var item = new CaptureItem
                    {
                        Order = order,
                        Timestamp = timestamp,
                        Thumbnail = ImageProcessor.ToBitmapSource(processed),
                        ImagePath = path,
                        SimilarityPercent = similarity,
                        IsDuplicate = isDuplicate,
                        IsIncluded = !(isDuplicate && settings.AutoRemoveDuplicates),
                    };
                    results.Add(item);
                    itemFound?.Report(item);
                    order++;
                    if (isDuplicate) duplicateCount++; else keptCount++;

                    if (!isDuplicate)
                    {
                        lastKeptFrame?.Dispose();
                        lastKeptFrame = processed;
                    }
                    else
                    {
                        processed.Dispose();
                    }

                    var pct = durationMs > 0 ? Math.Clamp(posMs * 100.0 / durationMs, 0, 100) : 0;
                    progress?.Report(new ScanProgress(pct, order, keptCount, duplicateCount));
                }

                frameIndex++;
            }

            lastKeptFrame?.Dispose();
            return results;
        }, ct);
    }

    /// <summary>Grabs the first frame of a video source (local path or direct stream URL), used to populate the
    /// crop-preview panel right after loading.</summary>
    public static Mat? GrabFrameAt(string videoSource, TimeSpan timestamp)
    {
        using var capture = new VideoCapture(videoSource);
        if (!capture.IsOpened())
            return null;

        if (timestamp > TimeSpan.Zero)
        {
            var fps = capture.Fps > 0 ? capture.Fps : 30;
            capture.Set(VideoCaptureProperties.PosFrames, (int)(timestamp.TotalSeconds * fps));
        }

        var frame = new Mat();
        return capture.Read(frame) && !frame.Empty() ? frame : null;
    }
}
