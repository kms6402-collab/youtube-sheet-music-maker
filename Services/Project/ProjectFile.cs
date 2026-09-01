using ScoreCap.Models;

namespace ScoreCap.Services.Project;

public class ProjectFile
{
    public string YoutubeUrl { get; set; } = string.Empty;
    public string VideoTitle { get; set; } = string.Empty;
    public string VideoSourceUrl { get; set; } = string.Empty;

    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropWidth { get; set; }
    public int CropHeight { get; set; }

    public double FrameIntervalSeconds { get; set; } = 1.0;
    public double DuplicateSimilarityThreshold { get; set; } = 94.0;
    public bool AutoRemoveDuplicates { get; set; } = true;
    public bool ExcludeSubtitleAndPlaybackUi { get; set; } = true;
    public bool EnhanceContrastAndDeskew { get; set; } = true;
    public int TrimBottomPx { get; set; } = 6;
    public bool UpscaleForQuality { get; set; } = true;
    public int StreamMaxHeight { get; set; } = 720;

    public PaperSize PaperSize { get; set; } = PaperSize.A4;
    public int Columns { get; set; } = 1;
    public double MarginMm { get; set; } = 12;
    public int Dpi { get; set; } = 300;
    public bool AddPageNumbers { get; set; } = true;
    public bool InsertTitleOnFirstPage { get; set; }
    public bool SaveOriginalPngs { get; set; } = true;
    public string OutputPath { get; set; } = string.Empty;
    public string ProjectTitle { get; set; } = "Untitled";

    public List<CaptureItemDto> Captures { get; set; } = new();
}

public class CaptureItemDto
{
    public int Order { get; set; }
    public double TimestampSeconds { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public double? SimilarityPercent { get; set; }
    public bool IsDuplicate { get; set; }
    public bool IsIncluded { get; set; } = true;
}
