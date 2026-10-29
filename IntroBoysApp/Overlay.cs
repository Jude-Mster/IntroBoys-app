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
        if (Build.VERSION.SdkInt >= BuildVersionCodes.P) lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
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

    public void CloseAll()
    {
        CloseEditor();
        HideMarkers();
        Remove(control);
        control = null; panel = null; title = null; detail = null; startStop = null;
    }

    // ------------------------------------------------------------------ the floating S
    public void ShowControl()
    {
        if (control != null || editor != null) return;
        var root = Ui.Row(c, 8);
        root.SetGravity(GravityFlags.Top);

        var s = new TextView(c) { Text = "S", Gravity = GravityFlags.Center };
        s.SetTextColor(Color.White);
        s.SetTextSize(Android.Util.ComplexUnitType.Sp, 20);
        s.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        s.Background = Ui.Circle(Ui.Red, Color.White, Ui.Dp(c, 2));
        s.Elevation = Ui.Dp(c, 6);
        root.AddView(s, new LinearLayout.LayoutParams(Ui.Dp(c, 44), Ui.Dp(c, 44)));

        panel = Ui.CardBox(c, 8);
        panel.Background = Ui.Round(c, Color.Argb(235, 18, 18, 21), 14, Ui.Line);
        title = Ui.Title(c, "READY", 15);
        detail = Ui.Text(c, "", 12, Ui.Muted);
        panel.AddView(title);
        panel.AddView(detail);
        var buttons = Ui.Row(c, 6);
        startStop = Ui.Button(c, "Start", true, () => { if (Runner.Running) Runner.Stop("Stopped."); else StartRun(); }, 13);
        buttons.AddView(startStop, Ui.Weight());
        buttons.AddView(Ui.Button(c, "Bubbles", false, OpenEditor, 13), Ui.Weight());
        buttons.AddView(Ui.Button(c, "Hide", false, () => TapService.SetControlVisible(false), 13), Ui.Weight());
        panel.AddView(buttons, Ui.Wide());
        panel.Visibility = ViewStates.Gone;
        root.AddView(panel, new LinearLayout.LayoutParams(Ui.Dp(c, 250), ViewGroup.LayoutParams.WrapContent));

        controlLp = Lp(false, true);
        controlLp.X = TapService.Prefs.GetInt("control_x", Ui.Dp(c, 8));
        controlLp.Y = TapService.Prefs.GetInt("control_y", Ui.Dp(c, 120));
        Draggable(s, root, () =>
        {
            panel.Visibility = panel.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
            Refresh();
        });
        control = root;
        Add(root, controlLp);
        Refresh();
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
        if (why != null && detail != null) { detail.Text = why; detail.SetTextColor(Ui.Gold); }
    }

    void OnRunnerChanged() => svc.Main.Post(Refresh);

    void Refresh()
    {
        if (control == null || title == null || detail == null || startStop == null) return;
        detail.SetTextColor(Ui.Muted);
        if (Runner.Running)
        {
            title.Text = "AUTO ATTACK · LIVE";
            title.SetTextColor(Ui.Green);
            detail.Text = Runner.StatusLine();
            startStop.Text = "Stop";
            ShowMarkers();
        }
        else
        {
            title.Text = "READY";
            title.SetTextColor(Color.White);
            var layout = BubbleLayout.Load();
            string lic = License.Allowed() ? "License: " + License.TimeLeftText() + " left" : License.Why();
            detail.Text = (Runner.LastStop.Length > 0 ? Runner.LastStop + "\n" : "") +
                          (layout.Bubbles.Count == 0 ? "No bubbles yet: tap Bubbles to place them." : layout.Bubbles.Count + " bubbles placed.") + "\n" + lic;
            startStop.Text = "Start";
            HideMarkers();
        }
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
        control = null; panel = null; title = null; detail = null; startStop = null;
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
            if (b.Mode == PressMode.Off) continue;
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
