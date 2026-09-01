using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ScoreCap.Models;

/// <summary>A single detected/kept video frame that will become one page (or grid cell) in the exported PDF.</summary>
public partial class CaptureItem : ObservableObject
{
    [ObservableProperty]
    private int _order;

    [ObservableProperty]
    private TimeSpan _timestamp;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private string _imagePath = string.Empty;

    /// <summary>Similarity (0-100) to the previously kept frame. Null for the first capture.</summary>
    [ObservableProperty]
    private double? _similarityPercent;

    [ObservableProperty]
    private bool _isDuplicate;

    /// <summary>Whether this capture is included in the exported PDF (unchecked automatically for detected duplicates).</summary>
    [ObservableProperty]
    private bool _isIncluded = true;

    public string TimestampLabel => Timestamp.ToString(Timestamp.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");

    public string SimilarityLabel => IsDuplicate
        ? $"중복 {SimilarityPercent:0}%"
        : SimilarityPercent.HasValue
            ? $"유사도 {SimilarityPercent:0}%"
            : string.Empty;

    partial void OnTimestampChanged(TimeSpan value) => OnPropertyChanged(nameof(TimestampLabel));

    partial void OnSimilarityPercentChanged(double? value) => OnPropertyChanged(nameof(SimilarityLabel));

    partial void OnIsDuplicateChanged(bool value) => OnPropertyChanged(nameof(SimilarityLabel));
}
