using Android.AccessibilityServices;
using Android.Content;

namespace IntroBoys.App;

/// <summary>
/// The move pad, held and dragged like a thumb (the way a WASD key is held on PC): the finger goes down in the middle, drags out to
/// the edge and STAYS DOWN; to change direction it drags round the edge to the new way, never lifting. A small pump keeps the touch
/// going in short continued pieces toward whatever way the walk wants right now, so reading the screen never makes it stop.
/// </summary>
sealed class Stick
{
    readonly TapService svc;
    readonly float cx, cy, reach;
    volatile float wantX, wantY;
    volatile bool want;
    float atX, atY;
    GestureDescription.StrokeDescription? held;
    CancellationTokenSource? pumping;
    Task? pump;
    const long Piece = 220;          // ms per continued piece of the touch
    bool wiggle;

    public Stick(TapService svc, BubbleLayout layout)
    {
        this.svc = svc;
        var pad = layout.Find(BubbleKind.MovePad)!;
        var (w, h) = svc.ScreenSize();
        (cx, cy) = layout.At(pad, w, h);
        reach = layout.SizeAt(pad, w, h).w * 0.85f;
        if (reach < 20) reach = 120;
    }

    (float x, float y) Edge(double angle)
    {
        double a = angle * Math.PI / 180;
        return (cx + (float)(reach * Math.Cos(a)), cy - (float)(reach * Math.Sin(a)));
    }

    /// <summary>Keeps the stick pushed toward <paramref name="angle"/> (degrees: 0 = right, 90 = up on the screen). Returns at once.</summary>
    public void Hold(double angle)
    {
        (wantX, wantY) = Edge(angle);
        want = true;
        if (pump == null || pump.IsCompleted)
        {
            pumping = new CancellationTokenSource();
            var ct = pumping.Token;
            pump = Task.Run(() => Pump(ct));
        }
    }

    async Task Pump(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            float tx = wantX, ty = wantY;
            var prev = held;
            float fx = prev == null ? cx : atX, fy = prev == null ? cy : atY;
            // the same spot again: a 1-pixel wiggle (a piece must move a little)
            if (Math.Abs(tx - fx) < 1 && Math.Abs(ty - fy) < 1) { wiggle = !wiggle; tx = fx + (wiggle ? 1 : -1); }
            GestureDescription.StrokeDescription? stroke = null;
            bool ok = await svc.DispatchAsync(() =>
            {
                var path = new Android.Graphics.Path();
                path.MoveTo(fx, fy);
                path.LineTo(tx, ty);
                stroke = prev == null
                    ? new GestureDescription.StrokeDescription(path, 0, Piece, true)
                    : prev.ContinueStroke(path, 0, Piece, true);
                return new GestureDescription.Builder().AddStroke(stroke!)!.Build()!;
            }, Piece);
            if (ok) { held = stroke; atX = tx; atY = ty; }
            else { held = null; try { await Task.Delay(40, ct); } catch { } }      // cut off (a tap elsewhere): a new touch next
        }
    }

    async Task StopPump()
    {
        pumping?.Cancel();
        if (pump != null) { try { await pump; } catch { } }
        pump = null;
    }

    /// <summary>A push of <paramref name="ms"/> milliseconds and let go (short steps), waited for.</summary>
    public async Task<bool> PushAsync(double angle, long ms)
    {
        await ReleaseAsync();
        var (tx, ty) = Edge(angle);
        return await svc.DispatchAsync(() =>
        {
            var path = new Android.Graphics.Path();
            path.MoveTo(cx, cy);
            path.LineTo(tx, ty);
            for (int i = 0; i < 300; i++) path.LineTo(tx + (i % 2 == 0 ? 2 : 0), ty);       // reach the edge fast, then stay
            return new GestureDescription.Builder().AddStroke(new GestureDescription.StrokeDescription(path, 0, Math.Max(1, ms)))!.Build()!;
        }, ms);
    }

    /// <summary>Lets go of the stick: drags back to the middle and lifts.</summary>
    public async Task ReleaseAsync()
    {
        await StopPump();
        want = false;
        var prev = held;
        held = null;
        if (prev == null) return;
        float x = atX, y = atY;
        await svc.DispatchAsync(() =>
        {
            var path = new Android.Graphics.Path();
            path.MoveTo(x, y);
            path.LineTo(cx, cy);
            return new GestureDescription.Builder().AddStroke(prev.ContinueStroke(path, 0, 60, false)!)!.Build()!;
        }, 60);
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

    static Android.Content.ISharedPreferences Prefs => TapService.Prefs;

    public Nav(TapService svc, BubbleLayout layout)
    {
        this.svc = svc;
        this.layout = layout;
        stick = new Stick(svc, layout);
        hand = Prefs.GetInt("nav_hand", 0) is int hd && hd != 0 ? hd : 1;
    }

    public Position Last => last;

    // 1.4.0: where a detour last got round a wall, and which way (kept while the app runs)
    static Position goodAt;
    static double goodDetour;

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
        return new Position(false, 0, 0, Screen.LastMap, "");
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

    /// <summary>
    /// A short push of the stick and the same push back: the map direction the push moved the character (null: it didn't move).
    /// 1.4.3: the position is read once it has stopped changing (a slow phone - the 16 FPS cloud phone - showed it mid-step, so the
    /// direction came out wrong and the camera went round in circles), and a push that moved the character too little to tell a
    /// direction (under 40) is made longer - up to 1.2 s - and that length is remembered for this screen. A phone whose first push
    /// already moves far enough keeps the 0.32 s push.
    /// </summary>
    async Task<double?> StepAsync(double angle, CancellationToken ct)
    {
        var (sw, sh) = svc.ScreenSize();
        string key = $"nav_step_ms_{Math.Max(sw, sh)}x{Math.Min(sw, sh)}";
        long ms = Math.Clamp(Prefs.GetInt(key, 320), 320, 1200);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var p0 = await SettledAsync(default, ct);
            await stick.PushAsync(angle, ms);
            var p1 = await SettledAsync(p0, ct);
            await stick.PushAsync(angle + 180, ms);
            if (!p0.Ok || !p1.Ok) return null;
            double d = p1.Dist(p0.X, p0.Y);
            if (d >= 40 || (d >= 15 && ms >= 1200))
            {
                if (ms != Prefs.GetInt(key, 320)) { Prefs.Edit()!.PutInt(key, (int)ms)!.Apply(); AppLog.Add($"walk: this phone needs {ms} ms pushes to measure the direction"); }
                await SettledAsync(p1, ct, 900);
                return MapAngle(p1.X - p0.X, p1.Y - p0.Y);
            }
            if (ms >= 1200) break;
            ms = Math.Min(1200, ms * 2);
            await SettledAsync(p1, ct, 900);
        }
        return null;
    }

    /// <summary>
    /// 1.4.3: the position once it stops changing: fresh pictures until two in a row agree (within 6), or <paramref name="maxMs"/>.
    /// With <paramref name="from"/> given, a first reading equal to it is only taken after a moment (the screen may not have caught up yet).
    /// </summary>
    async Task<Position> SettledAsync(Position from, CancellationToken ct, int maxMs = 1600)
    {
        var until = DateTime.UtcNow.AddMilliseconds(maxMs);
        Position prev = default;
        bool havePrev = false;
        var first = DateTime.UtcNow;
        while (true)
        {
            long seenAt = Screen.LastFrameAt;
            var p = await Screen.PositionAsync();
            if (Screen.LastFrameAt == seenAt && DateTime.UtcNow < until) { await Task.Delay(60, ct); continue; }
            if (p.Ok)
            {
                last = p;
                bool sameAsStart = from.Ok && p.Dist(from.X, from.Y) < 6 && (DateTime.UtcNow - first).TotalMilliseconds < 500;
                if (havePrev && prev.Ok && p.Dist(prev.X, prev.Y) < 6 && !sameAsStart) return p;
                prev = p; havePrev = true;
            }
            if (DateTime.UtcNow >= until) return prev.Ok ? prev : p;
            await Task.Delay(40, ct);
        }
    }

    /// <summary>
    /// After the teleport: turns the camera (it never walks for this) until it looks toward the map direction <paramref name="toward"/>,
    /// the way the member finds the door by turning the view. How far a camera drag turns is measured once and remembered.
    /// </summary>
    public async Task AlignAsync(double toward, CancellationToken ct)
    {
        var cam = layout.Find(BubbleKind.Camera);
        if (cam == null) { AppLog.Add("camera: no camera area placed, can't turn the view"); return; }
        if (!known && !await CalibrateAsync(ct)) return;
        // 1.4.2: how far a drag turns the view is kept per screen size (a 720p cloud phone turns very differently from a 1080p phone);
        // a phone that already learned it under the old name keeps using that
        var (sw, sh) = svc.ScreenSize();
        string key = $"cam_deg_per_px_{Math.Max(sw, sh)}x{Math.Min(sw, sh)}";
        double perPx = Prefs.GetFloat(key, Prefs.GetFloat("cam_deg_per_px", 0));
        if (Math.Abs(perPx) < 0.01)
        {
            // the first time on this phone: one smooth drag, then measure how far it turned the view
            double before = Forward;
            float d = 300;
            await TurnAsync(d, ct);
            if (!await CalibrateAsync(ct)) return;
            double turned = Norm(Forward - before);
            if (Math.Abs(turned) < 3) { AppLog.Add("camera: the drag didn't turn the view (put the camera area on open ground)"); return; }
            perPx = turned / d;
            Prefs.Edit()!.PutFloat(key, (float)perPx)!.Apply();
            AppLog.Add($"camera: a 100 px drag turns {perPx * 100:0}°");
        }
        // 1.4.2: turn, look, and learn from what the turn really did. On phones where it lands (within 40°) nothing changes; where it
        // doesn't (the cloud phone: asked for 138°, got 29°; asked for -50°, got 200°), the drag size is corrected from each turn, turns
        // are kept to 90° at a time, and after 6 tries or 40 s it goes on anyway - the walk steers by the measured direction, not the view.
        var t0 = DateTime.UtcNow;
        bool careful = false;
        // 1.4.4: the view after the teleport is the same every time, so the drag that turned it onto the gate last time is used
        // again straight away (one turn and one look, instead of learning it every run - on the cloud phone each look takes ~15 s)
        string gkey = $"gate_turn_{Math.Max(sw, sh)}x{Math.Min(sw, sh)}";
        double startFwd = Forward, totalPx = 0;
        var saved = (Prefs.GetString(gkey, "") ?? "").Split(',');
        if (saved.Length == 3 && double.TryParse(saved[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sFrom)
            && double.TryParse(saved[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sTo)
            && double.TryParse(saved[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sPx)
            && Math.Abs(Norm(startFwd - sFrom)) <= 30 && Math.Abs(Norm(toward - sTo)) <= 15 && Math.Abs(Norm(toward - startFwd)) > 12)
        {
            AppLog.Add("camera: the turn that faced the gate last time");
            Runner.ShowPhase("camera: turning to the gate");
            await TurnAsync((float)sPx, ct);
            totalPx += sPx;
            off = Norm(off + sPx * perPx);
            await Task.Delay(250, ct);
            if (!await CalibrateAsync(ct)) return;
        }
        for (int i = 0; i < 6 && (DateTime.UtcNow - t0).TotalSeconds < 70; i++)
        {
            double err = Norm(toward - Forward);
            if (Math.Abs(err) <= 12)
            {
                AppLog.Add($"camera: looking at the gate ({(int)Forward}°)");
                if (Math.Abs(totalPx) > 1) Prefs.Edit()!.PutString(gkey, string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#},{1:0.#},{2:0.#}", startFwd, toward, totalPx))!.Apply();
                return;
            }
            Runner.ShowPhase(careful ? "camera: learning how this phone turns (first time only)" : "camera: turning to the gate");
            double ask = careful ? Math.Clamp(err, -90, 90) : err;
            AppLog.Add($"camera: turning {(int)ask}° toward the gate");
            double before = Forward;
            float px = (float)(ask / perPx);
            await TurnAsync(px, ct);
            totalPx += px;
            off = Norm(off + px * perPx);                       // where the turn should have put it
            await Task.Delay(250, ct);
            if (!await CalibrateAsync(ct)) return;             // and where it really is (a step and back)
            double got = Norm(Forward - before);
            if (Math.Abs(got - ask) <= 40) continue;           // close to what was asked: the drag size is right
            careful = true;
            double was = perPx;
            if (Math.Abs(got) >= 5)
            {
                double seen = got / px;
                // 1.4.4: a clear turn (15° or more) is taken as it is - blending made the cloud phone need many slow tries
                perPx = Math.Sign(seen) != Math.Sign(perPx) || Math.Abs(got) >= 15 ? seen : perPx * 0.4 + seen * 0.6;
            }
            else perPx *= 0.6;                                  // hardly turned: bigger drags
            perPx = Math.Sign(perPx) * Math.Clamp(Math.Abs(perPx), 0.02, 3.0);
            Prefs.Edit()!.PutFloat(key, (float)perPx)!.Apply();
            AppLog.Add($"camera: asked {(int)ask}°, turned {(int)got}° - a 100 px drag turns about {perPx * 100:0}° on this screen (was {was * 100:0}°)");
        }
        AppLog.Add($"camera: close enough ({(int)Forward}°), going on");
    }

    /// <summary>
    /// Turns the view like a thumb would: one smooth, slightly curved drag across the camera area (long turns in two or three drags),
    /// slower for longer drags.
    /// </summary>
    async Task TurnAsync(float px, CancellationToken ct)
    {
        var cam = layout.Find(BubbleKind.Camera);
        if (cam == null) return;
        var (w, h) = svc.ScreenSize();
        var (cx, cy) = layout.At(cam, w, h);
        var (aw, ah) = layout.SizeAt(cam, w, h);
        float dir = Math.Sign(px), left = Math.Abs(px), most = Math.Max(120, aw * 0.8f);
        // 1.4.4: on a small screen (the 720p cloud phone) the drags were short, and the start of every drag (the game's touch slop and its
        // lag) is lost - many short drags turned a fifth of what was asked. Longer, slower drags there: 40 % of the screen width each.
        bool small = Math.Min(w, h) <= 800;
        if (small) most = Math.Max(most, w * 0.40f);
        var rnd = new Random();
        while (left > 2)
        {
            ct.ThrowIfCancellationRequested();
            float chunk = Math.Min(left, most);
            left -= chunk;
            float x1 = cx - dir * chunk / 2, x2 = cx + dir * chunk / 2;
            if (small)
            {
                // keep the whole drag on the screen (it may leave the camera area; it starts on open ground)
                float shift = Math.Max(0, 12 - Math.Min(x1, x2)) - Math.Max(0, Math.Max(x1, x2) - (w - 12));
                x1 += shift; x2 += shift;
            }
            float y = cy + (float)(rnd.NextDouble() - 0.5) * ah * 0.2f;
            float bend = chunk * (0.05f + (float)rnd.NextDouble() * 0.05f);
            long ms = (long)Math.Clamp(chunk * 1.8, small ? 700 : 450, small ? 1500 : 1100);
            await svc.DispatchAsync(() =>
            {
                var path = new Android.Graphics.Path();
                path.MoveTo(x1, y);
                path.QuadTo((x1 + x2) / 2, y - bend, x2, y + bend * 0.3f);
                return new GestureDescription.Builder().AddStroke(new GestureDescription.StrokeDescription(path, 0, ms))!.Build()!;
            }, ms);
            await Task.Delay((small ? 260 : 140) + rnd.Next(120), ct);
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
                stick.Hold(Norm(hand * (want - off)));
                long seenAt = Screen.LastFrameAt;
                var np = await Screen.PositionAsync();
                if (Screen.LastFrameAt == seenAt) { await Task.Delay(60, ct); continue; }
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
    public async Task<bool> WalkToAsync(int x, int y, double tol, CancellationToken ct, double maxSecs = 90, bool release = true)
    {
        if (!known && !await CalibrateAsync(ct)) return false;
        var start = DateTime.UtcNow;
        var p = await ReadAsync(ct);
        if (!p.Ok) return false;
        if (p.Dist(x, y) <= tol) return true;
        AppLog.Add($"walk: {p} → {x} , {y} ({(int)p.Dist(x, y)} away)");
        var history = new List<(DateTime t, double d)>();
        int detours = 0, misses = 0, flips = 0, jumps = 0;
        bool rechecked = false, canMove = false;
        Position detourFrom = default;
        double detourDist = 0;
        var startPlace = Screen.PlaceOf(p);
        Position firstStuck = default;
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
                stick.Hold(s);
                long seenAt = Screen.LastFrameAt;
                var np = await Screen.PositionAsync();
                if (Screen.LastFrameAt == seenAt) { await Task.Delay(60, ct); continue; }      // the same picture: no news
                if (!np.Ok || np.Dist(prev.X, prev.Y) > 3000)
                {
                    // 1.3.7: a far jump that the map title agrees with is no misread: the character is somewhere else now (died, kicked)
                    var pl = Screen.PlaceOf(np);
                    if (np.Ok && (np.Title ?? "").Length > 0 && pl != Place.Unknown && pl != startPlace && ++jumps >= 2) { AppLog.Add($"walk: the map changed ({Screen.PlaceText} at {np})"); return false; }
                    if (++misses > 12) { AppLog.Add("walk: the position can't be read"); return false; }
                    continue;
                }
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

                // 1.4.0: a detour that has just ended and got the character clearly on its way: remember it for this place
                if (detourFrom.Ok && DateTime.UtcNow >= detourUntil)
                {
                    if (detourDist - p.Dist(x, y) > 150 || p.Dist(detourFrom.X, detourFrom.Y) > 250) { goodAt = detourFrom; goodDetour = detour; }
                    detourFrom = default;
                }

                // stuck: not getting closer for about 2 seconds -> walk round it (left, right, further each time)
                history.Add((DateTime.UtcNow, p.Dist(x, y)));
                history.RemoveAll(h => (DateTime.UtcNow - h.t).TotalSeconds > 2.6);
                if (DateTime.UtcNow > detourUntil && history.Count >= 4 && (DateTime.UtcNow - history[0].t).TotalSeconds > 2.0 && history[0].d - history[^1].d < 60)
                {
                    if (++detours > 10) { AppLog.Add("walk: stuck, giving up this walk"); return false; }
                    if (detours == 1) firstStuck = p;
                    // 1.4.0: a detour that got round this wall before (the same place) is tried first, straight away
                    if (detours == 1 && goodAt.Ok && p.Dist(goodAt.X, goodAt.Y) < 900)
                    {
                        detour = goodDetour;
                        detourUntil = DateTime.UtcNow.AddMilliseconds(1800);
                        detourFrom = p; detourDist = p.Dist(x, y);
                        AppLog.Add($"walk: stuck at {p}, going round the way that worked here before ({(int)detour}°)");
                        history.Clear();
                        continue;
                    }
                    // 1.3.7: stuck twice - the camera may have been turned (by hand, by accident): measure again which way the stick goes
                    // (a step and the same step back) before walking round, so the detours go the right way
                    if (detours == 2 && !rechecked)
                    {
                        rechecked = true;
                        await stick.ReleaseAsync();
                        AppLog.Add("walk: stuck - checking which way the camera looks (turned?)");
                        if (!await CalibrateAsync(ct))
                        {
                            AppLog.Add($"walk: not moving at all at {p} (dead, a window open, or boxed in)");
                            return false;
                        }
                        canMove = true;
                        history.Clear(); prevStick = double.NaN;
                        var rp = await ReadAsync(ct, 1);
                        if (rp.Ok) { p = rp; prev = rp; }
                        detourUntil = DateTime.MinValue;
                        continue;
                    }
                    // 1.4.0: only when the step test did NOT show it can move (it can: then it is a wall - go round it, wider and longer;
                    // this gave up 8 times in a row in front of the same wall on the way to boss 5)
                    if (!canMove && detours >= 3 && firstStuck.Ok && p.Dist(firstStuck.X, firstStuck.Y) < 40)
                    {
                        AppLog.Add($"walk: not moving at all at {p}");
                        return false;
                    }
                    // small turns first (a pillar or a corner), wider only if that didn't help
                    // 1.4.0: left / right in turn, 35° → 60° → 90° → 120°, each longer (slides along a long wall instead of bumping it)
                    int k = (detours - 1) / 2;
                    detour = (detours % 2 == 1 ? 1 : -1) * new[] { 35, 60, 90, 120, 135 }[Math.Min(4, k)];
                    detourUntil = DateTime.UtcNow.AddMilliseconds(1100 + 450 * k);
                    detourFrom = p; detourDist = p.Dist(x, y);
                    AppLog.Add($"walk: stuck at {p}, going round ({(int)detour}°)");
                    history.Clear();
                }
            }
        }
        finally { if (release) await stick.ReleaseAsync(); }
    }

    /// <summary>
    /// Follows a way through the map (the PC Assistant's recorded paths): from the nearest point ahead, point by point (one every
    /// ~300 map units) without letting go of the stick, then the last part to (x, y).
    /// </summary>
    public async Task<bool> FollowAsync(IReadOnlyList<(int x, int y)> path, int x, int y, double tol, CancellationToken ct)
    {
        var p = await ReadAsync(ct);
        if (!p.Ok || path.Count == 0) return await WalkToAsync(x, y, tol, ct);
        // the nearest point of the path, then every ~300 units after it
        int from = Enumerable.Range(0, path.Count).OrderBy(i => p.Dist(path[i].x, path[i].y)).First();
        var legs = new List<(int x, int y)>();
        var lastLeg = path[from];
        for (int i = from + 1; i < path.Count; i++)
        {
            var q = path[i];
            if (Math.Sqrt(Math.Pow(q.x - lastLeg.x, 2) + Math.Pow(q.y - lastLeg.y, 2)) >= 300) { legs.Add(q); lastLeg = q; }
        }
        AppLog.Add($"walk: following the recorded way ({legs.Count} points)");
        foreach (var q in legs)
            if (!await WalkToAsync(q.x, q.y, 160, ct, 40, release: false)) { await stick.ReleaseAsync(); return await WalkToAsync(x, y, tol, ct); }
        return await WalkToAsync(x, y, tol, ct);
    }

    /// <summary>A few steps backwards (away from what the camera looks at): room to buff.</summary>
    public async Task StepAwayAsync(CancellationToken ct)
    {
        await stick.PushAsync(270, 650);
        await Task.Delay(150, ct);
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
        // a few smooth pinches, like two fingers would
        for (int i = 0; i < 3; i++) { ct.ThrowIfCancellationRequested(); await svc.PinchAsync(cx, cy, big, small, 620); await Task.Delay(220, ct); }
        AppLog.Add("camera: zoomed all the way out");
    }
}
