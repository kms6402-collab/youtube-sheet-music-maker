using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ScoreCap.Services.YtDlp;

/// <summary>Title/duration plus a direct, playable media URL that OpenCV's VideoCapture can stream from
/// without first downloading the whole file to disk.</summary>
public record StreamInfo(string Title, TimeSpan Duration, string StreamUrl);

/// <summary>Thin process wrapper around the yt-dlp.exe CLI (yt-dlp.exe is bundled/located via <see cref="YtDlpLocator"/>).</summary>
public class YtDlpDownloader
{
    private readonly string _ytDlpPath;

    public YtDlpDownloader(string ytDlpPath)
    {
        _ytDlpPath = ytDlpPath;
    }

    /// <summary>Resolves metadata and a direct, streamable URL for the video — no audio track is requested since
    /// only frames are needed, which also guarantees yt-dlp hands back a single un-merged format URL.
    ///
    /// Sequential frame-by-frame streaming is bandwidth-bound, not CPU-bound: pulling a 4K/1080p stream over HTTP
    /// while decoding it barely keeps up with realtime. <paramref name="maxHeight"/> caps the requested resolution
    /// so lower settings decode several times faster than realtime (DetectionSettings.UpscaleForQuality compensates
    /// for the smaller source afterward).</summary>
    public async Task<StreamInfo> GetStreamInfoAsync(string url, int maxHeight = 720, CancellationToken ct = default)
    {
        var format = $"bv*[height<={maxHeight}][ext=mp4]/bv*[height<={maxHeight}]" +
                     $"/best[height<={maxHeight}][ext=mp4]/best[height<={maxHeight}]" +
                     "/bv*[ext=mp4]/bv*/best[ext=mp4]/best";
        var json = await RunAndCaptureAsync(
            ["--dump-json", "-f", format, "--no-playlist", "--no-warnings", url], ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Untitled" : "Untitled";
        var seconds = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetDouble()
            : 0;

        var streamUrl = root.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(streamUrl)
            && root.TryGetProperty("requested_formats", out var formats)
            && formats.ValueKind == JsonValueKind.Array && formats.GetArrayLength() > 0)
        {
            streamUrl = formats[0].TryGetProperty("url", out var fu) ? fu.GetString() : null;
        }

        if (string.IsNullOrEmpty(streamUrl))
            throw new InvalidOperationException("재생 가능한 스트림 주소를 찾지 못했습니다.");

        return new StreamInfo(title, TimeSpan.FromSeconds(seconds), streamUrl);
    }

    /// <summary>Runs a YouTube keyword search via yt-dlp's "ytsearchN:" pseudo-URL and returns lightweight metadata
    /// (no per-video network round trip, thanks to --flat-playlist).</summary>
    public async Task<List<SearchResult>> SearchAsync(string keywords, int count, CancellationToken ct = default)
    {
        var searchTerm = $"ytsearch{Math.Max(1, count)}:{keywords}";
        var ndjson = await RunAndCaptureAsync(["--flat-playlist", "--dump-json", "--no-warnings", searchTerm], ct);

        var results = new List<SearchResult>();
        foreach (var line in ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrEmpty(id)) continue;

                var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "제목 없음" : "제목 없음";
                var uploader = root.TryGetProperty("uploader", out var u) && u.GetString() is { Length: > 0 } uName
                    ? uName
                    : root.TryGetProperty("channel", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                var seconds = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetDouble()
                    : 0;
                var url = root.TryGetProperty("webpage_url", out var w) && w.GetString() is { Length: > 0 } wUrl
                    ? wUrl
                    : $"https://www.youtube.com/watch?v={id}";
                // YouTube serves a stable thumbnail at this predictable path for every video id, so this is more
                // reliable than parsing yt-dlp's "thumbnails" array (often thin/absent in --flat-playlist mode).
                var thumbnailUrl = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";

                results.Add(new SearchResult(id, title, uploader, TimeSpan.FromSeconds(seconds), url, thumbnailUrl));
            }
        }

        return results;
    }

    private async Task<string> RunAndCaptureAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ytDlpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read stdout/stderr concurrently: reading them sequentially can deadlock once either
        // pipe's OS buffer fills up while yt-dlp is blocked writing to the other one.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp exited with code {process.ExitCode}: {stderr}");

        return stdout;
    }
}
