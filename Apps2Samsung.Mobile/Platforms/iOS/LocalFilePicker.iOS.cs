using Foundation;
using Microsoft.Maui.ApplicationModel;
using UIKit;
using UniformTypeIdentifiers;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// iOS picker: our own <c>UIDocumentPickerViewController</c> in import mode, used instead of
/// <c>FilePicker.Default.PickAsync</c>.
///
/// MAUI's picker shows the document picker in <c>UIDocumentPickerMode.Open</c> (open in place)
/// through the deprecated string-UTI initializer. In that mode Files only enables a file the
/// provider lets an app open in place and whose type the system knows; everything else is greyed
/// out, and on a real phone that was the user's .wgt or .tpk in "On My iPhone": a tap on the file
/// did nothing, while any other file could be chosen. Import mode (<c>asCopy: true</c>) has neither
/// restriction: the system copies the file into our sandbox and hands over a plain URL, no
/// security scope involved.
///
/// .wgt and .tpk have no uniform type on iOS, so Info.plist declares them
/// (UTImportedTypeDeclarations) and the picker lists those types next to public.data/public.item.
/// </summary>
public static partial class LocalFilePicker
{
    // The imported type declarations in Platforms/iOS/Info.plist.
    private const string WgtType = "org.tizen.widget-package";
    private const string TpkType = "org.tizen.package";

    // Keeps the picker (and its managed event handlers) alive while it is on screen.
    private static UIDocumentPickerViewController? _open;

    private static async partial Task<PickedFile?> PickPlatformAsync(string[] mimeTypes)
    {
        var url = await MainThread.InvokeOnMainThreadAsync(() => PresentAsync(UniformTypes(mimeTypes)));
        return url is null ? null : await Task.Run(() => Copy(url));
    }

    // Main thread: UIKit objects are created and presented there.
    private static Task<NSUrl?> PresentAsync(UTType[] types)
    {
        if (_open is not null)
            throw new InvalidOperationException("A file picker is already open.");

        var host = Platform.GetCurrentUIViewController()
            ?? throw new InvalidOperationException("No current view controller to show the file picker from.");

        var tcs = new TaskCompletionSource<NSUrl?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new UIDocumentPickerViewController(types, asCopy: true)
        {
            AllowsMultipleSelection = false,
        };
        picker.DidPickDocumentAtUrls += (_, e) => Finish(e.Urls.FirstOrDefault());
        picker.WasCancelled += (_, _) => Finish(null);

        _open = picker;
        host.PresentViewController(picker, true, null);
        return tcs.Task;

        void Finish(NSUrl? url)
        {
            _open = null;
            tcs.TrySetResult(url);
        }
    }

    // Runs on a background thread. The URL is the import copy in our own tmp folder: read it into
    // the picked cache and remove it, the system doesn't.
    private static PickedFile Copy(NSUrl url)
    {
        var path = url.Path ?? throw new IOException("The picked file has no path.");
        try
        {
            using var src = File.OpenRead(path);
            return CopyToCache(url.LastPathComponent, src);
        }
        finally
        {
            try { File.Delete(path); } catch { /* tmp, iOS clears it eventually */ }
        }
    }

    // The pages speak MIME (the Android filter); the document picker wants uniform types. Empty
    // means every file: public.item/public.data plus the declared Tizen package types, and the page
    // checks the extension itself, as it does on Android. A type the system doesn't know (the
    // declared ones, on a build without the plist entries) is simply left out.
    private static UTType[] UniformTypes(string[] mimeTypes)
    {
        IEnumerable<string> ids = mimeTypes.Length == 0
            ? new[] { WgtType, TpkType, "public.data", "public.item" }
            : mimeTypes.SelectMany(mime => mime switch
            {
                "image/*" => new[] { "public.image" },
                "application/zip" => new[] { "com.pkware.zip-archive", "public.zip-archive" },
                _ => new[] { "public.data" },
            }).Distinct();

        return ids.Select(UTType.CreateFromIdentifier).OfType<UTType>().ToArray();
    }
}
