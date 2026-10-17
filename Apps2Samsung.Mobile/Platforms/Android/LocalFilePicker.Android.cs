using Android.Content;
using Android.Database;
using Microsoft.Maui.ApplicationModel;
using AndroidApp = Android.App.Application;
using AndroidUri = Android.Net.Uri;
using Result = Android.App.Result;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Storage Access Framework picker, used instead of <c>FilePicker.Default.PickAsync</c>.
///
/// MAUI's picker resolves the picked content URI to a physical path inside
/// <c>IntermediateActivity.OnActivityResult</c> — i.e. on the UI thread — by calling
/// <c>ContentResolver.OpenInputStream</c>. StrictMode's thread policy travels with the Binder call
/// to the document provider, so a provider that has to fetch the file over the network
/// (Google Drive, OneDrive, cloud-backed Photos) trips the main-thread network check and the call
/// comes back as <c>NetworkOnMainThreadException</c>, wrapped in a bare
/// <c>Java.Lang.RuntimeException</c>. Picking the same file from local storage never touches the
/// network, which is why this only reproduces on some phones and some sources.
///
/// So: launch the picker ourselves and do the read on a background thread, where the policy does
/// not apply.
/// </summary>
public static partial class LocalFilePicker
{
    // Arbitrary, only has to be unique within the activity.
    internal const int RequestCode = 0x2A55;

    private static TaskCompletionSource<AndroidUri?>? _pending;

    private static async partial Task<PickedFile?> PickPlatformAsync(string[] mimeTypes)
    {
        var activity = Platform.CurrentActivity
            ?? throw new InvalidOperationException("No current activity to show the file picker from.");

        var tcs = new TaskCompletionSource<AndroidUri?>();
        if (Interlocked.CompareExchange(ref _pending, tcs, null) is not null)
            throw new InvalidOperationException("A file picker is already open.");

        AndroidUri? uri;
        try
        {
            try
            {
                activity.StartActivityForResult(Build(Intent.ActionOpenDocument, mimeTypes), RequestCode);
            }
            catch (ActivityNotFoundException)
            {
                // No document provider (stripped-down ROMs); the older chooser is still handled.
                activity.StartActivityForResult(Build(Intent.ActionGetContent, mimeTypes), RequestCode);
            }

            uri = await tcs.Task;
        }
        finally
        {
            Interlocked.CompareExchange(ref _pending, null, tcs);
        }

        return uri is null ? null : await Task.Run(() => Copy(uri));
    }

    /// <summary>Feeds the activity result back to the waiting <see cref="PickAsync"/>.</summary>
    /// <returns><c>true</c> if this was our request code.</returns>
    internal static bool OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != RequestCode)
            return false;

        var tcs = Interlocked.Exchange(ref _pending, null);
        tcs?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
        return true;
    }

    private static Intent Build(string action, string[] mimeTypes)
    {
        var intent = new Intent(action);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mimeTypes.Length == 1 ? mimeTypes[0] : "*/*");
        if (mimeTypes.Length > 1)
            intent.PutExtra(Intent.ExtraMimeTypes, mimeTypes);
        return intent;
    }

    // Runs on a background thread — see the class remarks.
    private static PickedFile Copy(AndroidUri uri)
    {
        var resolver = AndroidApp.Context.ContentResolver
            ?? throw new InvalidOperationException("No ContentResolver.");

        var name = DisplayName(resolver, uri);
        using var src = resolver.OpenInputStream(uri)
            ?? throw new IOException($"Couldn't open \"{name ?? "the picked file"}\".");
        return CopyToCache(name, src);
    }

    private static string? DisplayName(ContentResolver resolver, AndroidUri uri)
    {
        // OpenableColumns.DISPLAY_NAME — spelled out to avoid depending on how the constant is bound.
        const string DisplayNameColumn = "_display_name";

        using ICursor? cursor = resolver.Query(uri, new[] { DisplayNameColumn }, null, null, null);
        if (cursor is null || !cursor.MoveToFirst())
            return null;

        var column = cursor.GetColumnIndex(DisplayNameColumn);
        return column < 0 ? null : cursor.GetString(column);
    }
}
