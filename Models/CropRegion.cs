namespace ScoreCap.Models;

/// <summary>Crop rectangle expressed in source-video pixel coordinates.</summary>
public class CropRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static CropRegion FullFrame(int width, int height) => new() { X = 0, Y = 0, Width = width, Height = height };

    public CropRegion Clamp(int frameWidth, int frameHeight)
    {
        var x = Math.Clamp(X, 0, Math.Max(0, frameWidth - 1));
        var y = Math.Clamp(Y, 0, Math.Max(0, frameHeight - 1));
        var w = Math.Clamp(Width, 1, frameWidth - x);
        var h = Math.Clamp(Height, 1, frameHeight - y);
        return new CropRegion { X = x, Y = y, Width = w, Height = h };
    }
}
