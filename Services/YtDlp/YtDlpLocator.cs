using System.IO;
using System.Net.Http;

namespace ScoreCap.Services.YtDlp;

/// <summary>Finds (or fetches, on explicit user request) the yt-dlp.exe binary the app shells out to.</summary>
public class YtDlpLocator
{
    private const string LatestReleaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    public string ToolsDirectory { get; } =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");

    public string BundledPath => Path.Combine(ToolsDirectory, "yt-dlp.exe");

    /// <summary>Returns a runnable yt-dlp path: bundled copy first, then PATH, else null.</summary>
    public string? Find()
    {
        if (File.Exists(BundledPath))
            return BundledPath;

        var fromPath = FindOnPath("yt-dlp.exe") ?? FindOnPath("yt-dlp");
        return fromPath;
    }

    public bool IsFfmpegAvailable() =>
        FindOnPath("ffmpeg.exe") is not null || FindOnPath("ffmpeg") is not null;

    private static string? FindOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // ignore malformed PATH entries
            }
        }
        return null;
    }

    /// <summary>Downloads the latest yt-dlp.exe release into the app's tools folder. Call only after explicit user consent.</summary>
    public async Task<string> DownloadLatestAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ToolsDirectory);

        using var http = new HttpClient();
        using var response = await http.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var tempPath = BundledPath + ".download";
        await using (var fileStream = File.Create(tempPath))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                if (total > 0)
                    progress?.Report(readTotal * 100.0 / total);
            }
        }

        File.Move(tempPath, BundledPath, overwrite: true);
        return BundledPath;
    }
}
