using System.Windows;
using System.Windows.Threading;
using PdfSharp.Fonts;
using ScoreCap.Services.Pdf;

namespace ScoreCap;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        GlobalFontSettings.FontResolver = new AppFontResolver();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>Last-resort safety net: an unhandled exception on the UI thread would otherwise silently kill the
    /// whole app (WPF's default behavior). Show what went wrong instead and keep the app running.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"예상치 못한 오류가 발생했지만 프로그램은 계속 실행됩니다.\n\n{e.Exception.Message}",
            "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
