using CommunityToolkit.Mvvm.ComponentModel;

namespace ScoreCap.Models;

public partial class DetectionSettings : ObservableObject
{
    /// <summary>Seconds between sampled frames (0.5 / 1 / 2).</summary>
    [ObservableProperty]
    private double _frameIntervalSeconds = 1.0;

    /// <summary>0-100. Frames whose similarity to the previously kept frame is >= this are treated as duplicates.</summary>
    [ObservableProperty]
    private double _duplicateSimilarityThreshold = 94.0;

    [ObservableProperty]
    private bool _autoRemoveDuplicates = true;

    [ObservableProperty]
    private bool _excludeSubtitleAndPlaybackUi = true;

    [ObservableProperty]
    private bool _enhanceContrastAndDeskew = true;

    /// <summary>Pixels trimmed off the bottom of every capture after cropping, to remove a thin artifact/UI-bar
    /// line some sources show right at the frame edge.</summary>
    [ObservableProperty]
    private int _trimBottomPx = 6;

    /// <summary>When on, every capture is upscaled (Lanczos resize + unsharp-mask sharpening) after processing,
    /// since a lower-resolution stream capped for capture speed is otherwise fairly low-resolution for
    /// print-quality PDF output.</summary>
    [ObservableProperty]
    private bool _upscaleForQuality = true;

    /// <summary>Caps the requested stream resolution (480/720/1080). Capture speed is bandwidth-bound, so a lower
    /// cap plays through — and therefore captures — several times faster; UpscaleForQuality compensates afterward.</summary>
    [ObservableProperty]
    private int _streamMaxHeight = 720;
}
