using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScoreCap.Services.Search;
using ScoreCap.Services.YtDlp;

namespace ScoreCap.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private readonly string _ytDlpPath;
    private CancellationTokenSource? _searchCts;

    public ObservableCollection<SearchResult> Results { get; } = new();

    public IReadOnlyList<SearchTarget> Targets { get; } = Enum.GetValues<SearchTarget>();
    public IReadOnlyList<MatchMode> Modes { get; } = Enum.GetValues<MatchMode>();

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private SearchTarget _target = SearchTarget.Title;

    [ObservableProperty]
    private MatchMode _mode = MatchMode.Plain;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = "검색어를 입력하고 검색을 눌러주세요.";

    [ObservableProperty]
    private SearchResult? _selectedResult;

    public SearchViewModel(string ytDlpPath)
    {
        _ytDlpPath = ytDlpPath;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsSearching = true;
        StatusText = "검색 중...";
        Results.Clear();

        try
        {
            var seed = TextMatcher.ExtractPlainSeed(Query);
            var downloader = new YtDlpDownloader(_ytDlpPath);
            var candidates = await downloader.SearchAsync(seed, 30, ct);

            var matched = candidates.Where(r =>
                TextMatcher.IsMatch(Target == SearchTarget.Title ? r.Title : r.Uploader, Query, Mode)).ToList();

            // If the pattern filtered everything out (e.g. an overly strict regex), fall back to the raw
            // keyword results so the user isn't left with an empty list for a reasonable search term.
            var toShow = matched.Count > 0 ? matched : candidates;
            foreach (var r in toShow)
                Results.Add(r);

            StatusText = Results.Count == 0
                ? "검색 결과가 없습니다."
                : matched.Count > 0
                    ? $"{Results.Count}건 (패턴 일치)"
                    : $"{Results.Count}건 (패턴에 맞는 항목이 없어 전체 검색 결과를 표시합니다)";
        }
        catch (OperationCanceledException)
        {
            StatusText = "취소됨";
        }
        catch (Exception ex)
        {
            StatusText = "검색 실패";
            MessageBox.Show($"검색 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSearching = false;
        }
    }
}
