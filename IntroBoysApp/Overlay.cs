using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace IntroBoys.App;

/// <summary>
/// What the Assistant shows over the game: the floating S (tap it for the small panel: Start, Stop, Bubbles, Hide), dots where the
/// bubbles press while it runs, and the bubble editor. All of it lives in accessibility overlay windows of <see cref="TapService"/>.
/// </summary>
sealed class Overlay
{
    readonly TapService svc;
    readonly Context c;
    View? control;
    WindowManagerLayoutParams? controlLp;
    LinearLayout? panel;
    TextView? title, detail;
    Button? startStop;
    MarkerView? markers;
    Editor? editor;

    public Overlay(TapService svc)
    {
        this.svc = svc;
        c = svc;
        Runner.Changed += OnRunnerChanged;
        Runner.Pressed += OnPressed;
        License.Changed += OnRunnerChanged;
        Capture.Changed += OnRunnerChanged;
    }

    // ------------------------------------------------------------------ windows
    internal static WindowManagerLayoutParams Lp(bool fullScreen, bool touchable, bool keepScreenOn = false)
    {
        var flags = WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutInScreen | WindowManagerFlags.LayoutNoLimits;
        if (!touchable) flags |= WindowManagerFlags.NotTouchable;
        if (keepScreenOn) flags |= WindowManagerFlags.KeepScreenOn;
        int size = fullScreen ? ViewGroup.LayoutParams.MatchParent : ViewGroup.LayoutParams.WrapContent;
        var lp = new WindowManagerLayoutParams(size, size, WindowManagerTypes.AccessibilityOverlay, flags, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
        return lp;
    }

    void Add(View v, WindowManagerLayoutParams lp)
    {
        try { svc.Wm.AddView(v, lp); } catch { /* the service is going away */ }
    }

    void Remove(View? v)
    {
        if (v == null) return;
        try { svc.Wm.RemoveView(v); } catch { /* not shown */ }
    }

    /// <summary>
    /// 1.4.0: is (x, y) on the floating S or its open panel? A tap meant for the game that lands there presses the Assistant's own
    /// buttons instead (on the OPPO a buff bubble lay under the panel: auto attack pressed its own Stop the moment it started). Main thread.
    /// </summary>
    public bool Covers(float x, float y)
    {
        var v = control;
        if (v == null || v.Width <= 0 || !v.IsShown) return false;
        var at = new int[2];
        v.GetLocationOnScreen(at);
        return x >= at[0] && x < at[0] + v.Width && y >= at[1] && y < at[1] + v.Height;
    }

    public void CloseAll()
    {
        CloseEditor();
        HideMarkers();
        Remove(control);
        control = null; panel = null; title = null; detail = null; startStop = null; panelBox = null;
    }

    // ------------------------------------------------------------------ the floating S
    TextView? sub, hpText, posText, logText, totals, runsText, screenNote;
    HpBar? hpBar;
    LinearLayout? runsRow;
    Button? screenBtn;
    readonly Dictionary<RunMode, Button> modeBtns = new();
    bool ticking;
    volatile bool reading;

    public void ShowControl()
    {
        if (control != null || editor != null) return;
        var root = Ui.Row(c, 8);
        root.SetGravity(GravityFlags.Top);

        // the guild's brush S on a red circle (the app icon)
        var s = new ImageView(c);
        s.SetImageResource(Resource.Drawable.logo_s);
        s.SetScaleType(ImageView.ScaleType.FitCenter);
        int pad = Ui.Dp(c, 9);
        s.SetPadding(pad, pad, pad, pad);
        s.Background = Ui.Circle(Ui.Red, Color.White, Ui.Dp(c, 2));
        s.Elevation = Ui.Dp(c, 6);
        root.AddView(s, new LinearLayout.LayoutParams(Ui.Dp(c, 46), Ui.Dp(c, 46)));

        panel = Ui.CardBox(c, 6);
        panel.Background = Ui.Round(c, Color.Argb(238, 18, 18, 21), 14, Ui.Line);
        int pp = Ui.Dp(c, 10);
        panel.SetPadding(pp, pp, pp, pp);

        var head = Ui.Row(c, 6);
        var names = Ui.Column(c, 0);
        title = Ui.Title(c, "READY", 14);
        sub = Ui.Text(c, "", 11, Ui.Muted);
        names.AddView(title);
        names.AddView(sub);
        head.AddView(names, Ui.Weight());
        panel.AddView(head, Ui.Wide());

        // boss HP bar and position (read from the screen)
        hpBar = new HpBar(c);
        panel.AddView(hpBar, Ui.Wide(Ui.Dp(c, 8)));
        var hpRow = Ui.Row(c, 6);
        hpText = Ui.Text(c, "boss HP –", 11, Ui.Soft);
        posText = Ui.Text(c, "position –", 11, Ui.Soft);
        hpRow.AddView(hpText, Ui.Weight());
        hpRow.AddView(posText);
        panel.AddView(hpRow, Ui.Wide());
        screenNote = Ui.Text(c, "", 10, Ui.Gold);
        panel.AddView(screenNote);

        // live log
        logText = Ui.Text(c, "", 10, Ui.Muted);
        logText.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        logText.SetMaxLines(4);
        panel.AddView(logText, Ui.Wide());

        // what Start does
        var modes = Ui.Row(c, 4);
        foreach (var m in new[] { RunMode.Attack, RunMode.Labyrinth, RunMode.Den })
        {
            var b = Ui.Button(c, m == RunMode.Attack ? "Attack" : m == RunMode.Den ? "Den" : "Labyrinth", false, () => { if (!Runner.Running) Runner.Mode = m; }, 11);
            modeBtns[m] = b;
            modes.AddView(b, Ui.Weight());
        }
        panel.AddView(modes, Ui.Wide());
        runsRow = Ui.Row(c, 6);
        runsText = Ui.Text(c, "", 12, Ui.Soft);
        runsRow.AddView(runsText, Ui.Weight());
        runsRow.AddView(Ui.Button(c, "−", false, () => Runner.Runs = Runner.Runs - 1, 12));
        runsRow.AddView(Ui.Button(c, "+", false, () => Runner.Runs = Runner.Runs + 1, 12));
        panel.AddView(runsRow, Ui.Wide());
        totals = Ui.Text(c, "", 10, Ui.Muted);
        panel.AddView(totals);
        detail = Ui.Text(c, "", 11, Ui.Muted);
        panel.AddView(detail);

        var row1 = Ui.Row(c, 6);
        startStop = Ui.Button(c, "Start", true, () => { if (Runner.Running) Runner.Stop("Stopped."); else StartRun(); }, 12);
        row1.AddView(startStop, Ui.Weight());
        row1.AddView(Ui.Button(c, "Camera fix", false, () => _ = CameraFix(), 12), Ui.Weight());
        panel.AddView(row1, Ui.Wide());
        var row2 = Ui.Row(c, 6);
        row2.AddView(Ui.Button(c, "Bubbles", false, OpenEditor, 12), Ui.Weight());
        screenBtn = Ui.Button(c, "Live record", false, ToggleScreen, 12);
        row2.AddView(screenBtn, Ui.Weight());
        row2.AddView(Ui.Button(c, "Hide", false, () => TapService.SetControlVisible(false), 12), Ui.Weight());
        panel.AddView(row2, Ui.Wide());

        var scroller = new ScrollView(c);
        scroller.AddView(panel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        scroller.Visibility = ViewStates.Gone;
        root.AddView(scroller, new LinearLayout.LayoutParams(Ui.Dp(c, 280), ViewGroup.LayoutParams.WrapContent));

        controlLp = Lp(false, true);
        controlLp.X = TapService.Prefs.GetInt("control_x", Ui.Dp(c, 8));
        controlLp.Y = TapService.Prefs.GetInt("control_y", Ui.Dp(c, 60));
        Draggable(s, root, () =>
        {
            scroller.Visibility = scroller.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
            Refresh();
            if (scroller.Visibility == ViewStates.Visible) Tick();
        });
        control = root;
        panelBox = scroller;
        Add(root, controlLp);
        Refresh();
    }
    View? panelBox;

    /// <summary>While the panel is open: the time, the log and the totals stay current (once a second).</summary>
    void Tick()
    {
        if (ticking) return;
        ticking = true;
        void Next()
        {
            if (control == null || panelBox == null || panelBox.Visibility != ViewStates.Visible) { ticking = false; return; }
            // not running: keep the position and boss bar readings live, so the member sees the reading works
            if (!Runner.Running && Capture.CanSee && !reading)
            {
                reading = true;
                _ = Task.Run(async () =>
                {
                    try { await Screen.PositionAsync(); await Screen.BossHpAsync(BubbleLayout.Load()); }
                    finally { reading = false; }
                });
            }
            Refresh();
            svc.Main.PostDelayed(Next, 1000);
        }
        svc.Main.PostDelayed(Next, 1000);
    }

    async Task CameraFix()
    {
        if (Runner.Running) { AppLog.Add("camera fix: stop first"); return; }
        if (!License.Allowed()) { AppLog.Add("camera fix: " + License.Why()); return; }
        // the panel must not catch the drags: fold it while the fix runs
        if (panelBox != null) panelBox.Visibility = ViewStates.Gone;
        try { await svc.CameraFixAsync(BubbleLayout.Load()); }
        catch (Exception ex) { AppLog.Add("camera fix: " + ex.Message); }
        svc.Main.Post(Refresh);
    }

    void ToggleScreen()
    {
        if (Capture.On) CaptureService.StopSharing(c);
        else CaptureActivity.Request(c);
    }

    /// <summary>Drag the S to move the window; a tap without moving runs <paramref name="onTap"/>.</summary>
    void Draggable(View handle, View window, Action onTap)
    {
        float downX = 0, downY = 0; int startX = 0, startY = 0; bool moved = false;
        int slop = ViewConfiguration.Get(c)!.ScaledTouchSlop;
        handle.Touch += (_, e) =>
        {
            var ev = e.Event!;
            switch (ev.ActionMasked)
            {
                case MotionEventActions.Down:
                    downX = ev.RawX; downY = ev.RawY; startX = controlLp!.X; startY = controlLp.Y; moved = false;
                    break;
                case MotionEventActions.Move:
                    float dx = ev.RawX - downX, dy = ev.RawY - downY;
                    if (!moved && Math.Abs(dx) < slop && Math.Abs(dy) < slop) break;
                    moved = true;
                    controlLp!.X = Math.Max(0, startX + (int)dx);
                    controlLp.Y = Math.Max(0, startY + (int)dy);
                    try { svc.Wm.UpdateViewLayout(window, controlLp); } catch { /* gone */ }
                    break;
                case MotionEventActions.Up:
                    if (moved) TapService.Prefs.Edit()!.PutInt("control_x", controlLp!.X)!.PutInt("control_y", controlLp.Y)!.Apply();
                    else onTap();
                    break;
            }
            e.Handled = true;
        };
    }

    void StartRun()
    {
        string? why = Runner.Start();
        if (why != null && detail != null) { detail.Text = why; detail.SetTextColor(Ui.Gold); return; }
        // a run needs the whole screen: fold the panel away (the S stays; tap it to look)
        // 1.4.0: auto attack too - an open panel lay over the member's bubbles on some phones
        if (panelBox != null) panelBox.Visibility = ViewStates.Gone;
    }

    void OnRunnerChanged() => svc.Main.Post(Refresh);

    void Refresh()
    {
        if (control == null || title == null || detail == null || startStop == null) return;
        var layout = BubbleLayout.Load();
        var mode = Runner.Mode;
        detail.SetTextColor(Ui.Muted);
        if (Runner.Running)
        {
            title.Text = Runner.RunningMode == RunMode.Attack ? "AUTO ATTACK · LIVE" : (Runner.Phase.Length > 0 ? Runner.Phase.ToUpperInvariant() : "RUNNING");
            title.SetTextColor(Ui.Green);
            startStop.Text = "Stop";
            detail.Text = Runner.StatusLine();
            ShowMarkers();
        }
        else
        {
            title.Text = "READY";
            title.SetTextColor(Color.White);
            startStop.Text = "Start";
            string lic = License.Allowed() ? "License: " + License.TimeLeftText() + " left" : License.Why();
            int n = layout.Bubbles.Count(b => !b.IsArea);
            detail.Text = (Runner.LastStop.Length > 0 ? Runner.LastStop + "\n" : "") +
                          (n == 0 ? "No bubbles yet: tap Bubbles." : n + " bubbles") + " · " + lic;
            HideMarkers();
        }
        if (sub != null) sub.Text = Runner.ModeName(mode) + (mode == RunMode.Attack ? "" : " · " + (Runner.Runs == 0 ? "until stopped" : Runner.Runs + " runs"));
        foreach (var (m, b) in modeBtns)
            b.Background = m == mode ? Ui.Round(c, Ui.Red, 9) : Ui.Round(c, Color.Transparent, 9, Color.ParseColor("#3A3A40"));
        if (runsRow != null) runsRow.Visibility = mode == RunMode.Attack ? ViewStates.Gone : ViewStates.Visible;
        if (runsText != null) runsText.Text = "Runs in a row: " + (Runner.Runs == 0 ? "0 (until stopped)" : Runner.Runs.ToString());
        if (totals != null) totals.Text = "Done on this phone: Labyrinth " + Runner.TotalLabyrinth + " · Den " + Runner.TotalDen;
        if (logText != null) logText.Text = string.Join("\n", AppLog.Last(4));
        double hp = Screen.LastHp;
        hpBar?.Set(hp);
        if (hpText != null) hpText.Text = "boss HP " + (hp >= 0 ? Math.Round(hp * 100) + "%" : "–");
        if (posText != null) posText.Text = "where: " + Screen.PlaceText + " · position " + Screen.LastPos;
        if (screenNote != null)
        {
            screenNote.Text = !Capture.CanSee ? "This phone needs Live record on to read the screen (Android 10)."
                            : !layout.Bubbles.Any(b => b.Kind == BubbleKind.BossBar) ? "Place the Boss HP bar box (Bubbles) for Labyrinth and Den runs." : "";
            screenNote.Visibility = screenNote.Text!.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        }
        if (screenBtn != null) screenBtn.Text = Capture.On ? "Live rec ✓" : "Live record";
    }

    // ------------------------------------------------------------------ dots where it presses
    void ShowMarkers()
    {
        if (markers != null) return;
        markers = new MarkerView(c, BubbleLayout.Load(), svc);
        Add(markers, Lp(true, false, keepScreenOn: true));
    }

    void HideMarkers()
    {
        Remove(markers);
        markers = null;
    }

    void OnPressed(int index) => svc.Main.Post(() => markers?.Flash(index));

    // ------------------------------------------------------------------ the bubble editor
    void OpenEditor()
    {
        if (editor != null) return;
        Runner.Stop("Stopped to edit the bubbles.");
        HideMarkers();
        Remove(control);
        control = null; panel = null; title = null; detail = null; startStop = null; panelBox = null;
        editor = new Editor(svc, BubbleLayout.Load(), CloseEditorAndShowControl);
        Add(editor.Root, editor.Lp);
    }

    void CloseEditorAndShowControl()
    {
        CloseEditor();
        ShowControl();
    }

    void CloseEditor()
    {
        if (editor == null) return;
        Remove(editor.Root);
        editor = null;
    }
}

/// <summary>Small see-through dots where the bubbles press; the one just pressed lights up. Touches pass through it.</summary>
sealed class MarkerView : View
{
    readonly BubbleLayout layout;
    readonly TapService svc;
    readonly Paint dot = new() { AntiAlias = true };
    readonly Paint ring = new() { AntiAlias = true };
    readonly long[] lit;
    readonly int[] loc = new int[2];

    public MarkerView(Context c, BubbleLayout layout, TapService svc) : base(c)
    {
        this.layout = layout;
        this.svc = svc;
        lit = new long[layout.Bubbles.Count];
        ring.SetStyle(Paint.Style.Stroke);
        ring.StrokeWidth = Ui.Dp(c, 2);
    }

    public void Flash(int i)
    {
        if (i < 0 || i >= lit.Length) return;
        lit[i] = SystemClock.UptimeMillis();
        Invalidate();
        PostInvalidateDelayed(260);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        GetLocationOnScreen(loc);
        var (w, h) = svc.ScreenSize();
        float r = Ui.Dp(Context!, 5), big = Ui.Dp(Context!, 12);
        long now = SystemClock.UptimeMillis();
        for (int i = 0; i < layout.Bubbles.Count; i++)
        {
            var b = layout.Bubbles[i];
            if (b.Mode == PressMode.Off || b.IsArea) continue;
            var (x, y) = layout.At(b, w, h);
            x -= loc[0]; y -= loc[1];
            var col = Color.ParseColor(Bubble.Color(b.Kind));
            bool on = now - lit[i] < 250;
            dot.Color = Color.Argb(on ? 230 : 150, col.R, col.G, col.B);
            canvas.DrawCircle(x, y, r, dot);
            if (on)
            {
                ring.Color = Color.Argb(200, 255, 255, 255);
                canvas.DrawCircle(x, y, big, ring);
            }
        }
    }
}

/// <summary>The boss HP bar in the panel (empty and grey until the boss bar is read).</summary>
sealed class HpBar : View
{
    readonly Paint back = new() { AntiAlias = true }, fill = new() { AntiAlias = true };
    double value = -1;
    public HpBar(Context c) : base(c) { back.Color = Color.ParseColor("#2A2A2F"); fill.Color = Ui.Red; }
    public void Set(double v) { if (Math.Abs(v - value) > 0.001) { value = v; Invalidate(); } }
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float r = Height / 2f;
        canvas.DrawRoundRect(0, 0, Width, Height, r, r, back);
        if (value > 0) canvas.DrawRoundRect(0, 0, (float)(Width * Math.Min(1, value)), Height, r, r, fill);
    }
}
