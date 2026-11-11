using Apps2Samsung.Certificate;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Where the phone's automatic certificate backups go (#1068): somewhere that survives removing the
/// app, since the certificate store itself lives in the app's private data and goes with it.
/// Android 10 and newer: Download/Apps2Samsung through MediaStore (no permission needed, stays after an
/// uninstall). Older Android: the app's own Downloads folder (no permission either, but removed with the
/// app; Share backup covers that). iOS: Documents, which the Files app shows under On My iPhone ›
/// Apps2Samsung and the iCloud device backup includes.
/// </summary>
public static class CertificateBackupTargets
{
	public static void Configure()
	{
		var store = new MobileAppConfig().CertificateStorePath;
		var index = Path.Combine(FileSystem.AppDataDirectory, CertificateBackups.IndexFileName);
		CertificateBackups.Configure(store, index, Create());
		// A certificate made before this existed is backed up on the first start.
		Task.Run(CertificateBackups.Changed);
	}

	private static ICertificateBackupTarget Create()
	{
#if ANDROID
		if (OperatingSystem.IsAndroidVersionAtLeast(29))
			return new AndroidDownloadsTarget();
		var own = Android.App.Application.Context.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath
		          ?? Path.Combine(FileSystem.AppDataDirectory, "Certificate backups");
		return new FolderBackupTarget(own);
#elif IOS
		var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		return new FolderBackupTarget(Path.Combine(documents, "Certificate backups"), "Files › On My iPhone › Apps2Samsung › Certificate backups");
#else
		return new FolderBackupTarget(Path.Combine(FileSystem.AppDataDirectory, "Certificate backups"));
#endif
	}

#if ANDROID
	// Download/Apps2Samsung through MediaStore. The location is the content uri the insert returned;
	// after a reinstall the app no longer owns those files, Exists says false and a new backup is written.
	[System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
	private sealed class AndroidDownloadsTarget : ICertificateBackupTarget
	{
		private const string Folder = "Apps2Samsung";

		private static Android.Content.ContentResolver Resolver =>
			Android.App.Application.Context.ContentResolver ?? throw new IOException("No content resolver.");

		public string DisplayLocation => $"{Android.OS.Environment.DirectoryDownloads}/{Folder}";

		public string Write(string fileName, byte[] content)
		{
			var values = new Android.Content.ContentValues();
			values.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, fileName);
			values.Put(Android.Provider.MediaStore.IMediaColumns.MimeType, "application/octet-stream");
			values.Put(Android.Provider.MediaStore.IMediaColumns.RelativePath, $"{Android.OS.Environment.DirectoryDownloads}/{Folder}");
			values.Put(Android.Provider.MediaStore.IMediaColumns.IsPending, 1);

			var uri = Resolver.Insert(Android.Provider.MediaStore.Downloads.ExternalContentUri!, values)
			          ?? throw new IOException("Android refused a new file in Downloads.");
			try
			{
				using (var stream = Resolver.OpenOutputStream(uri) ?? throw new IOException("Could not open the new file in Downloads."))
					stream.Write(content, 0, content.Length);

				var done = new Android.Content.ContentValues();
				done.Put(Android.Provider.MediaStore.IMediaColumns.IsPending, 0);
				Resolver.Update(uri, done, null, null);
			}
			catch
			{
				try { Resolver.Delete(uri, null, null); } catch { /* best effort */ }
				throw;
			}
			return uri.ToString()!;
		}

		public bool Exists(string location)
		{
			try
			{
				using var cursor = Resolver.Query(Android.Net.Uri.Parse(location)!, new[] { Android.Provider.IBaseColumns.Id }, null, null, null);
				return cursor is not null && cursor.Count > 0;
			}
			catch
			{
				return false;
			}
		}

		public void Delete(string location)
		{
			try { Resolver.Delete(Android.Net.Uri.Parse(location)!, null, null); } catch { /* best effort */ }
		}
	}
#endif
}
