using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace IntroBoys.App;

/// <summary>
/// "Place your bubbles", drawn over the game: add bubbles, drag each onto the game button it should press (the dot in the middle is
/// where the finger lands), choose how it is pressed, then Save. The panel can be moved to the other side or folded away so it
/// never hides a game button.
/// </summary>
sealed class Editor
{
    public FrameLayout Root { get; }
    /// <summary>The editor's window settings (made untouchable for a moment by Test this tap).</summary>
    public WindowManagerLayoutParams Lp { get; } = Overlay.Lp(true, true);
    readonly TapService svc;
    readonly Context c;
    readonly BubbleLayout layout;
    readonly Action close;
    readonly List<BubbleView> views = new();
    readonly int size;
    readonly int[] loc = new int[2];
    BubbleView? selected;

    readonly LinearLayout panel, body, selBox, everyRow;
    readonly TextView selTitle, everyText, count;
    readonly Dictionary<PressMode, Button> modeButtons = new();
    bool panelRight;

    static readonly double[] Steps = { 0.5, 1, 1.5, 2, 3, 4, 5, 7, 10, 15, 20, 30, 45, 60, 90, 120, 180, 300, 600 };

    public Editor(TapService svc, BubbleLayout layout, Action close)
    {
        this.svc = svc;
        c = svc;
        this.layout = layout.Copy();
        this.close = close;
        size = Ui.Dp(c, 38);

        Root = new FrameLayout(c);
        Root.SetBackgroundColor(Color.Argb(90, 8, 8, 10));

        // ---------------- the panel
        panel = Ui.CardBox(c, 8);
        panel.Background = Ui.Round(c, Color.Argb(240, 18, 18, 21), 14, Ui.Line);
        var head = Ui.Row(c, 6);
        var t = Ui.Title(c, "PLACE YOUR BUBBLES", 14);
        head.AddView(t, Ui.Weight());
        head.AddView(Ui.Button(c, "⇄", false, FlipSide, 13));
        var fold = Ui.Button(c, "–", false, null, 13);
        head.AddView(fold);
        panel.AddView(head, Ui.Wide());

        body = Ui.Column(c, 8);
        body.AddView(Ui.Text(c, "Add a bubble, then drag it onto the game button. The dot in the middle is where it taps.", 12, Ui.Muted));
        var kinds = new[] { BubbleKind.Attack, BubbleKind.Target, BubbleKind.Skill, BubbleKind.Buff, BubbleKind.Potion, BubbleKind.AutoLoot, BubbleKind.Other };
        LinearLayout? row = null;
        for (int i = 0; i < kinds.Length; i++)
        {
            if (i % 3 == 0) { row = Ui.Row(c, 6); body.AddView(row, Ui.Wide()); }
            var k = kinds[i];
            row!.AddView(Ui.Button(c, "+ " + Bubble.Name(k), false, () => AddBubble(k), 12), Ui.Weight());
        }
        if (kinds.Length % 3 != 0) for (int i = kinds.Length % 3; i < 3; i++) row!.AddView(new View(c), Ui.Weight());

        // the selected bubble
        selBox = Ui.Column(c, 8);
        selBox.Background = Ui.Round(c, Ui.Field, 10);
        int p = Ui.Dp(c, 10);
        selBox.SetPadding(p, p, p, p);
        selTitle = Ui.Title(c, "", 14);
        selBox.AddView(selTitle);
        var modes = Ui.Row(c, 4);
        foreach (var m in new[] { PressMode.Hold, PressMode.Repeat, PressMode.Once, PressMode.Off })
        {
            var mb = Ui.Button(c, m == PressMode.Repeat ? "Every…" : m.ToString(), false, () => SetMode(m), 12);
            modeButtons[m] = mb;
            modes.AddView(mb, Ui.Weight());
        }
        selBox.AddView(modes, Ui.Wide());
        everyRow = Ui.Row(c, 6);
        everyRow.AddView(Ui.Text(c, "every", 13, Ui.Soft));
        everyText = Ui.Text(c, "", 14, Color.White, true);
        everyRow.AddView(everyText, Ui.Weight());
        everyRow.AddView(Ui.Button(c, "−", false, () => StepEvery(-1), 14));
        everyRow.AddView(Ui.Button(c, "+", false, () => StepEvery(+1), 14));
        selBox.AddView(everyRow, Ui.Wide());
        var selButtons = Ui.Row(c, 6);
        selButtons.AddView(Ui.Button(c, "Test this tap", false, TestTap, 12), Ui.Weight());
        var remove = Ui.Button(c, "Remove", false, RemoveSelected, 12);
        remove.SetTextColor(Color.ParseColor("#FF8A8E"));
        selButtons.AddView(remove, Ui.Weight());
        selBox.AddView(selButtons, Ui.Wide());
        selBox.Visibility = ViewStates.Gone;
        body.AddView(selBox, Ui.Wide());

        count = Ui.Text(c, "", 12, Ui.Soft);
        body.AddView(count);
        body.AddView(Ui.Text(c, "Keep the floating S away from your bubbles: a tap that lands on it never reaches the game.", 11, Ui.Muted));
        var foot = Ui.Row(c, 6);
        foot.AddView(Ui.Button(c, "Cancel", false, close, 13), Ui.Weight());
        foot.AddView(Ui.Button(c, "Save", true, Save, 13), Ui.Weight());
        body.AddView(foot, Ui.Wide());
        panel.AddView(body, Ui.Wide());

        fold.Click += (_, _) =>
        {
            body.Visibility = body.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
            fold.Text = body.Visibility == ViewStates.Visible ? "–" : "+";
        };

        panelRight = TapService.Prefs.GetBoolean("editor_right", false);
        Root.AddView(panel, PanelLp());

        // ---------------- the bubbles (placed once the window knows where it is on the screen)
        Root.Post(() =>
        {
            foreach (var b in this.layout.Bubbles) AddView(b);
            UpdateCount();
        });
        // a tap on the empty screen unselects
        Root.Click += (_, _) => Select(null);
    }

    FrameLayout.LayoutParams PanelLp()
    {
        var lp = new FrameLayout.LayoutParams(Ui.Dp(c, 270), ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = (panelRight ? GravityFlags.Right : GravityFlags.Left) | GravityFlags.Top
        };
        int m = Ui.Dp(c, 12);
        lp.SetMargins(m, m, m, m);
        return lp;
    }

    void FlipSide()
    {
        panelRight = !panelRight;
        TapService.Prefs.Edit()!.PutBoolean("editor_right", panelRight)!.Apply();
        panel.LayoutParameters = PanelLp();
    }

    // ------------------------------------------------------------------ bubbles
    void AddBubble(BubbleKind k)
    {
        // a new bubble starts in the middle of the screen, a little apart from the last new one
        Root.GetLocationOnScreen(loc);
        float x = loc[0] + Root.Width / 2f + (views.Count % 5) * Ui.Dp(c, 14);
        float y = loc[1] + Root.Height / 2f + (views.Count % 5) * Ui.Dp(c, 14);
        var b = Bubble.New(k, x, y);
        layout.Bubbles.Add(b);
        layout.Relabel();
        foreach (var v in views) v.Invalidate();
        Select(AddView(b));
        UpdateCount();
    }

    BubbleView AddView(Bubble b)
    {
        var v = new BubbleView(c, b);
        Root.AddView(v, new FrameLayout.LayoutParams(size, size));
        views.Add(v);
        Place(v);
        panel.BringToFront();

        float downX = 0, downY = 0, startX = 0, startY = 0; bool moved = false;
        int slop = ViewConfiguration.Get(c)!.ScaledTouchSlop;
        v.Touch += (_, e) =>
        {
            var ev = e.Event!;
            switch (ev.ActionMasked)
            {
                case MotionEventActions.Down:
                    downX = ev.RawX; downY = ev.RawY; startX = b.X; startY = b.Y; moved = false;
                    Select(v);
                    break;
                case MotionEventActions.Move:
                    float dx = ev.RawX - downX, dy = ev.RawY - downY;
                    if (!moved && Math.Abs(dx) < slop && Math.Abs(dy) < slop) break;
                    moved = true;
                    b.X = startX + dx; b.Y = startY + dy;
                    Place(v);
                    break;
            }
            e.Handled = true;
        };
        return v;
    }

    /// <summary>Puts the view so that its centre is on the bubble's screen point.</summary>
    void Place(BubbleView v)
    {
        Root.GetLocationOnScreen(loc);
        v.TranslationX = v.Bubble.X - loc[0] - size / 2f;
        v.TranslationY = v.Bubble.Y - loc[1] - size / 2f;
    }

    void Select(BubbleView? v)
    {
        selected = v;
        foreach (var x in views) x.Selected = x == v;
        if (v == null) { selBox.Visibility = ViewStates.Gone; return; }
        selBox.Visibility = ViewStates.Visible;
        body.Visibility = ViewStates.Visible;
        ShowSelected();
    }

    void ShowSelected()
    {
        if (selected == null) return;
        var b = selected.Bubble;
        selTitle.Text = b.Label + " · " + Bubble.Name(b.Kind) + " · " + b.Describe();
        foreach (var (m, btn) in modeButtons)
            btn.Background = m == b.Mode ? Ui.Round(c, Ui.Red, 9) : Ui.Round(c, Color.Transparent, 9, Color.ParseColor("#3A3A40"));
        everyRow.Visibility = b.Mode == PressMode.Repeat ? ViewStates.Visible : ViewStates.Gone;
        everyText.Text = Bubble.Secs(b.Every);
        selected.Invalidate();
    }

    void SetMode(PressMode m)
    {
        if (selected == null) return;
        selected.Bubble.Mode = m;
        ShowSelected();
    }

    void StepEvery(int dir)
    {
        if (selected == null) return;
        var b = selected.Bubble;
        int i = Array.FindIndex(Steps, s => s >= b.Every - 0.001);
        if (i < 0) i = Steps.Length - 1;
        i = Math.Clamp(i + dir, 0, Steps.Length - 1);
        b.Every = Steps[i];
        ShowSelected();
    }

    void RemoveSelected()
    {
        if (selected == null) return;
        layout.Bubbles.Remove(selected.Bubble);
        Root.RemoveView(selected);
        views.Remove(selected);
        layout.Relabel();
        foreach (var v in views) v.Invalidate();
        Select(null);
        UpdateCount();
    }

    /// <summary>Hides the editor for a moment and presses the selected bubble once, so the member sees the game react.</summary>
    async void TestTap()
    {
        if (selected == null) return;
        var b = selected.Bubble;
        if (!License.Allowed()) { count.Text = License.Why(); count.SetTextColor(Ui.Gold); return; }
        // the editor must let the tap through to the game: hide it and stop it catching touches
        Root.Visibility = ViewStates.Invisible;
        Lp.Flags |= WindowManagerFlags.NotTouchable;
        try { svc.Wm.UpdateViewLayout(Root, Lp); } catch { /* gone */ }
        try
        {
            await Task.Delay(250);
            bool ok = await svc.PressAsync(b.X, b.Y, 80);
            await Task.Delay(400);
            count.Text = ok ? "Tapped " + b.Label + "." : "The tap did not go through. Try again without touching the screen.";
        }
        catch (Exception ex) { count.Text = ex.Message; }
        finally
        {
            Lp.Flags &= ~WindowManagerFlags.NotTouchable;
            try { svc.Wm.UpdateViewLayout(Root, Lp); } catch { /* gone */ }
            Root.Visibility = ViewStates.Visible;
        }
    }

    void UpdateCount()
    {
        count.SetTextColor(Ui.Soft);
        count.Text = layout.Bubbles.Count == 0 ? "No bubbles yet." : layout.Bubbles.Count + (layout.Bubbles.Count == 1 ? " bubble" : " bubbles");
    }

    void Save()
    {
        var (w, h) = svc.ScreenSize();
        // the layout is kept in the screen size it was made on; if the screen was turned while editing, stretch it to now
        layout.ScreenW = w;
        layout.ScreenH = h;
        layout.Save();
        close();
    }
}

/// <summary>A bubble in the editor: a small coloured circle with its label, a ring when selected and a dot where it taps.</summary>
sealed class BubbleView : View
{
    public Bubble Bubble { get; }
    readonly Paint fill = new() { AntiAlias = true };
    readonly Paint edge = new() { AntiAlias = true };
    readonly Paint label = new() { AntiAlias = true };
    readonly Paint pin = new() { AntiAlias = true };

    public BubbleView(Context c, Bubble b) : base(c)
    {
        Bubble = b;
        edge.SetStyle(Paint.Style.Stroke);
        label.Color = Color.White;
        label.TextAlign = Paint.Align.Center;
        label.FakeBoldText = true;
        pin.Color = Color.White;
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float w = Width, h = Height, cx = w / 2, cy = h / 2;
        float r = Math.Min(w, h) / 2 - Ui.Dp(Context!, 3);
        var col = Color.ParseColor(Bubble.Color(Bubble.Kind));
        fill.Color = Bubble.Mode == PressMode.Off ? Color.Argb(120, col.R, col.G, col.B) : Color.Argb(215, col.R, col.G, col.B);
        canvas.DrawCircle(cx, cy, r, fill);
        edge.StrokeWidth = Ui.Dp(Context!, Selected ? 3 : 2);
        edge.Color = Selected ? Ui.Gold : Color.White;
        canvas.DrawCircle(cx, cy, r, edge);
        label.TextSize = Ui.Dp(Context!, Bubble.Label.Length > 3 ? 8 : 9);
        canvas.DrawText(Bubble.Label, cx, cy - Ui.Dp(Context!, 4), label);
        // the exact point it presses
        canvas.DrawCircle(cx, cy, Ui.Dp(Context!, 2.5f), pin);
    }
}
