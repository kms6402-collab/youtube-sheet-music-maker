using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ScoreCap.Models;

namespace ScoreCap.Services.Pdf;

public record PdfExportProgress(int Page, int TotalPages, double Percent);

/// <summary>Lays score captures out in a clean, evenly-spaced grid: the page is divided into exactly
/// "단(Columns)" equal-height slots, and each capture is fit (preserving aspect ratio) into its slot — so a
/// chosen "5단" always places exactly 5 systems on every full page, the way a typeset score would.</summary>
public class PdfExporter
{
    private const string FontFamilyName = "Malgun Gothic";
    private const double SlotGap = 4.0;
    private const double TitleBandHeight = 50.0;

    public Task ExportAsync(
        IReadOnlyList<CaptureItem> orderedItems,
        PdfExportSettings settings,
        IProgress<PdfExportProgress>? progress,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var document = new PdfDocument();
            document.Info.Title = settings.ProjectTitle;

            var (pageWidth, pageHeight) = GetPageSizePoints(settings.PaperSize);
            var marginPt = settings.MarginMm * 72.0 / 25.4;
            var perPage = Math.Max(1, settings.Columns);
            var totalPages = Math.Max(1, (int)Math.Ceiling(orderedItems.Count / (double)perPage));

            for (var pageIndex = 0; pageIndex < totalPages; pageIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var pageItems = orderedItems.Skip(pageIndex * perPage).Take(perPage).ToList();
                var pageNumber = pageIndex + 1;
                var showTitle = pageNumber == 1 && settings.InsertTitleOnFirstPage;

                AddGridPage(document, pageItems, settings, pageWidth, pageHeight, marginPt, perPage, showTitle,
                    pageNumber, totalPages);

                progress?.Report(new PdfExportProgress(pageNumber, totalPages, pageNumber * 100.0 / totalPages));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(settings.OutputPath)!);
            document.Save(settings.OutputPath);

            if (settings.SaveOriginalPngs)
            {
                var pngDir = Path.Combine(
                    Path.GetDirectoryName(settings.OutputPath)!,
                    Path.GetFileNameWithoutExtension(settings.OutputPath) + "_원본");
                Directory.CreateDirectory(pngDir);
                for (var idx = 0; idx < orderedItems.Count; idx++)
                {
                    var dest = Path.Combine(pngDir, $"page_{idx + 1:000}.png");
                    File.Copy(orderedItems[idx].ImagePath, dest, overwrite: true);
                }
            }
        }, ct);
    }

    private static (double width, double height) GetPageSizePoints(PaperSize size) => size switch
    {
        PaperSize.A4 => (595.28, 841.89),
        PaperSize.Letter => (612.0, 792.0),
        PaperSize.A3 => (841.89, 1190.55),
        _ => (595.28, 841.89),
    };

    private static void AddGridPage(PdfDocument doc, List<CaptureItem> pageItems, PdfExportSettings settings,
        double pageW, double pageH, double marginPt, int perPage, bool showTitle, int pageNumber, int totalPages)
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(pageW);
        page.Height = XUnit.FromPoint(pageH);
        using var gfx = XGraphics.FromPdfPage(page);

        var titleBandHeight = showTitle ? TitleBandHeight : 0;
        var footerHeight = settings.AddPageNumbers ? 22 : 0;
        var contentLeft = marginPt;
        var contentWidth = pageW - marginPt * 2;
        var contentTop = marginPt + titleBandHeight;
        var contentBottom = pageH - marginPt - footerHeight;
        var contentHeight = contentBottom - contentTop;

        if (showTitle)
            DrawTitleBand(gfx, settings.ProjectTitle, marginPt, pageW, titleBandHeight);

        var cellHeight = (contentHeight - SlotGap * (perPage - 1)) / perPage;

        for (var i = 0; i < pageItems.Count; i++)
        {
            using var image = XImage.FromFile(pageItems[i].ImagePath);
            var slotTop = contentTop + i * (cellHeight + SlotGap);

            var scale = Math.Min(contentWidth / image.PixelWidth, cellHeight / image.PixelHeight);
            var drawWidth = image.PixelWidth * scale;
            var drawHeight = image.PixelHeight * scale;

            var x = contentLeft + (contentWidth - drawWidth) / 2;
            var y = slotTop + (cellHeight - drawHeight) / 2;
            gfx.DrawImage(image, x, y, drawWidth, drawHeight);
        }

        if (settings.AddPageNumbers)
        {
            var font = new XFont(FontFamilyName, 9);
            gfx.DrawString($"{pageNumber} / {totalPages}", font, XBrushes.Gray,
                new XRect(0, pageH - marginPt, pageW, marginPt), XStringFormats.BottomCenter);
        }
    }

    private const double TitleFontSize = 10.0;

    private static void DrawTitleBand(XGraphics gfx, string title, double marginPt, double pageWidth, double bandHeight)
    {
        // Up to two user-entered lines (the title textbox caps input at two); anything past that is dropped
        // rather than overflowing the band. DrawString does NOT treat an embedded '\n' as a line break, so each
        // line is drawn with its own call rather than relying on that.
        var text = string.IsNullOrWhiteSpace(title) ? "Untitled" : title;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 2)
            lines = lines[..2];

        var font = new XFont(FontFamilyName, TitleFontSize, XFontStyleEx.Bold);
        var lineHeight = TitleFontSize * 1.25;
        var textBlockHeight = lineHeight * lines.Length;
        var startY = marginPt * 0.4 + Math.Max(0, (bandHeight - 12 - textBlockHeight) / 2);

        for (var i = 0; i < lines.Length; i++)
        {
            var rect = new XRect(marginPt, startY + i * lineHeight, pageWidth - marginPt * 2, lineHeight);
            gfx.DrawString(lines[i], font, XBrushes.Black, rect, XStringFormats.TopCenter);
        }

        var lineY = marginPt * 0.4 + bandHeight - 8;
        gfx.DrawLine(new XPen(XColors.Gray, 0.75), marginPt, lineY, pageWidth - marginPt, lineY);
    }
}
