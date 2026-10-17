using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// iOS picker: MAUI's <see cref="FilePicker"/> over <c>UIDocumentPickerViewController</c>. The
/// main-thread/network problem that made Android bypass MAUI's picker doesn't exist here (the
/// document picker hands back a local, security-scoped copy), so the shared API is enough.
/// </summary>
public static partial class LocalFilePicker
{
    private static async partial Task<PickedFile?> PickPlatformAsync(string[] mimeTypes)
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.iOS] = UniformTypes(mimeTypes),
            }),
        });
        if (result is null)
            return null;

        using var src = await result.OpenReadAsync();
        return await Task.Run(() => CopyToCache(result.FileName, src));
    }

    // The pages speak MIME (the Android filter); the document picker wants uniform type identifiers.
    // No UTI exists for .wgt/.tpk, so "every file" is public.data/public.item and the page checks the
    // extension itself, as it does on Android.
    private static IEnumerable<string> UniformTypes(string[] mimeTypes)
    {
        if (mimeTypes.Length == 0)
            return new[] { "public.item", "public.data" };

        var types = new List<string>();
        foreach (var mime in mimeTypes)
        {
            types.AddRange(mime switch
            {
                "image/*" => new[] { "public.image" },
                "application/zip" => new[] { "com.pkware.zip-archive", "public.zip-archive" },
                _ => new[] { "public.data" },
            });
        }
        return types.Distinct();
    }
}
