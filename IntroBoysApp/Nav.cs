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
    /// Learns which way the stick moves the character WITHOUT wandering off: a short step up and the same step back (and, the first
    /// time on this phone, a step right and back, to know which way round the stick turns). The character ends where it started.
    /// </summary>
    public async Task<bool> CalibrateAsync(CancellationToken ct)
    {
        var fwd = await StepAsync(90, ct);
        if (fwd == null) { var back = await StepAsync(270, ct); if (back != null) fwd = Norm(back.Value + 180); }
        if (fwd == null) { AppLog.Add("walk: couldn't measure the stick (blocked, or the position can't be read)"); return false; }
        if (Prefs.GetInt("nav_hand", 0) == 0)
        {
            var right = await StepAsync(0, ct);
            if (right != null)
            {
                // map angle = off + hand * stick angle: up (90) and right (0) are 90 apart one way or the other
                hand = Math.Sign(Norm(fwd.Value - right.Value)) >= 0 ? 1 : -1;
                Prefs.Edit()!.PutInt("nav_hand", hand)!.Apply();
            }
        }
        off = Norm(fwd.Value - hand * 90);
        known = true;
        AppLog.Add($"walk: the camera looks {(int)Forward}° on the map");
        return true;
    }

    /// <summary>A short push of the stick and the same push back: the map direction the push moved the character (null: it didn't move).</summary>
    async Task<double?> StepAsync(double angle, CancellationToken ct)
    {
        var p0 = await ReadAsync(ct);
        await stick.PushAsync(angle, 320);
        await stick.ReleaseAsync();
        await Task.Delay(120, ct);
        var p1 = await ReadAsync(ct);
        await stick.PushAsync(angle + 180, 320);
        await stick.ReleaseAsync();
        if (!p0.Ok || !p1.Ok || p1.Dist(p0.X, p0.Y) < 15) return null;
        return MapAngle(p1.X - p0.X, p1.Y - p0.Y);
    }

    /// <summary>
    /// After the teleport: turns the camera (it never walks for this) until it looks toward the map direction <paramref name="toward"/>,
    /// the way the member finds the door by turning the view. How far a camera drag turns is measured once and remembered.
    /// </summary>
    public async Task AlignAsync(double toward, CancellationToken ct)
    {
        var cam = layout.Find(BubbleKind.Camera);
        if (cam == null) { AppLog.Add("camera: no camera area placed, can't turn the view"); return; }
        var (w, h) = svc.ScreenSize();
        var (cx, cy) = layout.At(cam, w, h);
        var (aw, _) = layout.SizeAt(cam, w, h);
        if (!known && !await CalibrateAsync(ct)) return;
        double perPx = Prefs.GetFloat("cam_deg_per_px", 0);
        for (int i = 0; i < 6; i++)
        {
            double err = Norm(toward - Forward);
            if (Math.Abs(err) <= 8) { AppLog.Add($"camera: facing the way ({(int)Forward}°)"); return; }
            if (Math.Abs(perPx) < 0.01)
            {
                // learn it: a fixed drag, then measure again
                float d = Math.Min(aw * 0.5f, 360);
                double before = Forward;
                await svc.SwipeAsync(cx - d / 2, cy, cx + d / 2, cy, 450);
                await Task.Delay(300, ct);
                if (!await CalibrateAsync(ct)) return;
                double turned = Norm(Forward - before);
                if (Math.Abs(turned) < 3) { AppLog.Add("camera: the drag didn't turn the view (put the camera area on open ground)"); return; }
                perPx = turned / d;
                Prefs.Edit()!.PutFloat("cam_deg_per_px", (float)perPx)!.Apply();
                AppLog.Add($"camera: a 100 px drag turns {perPx * 100:0}°");
                continue;
            }
            float px = (float)Math.Clamp(err / perPx, -aw * 0.95, aw * 0.95);
            AppLog.Add($"camera: turning {(int)err}°");
            await svc.SwipeAsync(cx - px / 2, cy, cx + px / 2, cy, 500);
            await Task.Delay(300, ct);
            if (!await CalibrateAsync(ct)) return;
        }
    }

    /// <summary>
    /// Through the dungeon's gate: pushes straight toward (x, y) and, while the gate is still shut (no progress), keeps facing it and
    /// waits instead of walking round. Done once the character is <paramref name="past"/> map units on its way.
    /// </summary>
    public async Task<bool> GateAsync(int x, int y, double past, CancellationToken ct, double maxSecs = 70)
    {
        var p0 = await ReadAsync(ct);
        if (!p0.Ok) return false;
        double d0 = p0.Dist(x, y);
        var start = DateTime.UtcNow;
        var lastMove = DateTime.UtcNow;
        bool waiting = false;
        try
        {
            while ((DateTime.UtcNow - start).TotalSeconds < maxSecs)
            {
                ct.ThrowIfCancellationRequested();
                var p = last;
                double want = MapAngle(x - p.X, y - p.Y);
                var reading = Screen.PositionAsync();
                await stick.PushAsync(Norm(hand * (want - off)), Seg);
                var np = await reading;
                if (!np.Ok) continue;
                if (np.Dist(p.X, p.Y) > 25) { lastMove = DateTime.UtcNow; if (waiting) { AppLog.Add("gate: open, going through"); waiting = false; } }
                last = np;
                if (d0 - np.Dist(x, y) >= past) { AppLog.Add($"gate: through ({np})"); return true; }
                if ((DateTime.UtcNow - lastMove).TotalSeconds > 1.5 && !waiting)
                {
                    waiting = true;
                    AppLog.Add("gate: shut, waiting for it to open");
                }
                if (waiting) { await stick.ReleaseAsync(); await Task.Delay(1000, ct); }
            }
            AppLog.Add("gate: didn't get through in time");
            return false;
        }
        finally { await stick.ReleaseAsync(); }
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
        int detours = 0, misses = 0, flips = 0;
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
                    // turned well away from "up" and the character went the mirrored way: the stick turns the other way round
                    if (Math.Abs(Norm(s - 90)) > 30)
                    {
                        double now = Norm(off + hand * s);
                        double flippedOff = Norm(Forward - (-hand) * 90);
                        double mirrored = Norm(flippedOff + (-hand) * s);
                        if (Math.Abs(Norm(seen - now)) > 90 && Math.Abs(Norm(seen - mirrored)) < 45) flips++; else flips = 0;
                        if (flips >= 2)
                        {
                            off = flippedOff; hand = -hand; flips = 0;
                            Prefs.Edit()!.PutInt("nav_hand", hand)!.Apply();
                            AppLog.Add("walk: the stick turns the other way round, corrected");
                        }
                    }
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
}
