namespace IntroBoys.App;

/// <summary>
/// Labyrinth and Den runs on the phone, on the same plan as the PC Assistant 4.10.6:
///   teleport (the teleport scroll bubble, then the dungeon in the list) → wait for the map → zoom all the way out, the member's camera
///   fix, face the way → walk to each boss's fixed spot by map coordinate → wait for the boss (target until its HP bar shows) → attack
///   until the bar goes away (dead) → auto loot → next boss. After the last boss: the return scroll, and again for the next run.
/// Kicked out to town in the middle: the run starts over from the teleport.
/// </summary>
static class RunEngine
{
    // the five Labyrinth bosses and the Den (LOD) boss always spawn at the same place (from the PC Assistant)
    static readonly (int x, int y)[] LabySpots = { (3616, 28733), (3796, 22424), (3624, 16208), (3602, 9365), (3318, 1924) };
    static readonly (int x, int y) DenSpot = (1203, 2352);
    const double SpotTol = 150;               // close enough to a boss spot (map units)
    const double SpawnTimeout = 90;            // longest wait for a boss to show up
    const double FightTimeout = 240;           // longest fight
    const double GoneSecs = 3.0, NearDeadGoneSecs = 1.5;
    const double DenRespawnWait = 6;           // the LOD boss comes back (about 30 %) a moment after the kill
    const double ReturnWait = 8, TeleportLoad = 45;
    const double GateCountdown = 30;            // the Labyrinth's gate opens 30 s after you come in
    static readonly (int x, int y) LabyEntrance = (3595, 34153);
    static DateTime arrivedAt;
    static bool countdownDone;            // the 30 s gate countdown is only for the first run of a session

    /// <summary>
    /// The PC Assistant's recorded ways to each boss (route 1..5 = Labyrinth bosses, 6 = the Den boss), as map coordinates, built into
    /// the app (Assets/routes.json). Empty when there are none: then the walk goes straight and round what is in the way.
    /// </summary>
    static List<(int x, int y)>[]? routes;
    static List<(int x, int y)> Route(int i)
    {
        if (routes == null)
        {
            routes = Enumerable.Range(0, 6).Select(_ => new List<(int x, int y)>()).ToArray();
            try
            {
                using var st = Android.App.Application.Context.Assets!.Open("routes.json");
                using var doc = System.Text.Json.JsonDocument.Parse(st);
                int n = 0;
                foreach (var r in doc.RootElement.GetProperty("routes").EnumerateArray())
                {
                    if (n >= 6) break;
                    foreach (var pt in r.EnumerateArray()) routes[n].Add((pt[0].GetInt32(), pt[1].GetInt32()));
                    n++;
                }
            }
            catch { /* no routes built in */ }
        }
        return i >= 0 && i < routes.Length ? routes[i] : new();
    }

    /// <summary>Why a Labyrinth / Den run can't start yet (null when everything is in place).</summary>
    public static string? Missing(BubbleLayout layout)
    {
        var need = new List<string>();
        if (layout.Find(BubbleKind.Teleport) == null) need.Add("a Teleport bubble (on the teleport scroll)");
        if (layout.Find(BubbleKind.MovePad) == null) need.Add("the Move pad");
        if (layout.Find(BubbleKind.BossBar) == null) need.Add("the Boss HP bar box");
        if (!layout.Bubbles.Any(b => b.Kind == BubbleKind.Attack && b.Mode != PressMode.Off)) need.Add("an Attack bubble");
        if (need.Count > 0) return "For runs, place " + string.Join(", ", need) + " (Bubbles).";
        return null;
    }

    public static async Task RunAsync(TapService svc, RunMode mode, BubbleLayout layout, int runs, CancellationToken ct)
    {
        var nav = new Nav(svc, layout);
        bool den = mode == RunMode.Den;
        countdownDone = false;
        if (!Capture.CanSee) throw new Exception("this phone (Android 10) needs Live record on to read the screen: tap Live record on the panel");
        int kicks = 0;
        for (int run = 1; runs == 0 || run <= runs; run++)
        {
            ct.ThrowIfCancellationRequested();
            Runner.SetPhase($"{Runner.ModeName(mode)} run {run}: starting");
            var here = await nav.ReadAsync(ct);
            bool inside = here.Ok && (den ? here.InDen : here.InLabyrinth);
            int first = 0;
            if (!inside)
            {
                if (!await TeleportAsync(svc, layout, nav, den, ct)) throw new Exception("the teleport didn't work (see the log)");
                await ArriveAsync(svc, layout, nav, den, ct);
            }
            else if (!den && here.Dist(LabyEntrance.x, LabyEntrance.y) < 450)
            {
                // just teleported in by hand: the gate may still be shut
                AppLog.Add("at the Labyrinth entrance");
                arrivedAt = DateTime.UtcNow;
                await ArriveAsync(svc, layout, nav, den, ct);
            }
            else if (!den)
            {
                // already inside (a stopped run): carry on at the nearest boss
                first = Enumerable.Range(0, LabySpots.Length).OrderBy(i => here.Dist(LabySpots[i].x, LabySpots[i].y)).First();
                if (here.Dist(LabySpots[first].x, LabySpots[first].y) > 2500 && here.Y > LabySpots[0].y) first = 0;
                AppLog.Add($"already in the Labyrinth: starting at boss {first + 1}");
            }

            bool kicked = false;
            if (den) kicked = !await BossAsync(svc, layout, nav, DenSpot, "the Den boss", true, ct, 5);
            else
                for (int b = first; b < LabySpots.Length && !kicked; b++)
                    kicked = !await BossAsync(svc, layout, nav, LabySpots[b], $"boss {b + 1}", false, ct, b);

            if (kicked)
            {
                if (++kicks > 10) throw new Exception("kicked out of the dungeon too often");
                AppLog.Add("not in the dungeon any more: starting over from the teleport");
                run--;
                await Task.Delay(5000, ct);
                continue;
            }
            Runner.Cycles++;
            Runner.CountRun(mode);
            AppLog.Add($"{Runner.ModeName(mode)} run {run} done");
            bool more = runs == 0 || run < runs;
            await ReturnAsync(svc, layout, ct, more);
        }
    }

    // ------------------------------------------------------------------ teleport and arrival
    /// <summary>Uses the teleport scroll and picks the dungeon in its list (turning the pages until it shows up).</summary>
    static async Task<bool> TeleportAsync(TapService svc, BubbleLayout layout, Nav nav, bool den, CancellationToken ct)
    {
        Runner.SetPhase("teleporting to " + (den ? "the Den" : "the Labyrinth"));
        string[] names = den ? new[] { "Rebirth", "Ultimate Den", "Den of" } : new[] { "Labyrinth" };
        await Press(svc, layout, BubbleKind.Teleport);
        await Task.Delay(1500, ct);
        for (int page = 0; page < 5; page++)
        {
            // the list is in the middle of the screen
            var lines = await Screen.TextAsync(0.30, 0.08, 0.40, 0.86);
            var hit = lines.FirstOrDefault(l => names.Any(n => l.Text.Contains(n, StringComparison.OrdinalIgnoreCase)));
            if (hit.Text != null && hit.Text.Length > 0)
            {
                AppLog.Add("teleport: " + hit.Text.Trim());
                await svc.PressAsync(hit.Cx, hit.Cy, 80);
                break;
            }
            // not on this page: the arrow to the right of "1 / 3"
            var pageNo = lines.FirstOrDefault(l => System.Text.RegularExpressions.Regex.IsMatch(l.Text, @"^\s*\d\s*/\s*\d\s*$"));
            if (pageNo.Text == null || pageNo.Text.Length == 0)
            {
                AppLog.Add(lines.Count == 0 ? "teleport: the teleport list didn't open (is the Teleport bubble on the scroll?)" : "teleport: no page numbers in the list");
                if (page == 0 && lines.Count == 0) { await Press(svc, layout, BubbleKind.Teleport); await Task.Delay(1500, ct); continue; }
                return false;
            }
            await svc.PressAsync(pageNo.X + pageNo.W + pageNo.H * 5f, pageNo.Cy, 80);
            await Task.Delay(700, ct);
            if (page == 4) { AppLog.Add("teleport: the dungeon isn't in the list"); return false; }
        }
        // the loading screen, then the map
        var until = DateTime.UtcNow.AddSeconds(TeleportLoad);
        await Task.Delay(3000, ct);
        while (DateTime.UtcNow < until)
        {
            var p = await nav.ReadAsync(ct, 1);
            if (p.Ok && (den ? p.InDen : p.InLabyrinth)) { arrivedAt = DateTime.UtcNow; AppLog.Add("arrived: " + p.Map + " at " + p); return true; }
            await Task.Delay(800, ct);
        }
        AppLog.Add("teleport: didn't arrive (map: " + Screen.LastMap + ")");
        return false;
    }

    /// <summary>
    /// After the teleport, the way the member does it by hand: zoom all the way out, the member's own camera fix (if set), turn the view
    /// until it looks at the dungeon's gate (toward the first boss), wait for the gate to open, and go through it straight.
    /// It does not wander: measuring the direction is a short step and the same step back.
    /// </summary>
    static async Task ArriveAsync(TapService svc, BubbleLayout layout, Nav nav, bool den, CancellationToken ct)
    {
        Runner.SetPhase("finding the gate");
        await Task.Delay(1200, ct);
        await nav.ZoomOutAsync(ct);
        if (layout.CamFix.Count > 0) await svc.CameraFixAsync(layout, ct);
        var p = await nav.ReadAsync(ct);
        if (!p.Ok) return;
        var (tx, ty) = den ? DenSpot : LabySpots[0];
        await nav.AlignAsync(Math.Atan2(ty - p.Y, tx - p.X) * 180 / Math.PI, ct);
        if (!den && !countdownDone)
        {
            countdownDone = true;
            // the gate opens 30 s after coming in (the first run): wait it out, then run
            AppLog.Add("gate: waiting for the 30 s countdown");
            while (true)
            {
                double left = GateCountdown - (DateTime.UtcNow - arrivedAt).TotalSeconds;
                if (left <= 0) break;
                Runner.ShowPhase($"gate opens in {Math.Ceiling(left)} s");
                await Task.Delay((int)Math.Min(1000, left * 1000 + 50), ct);
            }
        }
        Runner.SetPhase("through the gate");
        await nav.GateAsync(tx, ty, 300, ct);
    }

    // ------------------------------------------------------------------ one boss
    /// <summary>Walks to the spot, waits for the boss, fights it, loots. False when the character is no longer in the dungeon.</summary>
    static async Task<bool> BossAsync(TapService svc, BubbleLayout layout, Nav nav, (int x, int y) spot, string name, bool den, CancellationToken ct, int routeIndex = -1)
    {
        Runner.SetPhase("walking to " + name);
        for (int tries = 0; tries < 4; tries++)
        {
            var way = Route(routeIndex);
            if (way.Count > 1 ? await nav.FollowAsync(way, spot.x, spot.y, SpotTol, ct) : await nav.WalkToAsync(spot.x, spot.y, SpotTol, ct)) break;
            var p = nav.Last;
            if (p.Ok && !(den ? p.InDen : p.InLabyrinth) && p.Map.Length > 0) return false;
            // the character won't move: usually a boss or monster is still fighting it
            var (hp, there) = await Screen.BossAsync(layout, true);
            if (there)
            {
                AppLog.Add($"walk to {name}: something is still fighting us, finishing it first");
                await FightAsync(svc, layout, nav, "the target", ct);
                await LootAsync(svc, layout, ct);
            }
            Runner.SetPhase($"walking to {name} (again)");
        }
        int fights = 0;
        while (true)
        {
            if (!await WaitForBossAsync(svc, layout, nav, spot, name, fights == 0 ? SpawnTimeout : DenRespawnWait, ct))
            {
                if (fights == 0) AppLog.Add($"{name}: didn't show up, going on");
                break;
            }
            await BuffAsync(svc, layout, nav, ct);
            await FightAsync(svc, layout, nav, name, ct);
            fights++;
            Runner.Kills++;
            await LootAsync(svc, layout, ct);
            if (!den) break;                    // the LOD boss may come back: watch a few seconds more
        }
        var after = await nav.ReadAsync(ct, 1);
        return !(after.Ok && after.Map.Length > 0 && !(den ? after.InDen : after.InLabyrinth));
    }

    /// <summary>Targets until the boss's HP bar shows (the boss spawned). False when it didn't within <paramref name="secs"/>.</summary>
    static async Task<bool> WaitForBossAsync(TapService svc, BubbleLayout layout, Nav nav, (int x, int y) spot, string name, double secs, CancellationToken ct)
    {
        Runner.SetPhase("waiting for " + name);
        var until = DateTime.UtcNow.AddSeconds(secs);
        var nextTarget = DateTime.MinValue;
        var nextHold = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (await Screen.BossHpAsync(layout) > 0) { AppLog.Add($"{name}: HP bar up"); return true; }
            if (DateTime.UtcNow >= nextTarget) { await Press(svc, layout, BubbleKind.Target); nextTarget = DateTime.UtcNow.AddSeconds(3); }
            if (DateTime.UtcNow >= nextHold) { await nav.HoldAsync(spot.x, spot.y, SpotTol * 2, ct); nextHold = DateTime.UtcNow.AddSeconds(4); }
            await Task.Delay(300, ct);
        }
        return false;
    }

    /// <summary>
    /// Attacks (the member's bubbles, like auto attack) until the boss is dead. Dead = no red in the bar AND no target name for a while
    /// (the bar can read empty while the boss lives): 1.5 s after it was nearly dead, 3 s otherwise, 5 s in the first 8 s of a fight;
    /// then the target is pressed once more to be sure.
    /// </summary>
    static async Task FightAsync(TapService svc, BubbleLayout layout, Nav nav, string name, CancellationToken ct)
    {
        Runner.SetPhase("fighting " + name);
        var start = DateTime.UtcNow;
        var fightLayout = layout.Copy();
        // only the fight buttons (no teleport / return / target spam in a fight: target is pressed when it is needed)
        fightLayout.Bubbles.RemoveAll(b => b.Kind is BubbleKind.Teleport or BubbleKind.Return or BubbleKind.Target or BubbleKind.Buff);
        // buffs don't work while attacking: they are done apart, away from the boss, when their time comes round
        var buffs = layout.Bubbles.Where(b => b.Kind == BubbleKind.Buff && b.Mode != PressMode.Off).ToList();
        double buffEvery = buffs.Count == 0 ? double.MaxValue : Math.Max(20, buffs.Min(b => b.Mode == PressMode.Repeat ? b.Every : 120));
        DateTime NextBuff() => buffs.Count == 0 ? DateTime.MaxValue : lastBuff == DateTime.MinValue ? DateTime.UtcNow.AddSeconds(buffEvery) : lastBuff.AddSeconds(buffEvery);
        var nextBuff = NextBuff();
        while (true)
        {
            Runner.ShowPhase("fighting " + name);
            double lo = 1, hi = 0;
            DateTime? goneSince = null;
            bool dead = false, lastThere = true;
            using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watcher = Task.Run(async () =>
            {
                while (!watch.IsCancellationRequested)
                {
                    var (hp, there) = await Screen.BossAsync(layout, true);
                    lastThere = there;
                    if (there) { goneSince = null; if (hp > 0) { lo = Math.Min(lo, hp); hi = Math.Max(hi, hp); } }
                    else goneSince ??= DateTime.UtcNow;
                    double secs = (DateTime.UtcNow - start).TotalSeconds;
                    double need = lo <= 0.15 && hi >= 0.30 ? 1.5 : 3.0;
                    if (secs < 8) need = Math.Max(need, 5);
                    if (goneSince is DateTime g && (DateTime.UtcNow - g).TotalSeconds >= need) { dead = true; return; }
                    if (secs > FightTimeout) { AppLog.Add($"{name}: fight took too long, going on"); dead = true; return; }
                    try { await Task.Delay(200, watch.Token); } catch { return; }
                }
            });
            string? why = await Runner.PressUntil(svc, fightLayout, () => dead || DateTime.UtcNow >= nextBuff, ct);
            watch.Cancel();
            try { await watcher; } catch { /* stopped */ }
            if (why != null && !dead) throw new Exception(why);
            if (!dead && DateTime.UtcNow >= nextBuff)
            {
                await BuffAsync(svc, layout, nav, ct);
                nextBuff = NextBuff();
                continue;
            }
            // make sure: target once more; a boss that is still there shows its bar again
            await Press(svc, layout, BubbleKind.Target);
            await Task.Delay(1200, ct);
            var (hp2, there2) = await Screen.BossAsync(layout, true);
            if (there2 && (DateTime.UtcNow - start).TotalSeconds < FightTimeout)
            {
                AppLog.Add($"{name}: still there ({(hp2 >= 0 ? Math.Round(hp2 * 100) + "%" : "no red")}), attacking on");
                continue;
            }
            break;
        }
        AppLog.Add($"{name}: dead ({(int)(DateTime.UtcNow - start).TotalSeconds} s)");
    }

    static DateTime lastBuff = DateTime.MinValue;

    /// <summary>
    /// Buffs the way it works in the game: stop attacking, step a few steps away from the boss, press each Buff bubble with a moment
    /// between, then the fight goes on.
    /// </summary>
    static async Task BuffAsync(TapService svc, BubbleLayout layout, Nav nav, CancellationToken ct)
    {
        var buffs = layout.Bubbles.Where(b => b.Kind == BubbleKind.Buff && b.Mode != PressMode.Off).ToList();
        if (buffs.Count == 0) return;
        Runner.SetPhase("buffing");
        await nav.StepAwayAsync(ct);
        var (w, h) = svc.ScreenSize();
        foreach (var b in buffs)
        {
            var (x, y) = layout.At(b, w, h);
            await svc.PressAsync(x, y, 80);
            AppLog.Add("buff: " + b.Label);
            await Task.Delay(900, ct);
        }
        lastBuff = DateTime.UtcNow;
        await Task.Delay(300, ct);
    }

    /// <summary>The game's auto loot picks the drops up (they fall at once): press it and give it a moment.</summary>
    static async Task LootAsync(TapService svc, BubbleLayout layout, CancellationToken ct)
    {
        Runner.SetPhase("looting");
        if (layout.Find(BubbleKind.AutoLoot) != null)
        {
            await Press(svc, layout, BubbleKind.AutoLoot);
            await Task.Delay(1500, ct);
            await Press(svc, layout, BubbleKind.AutoLoot);
        }
        await Task.Delay(2000, ct);
    }

    /// <summary>The return scroll after the last boss (and a moment for the town to load).</summary>
    static async Task ReturnAsync(TapService svc, BubbleLayout layout, CancellationToken ct, bool more)
    {
        if (layout.Find(BubbleKind.Return) == null) { if (more) AppLog.Add("no Return bubble: teleporting straight on"); return; }
        Runner.SetPhase("return scroll");
        await Press(svc, layout, BubbleKind.Return);
        await Task.Delay(TimeSpan.FromSeconds(ReturnWait), ct);
    }

    /// <summary>Presses the member's bubble of this kind (if there is one).</summary>
    static async Task Press(TapService svc, BubbleLayout layout, BubbleKind k)
    {
        var b = layout.Find(k);
        if (b == null) return;
        var (w, h) = svc.ScreenSize();
        var (x, y) = layout.At(b, w, h);
        await svc.PressAsync(x, y, 80);
    }
}
