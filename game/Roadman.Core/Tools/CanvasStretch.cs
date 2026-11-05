namespace Roadman.Core.Tools;

/// <summary>
/// The 2D canvas size Godot's <c>canvas_items</c> stretch mode with the <c>expand</c> aspect lays controls out in, so
/// the <c>--menu</c> screenshots show the menus as the window does.
/// </summary>
public static class CanvasStretch
{
    /// <summary>
    /// The base size (<c>display/window/size/viewport_width</c> and <c>_height</c>) widened or heightened to the
    /// aspect ratio of an image of <paramref name="imageWidth"/> × <paramref name="imageHeight"/>: the image divided by
    /// the smaller of its two scale factors against the base, rounded to whole pixels (halves away from zero). Any
    /// size not above 0 gives the image size unchanged.
    /// </summary>
    public static (int Width, int Height) ExpandedSize(int baseWidth, int baseHeight, int imageWidth, int imageHeight)
    {
        if (baseWidth <= 0 || baseHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
        {
            return (imageWidth, imageHeight);
        }

        var scale = Math.Min((double)imageWidth / baseWidth, (double)imageHeight / baseHeight);
        return (
            (int)Math.Round(imageWidth / scale, MidpointRounding.AwayFromZero),
            (int)Math.Round(imageHeight / scale, MidpointRounding.AwayFromZero));
    }
}
