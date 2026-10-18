using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace LunaDrome.Services;

/// <summary>
/// Built-in updates on Android: the downloaded APK goes to the system's PackageInstaller, which
/// asks the user to confirm and then replaces the app in place (same signing key, so the
/// library login, queue and settings stay). Android only lets an app do that once the user has
/// allowed it to "install unknown apps"; until then the settings page for that opens instead.
/// </summary>
internal static class AndroidUpdateInstaller
{
    private static Context AppContext => Android.App.Application.Context;

    /// <summary>False until the user allows LunaDrome to install apps (Android 8+ asks per app).</summary>
    public static bool MayInstall =>
        !OperatingSystem.IsAndroidVersionAtLeast(26) || AppContext.PackageManager!.CanRequestPackageInstalls();

    /// <summary>Opens Settings > Install unknown apps > LunaDrome.</summary>
    public static void OpenInstallPermissionSettings()
    {
        var intent = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources,
                Android.Net.Uri.Parse("package:" + AppContext.PackageName))
            .AddFlags(ActivityFlags.NewTask)!;
        AppContext.StartActivity(intent);
    }

    /// <summary>Hands the APK to the system installer; Android shows its "update this app?"
    /// prompt, then closes LunaDrome and installs. Failures arrive via <see cref="UpdateInstallReceiver.Failed"/>.</summary>
    public static async Task InstallAsync(string apkPath)
    {
        var installer = AppContext.PackageManager!.PackageInstaller;
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(AppContext.PackageName);
        var sessionId = installer.CreateSession(parameters);
        using var session = installer.OpenSession(sessionId);
        try
        {
            await using (var apk = File.OpenRead(apkPath))
            using (var target = session.OpenWrite("LunaDrome.apk", 0, apk.Length))
            {
                await apk.CopyToAsync(target);
                session.Fsync(target);
            }
            // The installer fills in the result as extras, so the intent has to be mutable (Android 12+).
            var flags = PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
            var callback = PendingIntent.GetBroadcast(AppContext, sessionId, new Intent(AppContext, typeof(UpdateInstallReceiver)), flags)!;
            session.Commit(callback.IntentSender);
        }
        catch
        {
            session.Abandon();
            throw;
        }
    }
}

/// <summary>The PackageInstaller's answer: show its confirmation prompt, or report why it failed.</summary>
[BroadcastReceiver(Exported = false)]
internal sealed class UpdateInstallReceiver : BroadcastReceiver
{
    public static event Action<string>? Failed;

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null)
            return;
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        switch (status)
        {
            case PackageInstallStatus.PendingUserAction:
#pragma warning disable CA1422 // the typed overload is Android 13+ only
                if (intent.GetParcelableExtra(Intent.ExtraIntent) is Intent confirm)
#pragma warning restore CA1422
                {
                    confirm.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(confirm);
                }
                break;
            case PackageInstallStatus.Success:
                break;   // Android replaces (and closes) the app
            default:
                var message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage);
                PlaybackLog.Write($"update install failed: {status} {message}");
                Failed?.Invoke(status == PackageInstallStatus.FailureAborted
                    ? "the installation was cancelled"
                    : message ?? status.ToString());
                break;
        }
    }
}
