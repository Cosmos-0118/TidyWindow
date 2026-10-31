using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// Turns a program or startup item into the picture to show for it. Searching and decoding run on the thread pool, a few at a time, and every
/// answer is kept, so a page that asks again (or a second page asking for the same program) gets it straight away. The pictures are
/// created and handed out on the UI thread, so call <see cref="LoadAsync"/> and <see cref="TryGet"/> from there.
/// </summary>
public sealed class IconService(IconFinder finder, IIconReader reader) : IDisposable
{
    /// <summary>Large enough to stay sharp at 200% scaling on a 24 px icon.</summary>
    private const int Size = 48;
    private static readonly string[] ImageFiles = [".png", ".jpg", ".jpeg", ".bmp"];

    private static volatile bool dark = true;
    private static readonly Lazy<IconService> shared = new(() => new(new IconFinder(new WindowsIconSystem(), () => dark), new WindowsIconReader()));
    public static IconService Shared => shared.Value;

    /// <summary>Raised on the UI thread when the theme flips between light and dark, because a Store app's logo comes in a version for each.</summary>
    public static event Action? DarkChanged;

    public static void SetDark(bool value)
    {
        if (dark == value) return;
        dark = value;
        DarkChanged?.Invoke();
    }

    private static string Key(IconQuery query) => query.Key + (dark ? "|dark" : "|light");

    private sealed record Picked(string Key, IconBitmap Bitmap);

    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(4);
    private readonly ConcurrentDictionary<string, Task<Picked?>> picked = new();
    private readonly ConcurrentDictionary<string, Task<IconBitmap?>> pixels = new();
    private readonly Dictionary<string, ImageSource> images = new();
    private readonly Dictionary<string, ImageSource?> answers = new();

    /// <summary>The picture if it has already been found, so a page that is rebuilt does not flash back to empty icons.</summary>
    public bool TryGet(IconQuery query, out ImageSource? image) => answers.TryGetValue(Key(query), out image);

    /// <summary>The icon for this query, or null when there is nothing to show but a plain glyph.</summary>
    public async Task<ImageSource?> LoadAsync(IconQuery query, CancellationToken cancel)
    {
        var key = Key(query);
        if (answers.TryGetValue(key, out var known)) return known;
        // The work belongs to the service rather than to whoever asked first, so leaving a page never throws away what others are waiting for.
        var found = await picked.GetOrAdd(key, _ => Task.Run(() => PickAsync(query))).WaitAsync(cancel);
        ImageSource? image = null;
        if (found is not null && !images.TryGetValue(found.Key, out image)) images[found.Key] = image = Draw(found.Bitmap);
        return answers[key] = image;
    }

    private async Task<Picked?> PickAsync(IconQuery query)
    {
        try
        {
            await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                foreach (var icon in finder.Find(query))
                    if (await pixels.GetOrAdd(icon.Key, _ => ReadAsync(icon)).ConfigureAwait(false) is { } bitmap) return new(icon.Key, bitmap);
                return null;
            }
            finally { gate.Release(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return null; }
    }

    private async Task<IconBitmap?> ReadAsync(IconRef icon)
    {
        try
        {
            if (icon.Source == IconSource.FileType || Array.IndexOf(ImageFiles, Path.GetExtension(icon.Path).ToLowerInvariant()) < 0) return reader.Read(icon, Size);
            return await DecodeAsync(icon.Path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or InvalidOperationException) { return null; }
    }

    // A Store app's logo is a PNG; it is read as bytes because the package folder is not one a StorageFile may always open.
    private static async Task<IconBitmap?> DecodeAsync(string path)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync((await File.ReadAllBytesAsync(path).ConfigureAwait(false)).AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0) return null;
        var scale = Math.Min(1.0, (double)Size / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = Math.Max(1, (int)Math.Round(decoder.PixelWidth * scale)); var height = Math.Max(1, (int)Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height, InterpolationMode = BitmapInterpolationMode.Fant };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return new(width, height, data.DetachPixelData());
    }

    private static ImageSource Draw(IconBitmap bitmap)
    {
        var image = new WriteableBitmap(bitmap.Width, bitmap.Height);
        bitmap.Bgra.CopyTo(image.PixelBuffer);
        image.Invalidate();
        return image;
    }

    public void Dispose() => lifetime.Cancel();
}
