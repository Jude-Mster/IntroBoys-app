using System.Security.Cryptography;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;

namespace IntroBoys.App;

/// <summary>
/// Updates from inside the app. The guild site keeps /app/introboys-android.json (version, size, checksum, download link); when it names
/// a newer build, the app downloads it, checks the checksum, and hands it to Android's installer, which shows the usual "Update" prompt.
/// Android itself refuses an update that isn't signed with the guild's key, so a changed file can never replace the app. Updating
/// in place keeps the member's permissions and bubbles: nobody has to uninstall.
/// </summary>
static class Updater
{
    public const string ManifestUrl = LicenseConfig.SiteUrl + "/app/introboys-android.json";

    public sealed record Release(long Code, string Name, string Url, string Sha256, long Size, string Notes);

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>The build number of the installed app.</summary>
    public static long InstalledCode(Context c)
    {
        try
        {
            var info = c.PackageManager!.GetPackageInfo(c.PackageName!, 0)!;
            return OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;
        }
        catch { return 0; }
    }

    /// <summary>
    /// The newer release the site offers, or null (none, or the site can't be reached). 1.3.9: two places are asked and the newer build
    /// wins - the file the Guild Leader links on the site (Admin, App download link; the site reads its build number and checksum from the
    /// file itself), and /app/introboys-android.json that comes with a site release. Before, only the second was looked at, so a new file
    /// linked on the site was never offered.
    /// </summary>
    public static async Task<Release?> CheckAsync(Context c)
    {
        var a = await FromLinkAsync();
        var b = await FromManifestAsync();
        var best = a == null ? b : b == null ? a : (a.Code >= b.Code ? a : b);
        return best != null && best.Code > InstalledCode(c) ? best : null;
    }

    /// <summary>The file linked in Admin (any https address: the checksum comes from the guild site and Android checks the signature).</summary>
    static async Task<Release?> FromLinkAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, LicenseConfig.ApiBase + "/" + LicenseConfig.Function)
            {
                Content = new StringContent("{\"action\":\"appUpdate\"}", System.Text.Encoding.UTF8, "application/json")
            };
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            if (!r.TryGetProperty("offered", out var off) || off.ValueKind != JsonValueKind.True) return null;
            string url = r.GetProperty("url").GetString() ?? "";
            string sha = (r.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || sha.Length != 64) return null;
            return new Release(r.GetProperty("versionCode").GetInt64(), r.TryGetProperty("versionName", out var n) ? n.GetString() ?? "" : "",
                               url, sha, r.TryGetProperty("size", out var z) ? z.GetInt64() : 0, r.TryGetProperty("notes", out var t) ? t.GetString() ?? "" : "");
        }
        catch { return null; }
    }

    static async Task<Release?> FromManifestAsync()
    {
        try
        {
            string json = await http.GetStringAsync(ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            long code = r.GetProperty("versionCode").GetInt64();
            string url = r.GetProperty("url").GetString() ?? "";
            string sha = (r.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
            long size = r.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            // only ever the guild's own site, and only a real checksum
            if (!url.StartsWith(LicenseConfig.SiteUrl + "/", StringComparison.OrdinalIgnoreCase) || sha.Length != 64) return null;
            return new Release(code, r.TryGetProperty("versionName", out var n) ? n.GetString() ?? "" : "",
                               url, sha, size, r.TryGetProperty("notes", out var t) ? t.GetString() ?? "" : "");
        }
        catch { return null; }
    }

    /// <summary>True when Android lets this app install updates (the member allows it once, in Settings).</summary>
    public static bool CanInstall(Context c) => c.PackageManager!.CanRequestPackageInstalls();

    /// <summary>The Settings page where the member allows the app to install its updates.</summary>
    public static Intent AllowInstallIntent(Context c) =>
        new(Android.Provider.Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + c.PackageName));

    /// <summary>Downloads the release, checks it, and starts Android's installer. Progress is 0..1. Throws with a readable message.</summary>
    public static async Task InstallAsync(Activity a, Release r, Action<double> progress, CancellationToken ct)
    {
        string path = Path.Combine(a.CacheDir!.AbsolutePath, "update.apk");
        using (var res = await http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!res.IsSuccessStatusCode) throw new Exception("The download failed (" + (int)res.StatusCode + ").");
            long total = res.Content.Headers.ContentLength ?? r.Size;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            var buf = new byte[81920];
            long got = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
                if (total > 0) progress(Math.Min(1, got / (double)total));
            }
        }
        string sha;
        await using (var f = File.OpenRead(path)) sha = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        if (sha != r.Sha256) { File.Delete(path); throw new Exception("The download was damaged. Try again."); }

        // Android's installer (a "session"): it shows the Update prompt and checks the signature
        var installer = a.PackageManager!.PackageInstaller;
        var prm = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        prm.SetAppPackageName(a.PackageName);
        int id = installer.CreateSession(prm);
        using var session = installer.OpenSession(id);
        await using (var input = File.OpenRead(path))
        using (var output = session.OpenWrite("update.apk", 0, input.Length))
        {
            await input.CopyToAsync(output, ct);
            session.Fsync(output);
        }
        var done = new Intent(a, typeof(InstallResult));
        var pending = PendingIntent.GetBroadcast(a, id, done, PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent)!;
        session.Commit(pending.IntentSender);
    }
}

/// <summary>Android's installer reports here: it asks to show its Update prompt, or says how it went.</summary>
[BroadcastReceiver(Name = "com.blacklistintroboys.guild.InstallResult", Exported = false)]
public class InstallResult : BroadcastReceiver
{
    public static event Action<string>? Message;

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, -999);
        if (status == PackageInstallStatus.PendingUserAction)
        {
#pragma warning disable CA1422, CS0618
            var confirm = intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
#pragma warning restore CA1422, CS0618
            if (confirm != null)
            {
                confirm.AddFlags(ActivityFlags.NewTask);
                try { context.StartActivity(confirm); } catch { Message?.Invoke("Couldn't show the update prompt. Open the app and try again."); }
            }
            return;
        }
        if (status == PackageInstallStatus.Success) { Message?.Invoke("Updated."); return; }
        string why = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage) ?? "";
        Message?.Invoke(status == PackageInstallStatus.FailureAborted ? "Update cancelled." :
                        status == PackageInstallStatus.FailureConflict || status == PackageInstallStatus.FailureIncompatible
                            ? "This update can't install over your app (" + why + "). Download it from the guild site instead."
                            : "The update didn't install" + (why.Length > 0 ? " (" + why + ")." : "."));
    }
}
