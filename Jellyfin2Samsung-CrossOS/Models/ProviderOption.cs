using Avalonia.Media;

namespace Apps2Samsung.Models;

public sealed class ProviderOption
{
    public string DisplayName { get; init; } = "";
    public IImage? PreviewImage { get; init; }  // can be a Bitmap later
}
