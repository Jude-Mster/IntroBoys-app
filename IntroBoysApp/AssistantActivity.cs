using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidX.Browser.CustomTabs;
using Uri = Android.Net.Uri;

namespace IntroBoys.App;

/// <summary>
/// The Assistant icon: sign in with the guild account, switch on the tap permission, show the floating S, and see the bubbles.
/// The site's Assistant page can open it with introboys://assistant.
/// </summary>
[Activity(Name = "com.blacklistintroboys.guild.AssistantActivity", Label = "IntroBoys Assistant", Icon = "@mipmap/ic_assist",
          Exported = true, Theme = "@style/AppTheme", LaunchMode = LaunchMode.SingleTask, TaskAffinity = "com.blacklistintroboys.guild.assistant",
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden)]
[IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { Intent.CategoryLauncher })]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "introboys", DataHost = "assistant")]
public class AssistantActivity : Activity
{
    TextView? who, days, licMsg, tapState, bubbleList, runMsg, codeText, codeHint;
    Button? signIn, signOut, recheck, tapButton, showControl, approve;
    LinearLayout? codeBox;
    CancellationTokenSource? signing;
    string verifyUrl = "";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        License.Begin();
        SetContentView(Build());
        License.Changed += OnChanged;
        TapService.StateChanged += OnChanged;
        Runner.Changed += OnChanged;
        BubbleLayout.Saved += OnChanged;
        Refresh();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Refresh();
    }

    protected override void OnDestroy()
    {
        License.Changed -= OnChanged;
        TapService.StateChanged -= OnChanged;
        Runner.Changed -= OnChanged;
        BubbleLayout.Saved -= OnChanged;
        signing?.Cancel();
        base.OnDestroy();
    }

    void OnChanged() => RunOnUiThread(Refresh);

    // ------------------------------------------------------------------ the screen
    View Build()
    {
        var c = this;
        var scroll = new ScrollView(c) { FillViewport = true };
        scroll.SetBackgroundColor(Ui.Ink);
        var col = Ui.Column(c, 14);
        int p = Ui.Dp(c, 18);
        col.SetPadding(p, Ui.Dp(c, 24), p, Ui.Dp(c, 24));
        scroll.AddView(col, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // header
        var head = Ui.Row(c, 12);
        var logo = new ImageView(c);
        logo.SetImageResource(Resource.Mipmap.ic_assist);
        head.AddView(logo, new LinearLayout.LayoutParams(Ui.Dp(c, 46), Ui.Dp(c, 46)));
        var names = Ui.Column(c, 0);
        names.AddView(Ui.Title(c, "INTROBOYS", 22));
        var sub = Ui.Text(c, "ASSISTANT · MOBILE · " + LicenseConfig.AppVersion, 12, Color.ParseColor("#FF5A5F"), true);
        sub.LetterSpacing = 0.12f;
        names.AddView(sub);
        head.AddView(names, Ui.Weight());
        col.AddView(head, Ui.Wide());

        // account
        var acct = Ui.CardBox(c);
        var top = Ui.Row(c, 8);
        who = Ui.Title(c, "Not signed in");
        top.AddView(who, Ui.Weight());
        days = Ui.Text(c, "", 11, Color.White, true);
        days.Background = Ui.Round(c, Color.Transparent, 4, Color.White);
        days.SetPadding(Ui.Dp(c, 8), Ui.Dp(c, 2), Ui.Dp(c, 8), Ui.Dp(c, 2));
        top.AddView(days);
        acct.AddView(top, Ui.Wide());
        licMsg = Ui.Text(c, "", 13, Ui.Soft);
        acct.AddView(licMsg);

        codeBox = Ui.Column(c, 8);
        codeBox.Background = Ui.Round(c, Ui.Field, 10);
        codeBox.SetPadding(Ui.Dp(c, 12), Ui.Dp(c, 12), Ui.Dp(c, 12), Ui.Dp(c, 12));
        codeBox.AddView(Ui.Text(c, "Your sign-in code", 12, Ui.Muted));
        codeText = Ui.Text(c, "", 28, Ui.Gold, true);
        codeText.LetterSpacing = 0.1f;
        codeBox.AddView(codeText);
        codeHint = Ui.Text(c, "Tap Approve: the guild site opens with this code. Check it matches, then approve it there.", 12, Ui.Soft);
        codeBox.AddView(codeHint);
        approve = Ui.Button(c, "Approve on the guild site", true, () => OpenSite(verifyUrl));
        codeBox.AddView(approve, Ui.Wide());
        codeBox.AddView(Ui.Button(c, "Cancel", false, () => signing?.Cancel()), Ui.Wide());
        codeBox.Visibility = ViewStates.Gone;
        acct.AddView(codeBox, Ui.Wide());

        var acctButtons = Ui.Row(c, 8);
        signIn = Ui.Button(c, "Sign in with the guild", true, SignIn);
        recheck = Ui.Button(c, "Check again", false, () => _ = License.RecheckAsync());
        signOut = Ui.Button(c, "Sign out", false, () => { Runner.Stop("Signed out."); _ = License.SignOutAsync(); });
        acctButtons.AddView(signIn, Ui.Weight());
        acctButtons.AddView(recheck, Ui.Weight());
        acctButtons.AddView(signOut, Ui.Weight());
        acct.AddView(acctButtons, Ui.Wide());
        col.AddView(acct, Ui.Wide());

        // permission
        var perm = Ui.CardBox(c);
        perm.AddView(Ui.Title(c, "TAP PERMISSION"));
        var (tapRow, tapVal) = Ui.Pair(c, "Taps on the game (accessibility)", "off", Ui.Gold);
        tapState = tapVal;
        perm.AddView(tapRow, Ui.Wide());
        perm.AddView(Ui.Text(c,
            "In Settings, open Accessibility, then \"Installed apps\" (or \"Downloaded apps\"), then IntroBoys Assistant taps, and switch it on.\n\n" +
            "If Android says \"Restricted setting\": tap App info below, then the ⋮ menu at the top right, then \"Allow restricted settings\", and try again.",
            12, Ui.Muted));
        var permButtons = Ui.Row(c, 8);
        tapButton = Ui.Button(c, "Open Accessibility", true, () => Open(new Intent(Settings.ActionAccessibilitySettings)));
        permButtons.AddView(tapButton, Ui.Weight());
        permButtons.AddView(Ui.Button(c, "App info", false, () =>
            Open(new Intent(Settings.ActionApplicationDetailsSettings, Uri.Parse("package:" + PackageName)))), Ui.Weight());
        perm.AddView(permButtons, Ui.Wide());
        col.AddView(perm, Ui.Wide());

        // run
        var run = Ui.CardBox(c);
        run.AddView(Ui.Title(c, "AUTO ATTACK"));
        run.AddView(Ui.Text(c,
            "1. Tap Show the floating S.\n2. Open the game.\n3. Tap the S, then Bubbles: put a bubble on each game button (attack, skills, buffs, auto loot) and Save.\n4. Tap the S, then Start.",
            13, Ui.Soft));
        bubbleList = Ui.Text(c, "", 13, Color.White);
        run.AddView(bubbleList);
        showControl = Ui.Button(c, "Show the floating S", true, ToggleControl);
        run.AddView(showControl, Ui.Wide());
        runMsg = Ui.Text(c, "", 12, Ui.Muted);
        run.AddView(runMsg);
        col.AddView(run, Ui.Wide());

        var guild = Ui.Button(c, "Open the guild hall", false, () => Open(new Intent(this, typeof(GuildActivity))));
        col.AddView(guild, Ui.Wide());
        var foot = Ui.Text(c, "Presses only the buttons you put bubbles on, like a finger would. Nothing is changed inside the game.", 11, Color.ParseColor("#8D8D93"));
        foot.Gravity = GravityFlags.Center;
        col.AddView(foot, Ui.Wide());
        return scroll;
    }

    void Refresh()
    {
        if (who == null) return;
        bool signedIn = License.State is not (LicenseState.SignedOut or LicenseState.NotMember);
        who.Text = License.UserName.Length > 0 ? License.UserName : signedIn ? "Signed in" : "Not signed in";
        bool ok = License.State == LicenseState.Licensed;
        days!.Visibility = ok ? ViewStates.Visible : ViewStates.Gone;
        days.Text = License.DaysLeft == 1 ? "1 DAY" : License.DaysLeft + " DAYS";
        licMsg!.Text = ok ? "License active · " + License.TimeLeftText() + " left" : License.Message;
        licMsg.SetTextColor(ok ? Ui.Green : License.State is LicenseState.Checking ? Ui.Soft : Ui.Gold);
        bool waiting = signing != null;
        signIn!.Visibility = !signedIn && !waiting ? ViewStates.Visible : ViewStates.Gone;
        recheck!.Visibility = signedIn ? ViewStates.Visible : ViewStates.Gone;
        signOut!.Visibility = signedIn ? ViewStates.Visible : ViewStates.Gone;
        codeBox!.Visibility = waiting && codeText!.Text!.Length > 0 ? ViewStates.Visible : ViewStates.Gone;

        bool tapsOn = TapService.Instance != null;
        tapState!.Text = tapsOn ? "on" : "off";
        tapState.SetTextColor(tapsOn ? Ui.Green : Ui.Gold);
        tapButton!.Text = tapsOn ? "Accessibility settings" : "Open Accessibility";

        var layout = BubbleLayout.Load();
        bubbleList!.Text = layout.Bubbles.Count == 0
            ? "No bubbles placed yet."
            : string.Join("\n", layout.Bubbles.Select(b => "•  " + b.Label + "  " + Bubble.Name(b.Kind) + " · " + b.Describe()));
        bool shown = TapService.Prefs.GetBoolean("show_control", false);
        showControl!.Text = shown ? "Hide the floating S" : "Show the floating S";
        runMsg!.Text = Runner.Running ? "Auto attack is running · " + Runner.Presses + " presses" :
                       !tapsOn ? "Switch on the tap permission first." :
                       Runner.LastStop;
    }

    // ------------------------------------------------------------------ actions
    async void SignIn()
    {
        if (signing != null) return;
        signing = new CancellationTokenSource();
        codeText!.Text = "";
        Refresh();
        try
        {
            await License.SignInAsync((code, tag, url) => RunOnUiThread(() =>
            {
                verifyUrl = url;
                codeText.Text = code;
                codeHint!.Text = "Device " + tag + ". Tap Approve: the guild site opens with this code. Check it matches, then approve it there.";
                Refresh();
                OpenSite(url);
            }), signing.Token);
        }
        catch (OperationCanceledException) { License.Fail("Sign-in cancelled."); }
        catch (Exception ex) { License.Fail("Sign-in failed: " + ex.Message); }
        finally
        {
            signing = null;
            codeText.Text = "";
            Refresh();
        }
    }

    void ToggleControl()
    {
        bool show = !TapService.Prefs.GetBoolean("show_control", false);
        TapService.SetControlVisible(show);
        if (show && TapService.Instance == null)
            Toast.MakeText(this, "Switch on the tap permission: the S shows up as soon as it is on.", ToastLength.Long)!.Show();
        else if (show)
            Toast.MakeText(this, "The S is on screen. Now open the game.", ToastLength.Short)!.Show();
        Refresh();
    }

    /// <summary>Opens a guild site page in Chrome (a Custom Tab), where the member is already signed in to the site.</summary>
    void OpenSite(string url)
    {
        if (!url.StartsWith(LicenseConfig.SiteUrl + "/", StringComparison.OrdinalIgnoreCase)) return;
        var uri = Uri.Parse(url)!;
        try { new CustomTabsIntent.Builder().Build().LaunchUrl(this, uri); }
        catch { Open(new Intent(Intent.ActionView, uri)); }
    }

    void Open(Intent i)
    {
        try { StartActivity(i); }
        catch { Toast.MakeText(this, "Couldn't open that on this phone.", ToastLength.Short)!.Show(); }
    }
}
