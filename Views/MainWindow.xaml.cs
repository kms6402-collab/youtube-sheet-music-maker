using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private bool _movingCrop;
    private bool _suppressCropTextSync;
    private Point _dragStartImagePoint;
    private Point _moveStartMouseImagePoint;
    private CropRegion _moveStartCrop = new();

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

    private const string GitHubRepoUrl = "https://github.com/kms6402-collab/youtube-sheet-music-maker";

    private void AboutMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            "유튜브 악보 메이커 v2.2.0\n영상에서 악보 프레임을 캡처해 한 권의 PDF로 만듭니다.\n\n" + GitHubRepoUrl,
            "정보", MessageBoxButton.OK, MessageBoxImage.Information);

    private void GitHubDownloadMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"{GitHubRepoUrl}/releases")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"브라우저를 열 수 없습니다.\n{GitHubRepoUrl}/releases\n\n{ex.Message}",
                "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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
        var left = offsetX + ViewModel.CropX * scale;
        var top = offsetY + ViewModel.CropY * scale;
        var width = Math.Max(0, ViewModel.CropWidth * scale);
        var height = Math.Max(0, ViewModel.CropHeight * scale);
        Canvas.SetLeft(CropRectShape, left);
        Canvas.SetTop(CropRectShape, top);
        CropRectShape.Width = width;
        CropRectShape.Height = height;

        PositionHandle(TopLeftHandle, left, top);
        PositionHandle(TopRightHandle, left + width, top);
        PositionHandle(BottomLeftHandle, left, top + height);
        PositionHandle(BottomRightHandle, left + width, top + height);

        SetCropTextBoxes(ViewModel.CropX * scale, ViewModel.CropY * scale, ViewModel.CropWidth * scale, ViewModel.CropHeight * scale);
    }

    private static void PositionHandle(Thumb handle, double centerX, double centerY)
    {
        handle.Visibility = Visibility.Visible;
        Canvas.SetLeft(handle, centerX - handle.Width / 2);
        Canvas.SetTop(handle, centerY - handle.Height / 2);
    }

    /// <summary>Drags a crop corner: resizes from that corner while the opposite corner stays put.</summary>
    private void CropHandle_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string corner }) return;
        var (scale, _, _) = GetPreviewTransform();
        if (scale <= 0) return;

        var dx = (int)Math.Round(e.HorizontalChange / scale);
        var dy = (int)Math.Round(e.VerticalChange / scale);
        if (dx == 0 && dy == 0) return;

        var x = ViewModel.CropX;
        var y = ViewModel.CropY;
        var w = ViewModel.CropWidth;
        var h = ViewModel.CropHeight;

        switch (corner)
        {
            case "TopLeft": x += dx; y += dy; w -= dx; h -= dy; break;
            case "TopRight": y += dy; w += dx; h -= dy; break;
            case "BottomLeft": x += dx; w -= dx; h += dy; break;
            case "BottomRight": w += dx; h += dy; break;
        }

        var frameW = ViewModel.PreviewFrameWidth;
        var frameH = ViewModel.PreviewFrameHeight;
        const int minSize = 8;
        x = Math.Clamp(x, 0, frameW - minSize);
        y = Math.Clamp(y, 0, frameH - minSize);
        w = Math.Clamp(w, minSize, frameW - x);
        h = Math.Clamp(h, minSize, frameH - y);

        ViewModel.SetCropRegion(new CropRegion { X = x, Y = y, Width = w, Height = h });
    }

    private void CropHandle_OnDragStarted(object sender, DragStartedEventArgs e)
    {
        // Handles capture their own mouse during the drag — make sure the canvas doesn't also think
        // a new-rectangle draw is in progress underneath.
        _dragging = false;
        _movingCrop = false;
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

    private bool IsInsideCurrentCrop(Point imagePoint) =>
        ViewModel.CropWidth > 0 && ViewModel.CropHeight > 0 &&
        imagePoint.X >= ViewModel.CropX && imagePoint.X <= ViewModel.CropX + ViewModel.CropWidth &&
        imagePoint.Y >= ViewModel.CropY && imagePoint.Y <= ViewModel.CropY + ViewModel.CropHeight;

    private void CropOverlayCanvas_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.PreviewFrameWidth <= 0) return;
        var point = ScreenToImagePoint(e.GetPosition(CropOverlayCanvas));

        if (IsInsideCurrentCrop(point))
        {
            // Grabbing inside the existing rectangle moves it instead of starting a brand new one.
            _movingCrop = true;
            _moveStartMouseImagePoint = point;
            _moveStartCrop = new CropRegion { X = ViewModel.CropX, Y = ViewModel.CropY, Width = ViewModel.CropWidth, Height = ViewModel.CropHeight };
        }
        else
        {
            _dragging = true;
            _dragStartImagePoint = point;
        }
        CropOverlayCanvas.CaptureMouse();
    }

    private void CropOverlayCanvas_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_movingCrop)
        {
            var current = ScreenToImagePoint(e.GetPosition(CropOverlayCanvas));
            var dx = (int)Math.Round(current.X - _moveStartMouseImagePoint.X);
            var dy = (int)Math.Round(current.Y - _moveStartMouseImagePoint.Y);

            var frameW = ViewModel.PreviewFrameWidth;
            var frameH = ViewModel.PreviewFrameHeight;
            var x = Math.Clamp(_moveStartCrop.X + dx, 0, Math.Max(0, frameW - _moveStartCrop.Width));
            var y = Math.Clamp(_moveStartCrop.Y + dy, 0, Math.Max(0, frameH - _moveStartCrop.Height));

            var (scale, offsetX, offsetY) = GetPreviewTransform();
            if (scale <= 0) return;
            CropRectShape.Visibility = Visibility.Visible;
            Canvas.SetLeft(CropRectShape, offsetX + x * scale);
            Canvas.SetTop(CropRectShape, offsetY + y * scale);
            SetCropTextBoxes(x * scale, y * scale, _moveStartCrop.Width * scale, _moveStartCrop.Height * scale);
            return;
        }

        if (!_dragging) return;
        var currentPoint = ScreenToImagePoint(e.GetPosition(CropOverlayCanvas));
        DrawLiveDragRect(_dragStartImagePoint, currentPoint);
    }

    private void CropOverlayCanvas_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CropOverlayCanvas.ReleaseMouseCapture();

        if (_movingCrop)
        {
            _movingCrop = false;
            var current = ScreenToImagePoint(e.GetPosition(CropOverlayCanvas));
            var dx = (int)Math.Round(current.X - _moveStartMouseImagePoint.X);
            var dy = (int)Math.Round(current.Y - _moveStartMouseImagePoint.Y);

            var frameW = ViewModel.PreviewFrameWidth;
            var frameH = ViewModel.PreviewFrameHeight;
            var x = Math.Clamp(_moveStartCrop.X + dx, 0, Math.Max(0, frameW - _moveStartCrop.Width));
            var y = Math.Clamp(_moveStartCrop.Y + dy, 0, Math.Max(0, frameH - _moveStartCrop.Height));

            ViewModel.SetCropRegion(new CropRegion { X = x, Y = y, Width = _moveStartCrop.Width, Height = _moveStartCrop.Height });
            return;
        }

        if (!_dragging) return;
        _dragging = false;

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
