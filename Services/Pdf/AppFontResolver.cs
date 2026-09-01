using System.IO;
using PdfSharp.Fonts;

namespace ScoreCap.Services.Pdf;

/// <summary>Resolves "Malgun Gothic" (맑은 고딕) directly from the Windows Fonts folder so PdfSharp 6.x (which no
/// longer relies on GDI+ for font lookup) can render text without extra configuration. Segoe UI — the previous
/// choice — has no Hangul glyphs, so Korean titles rendered as broken/missing characters; Malgun Gothic covers
/// both Hangul and Latin, and ships with every Windows 7+ install.</summary>
public class AppFontResolver : IFontResolver
{
    private const string FamilyName = "Malgun Gothic";
    private static readonly string FontsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    public string DefaultFontName => FamilyName;

    public byte[] GetFont(string faceName)
    {
        var fileName = faceName switch
        {
            "MalgunGothic#Bold" => "malgunbd.ttf",
            _ => "malgun.ttf",
        };
        var path = Path.Combine(FontsDir, fileName);
        if (File.Exists(path))
            return File.ReadAllBytes(path);

        // Fallback for the rare machine missing Malgun Gothic — won't render Hangul, but keeps PDF export working.
        var fallback = Path.Combine(FontsDir, "segoeui.ttf");
        return File.ReadAllBytes(File.Exists(fallback) ? fallback : path);
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        return new FontResolverInfo(isBold ? "MalgunGothic#Bold" : "MalgunGothic#Regular");
    }
}
