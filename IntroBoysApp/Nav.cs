using Android.AccessibilityServices;
using Android.Content;

namespace IntroBoys.App;

/// <summary>
/// The move pad, held like a thumb: the stick is pushed one way and kept down, then turned to a new way without letting go, so the
/// character runs without stopping. Built on Android's continued gestures.
/// </summary>
sealed class Stick
{
    readonly TapService svc;
    readonly float cx, cy, reach;
    GestureDescription.StrokeDescription? held;
    float atX, atY;

    public Stick(TapService svc, BubbleLayout layout)
    {
        this.svc = svc;
        var pad = layout.Find(BubbleKind.MovePad)!;
        var (w, h) = svc.ScreenSize();
        (cx, cy) = layout.At(pad, w, h);
        reach = layout.SizeAt(pad, w, h).w * 0.85f;
        if (reach < 20) reach = 120;
    }

    /// <summary>Pushes the stick toward <paramref name="angle"/> (degrees: 0 = right, 90 = up on the screen) for <paramref name="ms"/>.</summary>
    public async Task<bool> PushAsync(double angle, long ms)
    {
        double a = angle * Math.PI / 180;
        float tx = cx + (float)(reach * Math.Cos(a)), ty = cy - (float)(reach * Math.Sin(a));
        var prev = held;
        float fx = prev == null ? cx : atX, fy = prev == null ? cy : atY;
        GestureDescription.StrokeDescription? stroke = null;
        bool ok = await svc.DispatchAsync(() =>
        {
            var path = new Android.Graphics.Path();
            path.MoveTo(fx, fy);
            path.LineTo(tx, ty);
            stroke = prev == null
                ? new GestureDescription.StrokeDescription(path, 0, Math.Max(1, ms), true)
                : prev.ContinueStroke(path, 0, Math.Max(1, ms), true);
            return new GestureDescription.Builder().AddStroke(stroke!)!.Build()!;
        }, ms);
        if (ok) { held = stroke; atX = tx; atY = ty; }
        else held = null;           // cancelled (e.g. the member touched the screen): the next push starts a new touch
        return ok;
    }

    /// <summary>Lets go of the stick.</summary>
    public async Task ReleaseAsync()
    {
        var prev = held;
        held = null;
        if (prev == null) return;
        float x = atX, y = atY;
        await svc.DispatchAsync(() =>
        {
            var path = new Android.Graphics.Path();
            path.MoveTo(x, y);
            path.LineTo(cx, cy);
            return new GestureDescription.Builder().AddStroke(prev.ContinueStroke(path, 0, 40, false)!)!.Build()!;
        }, 40);
    }
}

/// <summary>
/// Walking by map coordinate, the same way as the PC Assistant: read the position, work out which way the stick moves the character
/// on the map (it depends on the camera, so it is measured, then corrected all the time while walking), steer straight at the target,
/// and walk round anything in the way when the position stops getting closer.
/// </summary>
sealed class Nav
{
    readonly TapService svc;
    readonly BubbleLayout layout;
    readonly Stick stick;
    // the map direction the stick moves the character: map angle = off + hand * stick angle (degrees)
    double off;
    int hand = 1;
    bool known;
    Position last;

    const long Seg = 320;                  // ms the stick is held toward one direction before it is checked again
    static Android.Content.ISharedPreferences Prefs => TapService.Prefs;

    public Nav(TapService svc, BubbleLayout layout)
    {
        this.svc = svc;
        this.layout = layout;
        stick = new Stick(svc, layout);
        hand = Prefs.GetInt("nav_hand", 0) is int hd && hd != 0 ? hd : 1;
    }

    public Position Last => last;

    static double Norm(double a) { a %= 360; if (a > 180) a -= 360; if (a < -180) a += 360; return a; }
    static double MapAngle(int dx, int dy) => Math.Atan2(dy, dx) * 180 / Math.PI;

    /// <summary>Reads the position (a few tries); a reading far from the last good one is taken as a misread.</summary>
    public async Task<Position> ReadAsync(CancellationToken ct, int tries = 3)
    {
        for (int i = 0; i < tries; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = await Screen.PositionAsync();
            if (p.Ok && (!last.Ok || p.Dist(last.X, last.Y) < 3000 || i == tries - 1)) { last = p; return p; }
            await Task.Delay(150, ct);
        }
        return default;
    }

    /// <summary>
    /// Learns which way the stick moves the character: a short push up, then right, reading the position before and after each.
    /// </summary>
    public async Task<bool> CalibrateAsync(CancellationToken ct)
    {
        foreach (var (first, second) in new[] { (90.0, 0.0), (270.0, 180.0), (0.0, 90.0) })
        {
            var p0 = await ReadAsync(ct);
            await stick.PushAsync(first, 650);
            var p1 = await ReadAsync(ct);
            await stick.PushAsync(second, 650);
            var p2 = await ReadAsync(ct);
            await stick.ReleaseAsync();
            if (!p0.Ok || !p1.Ok || !p2.Ok) { AppLog.Add("walk: can't read the position (is the mini-map open?)"); return false; }
            double d1 = p1.Dist(p0.X, p0.Y), d2 = p2.Dist(p1.X, p1.Y);
            if (d1 < 25 || d2 < 25) { AppLog.Add($"walk: blocked while measuring ({(int)d1}/{(int)d2}), trying another way"); continue; }
            double m1 = MapAngle(p1.X - p0.X, p1.Y - p0.Y), m2 = MapAngle(p2.X - p1.X, p2.Y - p1.Y);
            // turning the stick by (second - first) turned the walk by (m2 - m1): the same way round (+1) or mirrored (-1)
            double turn = Norm(m2 - m1), want = Norm(second - first);
            hand = Math.Sign(turn) == Math.Sign(want) ? 1 : -1;
            off = Norm(m1 - hand * first);
            known = true;
            Prefs.Edit()!.PutInt("nav_hand", hand)!.Apply();
            AppLog.Add($"walk: stick up goes {(int)Norm(off + hand * 90)}° on the map ({(hand > 0 ? "same" : "mirrored")})");
            return true;
        }
        return false;
    }

    /// <summary>The map direction the character runs when the stick is pushed up (the way the camera looks).</summary>
    public double Forward => Norm(off + hand * 90);
    public bool Known => known;

    /// <summary>
    /// Walks to (x, y) until within <paramref name="tol"/> map units. Returns true when there, false when it gave up (stuck, can't read
    /// the position, or out of time).
    /// </summary>
    public async Task<bool> WalkToAsync(int x, int y, double tol, CancellationToken ct, double maxSecs = 90)
    {
        if (!known && !await CalibrateAsync(ct)) return false;
        var start = DateTime.UtcNow;
        var p = await ReadAsync(ct);
        if (!p.Ok) return false;
        if (p.Dist(x, y) <= tol) return true;
        AppLog.Add($"walk: {p} → {x} , {y} ({(int)p.Dist(x, y)} away)");
        var history = new List<(DateTime t, double d)>();
        int detours = 0, misses = 0;
        double detour = 0;
        DateTime detourUntil = DateTime.MinValue;
        Position prev = p;
        double prevStick = double.NaN;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if ((DateTime.UtcNow - start).TotalSeconds > maxSecs) { AppLog.Add("walk: out of time"); return false; }
                double dist = p.Dist(x, y);
                if (dist <= tol) { AppLog.Add($"walk: there ({p})"); return true; }

                // steer straight at the target (or the detour angle for a moment)
                double want = MapAngle(x - p.X, y - p.Y);
                if (DateTime.UtcNow < detourUntil) want = Norm(want + detour);
                double s = Norm(hand * (want - off));
                // read the position while the stick is being pushed
                var reading = Screen.PositionAsync();
                bool pushed = await stick.PushAsync(s, Seg);
                var np = await reading;
                if (!pushed) { await Task.Delay(150, ct); }
                if (!np.Ok || np.Dist(prev.X, prev.Y) > 3000) { if (++misses > 12) { AppLog.Add("walk: the position can't be read"); return false; } continue; }
                misses = 0;
                last = np;

                // correct the stick direction from how the character really moved (the camera may have turned)
                double moved = np.Dist(prev.X, prev.Y);
                if (moved > 60 && !double.IsNaN(prevStick) && Math.Abs(Norm(prevStick - s)) < 12)
                {
                    double seen = MapAngle(np.X - prev.X, np.Y - prev.Y);
                    double newOff = Norm(seen - hand * s);
                    off = Norm(off + Norm(newOff - off) * 0.5);
                }
                prevStick = s;
                prev = np;
                p = np;

                // stuck: not getting closer for about 2 seconds -> walk round it (left, right, further each time)
                history.Add((DateTime.UtcNow, p.Dist(x, y)));
                history.RemoveAll(h => (DateTime.UtcNow - h.t).TotalSeconds > 2.2);
                if (DateTime.UtcNow > detourUntil && history.Count >= 5 && (DateTime.UtcNow - history[0].t).TotalSeconds > 1.8 && history[0].d - history[^1].d < 70)
                {
                    if (++detours > 8) { AppLog.Add("walk: stuck, giving up this walk"); return false; }
                    detour = (detours % 2 == 1 ? 1 : -1) * Math.Min(110, 50 + 15 * detours);
                    detourUntil = DateTime.UtcNow.AddMilliseconds(900 + 150 * detours);
                    AppLog.Add($"walk: stuck at {p}, going round ({(int)detour}°)");
                    history.Clear();
                }
            }
        }
        finally { await stick.ReleaseAsync(); }
    }

    /// <summary>Keeps the character on the spot while waiting: walks back when pushed off by more than <paramref name="tol"/>.</summary>
    public async Task HoldAsync(int x, int y, double tol, CancellationToken ct)
    {
        var p = await ReadAsync(ct, 1);
        if (p.Ok && p.Dist(x, y) > tol) await WalkToAsync(x, y, tol / 2, ct, 20);
    }

    // ------------------------------------------------------------------ the camera
    /// <summary>Zooms all the way out (pinches on the camera area a few times).</summary>
    public async Task ZoomOutAsync(CancellationToken ct)
    {
        var cam = layout.Find(BubbleKind.Camera);
        if (cam == null) return;
        var (w, h) = svc.ScreenSize();
        var (cx, cy) = layout.At(cam, w, h);
        var (aw, ah) = layout.SizeAt(cam, w, h);
        float big = Math.Min(aw, ah) * 0.9f, small = Math.Min(aw, ah) * 0.12f;
        for (int i = 0; i < 4; i++) { ct.ThrowIfCancellationRequested(); await svc.PinchAsync(cx, cy, big, small, 380); await Task.Delay(150, ct); }
        AppLog.Add("camera: zoomed all the way out");
    }

    /// <summary>
    /// Turns the camera so the character runs straight ahead toward the map direction <paramref name="toward"/> (like facing the door
    /// after the teleport). How far a camera drag turns is measured the first time and remembered.
    /// </summary>
    public async Task FaceAsync(double toward, CancellationToken ct)
    {
        var cam = layout.Find(BubbleKind.Camera);
        if (cam == null || !known) return;
        var (w, h) = svc.ScreenSize();
        var (cx, cy) = layout.At(cam, w, h);
        var (aw, _) = layout.SizeAt(cam, w, h);
        double perPx = Prefs.GetFloat("cam_deg_per_px", 0);
        if (Math.Abs(perPx) < 0.01)
        {
            // learn it: drag a fixed distance, measure how far the walking direction turned
            float d = Math.Min(aw * 0.4f, 300);
            double before = off;
            await svc.SwipeAsync(cx - d / 2, cy, cx + d / 2, cy, 450);
            await Task.Delay(250, ct);
            if (!await CalibrateAsync(ct)) return;
            double turned = Norm(off - before);
            if (Math.Abs(turned) < 3) { AppLog.Add("camera: the drag didn't turn the view (is the camera area on open ground?)"); return; }
            perPx = turned / d;
            Prefs.Edit()!.PutFloat("cam_deg_per_px", (float)perPx)!.Apply();
            AppLog.Add($"camera: a drag of 1 px turns {perPx:0.00}°");
        }
        for (int i = 0; i < 2; i++)
        {
            double err = Norm(toward - Forward);
            if (Math.Abs(err) < 12) break;
            float px = (float)Math.Clamp(err / perPx, -aw * 0.95, aw * 0.95);
            await svc.SwipeAsync(cx - px / 2, cy, cx + px / 2, cy, 500);
            await Task.Delay(250, ct);
            off = Norm(off + px * perPx);           // what the turn should have done; the walk corrects the rest
            AppLog.Add($"camera: turned {(int)err}° to face the way");
        }
    }
}
