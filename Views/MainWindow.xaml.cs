using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScoreCap.Models;
using ScoreCap.Services.Capture;
using ScoreCap.ViewModels;

namespace ScoreCap.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private bool _dragging;
    private bool _suppressCropTextSync;
    private Point _dragStartImagePoint;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.CropX) or nameof(MainViewModel.CropY)
                or nameof(MainViewModel.CropWidth) or nameof(MainViewModel.CropHeight)
                or nameof(MainViewModel.PreviewImage))
            {
                Dispatcher.InvokeAsync(RefreshCropDisplay, System.Windows.Threading.DispatcherPriority.Background);
            }
        };
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Caps the PDF title box at two lines: Enter inserts a line break unless one is already there.</summary>
    private void TitleTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (TitleTextBox.Text.Contains('\n'))
            e.Handled = true; // already have a line break — swallow further Enter presses
    }

    private void AboutMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        MessageBox.Show("ScoreCap – 유튜브 악보 캡처 · PDF 빌더\n영상에서 악보 프레임을 캡처해 한 권의 PDF로 만듭니다.",
            "정보", MessageBoxButton.OK, MessageBoxImage.Information);

    private void DeleteSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = CaptureListBox.SelectedItems.Cast<CaptureItem>().ToList();
        if (selected.Count == 0) return;
        ViewModel.DeleteItems(selected);
    }

    private void EditCaptureButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CaptureItem item }) return;

        if (!File.Exists(item.ImagePath))
        {
            MessageBox.Show("캡처 파일을 찾을 수 없습니다. 이미 삭제되었거나 이동된 것 같습니다.",
                "편집 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var editor = new ImageEditorWindow(item.ImagePath) { Owner = this };
            if (editor.ShowDialog() == true && editor.WasSaved)
            {
                using var mat = OpenCvSharp.Cv2.ImRead(item.ImagePath);
                item.Thumbnail = ImageProcessor.ToBitmapSource(mat);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"이미지를 편집할 수 없습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PreviewImageElement_OnSizeChanged(object sender, SizeChangedEventArgs e) => RefreshCropDisplay();

    private (double scale, double offsetX, double offsetY) GetPreviewTransform()
    {
        var frameW = ViewModel.PreviewFrameWidth;
        var frameH = ViewModel.PreviewFrameHeight;
        if (frameW <= 0 || frameH <= 0 || PreviewImageElement.ActualWidth <= 0)
            return (0, 0, 0);

        var renderedW = PreviewImageElement.ActualWidth;
        var renderedH = PreviewImageElement.ActualHeight;
        var scale = renderedW / frameW;
        var offsetX = (PreviewContainer.ActualWidth - renderedW) / 2;
        var offsetY = (PreviewContainer.ActualHeight - renderedH) / 2;
        return (scale, offsetX, offsetY);
    }

    /// <summary>Redraws the crop rectangle overlay and refreshes the X/Y/W/H textboxes, which show coordinates in
    /// the preview's own on-screen pixels (not the source video's resolution) so what the user reads matches what
    /// they see on screen.</summary>
    private void RefreshCropDisplay()
    {
        var (scale, offsetX, offsetY) = GetPreviewTransform();
        if (scale <= 0)
        {
            CropRectShape.Visibility = Visibility.Collapsed;
            return;
        }

        CropRectShape.Visibility = Visibility.Visible;
        Canvas.SetLeft(CropRectShape, offsetX + ViewModel.CropX * scale);
        Canvas.SetTop(CropRectShape, offsetY + ViewModel.CropY * scale);
        CropRectShape.Width = Math.Max(0, ViewModel.CropWidth * scale);
        CropRectShape.Height = Math.Max(0, ViewModel.CropHeight * scale);

        SetCropTextBoxes(ViewModel.CropX * scale, ViewModel.CropY * scale, ViewModel.CropWidth * scale, ViewModel.CropHeight * scale);
    }

    private void SetCropTextBoxes(double displayX, double displayY, double displayW, double displayH)
    {
        _suppressCropTextSync = true;
        CropXTextBox.Text = ((int)Math.Round(displayX)).ToString(CultureInfo.InvariantCulture);
        CropYTextBox.Text = ((int)Math.Round(displayY)).ToString(CultureInfo.InvariantCulture);
        CropWidthTextBox.Text = ((int)Math.Round(displayW)).ToString(CultureInfo.InvariantCulture);
        CropHeightTextBox.Text = ((int)Math.Round(displayH)).ToString(CultureInfo.InvariantCulture);
        _suppressCropTextSync = false;
    }

    private void CropTextBox_OnLostFocus(object sender, RoutedEventArgs e) => CommitCropTextBoxes();

    private void CropTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitCropTextBoxes();
            Keyboard.ClearFocus();
        }
    }

    private void CommitCropTextBoxes()
    {
        if (_suppressCropTextSync) return;
        var (scale, _, _) = GetPreviewTransform();
        if (scale <= 0) return;

        if (int.TryParse(CropXTextBox.Text, out var dx) && int.TryParse(CropYTextBox.Text, out var dy) &&
            int.TryParse(CropWidthTextBox.Text, out var dw) && int.TryParse(CropHeightTextBox.Text, out var dh))
        {
            ViewModel.SetCropRegion(new CropRegion
            {
                X = (int)Math.Round(dx / scale),
                Y = (int)Math.Round(dy / scale),
                Width = Math.Max(1, (int)Math.Round(dw / scale)),
                Height = Math.Max(1, (int)Math.Round(dh / scale)),
            });
        }
        else
        {
            RefreshCropDisplay(); // invalid input — snap the textboxes back to the current crop
        }
    }

    private Point ScreenToImagePoint(Point screenPoint)
    {
        var (scale, offsetX, offsetY) = GetPreviewTransform();
        if (scale <= 0) return new Point(0, 0);

        var x = (screenPoint.X - offsetX) / scale;
        var y = (screenPoint.Y - offsetY) / scale;
        x = Math.Clamp(x, 0, ViewModel.PreviewFrameWidth);
        y = Math.Clamp(y, 0, ViewModel.PreviewFrameHeight);
        return new Point(x, y);
    }

    private void CropOverlayCanvas_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.PreviewFrameWidth <= 0) return;
        _dragging = true;
        _dragStartImagePoint = ScreenToImagePoint(e.GetPosition(CropOverlayCanvas));
        CropOverlayCanvas.CaptureMouse();
    }

    private void CropOverlayCanvas_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var current = ScreenToImagePoint(e.GetPosition(CropOverlayCanvas));
        DrawLiveDragRect(_dragStartImagePoint, current);
    }

    private void CropOverlayCanvas_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        CropOverlayCanvas.ReleaseMouseCapture();

        var end = ScreenToImagePoint(e.GetPosition(CropOverlayCanvas));
        var x0 = (int)Math.Min(_dragStartImagePoint.X, end.X);
        var y0 = (int)Math.Min(_dragStartImagePoint.Y, end.Y);
        var w = (int)Math.Abs(end.X - _dragStartImagePoint.X);
        var h = (int)Math.Abs(end.Y - _dragStartImagePoint.Y);

        if (w > 8 && h > 8)
            ViewModel.SetCropRegion(new CropRegion { X = x0, Y = y0, Width = w, Height = h });
        else
            RefreshCropDisplay();
    }

    private void DrawLiveDragRect(Point startImagePoint, Point currentImagePoint)
    {
        var (scale, offsetX, offsetY) = GetPreviewTransform();
        if (scale <= 0) return;

        var x0 = Math.Min(startImagePoint.X, currentImagePoint.X);
        var y0 = Math.Min(startImagePoint.Y, currentImagePoint.Y);
        var w = Math.Abs(currentImagePoint.X - startImagePoint.X);
        var h = Math.Abs(currentImagePoint.Y - startImagePoint.Y);

        CropRectShape.Visibility = Visibility.Visible;
        Canvas.SetLeft(CropRectShape, offsetX + x0 * scale);
        Canvas.SetTop(CropRectShape, offsetY + y0 * scale);
        CropRectShape.Width = w * scale;
        CropRectShape.Height = h * scale;

        SetCropTextBoxes(x0 * scale, y0 * scale, w * scale, h * scale);
    }
}
