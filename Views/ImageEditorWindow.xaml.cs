using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenCvSharp;
using ScoreCap.Services.Capture;
using WpfPoint = System.Windows.Point;
using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;

namespace ScoreCap.Views;

public partial class ImageEditorWindow : System.Windows.Window
{
    private enum EditTool { Rectangle, Brush }

    private readonly string _imagePath;
    private readonly Stack<Mat> _undoStack = new();
    private const int MaxUndoDepth = 20;

    private Mat _mat;
    private EditTool _tool = EditTool.Rectangle;
    private bool _dragging;
    private WpfPoint _dragStartImagePoint;
    private WpfPoint _lastBrushImagePoint;

    public bool WasSaved { get; private set; }

    public ImageEditorWindow(string imagePath)
    {
        InitializeComponent();

        // RectToolButton.IsChecked starts unset (not "True" in XAML) deliberately: a RadioButton's Checked event
        // fires the instant IsChecked flips to true, even mid-BAML-load — setting it via XAML would fire
        // ToolRadioButton_OnChecked before RectDragPreview/BrushCursor further down in the tree are assigned,
        // crashing with a NullReferenceException. Setting it here, after InitializeComponent, fires the same
        // event once everything actually exists.
        RectToolButton.IsChecked = true;

        _imagePath = imagePath;
        _mat = Cv2.ImRead(imagePath);
        if (_mat.Empty())
            throw new InvalidOperationException($"이미지를 열 수 없습니다: {imagePath}");
        RefreshDisplay();
    }

    private void ToolRadioButton_OnChecked(object sender, RoutedEventArgs e)
    {
        _tool = ReferenceEquals(sender, BrushToolButton) ? EditTool.Brush : EditTool.Rectangle;
        if (RectDragPreview is null || BrushCursor is null) return; // still initializing
        RectDragPreview.Visibility = Visibility.Collapsed;
        BrushCursor.Visibility = Visibility.Collapsed;
    }

    private void ImageElement_OnSizeChanged(object sender, SizeChangedEventArgs e) { }

    private (double scale, double offsetX, double offsetY) GetTransform()
    {
        if (ImageElement.ActualWidth <= 0 || _mat.Width <= 0)
            return (0, 0, 0);

        var renderedW = ImageElement.ActualWidth;
        var renderedH = ImageElement.ActualHeight;
        var scale = renderedW / _mat.Width;
        var offsetX = (ImageContainer.ActualWidth - renderedW) / 2;
        var offsetY = (ImageContainer.ActualHeight - renderedH) / 2;
        return (scale, offsetX, offsetY);
    }

    private WpfPoint ScreenToImagePoint(WpfPoint screenPoint)
    {
        var (scale, offsetX, offsetY) = GetTransform();
        if (scale <= 0) return new WpfPoint(0, 0);

        var x = (screenPoint.X - offsetX) / scale;
        var y = (screenPoint.Y - offsetY) / scale;
        x = Math.Clamp(x, 0, _mat.Width - 1);
        y = Math.Clamp(y, 0, _mat.Height - 1);
        return new WpfPoint(x, y);
    }

    private static CvPoint ToCvPoint(WpfPoint p) => new((int)p.X, (int)p.Y);

    private void RefreshDisplay()
    {
        ImageElement.Source = ImageProcessor.ToBitmapSource(_mat);
    }

    private void PushUndo()
    {
        _undoStack.Push(_mat.Clone());
        while (_undoStack.Count > MaxUndoDepth)
        {
            // drop the oldest snapshot (bottom of the stack) to bound memory use
            var items = _undoStack.ToArray();
            items[^1].Dispose();
            _undoStack.Clear();
            for (var i = items.Length - 2; i >= 0; i--)
                _undoStack.Push(items[i]);
        }
    }

    private void UndoButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_undoStack.Count == 0) return;
        _mat.Dispose();
        _mat = _undoStack.Pop();
        RefreshDisplay();
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        var original = Cv2.ImRead(_imagePath);
        if (original.Empty()) return;
        while (_undoStack.Count > 0) _undoStack.Pop().Dispose();
        _mat.Dispose();
        _mat = original;
        RefreshDisplay();
    }

    private void OverlayCanvas_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = ScreenToImagePoint(e.GetPosition(OverlayCanvas));
        _dragging = true;
        OverlayCanvas.CaptureMouse();

        if (_tool == EditTool.Brush)
        {
            PushUndo();
            _lastBrushImagePoint = point;
            PaintBrushDot(point);
            RefreshDisplay();
        }
        else
        {
            _dragStartImagePoint = point;
        }
    }

    private void OverlayCanvas_OnMouseMove(object sender, MouseEventArgs e)
    {
        var point = ScreenToImagePoint(e.GetPosition(OverlayCanvas));
        var (scale, offsetX, offsetY) = GetTransform();

        if (_tool == EditTool.Brush && scale > 0)
        {
            var radius = BrushSizeSlider.Value * scale;
            BrushCursor.Width = radius * 2;
            BrushCursor.Height = radius * 2;
            Canvas.SetLeft(BrushCursor, offsetX + point.X * scale - radius);
            Canvas.SetTop(BrushCursor, offsetY + point.Y * scale - radius);
            BrushCursor.Visibility = Visibility.Visible;
        }
        else
        {
            BrushCursor.Visibility = Visibility.Collapsed;
        }

        if (!_dragging) return;

        if (_tool == EditTool.Brush)
        {
            Cv2.Line(_mat, ToCvPoint(_lastBrushImagePoint), ToCvPoint(point), Scalar.White,
                (int)(BrushSizeSlider.Value * 2), LineTypes.AntiAlias);
            _lastBrushImagePoint = point;
            RefreshDisplay();
        }
        else if (scale > 0)
        {
            var x0 = Math.Min(_dragStartImagePoint.X, point.X);
            var y0 = Math.Min(_dragStartImagePoint.Y, point.Y);
            var w = Math.Abs(point.X - _dragStartImagePoint.X);
            var h = Math.Abs(point.Y - _dragStartImagePoint.Y);

            RectDragPreview.Visibility = Visibility.Visible;
            Canvas.SetLeft(RectDragPreview, offsetX + x0 * scale);
            Canvas.SetTop(RectDragPreview, offsetY + y0 * scale);
            RectDragPreview.Width = w * scale;
            RectDragPreview.Height = h * scale;
        }
    }

    private void OverlayCanvas_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        OverlayCanvas.ReleaseMouseCapture();

        if (_tool == EditTool.Rectangle)
        {
            var end = ScreenToImagePoint(e.GetPosition(OverlayCanvas));
            var x0 = (int)Math.Min(_dragStartImagePoint.X, end.X);
            var y0 = (int)Math.Min(_dragStartImagePoint.Y, end.Y);
            var w = (int)Math.Abs(end.X - _dragStartImagePoint.X);
            var h = (int)Math.Abs(end.Y - _dragStartImagePoint.Y);

            RectDragPreview.Visibility = Visibility.Collapsed;

            if (w > 2 && h > 2)
            {
                PushUndo();
                var rect = new CvRect(x0, y0, Math.Min(w, _mat.Width - x0), Math.Min(h, _mat.Height - y0));
                Cv2.Rectangle(_mat, rect, Scalar.White, thickness: -1);
                RefreshDisplay();
            }
        }
    }

    private void OverlayCanvas_OnMouseLeave(object sender, MouseEventArgs e)
    {
        BrushCursor.Visibility = Visibility.Collapsed;
    }

    private void PaintBrushDot(WpfPoint imagePoint)
    {
        var radius = (int)BrushSizeSlider.Value;
        Cv2.Circle(_mat, ToCvPoint(imagePoint), radius, Scalar.White, thickness: -1, LineTypes.AntiAlias);
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        ImageProcessor.SaveAsPng(_mat, _imagePath);
        WasSaved = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
