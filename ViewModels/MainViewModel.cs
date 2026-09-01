using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenCvSharp;
using ScoreCap.Models;
using ScoreCap.Services.Capture;
using ScoreCap.Services.Pdf;
using ScoreCap.Services.Project;
using ScoreCap.Services.YtDlp;
using ScoreCap.Views;

namespace ScoreCap.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly YtDlpLocator _ytDlpLocator = new();
    private readonly ProjectService _projectService = new();
    private readonly string _sessionCacheDir =
        Path.Combine(Path.GetTempPath(), "ScoreCap", Guid.NewGuid().ToString("N"));

    private Mat? _previewFrame;
    private CancellationTokenSource? _busyCts;
    private TimeSpan _videoDuration;

    public ObservableCollection<CaptureItem> Captures { get; } = new();

    /// <summary>Filtered view over Captures shown by the results grid — lets the user narrow a long list down to
    /// just what they're looking for instead of scrolling through everything.</summary>
    public ICollectionView CapturesView { get; }

    [ObservableProperty]
    private CaptureFilterMode _filterMode = CaptureFilterMode.All;

    partial void OnFilterModeChanged(CaptureFilterMode value) => CapturesView.Refresh();

    public DetectionSettings Detection { get; } = new();

    public PdfExportSettings PdfSettings { get; } = new();

    [ObservableProperty]
    private string _youtubeUrl = string.Empty;

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? _previewImage;

    [ObservableProperty]
    private int _cropX;

    [ObservableProperty]
    private int _cropY;

    [ObservableProperty]
    private int _cropWidth;

    [ObservableProperty]
    private int _cropHeight;

    /// <summary>Direct, playable stream URL resolved via yt-dlp — the video is never downloaded to disk;
    /// OpenCV reads frames straight from this URL as the "video" plays through.</summary>
    [ObservableProperty]
    private string _videoSourceUrl = string.Empty;

    [ObservableProperty]
    private string _projectPath = string.Empty;

    [ObservableProperty]
    private string _projectDisplayName = "제목 없음";

    [ObservableProperty]
    private bool _isYtDlpMissing;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyStatusText = string.Empty;

    [ObservableProperty]
    private double _busyPercent;

    /// <summary>True while a busy operation has no meaningful percent to show (e.g. waiting on yt-dlp's YouTube
    /// metadata lookup) — the progress bar animates instead of sitting frozen at 0%.</summary>
    [ObservableProperty]
    private bool _isBusyIndeterminate;

    [ObservableProperty]
    private int _scannedFrameCount;

    [ObservableProperty]
    private int _keptCount;

    [ObservableProperty]
    private int _duplicateCount;

    public string ScanSummaryText => $"검사 {ScannedFrameCount}프레임 · 채택 {KeptCount} · 중복 {DuplicateCount}";

    partial void OnScannedFrameCountChanged(int value) => OnPropertyChanged(nameof(ScanSummaryText));
    partial void OnKeptCountChanged(int value) => OnPropertyChanged(nameof(ScanSummaryText));
    partial void OnDuplicateCountChanged(int value) => OnPropertyChanged(nameof(ScanSummaryText));

    [ObservableProperty]
    private string _expectedPdfSizeText = "-";

    [ObservableProperty]
    private string _outputPageCountText = "0";

    partial void OnPreviewImageChanged(System.Windows.Media.Imaging.BitmapSource? value)
    {
        OnPropertyChanged(nameof(PreviewFrameWidth));
        OnPropertyChanged(nameof(PreviewFrameHeight));
    }

    public MainViewModel()
    {
        Directory.CreateDirectory(_sessionCacheDir);
        IsYtDlpMissing = _ytDlpLocator.Find() is null;
        PdfSettings.OutputPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ScoreCap", "output.pdf");

        CapturesView = CollectionViewSource.GetDefaultView(Captures);
        CapturesView.Filter = FilterCapture;

        Captures.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (CaptureItem item in e.NewItems)
                    item.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(CaptureItem.IsIncluded))
                        {
                            UpdateExportEstimate();
                            CapturesView.Refresh();
                        }
                    };
            UpdateExportEstimate();
        };
    }

    private bool FilterCapture(object obj)
    {
        if (obj is not CaptureItem item) return true;
        return FilterMode switch
        {
            CaptureFilterMode.Included => item.IsIncluded,
            CaptureFilterMode.Excluded => !item.IsIncluded,
            _ => true,
        };
    }

    [RelayCommand]
    private async Task DownloadYtDlpAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyStatusText = "yt-dlp 다운로드 중...";
        try
        {
            var progress = new Progress<double>(p => BusyPercent = p);
            await _ytDlpLocator.DownloadLatestAsync(progress);
            IsYtDlpMissing = _ytDlpLocator.Find() is null;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"yt-dlp 다운로드에 실패했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            BusyPercent = 0;
        }
    }

    [RelayCommand]
    private void OpenSearch()
    {
        var ytDlpPath = _ytDlpLocator.Find();
        if (ytDlpPath is null)
        {
            IsYtDlpMissing = true;
            MessageBox.Show("yt-dlp.exe를 찾을 수 없습니다. 먼저 다운로드해주세요.", "yt-dlp 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = new SearchWindow(ytDlpPath) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() == true && window.SelectedUrl is not null)
            YoutubeUrl = window.SelectedUrl;
    }

    [RelayCommand]
    private async Task LoadVideoAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(YoutubeUrl)) return;

        if (Captures.Count > 0)
        {
            var result = MessageBox.Show(
                "새 영상을 불러오면 현재 캡처 결과가 모두 초기화됩니다. 계속하시겠습니까?",
                "캡처 결과 초기화", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            Captures.Clear();
            ScannedFrameCount = 0;
            KeptCount = 0;
            DuplicateCount = 0;
        }

        var ytDlpPath = _ytDlpLocator.Find();
        if (ytDlpPath is null)
        {
            IsYtDlpMissing = true;
            MessageBox.Show("yt-dlp.exe를 찾을 수 없습니다. 먼저 다운로드해주세요.", "yt-dlp 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        IsBusyIndeterminate = true;
        _busyCts = new CancellationTokenSource();
        // yt-dlp's YouTube metadata lookup is a single network round trip with no incremental progress —
        // consistently ~5s regardless of video, so this status text is the only feedback available for it.
        BusyStatusText = "스트림 주소 확인 중... (유튜브 메타데이터 조회, 몇 초 걸릴 수 있음)";
        BusyPercent = 0;
        try
        {
            var downloader = new YtDlpDownloader(ytDlpPath);
            var info = await downloader.GetStreamInfoAsync(YoutubeUrl, Detection.StreamMaxHeight, _busyCts.Token);
            ProjectTitleFromVideo(info.Title);
            VideoSourceUrl = info.StreamUrl;
            _videoDuration = info.Duration;

            BusyStatusText = "미리보기 불러오는 중...";
            var previewFrame = await Task.Run(() =>
            {
                using var capture = new VideoCapture(VideoSourceUrl);
                if (!capture.IsOpened())
                    throw new InvalidOperationException("스트림을 열 수 없습니다.");

                using var frame = new Mat();
                return capture.Read(frame) && !frame.Empty() ? frame.Clone() : null;
            }, _busyCts.Token);

            if (previewFrame is not null)
            {
                _previewFrame?.Dispose();
                _previewFrame = previewFrame;
                PreviewImage = ImageProcessor.ToBitmapSource(_previewFrame);
                SetCropRegion(CropRegion.FullFrame(_previewFrame.Width, _previewFrame.Height));
            }
        }
        catch (OperationCanceledException)
        {
            BusyStatusText = "취소됨";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"영상을 불러오지 못했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            IsBusyIndeterminate = false;
            BusyPercent = 0;
            _busyCts = null;
        }
    }

    [RelayCommand]
    private async Task StartDetectionAsync()
    {
        if (IsBusy || string.IsNullOrEmpty(VideoSourceUrl)) return;

        IsBusy = true;
        _busyCts = new CancellationTokenSource();
        BusyStatusText = "영상을 재생하며 캡처 중...";
        BusyPercent = 0;
        ScannedFrameCount = 0;
        KeptCount = 0;
        DuplicateCount = 0;
        Captures.Clear();
        var completed = false;

        try
        {
            var scanner = new FrameScanner();
            var crop = new CropRegion { X = CropX, Y = CropY, Width = CropWidth, Height = CropHeight };
            var frameCacheDir = Path.Combine(_sessionCacheDir, "frames");

            var scanProgress = new Progress<ScanProgress>(p =>
            {
                BusyPercent = p.Percent;
                ScannedFrameCount = p.ScannedFrames;
                KeptCount = p.KeptCount;
                DuplicateCount = p.DuplicateCount;
            });
            var itemFound = new Progress<CaptureItem>(item => Captures.Add(item));

            await scanner.ScanAsync(VideoSourceUrl, crop, Detection, frameCacheDir, _videoDuration, scanProgress, itemFound, _busyCts.Token);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            BusyStatusText = "취소됨";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"자동 감지 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            BusyPercent = 0;
            _busyCts = null;
        }

        if (completed)
        {
            MessageBox.Show($"캡처가 완료되었습니다.\n채택 {KeptCount}개 · 중복 {DuplicateCount}개 (검사 {ScannedFrameCount}프레임)",
                "캡처 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private void CancelBusyOperation() => _busyCts?.Cancel();

    [RelayCommand]
    private async Task AutoDetectCropAsync()
    {
        if (IsBusy || string.IsNullOrEmpty(VideoSourceUrl)) return;

        IsBusy = true;
        IsBusyIndeterminate = true;
        BusyStatusText = "악보가 보이는 지점을 찾는 중...";
        try
        {
            // The very first frame is usually an intro/title card, not the score — grab a frame further into the
            // video instead so detection actually runs against real notation.
            var offsetSeconds = _videoDuration > TimeSpan.Zero
                ? Math.Clamp(_videoDuration.TotalSeconds * 0.15, 5, 60)
                : 10;
            var frame = await Task.Run(() => FrameScanner.GrabFrameAt(VideoSourceUrl, TimeSpan.FromSeconds(offsetSeconds)));
            if (frame is null) return;

            _previewFrame?.Dispose();
            _previewFrame = frame;
            PreviewImage = ImageProcessor.ToBitmapSource(_previewFrame);

            var region = SheetMusicDetector.Detect(_previewFrame, Detection.ExcludeSubtitleAndPlaybackUi);
            SetCropRegion(region);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"악보 자동 인식에 실패했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            IsBusyIndeterminate = false;
        }
    }

    [RelayCommand]
    private void SetFullFrameCrop()
    {
        if (_previewFrame is null) return;
        SetCropRegion(CropRegion.FullFrame(_previewFrame.Width, _previewFrame.Height));
    }

    public int PreviewFrameWidth => _previewFrame?.Width ?? 0;

    public int PreviewFrameHeight => _previewFrame?.Height ?? 0;

    public void SetCropRegion(CropRegion region)
    {
        CropX = region.X;
        CropY = region.Y;
        CropWidth = region.Width;
        CropHeight = region.Height;
    }

    [RelayCommand]
    private void AddManualCapture()
    {
        if (_previewFrame is null) return;

        var crop = new CropRegion { X = CropX, Y = CropY, Width = CropWidth, Height = CropHeight };
        using var cropped = crop.IsEmpty ? _previewFrame.Clone() : ImageProcessor.Crop(_previewFrame, crop);
        using var trimmed = ImageProcessor.TrimBottom(cropped, Detection.TrimBottomPx);
        using var enhanced = Detection.EnhanceContrastAndDeskew ? ImageProcessor.EnhanceForSheetMusic(trimmed) : trimmed.Clone();
        var processed = Detection.UpscaleForQuality ? ImageProcessor.UpscaleAndSharpen(enhanced) : enhanced.Clone();

        var order = Captures.Count;
        var path = Path.Combine(_sessionCacheDir, "frames", $"manual_{order:0000}.png");
        ImageProcessor.SaveAsPng(processed, path);

        Captures.Add(new CaptureItem
        {
            Order = order,
            Timestamp = TimeSpan.Zero,
            Thumbnail = ImageProcessor.ToBitmapSource(processed),
            ImagePath = path,
            IsIncluded = true,
        });
        processed.Dispose();
    }

    [RelayCommand]
    private void DeleteCapture(CaptureItem? item)
    {
        if (item is null) return;
        Captures.Remove(item);
        RenumberCaptures();
    }

    [RelayCommand]
    private void MoveCaptureUp(CaptureItem? item)
    {
        if (item is null) return;
        var index = Captures.IndexOf(item);
        if (index > 0)
        {
            Captures.Move(index, index - 1);
            RenumberCaptures();
        }
    }

    [RelayCommand]
    private void MoveCaptureDown(CaptureItem? item)
    {
        if (item is null) return;
        var index = Captures.IndexOf(item);
        if (index >= 0 && index < Captures.Count - 1)
        {
            Captures.Move(index, index + 1);
            RenumberCaptures();
        }
    }

    public void DeleteItems(IEnumerable<CaptureItem> items)
    {
        foreach (var item in items.ToList())
            Captures.Remove(item);
        RenumberCaptures();
    }

    private void RenumberCaptures()
    {
        for (var i = 0; i < Captures.Count; i++)
            Captures[i].Order = i;
    }

    [RelayCommand]
    private void BrowseSavePath()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PDF 파일 (*.pdf)|*.pdf",
            FileName = string.IsNullOrWhiteSpace(PdfSettings.OutputPath)
                ? "output.pdf"
                : Path.GetFileName(PdfSettings.OutputPath),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(PdfSettings.OutputPath))
                ? Path.GetDirectoryName(PdfSettings.OutputPath)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() == true)
            PdfSettings.OutputPath = dialog.FileName;
    }

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        if (IsBusy) return;
        var included = Captures.Where(c => c.IsIncluded).OrderBy(c => c.Order).ToList();
        if (included.Count == 0)
        {
            MessageBox.Show("내보낼 캡처가 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(PdfSettings.OutputPath))
        {
            BrowseSavePath();
            if (string.IsNullOrWhiteSpace(PdfSettings.OutputPath)) return;
        }

        IsBusy = true;
        _busyCts = new CancellationTokenSource();
        BusyStatusText = "PDF 렌더링 중...";
        BusyPercent = 0;
        try
        {
            var exporter = new PdfExporter();
            var progress = new Progress<PdfExportProgress>(p =>
            {
                BusyPercent = p.Percent;
                BusyStatusText = $"PDF 페이지 {p.Page}/{p.TotalPages} 렌더링";
            });
            await exporter.ExportAsync(included, PdfSettings, progress, _busyCts.Token);
            MessageBox.Show($"PDF를 저장했습니다.\n{PdfSettings.OutputPath}", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            BusyStatusText = "취소됨";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"PDF 내보내기에 실패했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            BusyPercent = 0;
            _busyCts = null;
        }
    }

    [RelayCommand]
    private void NewProject()
    {
        Captures.Clear();
        YoutubeUrl = string.Empty;
        VideoSourceUrl = string.Empty;
        _videoDuration = TimeSpan.Zero;
        PreviewImage = null;
        ProjectPath = string.Empty;
        ProjectDisplayName = "제목 없음";
    }

    [RelayCommand]
    private void SaveProject()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath))
        {
            var dialog = new SaveFileDialog { Filter = "ScoreCap 프로젝트 (*.scap)|*.scap", FileName = ProjectDisplayName + ".scap" };
            if (dialog.ShowDialog() != true) return;
            ProjectPath = dialog.FileName;
        }

        var project = new ProjectFile
        {
            YoutubeUrl = YoutubeUrl,
            VideoTitle = ProjectDisplayName,
            VideoSourceUrl = VideoSourceUrl,
            CropX = CropX,
            CropY = CropY,
            CropWidth = CropWidth,
            CropHeight = CropHeight,
            FrameIntervalSeconds = Detection.FrameIntervalSeconds,
            DuplicateSimilarityThreshold = Detection.DuplicateSimilarityThreshold,
            AutoRemoveDuplicates = Detection.AutoRemoveDuplicates,
            ExcludeSubtitleAndPlaybackUi = Detection.ExcludeSubtitleAndPlaybackUi,
            EnhanceContrastAndDeskew = Detection.EnhanceContrastAndDeskew,
            TrimBottomPx = Detection.TrimBottomPx,
            UpscaleForQuality = Detection.UpscaleForQuality,
            StreamMaxHeight = Detection.StreamMaxHeight,
            PaperSize = PdfSettings.PaperSize,
            Columns = PdfSettings.Columns,
            MarginMm = PdfSettings.MarginMm,
            Dpi = PdfSettings.Dpi,
            AddPageNumbers = PdfSettings.AddPageNumbers,
            InsertTitleOnFirstPage = PdfSettings.InsertTitleOnFirstPage,
            SaveOriginalPngs = PdfSettings.SaveOriginalPngs,
            OutputPath = PdfSettings.OutputPath,
            ProjectTitle = PdfSettings.ProjectTitle,
            Captures = Captures.Select(c => new CaptureItemDto
            {
                Order = c.Order,
                TimestampSeconds = c.Timestamp.TotalSeconds,
                ImagePath = c.ImagePath,
                SimilarityPercent = c.SimilarityPercent,
                IsDuplicate = c.IsDuplicate,
                IsIncluded = c.IsIncluded,
            }).ToList(),
        };
        _projectService.Save(ProjectPath, project);
        ProjectDisplayName = Path.GetFileNameWithoutExtension(ProjectPath);
    }

    [RelayCommand]
    private void OpenProject()
    {
        var dialog = new OpenFileDialog { Filter = "ScoreCap 프로젝트 (*.scap)|*.scap" };
        if (dialog.ShowDialog() != true) return;

        var project = _projectService.Load(dialog.FileName);
        ProjectPath = dialog.FileName;
        ProjectDisplayName = Path.GetFileNameWithoutExtension(dialog.FileName);
        YoutubeUrl = project.YoutubeUrl;
        VideoSourceUrl = project.VideoSourceUrl;

        CropX = project.CropX;
        CropY = project.CropY;
        CropWidth = project.CropWidth;
        CropHeight = project.CropHeight;

        Detection.FrameIntervalSeconds = project.FrameIntervalSeconds;
        Detection.DuplicateSimilarityThreshold = project.DuplicateSimilarityThreshold;
        Detection.AutoRemoveDuplicates = project.AutoRemoveDuplicates;
        Detection.ExcludeSubtitleAndPlaybackUi = project.ExcludeSubtitleAndPlaybackUi;
        Detection.EnhanceContrastAndDeskew = project.EnhanceContrastAndDeskew;
        Detection.TrimBottomPx = project.TrimBottomPx;
        Detection.UpscaleForQuality = project.UpscaleForQuality;
        Detection.StreamMaxHeight = project.StreamMaxHeight;

        PdfSettings.PaperSize = project.PaperSize;
        PdfSettings.Columns = project.Columns;
        PdfSettings.MarginMm = project.MarginMm;
        PdfSettings.Dpi = project.Dpi;
        PdfSettings.AddPageNumbers = project.AddPageNumbers;
        PdfSettings.InsertTitleOnFirstPage = project.InsertTitleOnFirstPage;
        PdfSettings.SaveOriginalPngs = project.SaveOriginalPngs;
        PdfSettings.OutputPath = project.OutputPath;
        PdfSettings.ProjectTitle = project.ProjectTitle;

        Captures.Clear();
        foreach (var dto in project.Captures.OrderBy(c => c.Order))
        {
            if (!File.Exists(dto.ImagePath)) continue;
            using var mat = Cv2.ImRead(dto.ImagePath);
            Captures.Add(new CaptureItem
            {
                Order = dto.Order,
                Timestamp = TimeSpan.FromSeconds(dto.TimestampSeconds),
                Thumbnail = ImageProcessor.ToBitmapSource(mat),
                ImagePath = dto.ImagePath,
                SimilarityPercent = dto.SimilarityPercent,
                IsDuplicate = dto.IsDuplicate,
                IsIncluded = dto.IsIncluded,
            });
        }

        if (!string.IsNullOrWhiteSpace(VideoSourceUrl))
        {
            // The saved stream URL may have expired since the project was last saved (YouTube signs these
            // with a short-lived token) — fail silently here; the user can just click "영상 로드" again.
            try
            {
                using var frame = FrameScanner.GrabFrameAt(VideoSourceUrl, TimeSpan.Zero);
                if (frame is not null)
                {
                    _previewFrame?.Dispose();
                    _previewFrame = frame.Clone();
                    PreviewImage = ImageProcessor.ToBitmapSource(_previewFrame);
                }
            }
            catch (Exception)
            {
                // ignore — stale stream URL
            }
        }
    }

    private void ProjectTitleFromVideo(string title)
    {
        PdfSettings.ProjectTitle = title;
        ProjectDisplayName = title;

        var dir = Path.GetDirectoryName(PdfSettings.OutputPath);
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ScoreCap");
        var safeName = SanitizeFileName(title);
        PdfSettings.OutputPath = Path.Combine(dir, (safeName.Length > 0 ? safeName : "output") + ".pdf");
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    private void UpdateExportEstimate()
    {
        var included = Captures.Count(c => c.IsIncluded);
        OutputPageCountText = included.ToString();
        long totalBytes = 0;
        foreach (var c in Captures.Where(c => c.IsIncluded))
        {
            if (File.Exists(c.ImagePath))
                totalBytes += new FileInfo(c.ImagePath).Length;
        }
        ExpectedPdfSizeText = totalBytes > 1024 * 1024
            ? $"{totalBytes / (1024.0 * 1024.0):0.#} MB"
            : $"{totalBytes / 1024.0:0.#} KB";
    }
}
