using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using ScoreCap.Services.Capture;
using WpfPoint = System.Windows.Point;
using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;

namespace ScoreCap.Views;

public partial class ImageEditorWindow : System.Windows.Window
{
    private enum EditTool { RectEraser, BrushEraser, LineEraser, Lasso, BrushDraw, LineDraw, Text }

    private const double MinZoom = 0.5;
    private const double MaxZoom = 6.0;
    private const double ZoomStep = 1.15;

    private readonly string _imagePath;
    private readonly Stack<Mat> _undoStack = new();
    private const int MaxUndoDepth = 20;

    private Mat _mat;
    private EditTool _tool = EditTool.RectEraser;
    private bool _dragging;
    private WpfPoint _dragStartImagePoint;
    private WpfPoint _lastBrushImagePoint;
    private List<WpfPoint>? _lassoImagePoints;

    private double _zoom = 1.0;
    private double _baseFitWidth;
    private double _baseFitHeight;

    public bool WasSaved { get; private set; }

    public ImageEditorWindow(string imagePath)
    {
        InitializeComponent();

        // RectToolButton.IsChecked starts unset (not "True" in XAML) deliberately: a RadioButton's Checked event
        // fires the instant IsChecked flips to true, even mid-BAML-load — setting it via XAML would fire
        // ToolRadioButton_OnChecked before RectDragPreview/BrushCursor/LineDragPreview/LassoPreview further down
        // in the tree are assigned, crashing with a NullReferenceException. Setting it here, after
        // InitializeComponent, fires the same event once everything actually exists.
        RectToolButton.IsChecked = true;

        _imagePath = imagePath;
        _mat = Cv2.ImRead(imagePath);
        if (_mat.Empty())
            throw new InvalidOperationException($"이미지를 열 수 없습니다: {imagePath}");
        RefreshDisplay();
    }

    private void ToolRadioButton_OnChecked(object sender, RoutedEventArgs e)
    {
        _tool = sender switch
        {
            var s when ReferenceEquals(s, BrushToolButton) => EditTool.BrushEraser,
            var s when ReferenceEquals(s, LineToolButton) => EditTool.LineEraser,
            var s when ReferenceEquals(s, LassoToolButton) => EditTool.Lasso,
            var s when ReferenceEquals(s, DrawToolButton) => EditTool.BrushDraw,
            var s when ReferenceEquals(s, LineDrawToolButton) => EditTool.LineDraw,
            var s when ReferenceEquals(s, TextToolButton) => EditTool.Text,
            _ => EditTool.RectEraser,
        };

        if (RectDragPreview is null || BrushCursor is null || LineDragPreview is null) return; // still initializing
        RectDragPreview.Visibility = Visibility.Collapsed;
        BrushCursor.Visibility = Visibility.Collapsed;
        LineDragPreview.Visibility = Visibility.Collapsed;
        // LassoPreview is intentionally left alone here: a finished lasso selection should survive switching to
        // another tool (e.g. drawing a lasso, then using the rectangle eraser inside it), and only gets cleared
        // when a new lasso is started or "자동 영역" consumes it.

        var usesBrushSize = _tool is EditTool.BrushEraser or EditTool.LineEraser or EditTool.BrushDraw or EditTool.LineDraw;
        BrushSizeSlider.IsEnabled = usesBrushSize;
        SizeSliderLabel.Text = usesBrushSize ? "굵기" : "굵기 (해당 없음)";
    }

    private void ImageElement_OnSizeChanged(object sender, SizeChangedEventArgs e) { }

    private void CanvasScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecomputeBaseFit();
        ApplyZoom();
    }

    /// <summary>Recomputes the "fit to viewport at 100%" size — the baseline that <see cref="_zoom"/> multiplies.
    /// Reading it from the ScrollViewer's viewport (not ImageContainer) because ImageContainer now auto-sizes to
    /// the Image's explicit Width/Height, so it no longer reflects the available screen space on its own.</summary>
    private void RecomputeBaseFit()
    {
        var viewportW = CanvasScrollViewer.ViewportWidth > 0 ? CanvasScrollViewer.ViewportWidth : CanvasScrollViewer.ActualWidth;
        var viewportH = CanvasScrollViewer.ViewportHeight > 0 ? CanvasScrollViewer.ViewportHeight : CanvasScrollViewer.ActualHeight;
        if (viewportW <= 0 || viewportH <= 0 || _mat.Width <= 0 || _mat.Height <= 0) return;

        var fitScale = Math.Min(viewportW / _mat.Width, viewportH / _mat.Height);
        _baseFitWidth = _mat.Width * fitScale;
        _baseFitHeight = _mat.Height * fitScale;
    }

    private void ApplyZoom()
    {
        if (_baseFitWidth <= 0 || _baseFitHeight <= 0) return;
        ImageElement.Width = _baseFitWidth * _zoom;
        ImageElement.Height = _baseFitHeight * _zoom;
        ZoomLabel.Text = $"확대 {_zoom * 100:0}%";
        UpdateLassoPreview();
    }

    /// <summary>Wheel-to-zoom, anchored at the cursor so the pixel under the mouse stays put — lets the user zoom
    /// in for detailed edits without losing their place. Handled at Preview so it replaces the ScrollViewer's
    /// default wheel-scroll instead of fighting it.</summary>
    private void CanvasScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_baseFitWidth <= 0) return;

        var cursorViewportPoint = e.GetPosition(CanvasScrollViewer);
        var oldContentW = ImageElement.Width;
        var oldContentH = ImageElement.Height;
        if (oldContentW <= 0 || oldContentH <= 0) return;

        var fractionX = (CanvasScrollViewer.HorizontalOffset + cursorViewportPoint.X) / oldContentW;
        var fractionY = (CanvasScrollViewer.VerticalOffset + cursorViewportPoint.Y) / oldContentH;

        var factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        var newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001) return;

        _zoom = newZoom;
        ApplyZoom();

        var newContentW = ImageElement.Width;
        var newContentH = ImageElement.Height;
        Dispatcher.InvokeAsync(() =>
        {
            CanvasScrollViewer.ScrollToHorizontalOffset(fractionX * newContentW - cursorViewportPoint.X);
            CanvasScrollViewer.ScrollToVerticalOffset(fractionY * newContentH - cursorViewportPoint.Y);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

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

    /// <summary>Clamps to the full [0, Width]/[0, Height] range (not Width-1/Height-1) so a drag that reaches the
    /// image edge can produce a rectangle/line that actually covers the last row/column of pixels — OpenCV's
    /// drawing functions clip coordinates at the image bounds safely, so there is no out-of-range risk.</summary>
    private WpfPoint ScreenToImagePoint(WpfPoint screenPoint)
    {
        var (scale, offsetX, offsetY) = GetTransform();
        if (scale <= 0) return new WpfPoint(0, 0);

        var x = (screenPoint.X - offsetX) / scale;
        var y = (screenPoint.Y - offsetY) / scale;
        x = Math.Clamp(x, 0, _mat.Width);
        y = Math.Clamp(y, 0, _mat.Height);
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
        ClearLasso();
        RefreshDisplay();
    }

    private void AutoDetectButton_OnClick(object sender, RoutedEventArgs e)
    {
        var regions = TextRegionDetector.FindTextRegions(_mat);
        if (regions.Count == 0)
        {
            MessageBox.Show("한글로 보이는 부분을 찾지 못했습니다.", "자동 인식", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PushUndo();
        foreach (var region in regions)
            Cv2.Rectangle(_mat, region, Scalar.White, thickness: -1);
        RefreshDisplay();
    }

    /// <summary>Runs the same glyph-cluster heuristic as "자동 인식", but restricted to the area the user outlined
    /// with the 올가미(lasso) tool: crops to the lasso's bounding box (smaller/cleaner input for the heuristic),
    /// then intersects the detected glyph regions with the actual lasso polygon (not just its bounding box) so a
    /// stray corner of the box can't erase notation the user didn't circle.</summary>
    private void AutoRegionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_lassoImagePoints is not { Count: >= 3 })
        {
            MessageBox.Show("먼저 올가미 도구로 영역을 지정해주세요.", "자동 영역", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var xs = _lassoImagePoints.Select(p => p.X).ToList();
        var ys = _lassoImagePoints.Select(p => p.Y).ToList();
        var x0 = (int)Math.Clamp(Math.Floor(xs.Min()), 0, _mat.Width - 1);
        var y0 = (int)Math.Clamp(Math.Floor(ys.Min()), 0, _mat.Height - 1);
        var x1 = (int)Math.Clamp(Math.Ceiling(xs.Max()), x0 + 1, _mat.Width);
        var y1 = (int)Math.Clamp(Math.Ceiling(ys.Max()), y0 + 1, _mat.Height);
        var boundsRect = new CvRect(x0, y0, x1 - x0, y1 - y0);

        using var subImage = new Mat(_mat, boundsRect); // view into _mat — writes below land directly on it

        // Reference the FULL image's dimensions, not the (usually much smaller) crop's own — the detector's size
        // thresholds are relative fractions, so using the crop's own size would reject ordinary-sized text as
        // "too big for this tiny region" purely because the user drew a tight lasso around it.
        var localRegions = TextRegionDetector.FindTextRegions(subImage, _mat.Width, _mat.Height);

        using var eraseMask = new Mat(subImage.Size(), MatType.CV_8UC1, Scalar.Black);
        foreach (var r in localRegions)
            Cv2.Rectangle(eraseMask, r, Scalar.White, thickness: -1);

        var polygonLocal = _lassoImagePoints
            .Select(p => new CvPoint((int)Math.Round(p.X - x0), (int)Math.Round(p.Y - y0)))
            .ToArray();
        using var lassoMask = new Mat(subImage.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.FillPoly(lassoMask, new[] { polygonLocal }, Scalar.White);

        using var finalMask = new Mat();
        Cv2.BitwiseAnd(eraseMask, lassoMask, finalMask);

        if (Cv2.CountNonZero(finalMask) == 0)
        {
            MessageBox.Show("선택한 영역에서 한글로 보이는 부분을 찾지 못했습니다.", "자동 영역", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PushUndo();
        subImage.SetTo(Scalar.White, finalMask);
        ClearLasso();
        RefreshDisplay();
    }

    private void ClearLasso()
    {
        _lassoImagePoints = null;
        LassoPreview.Visibility = Visibility.Collapsed;
        AutoRegionButton.IsEnabled = false;
    }

    private void UpdateLassoPreview()
    {
        if (_lassoImagePoints is not { Count: > 0 })
        {
            LassoPreview.Visibility = Visibility.Collapsed;
            return;
        }

        var (scale, offsetX, offsetY) = GetTransform();
        if (scale <= 0) return;

        LassoPreview.Points = new PointCollection(
            _lassoImagePoints.Select(p => new WpfPoint(offsetX + p.X * scale, offsetY + p.Y * scale)));
        LassoPreview.Visibility = Visibility.Visible;
    }

    private void OverlayCanvas_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = ScreenToImagePoint(e.GetPosition(OverlayCanvas));

        if (_tool == EditTool.Text)
        {
            PromptAndStampText(point);
            return;
        }

        _dragging = true;
        OverlayCanvas.CaptureMouse();

        switch (_tool)
        {
            case EditTool.BrushEraser:
            case EditTool.BrushDraw:
                PushUndo();
                _lastBrushImagePoint = point;
                PaintBrushDot(point);
                RefreshDisplay();
                break;

            case EditTool.Lasso:
                _lassoImagePoints = new List<WpfPoint> { point };
                AutoRegionButton.IsEnabled = false;
                UpdateLassoPreview();
                break;

            default:
                _dragStartImagePoint = point;
                break;
        }
    }

    private void OverlayCanvas_OnMouseMove(object sender, MouseEventArgs e)
    {
        var point = ScreenToImagePoint(e.GetPosition(OverlayCanvas));
        var (scale, offsetX, offsetY) = GetTransform();

        var showBrushCursor = _tool is EditTool.BrushEraser or EditTool.BrushDraw && scale > 0;
        if (showBrushCursor)
        {
            var radius = BrushSizeSlider.Value * scale;
            BrushCursor.Width = radius * 2;
            BrushCursor.Height = radius * 2;
            Canvas.SetLeft(BrushCursor, offsetX + point.X * scale - radius);
            Canvas.SetTop(BrushCursor, offsetY + point.Y * scale - radius);
            BrushCursor.Stroke = _tool == EditTool.BrushDraw
                ? System.Windows.Media.Brushes.Black
                : (System.Windows.Media.Brush)FindResource("AccentBrush");
            BrushCursor.Visibility = Visibility.Visible;
        }
        else
        {
            BrushCursor.Visibility = Visibility.Collapsed;
        }

        if (!_dragging) return;

        switch (_tool)
        {
            case EditTool.BrushEraser:
            case EditTool.BrushDraw:
                var brushColor = _tool == EditTool.BrushDraw ? Scalar.Black : Scalar.White;
                Cv2.Line(_mat, ToCvPoint(_lastBrushImagePoint), ToCvPoint(point), brushColor,
                    (int)(BrushSizeSlider.Value * 2), LineTypes.AntiAlias);
                _lastBrushImagePoint = point;
                RefreshDisplay();
                break;

            case EditTool.LineEraser when scale > 0:
            case EditTool.LineDraw when scale > 0:
                LineDragPreview.Stroke = _tool == EditTool.LineDraw
                    ? System.Windows.Media.Brushes.Black
                    : (System.Windows.Media.Brush)FindResource("AccentBrush");
                LineDragPreview.Visibility = Visibility.Visible;
                LineDragPreview.X1 = offsetX + _dragStartImagePoint.X * scale;
                LineDragPreview.Y1 = offsetY + _dragStartImagePoint.Y * scale;
                LineDragPreview.X2 = offsetX + point.X * scale;
                LineDragPreview.Y2 = offsetY + point.Y * scale;
                LineDragPreview.StrokeThickness = Math.Max(2, BrushSizeSlider.Value * 2 * scale);
                break;

            case EditTool.RectEraser when scale > 0:
                var x0 = Math.Min(_dragStartImagePoint.X, point.X);
                var y0 = Math.Min(_dragStartImagePoint.Y, point.Y);
                var w = Math.Abs(point.X - _dragStartImagePoint.X);
                var h = Math.Abs(point.Y - _dragStartImagePoint.Y);

                RectDragPreview.Visibility = Visibility.Visible;
                Canvas.SetLeft(RectDragPreview, offsetX + x0 * scale);
                Canvas.SetTop(RectDragPreview, offsetY + y0 * scale);
                RectDragPreview.Width = w * scale;
                RectDragPreview.Height = h * scale;
                break;

            case EditTool.Lasso:
                _lassoImagePoints?.Add(point);
                UpdateLassoPreview();
                break;
        }
    }

    private void OverlayCanvas_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        OverlayCanvas.ReleaseMouseCapture();

        var end = ScreenToImagePoint(e.GetPosition(OverlayCanvas));

        switch (_tool)
        {
            case EditTool.RectEraser:
            {
                RectDragPreview.Visibility = Visibility.Collapsed;
                var x0 = (int)Math.Min(_dragStartImagePoint.X, end.X);
                var y0 = (int)Math.Min(_dragStartImagePoint.Y, end.Y);
                var w = (int)Math.Abs(end.X - _dragStartImagePoint.X);
                var h = (int)Math.Abs(end.Y - _dragStartImagePoint.Y);

                if (w > 2 && h > 2)
                {
                    PushUndo();
                    var rect = new CvRect(x0, y0, Math.Min(w, _mat.Width - x0), Math.Min(h, _mat.Height - y0));
                    Cv2.Rectangle(_mat, rect, Scalar.White, thickness: -1);
                    RefreshDisplay();
                }
                break;
            }

            case EditTool.LineEraser:
            case EditTool.LineDraw:
            {
                LineDragPreview.Visibility = Visibility.Collapsed;
                var dx = end.X - _dragStartImagePoint.X;
                var dy = end.Y - _dragStartImagePoint.Y;

                if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1)
                {
                    PushUndo();
                    var color = _tool == EditTool.LineDraw ? Scalar.Black : Scalar.White;
                    Cv2.Line(_mat, ToCvPoint(_dragStartImagePoint), ToCvPoint(end), color,
                        (int)(BrushSizeSlider.Value * 2), LineTypes.AntiAlias);
                    RefreshDisplay();
                }
                break;
            }

            case EditTool.Lasso:
                _lassoImagePoints?.Add(end);
                if (_lassoImagePoints is { Count: >= 3 })
                {
                    AutoRegionButton.IsEnabled = true;
                    UpdateLassoPreview();
                }
                else
                {
                    ClearLasso();
                }
                break;
        }
    }

    private void OverlayCanvas_OnMouseLeave(object sender, MouseEventArgs e)
    {
        BrushCursor.Visibility = Visibility.Collapsed;
    }

    private void PaintBrushDot(WpfPoint imagePoint)
    {
        var radius = (int)BrushSizeSlider.Value;
        var color = _tool == EditTool.BrushDraw ? Scalar.Black : Scalar.White;
        Cv2.Circle(_mat, ToCvPoint(imagePoint), radius, color, thickness: -1, LineTypes.AntiAlias);
    }

    private void PromptAndStampText(WpfPoint imagePoint)
    {
        var dialog = new TextInputDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(dialog.ResultText)) return;

        PushUndo();
        StampText(imagePoint, dialog.ResultText, dialog.ResultFontSize);
        RefreshDisplay();
    }

    /// <summary>Renders text through WPF's text stack (so Hangul renders correctly) onto a transparent bitmap,
    /// then alpha-composites it onto the capture at the clicked point. Manual per-pixel blend instead of Mat
    /// arithmetic: the stamp is at most a few thousand pixels, so a plain loop is simpler and fast enough.</summary>
    private void StampText(WpfPoint imagePoint, string text, double fontSize)
    {
        var typeface = new Typeface(new System.Windows.Media.FontFamily("Malgun Gothic"), FontStyles.Normal,
            FontWeights.SemiBold, FontStretches.Normal);
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, fontSize, System.Windows.Media.Brushes.Black, 96.0);

        const double pad = 4.0;
        var stampW = (int)Math.Ceiling(formatted.Width + pad * 2);
        var stampH = (int)Math.Ceiling(formatted.Height + pad * 2);
        if (stampW <= 0 || stampH <= 0) return;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawText(formatted, new WpfPoint(pad, pad));

        var rtb = new RenderTargetBitmap(stampW, stampH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var stride = stampW * 4;
        var buffer = new byte[stride * stampH];
        rtb.CopyPixels(buffer, stride, 0);

        var x0 = Math.Clamp((int)Math.Round(imagePoint.X), 0, Math.Max(0, _mat.Width - stampW));
        var y0 = Math.Clamp((int)Math.Round(imagePoint.Y - stampH / 2.0), 0, Math.Max(0, _mat.Height - stampH));
        var destW = Math.Min(stampW, _mat.Width - x0);
        var destH = Math.Min(stampH, _mat.Height - y0);
        if (destW <= 0 || destH <= 0) return;

        for (var j = 0; j < destH; j++)
        {
            for (var i = 0; i < destW; i++)
            {
                var srcIdx = j * stride + i * 4;
                var alpha = buffer[srcIdx + 3];
                if (alpha == 0) continue;

                var b = buffer[srcIdx + 0];
                var g = buffer[srcIdx + 1];
                var r = buffer[srcIdx + 2];
                var inv = 1.0 - alpha / 255.0;

                var dst = _mat.At<Vec3b>(y0 + j, x0 + i);
                var outB = (byte)Math.Clamp(b + dst.Item0 * inv, 0, 255);
                var outG = (byte)Math.Clamp(g + dst.Item1 * inv, 0, 255);
                var outR = (byte)Math.Clamp(r + dst.Item2 * inv, 0, 255);
                _mat.Set(y0 + j, x0 + i, new Vec3b(outB, outG, outR));
            }
        }
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
