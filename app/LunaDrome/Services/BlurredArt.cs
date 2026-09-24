using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace LunaDrome.Services;

/// <summary>
/// Stand-in for the WPF app's &lt;BlurEffect Radius="50"/&gt; header backdrops (Home hero, artist
/// header) - WinUI/Uno has no element blur effect. Renders the cover art cropped to the banner's
/// aspect ratio, blurred with SkiaSharp (bundled with Uno's Skia renderer), onto a canvas with
/// transparent padding on every side. The padding matters: WPF's BlurEffect bleeds past the
/// element's bounds, which is the soft colored glow visible around the live app's banners - the
/// caller shows this bitmap with a matching negative margin to reproduce it.
/// </summary>
public static class BlurredArt
{
    // Banner area (content) and glow padding, in bitmap pixels. Displayed ~2.3x larger in the app
    // (936x232 banner), so 20px padding = ~46px of glow, matching BlurEffect Radius 50.
    public const int ContentWidth = 400;
    public const int ContentHeight = 100;
    public const int Padding = 20;
    private const float Sigma = 7f;

    private static readonly HttpClient Http = new();

    public static async Task<BitmapImage?> LoadAsync(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            var png = await Task.Run(() => Render(bytes));
            if (png == null)
                return null;

            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(png);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch
        {
            return null; // backdrop is decorative - no art just means a plain card
        }
    }

    /// <summary>
    /// Average color of the cover art, for the album page's tinted header (Feishin derives that
    /// header color from the cover the same way - an average, via fast-average-color).
    /// </summary>
    public static async Task<Windows.UI.Color?> AverageColorAsync(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            return await Task.Run<Windows.UI.Color?>(() =>
            {
                using var source = SKImage.FromEncodedData(bytes);
                if (source == null)
                    return null;

                // Downscale to 1x1 with mipmapped linear sampling = the average of all pixels.
                using var surface = SKSurface.Create(new SKImageInfo(1, 1));
                surface.Canvas.DrawImage(source, SKRect.Create(0, 0, 1, 1), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                using var pixel = surface.Snapshot();
                using var bitmap = SKBitmap.FromImage(pixel);
                var c = bitmap.GetPixel(0, 0);
                return Windows.UI.Color.FromArgb(255, c.Red, c.Green, c.Blue);
            });
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? Render(byte[] imageBytes)
    {
        using var source = SKImage.FromEncodedData(imageBytes);
        if (source == null)
            return null;

        // Step 1: the cover UniformToFill-cropped into the banner's aspect ratio.
        var scale = Math.Max((float)ContentWidth / source.Width, (float)ContentHeight / source.Height);
        var drawWidth = source.Width * scale;
        var drawHeight = source.Height * scale;
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        using var contentSurface = SKSurface.Create(new SKImageInfo(ContentWidth, ContentHeight));
        contentSurface.Canvas.DrawImage(source,
            SKRect.Create((ContentWidth - drawWidth) / 2, (ContentHeight - drawHeight) / 2, drawWidth, drawHeight),
            sampling);
        using var content = contentSurface.Snapshot();

        // Step 2: blur it onto a transparent canvas with padding. Decal tiling treats everything
        // outside the content as transparent, so the blur fades out into the padding (the glow)
        // instead of smearing edge pixels the way Clamp would.
        var info = new SKImageInfo(ContentWidth + Padding * 2, ContentHeight + Padding * 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(Sigma, Sigma, SKShaderTileMode.Decal) };
        surface.Canvas.DrawImage(content, Padding, Padding, sampling, paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
