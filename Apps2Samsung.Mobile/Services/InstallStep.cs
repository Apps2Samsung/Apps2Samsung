namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// The numbered steps of an install, as the progress sheet shows them (#755). The Install tab sets
/// the first two; <see cref="WgtInstaller"/> reports the rest as it reaches them.
/// </summary>
public enum InstallStep
{
	Download = 1,
	Certificate = 2,
	Sign = 3,
	Install = 4,
	Open = 5,
}
