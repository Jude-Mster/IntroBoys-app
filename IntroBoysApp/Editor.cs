using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace IntroBoys.App;

/// <summary>
/// "Bubbles", drawn over the game: add bubbles and drag each onto the game button it should press (the dot in the middle is where the
/// finger lands), choose how it is pressed, place the camera area and the move pad, and set the camera fix after a teleport. ✓ saves,
/// ✕ leaves without saving. The panel can be moved to the other side or folded away so it never hides a game button.
/// </summary>
sealed class Editor
{
    public FrameLayout Root { get; }
    /// <summary>The editor's window settings (made untouchable for a moment while a test runs).</summary>
    public WindowManagerLayoutParams Lp { get; } = Overlay.Lp(true, true);
    readonly TapService svc;
    readonly Context c;
    readonly BubbleLayout layout;
    readonly Action close;
    readonly List<ItemView> views = new();
    readonly int size;
    readonly int[] loc = new int[2];
    ItemView? selected;

    readonly LinearLayout panel, body, selBox, modesRow, everyRow, camBox, padBox, barBox, stepsList, tapButtons;
    readonly TextView barRead;
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
        head.AddView(Ui.Title(c, "BUBBLES", 14), Ui.Weight());
        head.AddView(Ui.Button(c, "⇄", false, FlipSide, 13));
        var fold = Ui.Button(c, "–", false, null, 13);
        head.AddView(fold);
        // always there, even with the panel folded: leave without saving, or save
        head.AddView(Ui.Button(c, "✕", false, close, 13));
        head.AddView(Ui.Button(c, "✓", true, Save, 13));
        panel.AddView(head, Ui.Wide());

        body = Ui.Column(c, 8);
        body.AddView(Ui.Text(c, "Add a bubble and drag it onto the game button (the dot in the middle is where it taps). Tap one to change or remove it. Fold the panel (–) to move bubbles under it.", 11, Ui.Muted));
        var kinds = new[] { BubbleKind.Attack, BubbleKind.Target, BubbleKind.Skill, BubbleKind.Buff, BubbleKind.Potion, BubbleKind.AutoLoot,
                            BubbleKind.Teleport, BubbleKind.Return, BubbleKind.Other, BubbleKind.Camera, BubbleKind.MovePad, BubbleKind.BossBar };
        LinearLayout? row = null;
        for (int i = 0; i < kinds.Length; i++)
        {
            if (i % 3 == 0) { row = Ui.Row(c, 6); body.AddView(row, Ui.Wide()); }
            var k = kinds[i];
            row!.AddView(Ui.Button(c, "+ " + Bubble.Name(k), false, () => AddBubble(k), 11), Ui.Weight());
        }
        if (kinds.Length % 3 != 0) for (int i = kinds.Length % 3; i < 3; i++) row!.AddView(new View(c), new LinearLayout.LayoutParams(0, 1, 1));   // an empty slot: 1 px tall

        // ---------------- the selected bubble or area
        selBox = Ui.Column(c, 8);
        selBox.Background = Ui.Round(c, Ui.Field, 10);
        int p = Ui.Dp(c, 10);
        selBox.SetPadding(p, p, p, p);
        selTitle = Ui.Title(c, "", 13);
        selBox.AddView(selTitle);

        modesRow = Ui.Row(c, 4);
        foreach (var m in new[] { PressMode.Hold, PressMode.Repeat, PressMode.Once, PressMode.Off })
        {
            var mb = Ui.Button(c, m == PressMode.Repeat ? "Every…" : m.ToString(), false, () => SetMode(m), 11);
            modeButtons[m] = mb;
            modesRow.AddView(mb, Ui.Weight());
        }
        selBox.AddView(modesRow, Ui.Wide());
        everyRow = Ui.Row(c, 6);
        everyRow.AddView(Ui.Text(c, "every", 13, Ui.Soft));
        everyText = Ui.Text(c, "", 14, Color.White, true);
        everyRow.AddView(everyText, Ui.Weight());
        everyRow.AddView(Ui.Button(c, "−", false, () => StepEvery(-1), 14));
        everyRow.AddView(Ui.Button(c, "+", false, () => StepEvery(+1), 14));
        selBox.AddView(everyRow, Ui.Wide());
        tapButtons = Ui.Row(c, 6);
        tapButtons.AddView(Ui.Button(c, "Test this tap", false, () => _ = Test(TestTap), 11), Ui.Weight());
        selBox.AddView(tapButtons, Ui.Wide());

        // camera area: the camera fix after a teleport (the one route to edit)
        camBox = Ui.Column(c, 6);
        camBox.AddView(Ui.Text(c, "Drag the box over the open ground where you turn the view; drag its corner to size it. " +
                                  "After a teleport the Assistant does these steps on it, in order, to set the view the same way every time:", 11, Ui.Muted));
        stepsList = Ui.Column(c, 4);
        camBox.AddView(stepsList, Ui.Wide());
        var addOps1 = Ui.Row(c, 4);
        foreach (var (op, label) in new[] { (CamOp.Left, "← drag"), (CamOp.Right, "drag →"), (CamOp.Up, "↑ drag"), (CamOp.Down, "↓ drag") })
            addOps1.AddView(Ui.Button(c, label, false, () => AddStep(op), 11), Ui.Weight());
        camBox.AddView(addOps1, Ui.Wide());
        var addOps2 = Ui.Row(c, 4);
        addOps2.AddView(Ui.Button(c, "+ Zoom in", false, () => AddStep(CamOp.ZoomIn), 11), Ui.Weight());
        addOps2.AddView(Ui.Button(c, "+ Zoom out", false, () => AddStep(CamOp.ZoomOut), 11), Ui.Weight());
        camBox.AddView(addOps2, Ui.Wide());
        camBox.AddView(Ui.Button(c, "Test the camera fix", false, () => _ = Test(TestCamera), 12), Ui.Wide());
        selBox.AddView(camBox, Ui.Wide());

        // move pad
        barBox = Ui.Column(c, 6);
        barBox.AddView(Ui.Text(c, "During a boss fight, put this thin box exactly over the boss's red HP bar (from where the bar starts to where it ends when full). " +
                                  "The Assistant reads how much red is left: it knows when the boss shows up, how its HP goes, and when it is dead.", 11, Ui.Muted));
        barRead = Ui.Text(c, "", 12, Color.White, true);
        barBox.AddView(barRead);
        barBox.AddView(Ui.Button(c, "Read the bar now", false, ReadBar, 12), Ui.Wide());
        selBox.AddView(barBox, Ui.Wide());

        padBox = Ui.Column(c, 6);
        padBox.AddView(Ui.Text(c, "Put the middle on the game's move pad, and drag the ring's edge to how far the stick goes. Labyrinth and Den runs walk with it.", 11, Ui.Muted));
        padBox.AddView(Ui.Button(c, "Test: walk forward 1 s", false, () => _ = Test(TestPad), 12), Ui.Wide());
        selBox.AddView(padBox, Ui.Wide());

        var remove = Ui.Button(c, "Remove", false, RemoveSelected, 11);
        remove.SetTextColor(Color.ParseColor("#FF8A8E"));
        selBox.AddView(remove, Ui.Wide());
        selBox.Visibility = ViewStates.Gone;
        body.AddView(selBox, Ui.Wide());

        count = Ui.Text(c, "", 12, Ui.Soft);
        var countRow = Ui.Row(c, 6);
        countRow.AddView(count, Ui.Weight());
        var clear = Ui.Button(c, "Remove all", false, RemoveAll, 11);
        clear.SetTextColor(Color.ParseColor("#FF8A8E"));
        countRow.AddView(clear);
        body.AddView(countRow, Ui.Wide());
        body.AddView(Ui.Text(c, "Keep the floating S away from your bubbles: a tap that lands on it never reaches the game.", 11, Ui.Muted));
        var foot = Ui.Row(c, 6);
        foot.AddView(Ui.Button(c, "Cancel", false, close, 13), Ui.Weight());
        foot.AddView(Ui.Button(c, "Save", true, Save, 13), Ui.Weight());
        body.AddView(foot, Ui.Wide());

        var scroller = new ScrollView(c);
        scroller.AddView(body, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        panel.AddView(scroller, Ui.Wide());

        fold.Click += (_, _) =>
        {
            scroller.Visibility = scroller.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
            fold.Text = scroller.Visibility == ViewStates.Visible ? "–" : "+";
        };

        panelRight = TapService.Prefs.GetBoolean("editor_right", false);
        Root.AddView(panel, PanelLp());

        // ---------------- the bubbles (placed once the window knows where it is on the screen)
        Root.Post(() =>
        {
            // areas first, so bubbles sit on top of them
            foreach (var b in this.layout.Bubbles.OrderBy(x => x.IsArea ? 0 : 1)) AddView(b);
            UpdateCount();
        });
        Root.Click += (_, _) => Select(null);
    }

    FrameLayout.LayoutParams PanelLp()
    {
        var lp = new FrameLayout.LayoutParams(Ui.Dp(c, 290), ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = (panelRight ? GravityFlags.Right : GravityFlags.Left) | GravityFlags.Top
        };
        int m = Ui.Dp(c, 10);
        lp.SetMargins(m, m, m, m);
        return lp;
    }

    void FlipSide()
    {
        panelRight = !panelRight;
        TapService.Prefs.Edit()!.PutBoolean("editor_right", panelRight)!.Apply();
        panel.LayoutParameters = PanelLp();
    }

    // ------------------------------------------------------------------ bubbles and areas
    void AddBubble(BubbleKind k)
    {
        Root.GetLocationOnScreen(loc);
        float w = Root.Width, h = Root.Height;
        Bubble b;
        if (k == BubbleKind.Camera)
        {
            if (layout.Find(k) is { } had) { Select(views.FirstOrDefault(v => v.Bubble == had)); return; }      // one camera area
            b = Bubble.New(k, loc[0] + w * 0.6f, loc[1] + h * 0.42f);
            b.W = w * 0.42f; b.H = h * 0.5f;
        }
        else if (k == BubbleKind.BossBar)
        {
            if (layout.Find(k) is { } had) { Select(views.FirstOrDefault(v => v.Bubble == had)); return; }      // one boss HP bar
            b = Bubble.New(k, loc[0] + w * 0.5f, loc[1] + h * 0.12f);
            b.W = w * 0.22f; b.H = Ui.Dp(c, 14);
        }
        else if (k == BubbleKind.MovePad)
        {
            if (layout.Find(k) is { } had) { Select(views.FirstOrDefault(v => v.Bubble == had)); return; }      // one move pad
            b = Bubble.New(k, loc[0] + Math.Min(w, h) * 0.3f, loc[1] + h * 0.72f);
            b.W = Ui.Dp(c, 62);
        }
        else
        {
            // a new bubble starts in the middle of the screen, a little apart from the last new one
            int n = views.Count(v => v is BubbleView);
            b = Bubble.New(k, loc[0] + w / 2f + (n % 5) * Ui.Dp(c, 14), loc[1] + h / 2f + (n % 5) * Ui.Dp(c, 14));
        }
        layout.Bubbles.Add(b);
        layout.Relabel();
        foreach (var v in views) v.Invalidate();
        Select(AddView(b));
        UpdateCount();
    }

    ItemView AddView(Bubble b)
    {
        ItemView v = b.IsArea ? new AreaView(c, b) : new BubbleView(c, b);
        Root.AddView(v, new FrameLayout.LayoutParams(size, size));
        views.Add(v);
        Place(v);
        foreach (var x in views.Where(x => x is BubbleView)) x.BringToFront();
        panel.BringToFront();

        float downX = 0, downY = 0, startX = 0, startY = 0, startW = 0, startH = 0; bool moved = false, resizing = false;
        int slop = ViewConfiguration.Get(c)!.ScaledTouchSlop;
        v.Touch += (_, e) =>
        {
            var ev = e.Event!;
            switch (ev.ActionMasked)
            {
                case MotionEventActions.Down:
                    downX = ev.RawX; downY = ev.RawY; startX = b.X; startY = b.Y; startW = b.W; startH = b.H; moved = false;
                    resizing = v is AreaView av && av.OnHandle(ev.GetX(), ev.GetY());
                    Select(v);
                    break;
                case MotionEventActions.Move:
                    float dx = ev.RawX - downX, dy = ev.RawY - downY;
                    if (!moved && Math.Abs(dx) < slop && Math.Abs(dy) < slop) break;
                    moved = true;
                    if (resizing && b.IsBox)
                    {
                        // the top-left corner stays put; the bottom-right follows the finger
                        float minW = b.Kind == BubbleKind.BossBar ? Ui.Dp(c, 30) : Ui.Dp(c, 80), minH = b.Kind == BubbleKind.BossBar ? Ui.Dp(c, 4) : Ui.Dp(c, 60);
                        b.W = Math.Max(minW, startW + dx); b.H = Math.Max(minH, startH + dy);
                        b.X = startX - startW / 2 + b.W / 2; b.Y = startY - startH / 2 + b.H / 2;
                    }
                    else if (resizing) b.W = Math.Max(Ui.Dp(c, 24), startW + (dx + dy) / 2);
                    else { b.X = startX + dx; b.Y = startY + dy; }
                    Place(v);
                    break;
            }
            e.Handled = true;
        };
        return v;
    }

    /// <summary>Puts the view so that its centre is on the bubble's screen point (areas: sized to the area).</summary>
    void Place(ItemView v)
    {
        Root.GetLocationOnScreen(loc);
        var b = v.Bubble;
        float w = size, h = size;
        if (b.IsBox) { w = b.W; h = b.H; }
        else if (b.Kind == BubbleKind.MovePad) { w = h = b.W * 2 + Ui.Dp(c, 16); }
        var lp = v.LayoutParameters;
        if (lp != null && (lp.Width != (int)w || lp.Height != (int)h)) { lp.Width = (int)w; lp.Height = (int)h; v.LayoutParameters = lp; }
        v.TranslationX = b.X - loc[0] - w / 2f;
        v.TranslationY = b.Y - loc[1] - h / 2f;
    }

    void Select(ItemView? v)
    {
        selected = v;
        foreach (var x in views) x.Selected = x == v;
        if (v == null) { selBox.Visibility = ViewStates.Gone; return; }
        selBox.Visibility = ViewStates.Visible;
        // a folded panel stays folded while bubbles are moved; unfold it (+) to change the selected one
        ShowSelected();
    }

    void ShowSelected()
    {
        if (selected == null) return;
        var b = selected.Bubble;
        bool area = b.IsArea;
        selTitle.Text = area ? Bubble.Name(b.Kind).ToUpperInvariant() : b.Label + " · " + Bubble.Name(b.Kind) + " · " + b.Describe();
        modesRow.Visibility = area ? ViewStates.Gone : ViewStates.Visible;
        tapButtons.Visibility = area ? ViewStates.Gone : ViewStates.Visible;
        camBox.Visibility = b.Kind == BubbleKind.Camera ? ViewStates.Visible : ViewStates.Gone;
        padBox.Visibility = b.Kind == BubbleKind.MovePad ? ViewStates.Visible : ViewStates.Gone;
        barBox.Visibility = b.Kind == BubbleKind.BossBar ? ViewStates.Visible : ViewStates.Gone;
        foreach (var (m, btn) in modeButtons)
            btn.Background = m == b.Mode ? Ui.Round(c, Ui.Red, 9) : Ui.Round(c, Color.Transparent, 9, Color.ParseColor("#3A3A40"));
        everyRow.Visibility = !area && b.Mode == PressMode.Repeat ? ViewStates.Visible : ViewStates.Gone;
        everyText.Text = Bubble.Secs(b.Every);
        if (b.Kind == BubbleKind.Camera) ShowSteps();
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

    // ------------------------------------------------------------------ the camera fix steps
    void AddStep(CamOp op)
    {
        if (layout.CamFix.Count >= 12) return;
        layout.CamFix.Add(new CamStep { Op = op, Amount = op is CamOp.ZoomIn or CamOp.ZoomOut ? 0.6 : 0.5 });
        ShowSteps();
    }

    void ShowSteps()
    {
        stepsList.RemoveAllViews();
        if (layout.CamFix.Count == 0) { stepsList.AddView(Ui.Text(c, "No steps yet: add them below.", 12, Ui.Soft)); return; }
        for (int i = 0; i < layout.CamFix.Count; i++)
        {
            var st = layout.CamFix[i];
            var r = Ui.Row(c, 4);
            r.AddView(Ui.Text(c, (i + 1) + ". " + st.Describe(), 12, Color.White), Ui.Weight());
            r.AddView(Ui.Button(c, "−", false, () => { st.Amount = Math.Max(0.1, Math.Round(st.Amount - 0.1, 1)); ShowSteps(); }, 11));
            r.AddView(Ui.Button(c, "+", false, () => { st.Amount = Math.Min(1.0, Math.Round(st.Amount + 0.1, 1)); ShowSteps(); }, 11));
            var x = Ui.Button(c, "✕", false, () => { layout.CamFix.Remove(st); ShowSteps(); }, 11);
            x.SetTextColor(Color.ParseColor("#FF8A8E"));
            r.AddView(x);
            stepsList.AddView(r, Ui.Wide());
        }
    }

    // ------------------------------------------------------------------ removing
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

    void RemoveAll()
    {
        foreach (var v in views) Root.RemoveView(v);
        views.Clear();
        layout.Bubbles.Clear();
        layout.CamFix.Clear();
        Select(null);
        UpdateCount();
    }

    // ------------------------------------------------------------------ tests (the editor gets out of the way for a moment)
    async Task Test(Func<Task<string>> run)
    {
        if (!License.Allowed()) { count.Text = License.Why(); count.SetTextColor(Ui.Gold); return; }
        Root.Visibility = ViewStates.Invisible;
        Lp.Flags |= WindowManagerFlags.NotTouchable;
        try { svc.Wm.UpdateViewLayout(Root, Lp); } catch { /* gone */ }
        try
        {
            await Task.Delay(250);
            string msg = await run();
            await Task.Delay(400);
            count.SetTextColor(Ui.Soft);
            count.Text = msg;
        }
        catch (Exception ex) { count.Text = ex.Message; }
        finally
        {
            Lp.Flags &= ~WindowManagerFlags.NotTouchable;
            try { svc.Wm.UpdateViewLayout(Root, Lp); } catch { /* gone */ }
            Root.Visibility = ViewStates.Visible;
        }
    }

    async Task<string> TestTap()
    {
        if (selected == null) return "";
        var b = selected.Bubble;
        bool ok = await svc.PressAsync(b.X, b.Y, 80);
        return ok ? "Tapped " + b.Label + "." : "The tap did not go through. Try again without touching the screen.";
    }

    async Task<string> TestCamera()
    {
        var (w, h) = svc.ScreenSize();
        var copy = layout.Copy();
        copy.ScreenW = w; copy.ScreenH = h;
        bool ok = await svc.CameraFixAsync(copy);
        return ok ? "Camera fix done: did the view end up how you want it?" : "Add some steps first.";
    }

    async Task<string> TestPad()
    {
        var pad = layout.Find(BubbleKind.MovePad);
        if (pad == null) return "";
        bool ok = await svc.SwipeAsync(pad.X, pad.Y, pad.X, pad.Y - pad.W, 1000);
        return ok ? "Walked forward. If not, move the pad's middle onto the game's stick." : "The drag did not go through.";
    }

    /// <summary>Reads the boss HP bar in the box as it is placed now (needs screen sharing).</summary>
    void ReadBar()
    {
        if (!Capture.CanSee) { barRead.Text = "The Assistant can't see the screen on this phone: tap Live record on the floating panel first."; return; }
        var (w, h) = svc.ScreenSize();
        var copy = layout.Copy();
        copy.ScreenW = w; copy.ScreenH = h;
        // the editor's own dim colour is drawn over the game: hide it for the reading
        Root.Visibility = ViewStates.Invisible;
        Root.PostDelayed(async () =>
        {
            var (hp, there) = await Screen.BossAsync(copy, true);
            Root.Visibility = ViewStates.Visible;
            barRead.Text = hp >= 0 ? "Boss HP: " + Math.Round(hp * 100) + "%" : there ? "A target is there, but no red in the box: move the box onto the red bar." : hp == -1 ? "No red bar in the box (target the boss first)." : "Place the box first.";
        }, 400);
    }

    void UpdateCount()
    {
        count.SetTextColor(Ui.Soft);
        int n = layout.Bubbles.Count(b => !b.IsArea);
        count.Text = (n == 0 ? "No bubbles yet" : n + (n == 1 ? " bubble" : " bubbles")) +
                     (layout.Find(BubbleKind.Camera) != null ? " · camera area" : "") + (layout.Find(BubbleKind.MovePad) != null ? " · move pad" : "");
    }

    void Save()
    {
        var (w, h) = svc.ScreenSize();
        // the layout is kept in the screen size it was made on
        layout.ScreenW = w;
        layout.ScreenH = h;
        layout.Save();
        AppLog.Add("bubbles saved (" + layout.Bubbles.Count + ")");
        close();
    }
}

/// <summary>Something placed in the editor: a bubble or an area.</summary>
abstract class ItemView : View
{
    public Bubble Bubble { get; }
    protected ItemView(Context c, Bubble b) : base(c) { Bubble = b; }
}

/// <summary>A bubble in the editor: a small coloured circle with its label, a ring when selected and a dot where it taps.</summary>
sealed class BubbleView : ItemView
{
    readonly Paint fill = new() { AntiAlias = true };
    readonly Paint edge = new() { AntiAlias = true };
    readonly Paint label = new() { AntiAlias = true };
    readonly Paint pin = new() { AntiAlias = true };

    public BubbleView(Context c, Bubble b) : base(c, b)
    {
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

/// <summary>The camera area (a dashed box) or the move pad (a dashed ring with its middle); the corner dot resizes it.</summary>
sealed class AreaView : ItemView
{
    readonly Paint fill = new() { AntiAlias = true }, edge = new() { AntiAlias = true }, text = new() { AntiAlias = true }, knob = new() { AntiAlias = true };

    public AreaView(Context c, Bubble b) : base(c, b)
    {
        edge.SetStyle(Paint.Style.Stroke);
        edge.StrokeWidth = Ui.Dp(c, 2);
        edge.SetPathEffect(new DashPathEffect(new float[] { Ui.Dp(c, 8), Ui.Dp(c, 6) }, 0));
        text.Color = Color.White;
        text.TextAlign = Paint.Align.Center;
        text.FakeBoldText = true;
        text.TextSize = Ui.Dp(c, 11);
        knob.Color = Ui.Gold;
    }

    float KnobR => Ui.Dp(Context!, 11);

    (float x, float y) KnobAt() => Thin ? (Width - KnobR * 0.6f, Height / 2f) : Bubble.IsBox
        ? (Width - KnobR - 2, Height - KnobR - 2)
        : (Width / 2f + Bubble.W * 0.7071f, Height / 2f + Bubble.W * 0.7071f);

    // the boss HP bar box is thin: its resize dot sits just below its right end
    bool Thin => Bubble.Kind == BubbleKind.BossBar;

    /// <summary>True when a touch at (x, y) in the view is on the resize dot.</summary>
    public bool OnHandle(float x, float y)
    {
        var (kx, ky) = KnobAt();
        return Math.Abs(x - kx) < KnobR * 2 && Math.Abs(y - ky) < KnobR * 2;
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var col = Color.ParseColor(Bubble.Color(Bubble.Kind));
        fill.Color = Color.Argb(40, col.R, col.G, col.B);
        edge.Color = Selected ? Ui.Gold : Color.Argb(230, col.R, col.G, col.B);
        float cx = Width / 2f, cy = Height / 2f;
        if (Bubble.IsBox)
        {
            float r = Thin ? 2 : Ui.Dp(Context!, 12), i = edge.StrokeWidth;
            canvas.DrawRoundRect(i, i, Width - i, Height - i, r, r, fill);
            canvas.DrawRoundRect(i, i, Width - i, Height - i, r, r, edge);
            if (!Thin) canvas.DrawText("CAMERA AREA", cx, cy, text);
        }
        else
        {
            canvas.DrawCircle(cx, cy, Bubble.W, fill);
            canvas.DrawCircle(cx, cy, Bubble.W, edge);
            canvas.DrawCircle(cx, cy, Ui.Dp(Context!, 5), knob);
            canvas.DrawText("MOVE PAD", cx, cy - Ui.Dp(Context!, 10), text);
        }
        var (kx, ky) = KnobAt();
        canvas.DrawCircle(kx, ky, KnobR * 0.6f, knob);
    }
}
