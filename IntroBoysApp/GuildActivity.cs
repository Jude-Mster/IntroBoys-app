using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AndroidX.Browser.CustomTabs;
using AndroidX.Browser.Trusted;
using Uri = Android.Net.Uri;

namespace IntroBoys.App;

/// <summary>
/// The guild hall icon. Opens blacklistintroboys.com in Chrome's app mode (a Trusted Web Activity), like the old app did: the site
/// keeps its Discord sign-in, alerts and "installed app" look. Without a browser that can do that, it opens a Chrome tab, and
/// without that, any browser.
/// </summary>
[Activity(Name = "com.blacklistintroboys.guild.GuildActivity", Label = "BLACKLIST", Icon = "@mipmap/ic_guild",
          MainLauncher = true, Exported = true, Theme = "@style/LaunchTheme", LaunchMode = LaunchMode.SingleTask,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden)]
public class GuildActivity : Activity
{
    const string Start = LicenseConfig.SiteUrl + "/dashboard";
    static readonly string[] Browsers = { "com.android.chrome", "com.chrome.beta", "com.chrome.dev", "com.microsoft.emmx", "com.sec.android.app.sbrowser", "com.brave.browser" };

    Connection? conn;
    bool launched;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (savedInstanceState != null && savedInstanceState.GetBoolean("launched")) { Finish(); return; }
        Launch(Intent?.Data?.ToString());
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutBoolean("launched", launched);
    }

    // coming back here means the member left the site with Back: close, like the old app
    protected override void OnRestart()
    {
        base.OnRestart();
        if (launched) Finish();
    }

    protected override void OnDestroy()
    {
        if (conn != null) { try { UnbindService(conn); } catch { /* not bound */ } conn = null; }
        base.OnDestroy();
    }

    void Launch(string? url)
    {
        // only the guild's own pages
        if (url == null || !url.StartsWith(LicenseConfig.SiteUrl + "/", StringComparison.OrdinalIgnoreCase)) url = Start;
        var uri = Uri.Parse(url)!;
        string? pkg = null;
        try { pkg = CustomTabsClient.GetPackageName(this, Browsers.ToList()); } catch { /* no browser list */ }
        if (pkg == null) { Fallback(uri); return; }

        conn = new Connection(this, uri);
        bool bound = false;
        try { bound = CustomTabsClient.BindCustomTabsService(this, pkg, conn); } catch { /* refused */ }
        if (!bound) { conn = null; Fallback(uri); return; }
        // if the browser never answers, don't leave the member on a black screen
        new Handler(Looper.MainLooper!).PostDelayed(() => { if (!launched) Fallback(uri); }, 4000);
    }

    internal void OpenTrusted(CustomTabsClient client, Uri uri)
    {
        if (launched) return;
        try
        {
            client.Warmup(0);
            var session = client.NewSession((CustomTabsCallback?)null);
            if (session == null) { Fallback(uri); return; }
            new TrustedWebActivityIntentBuilder(uri).Build(session).LaunchTrustedWebActivity(this);
            launched = true;
        }
        catch { Fallback(uri); }
    }

    void Fallback(Uri uri)
    {
        if (launched) return;
        launched = true;
        try
        {
            var tab = new CustomTabsIntent.Builder().Build();
            tab.LaunchUrl(this, uri);
        }
        catch
        {
            try { StartActivity(new Intent(Intent.ActionView, uri)); }
            catch { Toast.MakeText(this, "Install Chrome to open the guild hall.", ToastLength.Long)!.Show(); Finish(); }
        }
    }

    sealed class Connection : CustomTabsServiceConnection
    {
        readonly GuildActivity a;
        readonly Uri uri;
        public Connection(GuildActivity a, Uri uri) { this.a = a; this.uri = uri; }
        public override void OnCustomTabsServiceConnected(ComponentName name, CustomTabsClient client) =>
            a.RunOnUiThread(() => a.OpenTrusted(client, uri));
        public override void OnServiceDisconnected(ComponentName? name) { }
    }
}
