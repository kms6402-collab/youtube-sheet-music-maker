using System.Windows;
using System.Windows.Input;
using ScoreCap.Services.YtDlp;
using ScoreCap.ViewModels;

namespace ScoreCap.Views;

public partial class SearchWindow : Window
{
    private SearchViewModel ViewModel => (SearchViewModel)DataContext;

    public string? SelectedUrl { get; private set; }

    public SearchWindow(string ytDlpPath)
    {
        InitializeComponent();
        DataContext = new SearchViewModel(ytDlpPath);
        Loaded += (_, _) => QueryTextBox.Focus();
    }

    private void QueryTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel.SearchCommand.CanExecute(null))
            ViewModel.SearchCommand.Execute(null);
    }

    private void ResultsListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedResult is not null)
            Confirm(ViewModel.SelectedResult);
    }

    private void ChooseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedResult is null)
        {
            MessageBox.Show("목록에서 영상을 선택해주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Confirm(ViewModel.SelectedResult);
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Confirm(SearchResult result)
    {
        SelectedUrl = result.Url;
        DialogResult = true;
        Close();
    }
}
