namespace ScoreCap.Services.YtDlp;

public record SearchResult(string Id, string Title, string Uploader, TimeSpan Duration, string Url, string ThumbnailUrl)
{
    public string DurationLabel => Duration <= TimeSpan.Zero
        ? string.Empty
        : Duration.ToString(Duration.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
}
