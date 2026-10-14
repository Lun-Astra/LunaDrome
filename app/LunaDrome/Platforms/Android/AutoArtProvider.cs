using Android.Content;
using Android.Database;
using Android.OS;
using Android.Runtime;

namespace LunaDrome.Services;

/// <summary>
/// Cover art for Android Auto's browse lists. The car only loads artwork through content://
/// URIs (not the http URLs of the Navidrome server), so a row's icon is
/// content://com.lunastra.lunadrome.art/&lt;cover id&gt;, and this downloads the cover once into
/// the cache folder and hands the car the file. The URI holds only the cover id: the server
/// address and login token never leave the app.
/// </summary>
[ContentProvider(new[] { Authority }, Exported = true)]
public class AutoArtProvider : ContentProvider
{
    private const string Authority = "com.lunastra.lunadrome.art";
    private const int Size = 300;
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public AutoArtProvider() { }
    protected AutoArtProvider(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public static string? UriFor(string? coverId) =>
        string.IsNullOrEmpty(coverId) ? null : $"content://{Authority}/{Uri.EscapeDataString(coverId)}";

    public override bool OnCreate() => true;

    public override string? GetType(Android.Net.Uri uri) => "image/jpeg";

    // Called on a binder thread, so waiting for the download here is fine.
    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        var coverId = uri.LastPathSegment;
        if (string.IsNullOrEmpty(coverId) || Context?.CacheDir is not { } cacheDir)
            throw new Java.IO.FileNotFoundException(uri.ToString());

        var folder = Path.Combine(cacheDir.AbsolutePath, "auto-art");
        var file = Path.Combine(folder, string.Concat(coverId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".img");
        if (!File.Exists(file))
        {
            try
            {
                var client = AutoLibrary.GetClientAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("not logged in");
                var bytes = s_http.GetByteArrayAsync(client.GetCoverArtUrl(coverId, Size)).GetAwaiter().GetResult();
                Directory.CreateDirectory(folder);
                var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex)
            {
                AndroidLog.Write($"auto art {coverId}: {ex.Message}");
                throw new Java.IO.FileNotFoundException(uri.ToString());
            }
        }
        return ParcelFileDescriptor.Open(new Java.IO.File(file), ParcelFileMode.ReadOnly);
    }

    // Read-only files: nothing to query or change.
    public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder) => null;
    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => null;
    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => 0;
    public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;
}
