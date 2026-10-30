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
/// The Assistant icon: a first-time guide (sign in, allow restricted settings, switch on taps, place bubbles) with a green check per
/// step, the account, updates from inside the app, and the bubbles. The site's Assistant page can open it with introboys://assistant.
/// </summary>
[Activity(Name = "com.blacklistintroboys.guild.AssistantActivity", Label = "IntroBoys Assistant", Icon = "@mipmap/ic_assist",
          Exported = true, Theme = "@style/AppTheme", LaunchMode = LaunchMode.SingleTask, TaskAffinity = "com.blacklistintroboys.guild.assistant",
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden)]
[IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { Intent.CategoryLauncher })]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "introboys", DataHost = "assistant")]
public class AssistantActivity : Activity
{
    // Android 13 and newer block the tap permission for apps installed from a file until the member allows "restricted settings"
    static bool Restricted => OperatingSystem.IsAndroidVersionAtLeast(33);

    TextView? who, days, licMsg, bubbleList, runMsg, codeText, codeHint, updText, updMsg, setupDone;
    Button? signIn, signOut, recheck, showControl, updButton;
    LinearLayout? codeBox, updCard;
    Step? stepSign, stepRestricted, stepTaps, stepScreen, stepBubbles;
    TextView? totalsText;
    CancellationTokenSource? signing, updating;
    Updater.Release? update;
    string verifyUrl = "";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AppLog.CatchCrashes();
        License.Begin();
        Remote.Begin();
        SetContentView(Build());
        License.Changed += OnChanged;
        TapService.StateChanged += OnChanged;
        Runner.Changed += OnChanged;
        BubbleLayout.Saved += OnChanged;
        InstallResult.Message += OnInstallMessage;
        Capture.Changed += OnChanged;
        Refresh();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Refresh();
        _ = CheckUpdateAsync();
    }

    protected override void OnDestroy()
    {
        License.Changed -= OnChanged;
        TapService.StateChanged -= OnChanged;
        Runner.Changed -= OnChanged;
        BubbleLayout.Saved -= OnChanged;
        InstallResult.Message -= OnInstallMessage;
        Capture.Changed -= OnChanged;
        signing?.Cancel();
        updating?.Cancel();
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

        // an update, when the site has one
        updCard = Ui.CardBox(c, 8);
        updCard.Background = Ui.Round(c, Ui.Card, 14, Ui.Gold);
        updCard.AddView(Ui.Title(c, "UPDATE READY"));
        updText = Ui.Text(c, "", 13, Ui.Soft);
        updCard.AddView(updText);
        updButton = Ui.Button(c, "Update now", true, () => _ = UpdateAsync());
        updCard.AddView(updButton, Ui.Wide());
        updMsg = Ui.Text(c, "", 12, Ui.Muted);
        updCard.AddView(updMsg);
        updCard.Visibility = ViewStates.Gone;
        col.AddView(updCard, Ui.Wide());

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
        codeHint = Ui.Text(c, "", 12, Ui.Soft);
        codeBox.AddView(codeHint);
        codeBox.AddView(Ui.Button(c, "Approve on the guild site", true, () => OpenSite(verifyUrl)), Ui.Wide());
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

        // first-time setup
        var setup = Ui.CardBox(c, 12);
        var setupHead = Ui.Row(c, 8);
        setupHead.AddView(Ui.Title(c, "SET UP (ONCE)"), Ui.Weight());
        setupDone = Ui.Text(c, "", 12, Ui.Green, true);
        setupHead.AddView(setupDone);
        setup.AddView(setupHead, Ui.Wide());
        int n = 1;
        stepSign = new Step(c, n++, "Sign in with the guild", "Tap Sign in above and approve the code on the guild site. You need Assistant days.");
        setup.AddView(stepSign.View, Ui.Wide());
        if (Restricted)
        {
            stepRestricted = new Step(c, n++, "Allow restricted settings",
                "Android blocks the tap permission for apps installed from a file until you allow it. Tap App info, then the ⋮ at the top right, " +
                "then Allow restricted settings, and confirm. No ⋮ option yet? Do step " + n + " once (Android says it was denied), then come back.");
            stepRestricted.AddButton("App info", false, () => Open(new Intent(Settings.ActionApplicationDetailsSettings, Uri.Parse("package:" + PackageName))));
            setup.AddView(stepRestricted.View, Ui.Wide());
        }
        stepTaps = new Step(c, n++, "Switch on IntroBoys Assistant taps",
            "Tap Accessibility, then Installed apps (or Downloaded apps), then IntroBoys Assistant taps. Switch it on and tap Allow.");
        stepTaps.AddButton("Accessibility", true, () => Open(new Intent(Settings.ActionAccessibilitySettings)));
        setup.AddView(stepTaps.View, Ui.Wide());
        stepScreen = new Step(c, n++, "Live record (optional)",
            "Not needed to run: the Assistant reads the game through its taps permission. Live record (Android's screen sharing) makes the picture on " +
            "the guild website's live view smoother, and is needed on Android 9 and 10. Android asks each time; pick \"Entire screen\".");
        stepScreen.AddButton("Allow", true, () => CaptureActivity.Request(this));
        stepScreen.AddButton("Stop", false, () => CaptureService.StopSharing(this));
        setup.AddView(stepScreen.View, Ui.Wide());
        stepBubbles = new Step(c, n++, "Place your bubbles",
            "Tap Show the floating S, open the game, tap the S, then Bubbles. Drag a bubble onto each game button (attack, skills, buffs, auto loot), " +
            "and place the camera area and the move pad. Tap the camera area to set the camera fix after a teleport. Save with ✓.");
        setup.AddView(stepBubbles.View, Ui.Wide());
        setup.AddView(Ui.Text(c, "You only do this once on this phone. Updates from this screen keep everything.", 11, Ui.Muted));
        col.AddView(setup, Ui.Wide());

        // run
        var run = Ui.CardBox(c);
        run.AddView(Ui.Title(c, "RUN"));
        run.AddView(Ui.Text(c, "Open the game and tap the floating S: pick Attack, Labyrinth or Den, then Start. " +
                               "Runs need screen sharing on and, in Bubbles: Attack, Target, Teleport (on the scroll), Return, Auto loot, the Move pad, " +
                               "the Camera area and the Boss HP bar box.", 13, Ui.Soft));
        totalsText = Ui.Text(c, "", 13, Color.White, true);
        run.AddView(totalsText);
        bubbleList = Ui.Text(c, "", 13, Color.White);
        run.AddView(bubbleList);
        showControl = Ui.Button(c, "Show the floating S", true, ToggleControl);
        run.AddView(showControl, Ui.Wide());
        runMsg = Ui.Text(c, "", 12, Ui.Muted);
        run.AddView(runMsg);
        run.AddView(Ui.Button(c, "Share the log", false, ShareLog), Ui.Wide());
        col.AddView(run, Ui.Wide());

        col.AddView(Ui.Button(c, "Open the guild hall", false, () => Open(new Intent(this, typeof(GuildActivity)))), Ui.Wide());
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
        var layout = BubbleLayout.Load();
        stepSign!.Set(ok);
        stepRestricted?.Set(tapsOn);           // can't be seen directly: done once taps could be switched on
        stepTaps!.Set(tapsOn);
        stepScreen!.Set(Capture.On);
        stepBubbles!.Set(layout.Bubbles.Count > 0);
        totalsText!.Text = "Done on this phone: Labyrinth " + Runner.TotalLabyrinth + " · Den " + Runner.TotalDen;
        setupDone!.Text = ok && tapsOn && layout.Bubbles.Count > 0 ? "ALL DONE ✓" : "";

        bubbleList!.Text = layout.Bubbles.Count == 0
            ? "No bubbles placed yet."
            : string.Join("\n", layout.Bubbles.Select(b => "•  " + b.Label + "  " + Bubble.Name(b.Kind) + " · " + b.Describe()));
        bool shown = TapService.ControlWanted;
        showControl!.Text = shown ? "Hide the floating S" : "Show the floating S";
        bool canShow = shown || (ok && tapsOn);       // only with an active license (and the tap permission)
        showControl.Alpha = canShow ? 1f : 0.45f;
        runMsg!.Text = Runner.Running ? "Auto attack is running · " + Runner.Presses + " presses" :
                       !tapsOn ? "The floating S shows up once IntroBoys Assistant taps is switched on (set-up step " + stepTaps.Number + ")." :
                       Runner.LastStop;
        RefreshUpdate();
    }

    // ------------------------------------------------------------------ updates
    async Task CheckUpdateAsync()
    {
        if (updating != null) return;
        var r = await Updater.CheckAsync(this);
        RunOnUiThread(() => { update = r; RefreshUpdate(); });
    }

    void RefreshUpdate()
    {
        if (updCard == null) return;
        updCard.Visibility = update != null ? ViewStates.Visible : ViewStates.Gone;
        if (update == null) return;
        string size = update.Size > 0 ? " · " + (update.Size / 1048576.0).ToString("0.#") + " MB" : "";
        updText!.Text = "Version " + update.Name + " (build " + update.Code + ")" + size + (update.Notes.Length > 0 ? "\n" + update.Notes : "") +
                        (Updater.CanInstall(this) ? "" : "\n\nFirst time: Android asks you to allow this app to install updates. Switch it on, come back, and tap Update now again.");
        updButton!.Enabled = updating == null;
    }

    async Task UpdateAsync()
    {
        if (update == null || updating != null) return;
        if (!Updater.CanInstall(this)) { Open(Updater.AllowInstallIntent(this)); return; }
        updating = new CancellationTokenSource();
        RefreshUpdate();
        try
        {
            if (Runner.Running) Runner.Stop("Stopped to update.");
            await Updater.InstallAsync(this, update, f => RunOnUiThread(() => updMsg!.Text = "Downloading… " + (int)(f * 100) + "%"), updating.Token);
            updMsg!.Text = "Tap Update (or Install) on Android's prompt.";
        }
        catch (Exception ex) { updMsg!.Text = ex.Message; }
        finally { updating = null; RefreshUpdate(); }
    }

    void OnInstallMessage(string m) => RunOnUiThread(() => { if (updMsg != null) updMsg.Text = m; });

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
        catch (System.OperationCanceledException) { License.Fail("Sign-in cancelled."); }
        catch (Exception ex) { License.Fail("Sign-in failed: " + ex.Message); }
        finally
        {
            signing = null;
            codeText.Text = "";
            Refresh();
        }
    }

    /// <summary>Sends the live log (the last 500 lines) wherever the member wants: Messenger, Discord, email ...</summary>
    void ShareLog()
    {
        var i = new Intent(Intent.ActionSend);
        i.SetType("text/plain");
        i.PutExtra(Intent.ExtraSubject, "IntroBoys Assistant log " + LicenseConfig.AppVersion);
        // 1.3.8: the whole log goes as a file (some apps cut long text short); the text is only the last lines
        var file = AppLog.AsFile(LicenseConfig.AppVersion);
        Uri? uri = null;
        if (file != null)
        {
            try { uri = AndroidX.Core.Content.FileProvider.GetUriForFile(this, LogProvider.Authority, file); } catch { uri = null; }
        }
        if (uri != null)
        {
            i.PutExtra(Intent.ExtraStream, uri);
            i.AddFlags(ActivityFlags.GrantReadUriPermission);
            i.PutExtra(Intent.ExtraText, "IntroBoys Assistant " + LicenseConfig.AppVersion + " log (the whole log is in the attached file)\nphone: " + AppLog.Device() + "\n" + string.Join("\n", AppLog.Last(25)));
        }
        else i.PutExtra(Intent.ExtraText, "IntroBoys Assistant " + LicenseConfig.AppVersion + "\nphone: " + AppLog.Device() + "\n" + AppLog.All());
        Open(Intent.CreateChooser(i, "Share the log")!);
    }

    void ToggleControl()
    {
        bool show = !TapService.ControlWanted;
        if (show && !License.Allowed())
        {
            Toast.MakeText(this, "The floating S needs an active Assistant license: sign in, and buy days if you have none.", ToastLength.Long)!.Show();
            return;
        }
        if (show && TapService.Instance == null)
        {
            Toast.MakeText(this, "Switch on IntroBoys Assistant taps first (set-up step " + stepTaps!.Number + ").", ToastLength.Long)!.Show();
            return;
        }
        TapService.SetControlVisible(show);
        if (show)
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

    /// <summary>One numbered set-up step: a number that turns into a green check, a title, what to do, and its buttons.</summary>
    sealed class Step
    {
        public LinearLayout View { get; }
        public int Number { get; }
        readonly TextView badge, title;
        readonly LinearLayout buttons;
        readonly Context c;

        public Step(Context c, int number, string title, string detail)
        {
            this.c = c;
            Number = number;
            View = Ui.Row(c, 12);
            View.SetGravity(GravityFlags.Top);
            badge = new TextView(c) { Text = number.ToString(), Gravity = GravityFlags.Center };
            badge.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
            View.AddView(badge, new LinearLayout.LayoutParams(Ui.Dp(c, 28), Ui.Dp(c, 28)));
            var col = Ui.Column(c, 6);
            this.title = Ui.Text(c, title, 15, Color.White, true);
            col.AddView(this.title);
            col.AddView(Ui.Text(c, detail, 12, Ui.Muted));
            buttons = Ui.Row(c, 8);
            buttons.Visibility = ViewStates.Gone;
            col.AddView(buttons, Ui.Wide());
            View.AddView(col, Ui.Weight());
            Set(false);
        }

        public void AddButton(string text, bool primary, Action onClick)
        {
            buttons.AddView(Ui.Button(c, text, primary, onClick, 13), Ui.Weight());
            buttons.Visibility = ViewStates.Visible;
        }

        public void Set(bool done)
        {
            badge.Text = done ? "✓" : Number.ToString();
            badge.SetTextColor(done ? Ui.Ink : Color.White);
            badge.Background = done ? Ui.Circle(Ui.Green) : Ui.Circle(Color.Transparent, Color.ParseColor("#5A5A62"), Ui.Dp(c, 2));
            title.SetTextColor(done ? Ui.Green : Color.White);
        }
    }
}
