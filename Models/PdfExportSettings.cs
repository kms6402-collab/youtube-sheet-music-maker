using CommunityToolkit.Mvvm.ComponentModel;

namespace ScoreCap.Models;

public enum PaperSize { A4, Letter, A3 }

public partial class PdfExportSettings : ObservableObject
{
    [ObservableProperty]
    private PaperSize _paperSize = PaperSize.A4;

    /// <summary>Number of score images stacked per page (1-5).</summary>
    [ObservableProperty]
    private int _columns = 5;

    [ObservableProperty]
    private double _marginMm = 12;

    [ObservableProperty]
    private int _dpi = 300;

    [ObservableProperty]
    private bool _addPageNumbers = true;

    /// <summary>When on, ProjectTitle is printed as a heading at the top of the first content page
    /// (no separate blank cover page).</summary>
    [ObservableProperty]
    private bool _insertTitleOnFirstPage;

    [ObservableProperty]
    private bool _saveOriginalPngs = true;

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private string _projectTitle = "Untitled";
}
