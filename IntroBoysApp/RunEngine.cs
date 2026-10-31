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
    static readonly (int x, int y) DenSpot = (1205, 2383);      // 1.3.7: where the character fights the LOD boss (PC 4.11.1)
    const double SpotTol = 150;               // close enough to a boss spot (map units)
    const double SpawnTimeout = 90;            // longest wait for a boss to show up
    const double FightTimeout = 480;           // longest fight (1.4.1: was 240 - boss 4 and 5 took longer on a weaker character and were left alive)
    const double GoneSecs = 3.0, NearDeadGoneSecs = 1.5;
    const double DenRespawnWait = 6;           // the LOD boss comes back (about 30 %) a moment after the kill
    const double ReturnWait = 8, TeleportLoad = 45;
    const double GateCountdown = 30;            // the Labyrinth's gate opens 30 s after you come in
    static readonly (int x, int y) LabyEntrance = (3595, 34153);
    /// <summary>1.4.1: a point to walk through first on the way to each Labyrinth boss (null = straight there).</summary>
    static readonly (int x, int y)?[] Via = { null, null, null, null, (3600, 3900) };
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
        AppLog.Add("phone: " + AppLog.Device());
        countdownDone = false;
        restartAtOne = false;                                 // 1.5.3: a kick from an earlier, stopped run doesn't count
        // 1.4.9: no need to tap Live record first: on a phone that can only see the game through screen sharing (Android 9 and 10,
        // emulators) the run asks Android for it by itself and starts as soon as it is allowed
        if (!Capture.CanSee)
        {
            Runner.SetPhase("asking Android to share the screen (tick \"Don't show again\", then Start now)");
            CaptureActivity.Request(Android.App.Application.Context);
            var until = DateTime.UtcNow.AddSeconds(45);
            while (!Capture.CanSee && DateTime.UtcNow < until) await Task.Delay(400, ct);
            if (!Capture.CanSee) throw new Exception("screen sharing wasn't allowed, so the run can't see the game - press Start again and tap Start now / Allow");
            AppLog.Add("screen: shared - starting");
            await Task.Delay(800, ct);
        }
        int kicks = 0;
        for (int run = 1; runs == 0 || run <= runs; run++)
        {
            // 1.5.2: the war break (Runner.WarBreak): the run is cut off at :27 of every war hour wherever it is, then WarBreakAsync
            // (return scroll, auto hunt on through the war, auto hunt off after it) and the run starts again from the teleport, at boss 1
            using var warCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var rt = warCts.Token;
            if (Runner.WarBreak && mode != RunMode.Attack)
            {
                if (WarClock.InBreak(DateTime.UtcNow)) { await WarBreakAsync(svc, layout, nav, ct); restartAtOne = true; run--; continue; }
                warCts.CancelAfter(WarClock.NextBreak(DateTime.UtcNow) - DateTime.UtcNow);
            }
            try
            {
                rt.ThrowIfCancellationRequested();
                Runner.SetPhase($"{Runner.ModeName(mode)} run {run}: starting");
                var here = await nav.ReadAsync(rt);
                var place = await PlaceNowAsync(nav, rt);
                AppLog.Add("where: " + Screen.PlaceText);
                bool inside = here.Ok && place == Target(den);
                int first = 0;
                if (!inside)
                {
                    // 1.3.8: a second try (the list opened on another page, or a word was misread on a smaller screen)
                    if (!await TeleportAsync(svc, layout, nav, den, rt))
                    {
                        AppLog.Add("teleport: trying once more");
                        await Task.Delay(2000, rt);
                        if (!await TeleportAsync(svc, layout, nav, den, rt)) throw new Exception("the teleport didn't work (see the log)");
                    }
                    await ArriveAsync(svc, layout, nav, den, rt);
                }
                else if (!den && here.Dist(LabyEntrance.x, LabyEntrance.y) < 450)
                {
                    // just teleported in by hand: the gate may still be shut
                    AppLog.Add("at the Labyrinth entrance");
                    arrivedAt = DateTime.UtcNow;
                    await ArriveAsync(svc, layout, nav, den, rt);
                }
                else if (!den && restartAtOne)
                {
                    AppLog.Add("back in the Labyrinth after a kick: starting at boss 1");
                }
                else if (!den)
                {
                    // already inside (a stopped run): carry on at the nearest boss
                    first = Enumerable.Range(0, LabySpots.Length).OrderBy(i => here.Dist(LabySpots[i].x, LabySpots[i].y)).First();
                    if (here.Dist(LabySpots[first].x, LabySpots[first].y) > 2500 && here.Y > LabySpots[0].y) first = 0;
                    AppLog.Add($"already in the Labyrinth: starting at boss {first + 1}");
                }

                restartAtOne = false;
                bool kicked = false;
                if (den) kicked = !await BossAsync(svc, layout, nav, DenSpot, "the Den boss", true, rt, 5);
                else
                    for (int b = first; b < LabySpots.Length && !kicked; b++)
                        kicked = !await BossAsync(svc, layout, nav, LabySpots[b], $"boss {b + 1}", false, rt, b);

                if (kicked)
                {
                    if (++kicks > 10) throw new Exception("kicked out of the dungeon too often");
                    // 1.3.7: back to the run from stage 1 (boss 1): wait for the town to load, then the teleport again
                    AppLog.Add($"kicked out ({Screen.PlaceText}): back to the run, starting at boss 1 again");
                    Runner.SetPhase("kicked out - back to the run");
                    restartAtOne = true;
                    run--;
                    await Task.Delay(5000, rt);
                    continue;
                }
                Runner.Cycles++;
                Runner.CountRun(mode);
                AppLog.Add($"{Runner.ModeName(mode)} run {run} done");
                bool more = runs == 0 || run < runs;
                await ReturnAsync(svc, layout, nav, rt, more);

            }
            catch (System.OperationCanceledException) when (!ct.IsCancellationRequested && warCts.IsCancellationRequested)
            {
                AppLog.Add("war break: it's :27 - leaving the run for the war");
                await WarBreakAsync(svc, layout, nav, ct);
                restartAtOne = true;
                run--;
            }
        }
    }

    // ------------------------------------------------------------------ teleport and arrival
    /// <summary>Uses the teleport scroll and picks the dungeon in its list (turning the pages until it shows up).</summary>
    static async Task<bool> TeleportAsync(TapService svc, BubbleLayout layout, Nav nav, bool den, CancellationToken ct)
    {
        Runner.SetPhase("teleporting to " + (den ? "the Den" : "the Labyrinth"));
        string[] names = den ? new[] { "Rebirth", "Ultimate Den", "Den of", "Rebir" } : new[] { "Labyrinth", "Labyr", "rinth (Ad" };
        await Press(svc, layout, BubbleKind.Teleport);
        await Task.Delay(1500, ct);
        for (int page = 0; page < 5; page++)
        {
            // the list is in the middle of the screen
            // 1.3.8: read at twice the size on smaller screens (the list's letters are small there)
            var lines = await Screen.TextAsync(0.30, 0.08, 0.40, 0.86, Math.Min(svc.ScreenSize().w, svc.ScreenSize().h) < 1000 ? 2 : 1);
            // 1.4.5: a chat line passing by ("... (Labyrinth Key] ...") is not the list's entry: the entry is "[The Labyrinth (Adept)]"
            // (or the Den's name) - no "Key", no chat name, not a long sentence
            bool Entry(TextLine l)
            {
                var t = l.Text;
                if (t.Contains("Key", StringComparison.OrdinalIgnoreCase) || t.Contains("PVP", StringComparison.OrdinalIgnoreCase) || t.Length > 34) return false;
                return den ? names.Any(n => t.Contains(n, StringComparison.OrdinalIgnoreCase))
                           : System.Text.RegularExpressions.Regex.IsMatch(t, @"(The\s*Laby|Labyrinth\s*\(|rinth\s*\(Ad)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            var hit = lines.FirstOrDefault(Entry);
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
            if (p.Ok && Screen.PlaceOf(p) == Target(den)) { arrivedAt = DateTime.UtcNow; AppLog.Add("arrived: " + Screen.PlaceText + " at " + p); return true; }
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
        nav.Forget();                                         // 1.5.4: the camera starts afresh after every teleport
        var p = await nav.ReadAsync(ct);
        if (!p.Ok) return;
        var (tx, ty) = den ? DenSpot : LabySpots[0];
        await nav.AlignAsync(Math.Atan2(ty - p.Y, tx - p.X) * 180 / Math.PI, ct);
        // 1.6.2: the gate opens 30 s after coming in - on EVERY run (every run is a Labyrinth of its own): wait it out, then run
        if (!den)
        {
            countdownDone = true;
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
        // 1.5.4: the Labyrinth's gate is at about y 33460, some 700 from where you arrive (3595 , 34153). "Through" used to be 300 on the
        // way - still in front of the gate - so on every run but the first (no countdown wait) it ran into the shut gate and walked round
        // it in circles. Now it pushes at the gate until it is really past it (y under 33380), however long the gate takes to open.
        await nav.GateAsync(tx, ty, den ? 300 : Math.Max(300, p.Y - 33380), ct, 75);
    }

    // ------------------------------------------------------------------ one boss
    /// <summary>Walks to the spot, waits for the boss, fights it, loots. False when the character is no longer in the dungeon.</summary>
    static async Task<bool> BossAsync(TapService svc, BubbleLayout layout, Nav nav, (int x, int y) spot, string name, bool den, CancellationToken ct, int routeIndex = -1)
    {
        Runner.SetPhase("walking to " + name);
        bool there = false;
        // 1.4.1: the straight line from boss 4 to boss 5 hugs the west side of the Labyrinth and runs into its walls (stuck at about
        // 3415 , 4740 on two phones, and 3510 , 7200). The way that works - and the way members walk it - is down the middle (x 3600)
        // to about 3600 , 3900, then across to boss 5.
        if (!den && routeIndex >= 0 && routeIndex < Via.Length && Via[routeIndex] is { } via && Route(routeIndex).Count <= 1)
        {
            var here = await nav.ReadAsync(ct, 2);
            if (here.Ok && here.Y > via.y + 300 && here.Dist(spot.x, spot.y) > here.Dist(via.x, via.y))
            {
                AppLog.Add($"walk: down the middle first ({via.x} , {via.y}), then to {name}");
                await nav.WalkToAsync(via.x, via.y, 250, ct, 75, release: false);
            }
        }
        for (int tries = 0; tries < 4; tries++)
        {
            // 1.5.4: never walk to a boss outside the dungeon (it tried to walk to boss 1 in town, 11000 away)
            var here0 = await nav.ReadAsync(ct, 2);
            var pl0 = Screen.PlaceOf(here0);
            if (pl0 != Place.Unknown && pl0 != Target(den) && await OutsideAsync(nav, den, ct)) return false;
            // 1.6.0: the PC's recorded ways between the bosses (not the first one, from the entrance: that is walked straight)
            var way = routeIndex >= 1 ? Route(routeIndex) : new List<(int x, int y)>();
            if (way.Count > 1 ? await nav.FollowAsync(way, spot.x, spot.y, SpotTol, ct) : await nav.WalkToAsync(spot.x, spot.y, SpotTol, ct)) { there = true; break; }
            var p = nav.Last;
            if (await OutsideAsync(nav, den, ct)) return false;
            // the character won't move: usually a boss or monster is still fighting it
            // only a red bar counts here (text near the box can be a game message)
            var (hp, _) = await Screen.BossAsync(layout, false);
            if (hp > 0)
            {
                AppLog.Add($"walk to {name}: something is still fighting us, finishing it first");
                await FightAsync(svc, layout, nav, "the target", ct);
                await LootAsync(svc, layout, ct);
            }
            Runner.SetPhase($"walking to {name} (again)");
        }
        if (!there)
        {
            // 1.3.7: never got there: look harder at where the character is (a death or a kick takes a few seconds to show the town)
            for (int i = 0; i < 4; i++)
            {
                if (await OutsideAsync(nav, den, ct)) return false;
                await Task.Delay(1500, ct);
            }
            AppLog.Add($"{name}: couldn't get to the spot, waiting where it is");
        }
        int fights = 0;
        while (true)
        {
            // 1.5.2 (as PC 4.11): after a Den kill everything is looted first (above), then the respawn is watched for 6 s with the
            // target pressed every second; a respawned boss is fought and looted the same way, up to 5 times; then the return scroll
            if (den && fights > 0)
            {
                if (fights > 5) break;
                AppLog.Add($"LOD: watching {DenRespawnWait:0} s for the boss to respawn");
            }
            int up = await WaitForBossAsync(svc, layout, nav, spot, name, fights == 0 ? SpawnTimeout : DenRespawnWait, den, ct, fights > 0 ? 1.0 : 3.0);
            if (den && fights > 0 && up == 0) { AppLog.Add("LOD: no respawn"); break; }
            if (den && fights > 0 && up > 0) AppLog.Add($"LOD: the boss respawned ({fights}) - attacking it");
            if (up < 0) return false;                          // 1.3.7: not in the dungeon any more
            if (up == 0)
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
        // 1.5.4: after the kill one quick look where the character is (a death in the fight wakes it up in town - it walked on to the
        // next boss in town); the full check only when that look says it is not in the dungeon
        if (fights > 0)
        {
            var pl = Screen.PlaceOf(await nav.ReadAsync(ct, 1));
            return pl == Target(den) || pl == Place.Unknown || !await OutsideAsync(nav, den, ct);
        }
        return !await OutsideAsync(nav, den, ct);
    }

    /// <summary>
    /// Targets until the boss's HP bar shows (the boss spawned): 1. 0 when it didn't within <paramref name="secs"/>; -1 when the character
    /// is not in the dungeon any more (1.3.7: looked at every 5 s - it used to walk on toward the boss spot in town).
    /// </summary>
    static async Task<int> WaitForBossAsync(TapService svc, BubbleLayout layout, Nav nav, (int x, int y) spot, string name, double secs, bool den, CancellationToken ct, double targetEvery = 3.0)
    {
        Runner.SetPhase("waiting for " + name);
        var until = DateTime.UtcNow.AddSeconds(secs);
        var nextPlace = DateTime.UtcNow.AddSeconds(5);
        var nextTarget = DateTime.MinValue;
        var nextHold = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (await Screen.BossHpAsync(layout) > 0) { AppLog.Add($"{name}: HP bar up"); return 1; }
            if (DateTime.UtcNow >= nextPlace)
            {
                nextPlace = DateTime.UtcNow.AddSeconds(5);
                var pl = Screen.PlaceOf(await nav.ReadAsync(ct, 1));
                if (pl != Place.Unknown && pl != Target(den) && await OutsideAsync(nav, den, ct)) return -1;
            }
            if (DateTime.UtcNow >= nextTarget) { await Press(svc, layout, BubbleKind.Target); nextTarget = DateTime.UtcNow.AddSeconds(targetEvery); }
            if (DateTime.UtcNow >= nextHold) { await nav.HoldAsync(spot.x, spot.y, SpotTol * 2, ct); nextHold = DateTime.UtcNow.AddSeconds(4); }
            await Task.Delay(300, ct);
        }
        return 0;
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
        fightLayout.Bubbles.RemoveAll(b => b.Kind is BubbleKind.Teleport or BubbleKind.Return or BubbleKind.Target or BubbleKind.Buff
                                                or BubbleKind.AutoLoot or BubbleKind.Potion or BubbleKind.AutoHunt);
        bool hunt = layout.Find(BubbleKind.AutoHunt) != null;
        if (hunt) { await Press(svc, layout, BubbleKind.AutoHunt); AppLog.Add("auto hunt: on"); }
        // buffs don't work while attacking: they are done apart, away from the boss, when their time comes round
        var buffs = layout.Bubbles.Where(b => b.Kind == BubbleKind.Buff && b.Mode != PressMode.Off).ToList();
        double buffEvery = buffs.Count == 0 ? double.MaxValue : Math.Max(20, buffs.Min(b => b.Mode == PressMode.Repeat ? b.Every : 120));
        DateTime NextBuff() => buffs.Count == 0 ? DateTime.MaxValue : lastBuff == DateTime.MinValue ? DateTime.UtcNow.AddSeconds(buffEvery) : lastBuff.AddSeconds(buffEvery);
        var nextBuff = NextBuff();
        lootBase = await Screen.GroundYellowAsync();          // 1.5.1: the ground before anything dropped (see LootAsync)
        // 1.3.8: what was seen of the bar is kept for the whole fight (it was forgotten after every check, so on a phone where the red is
        // hard to read the fight stopped every 5 s to look again - and one missed name made the run leave a living boss)
        double lo = 1, hi = 0;
        int confirmMisses = 0, redReads = 0;
        var lastRed = DateTime.UtcNow;
        while (true)
        {
            Runner.ShowPhase("fighting " + name);
            DateTime? goneSince = null;
            bool dead = false, lastThere = true;
            using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watcher = Task.Run(async () =>
            {
                while (!watch.IsCancellationRequested)
                {
                    var (hp, there) = await Screen.BossAsync(layout, hi > 0);
                    if (hp <= 0 && hi <= 0) there = false;            // never saw its red bar: text near the box isn't a boss
                    lastThere = there;
                    if (there) { goneSince = null; if (hp > 0) { lo = Math.Min(lo, hp); hi = Math.Max(hi, hp); redReads++; lastRed = DateTime.UtcNow; } }
                    else goneSince ??= DateTime.UtcNow;
                    double secs = (DateTime.UtcNow - start).TotalSeconds;
                    double need = lo <= 0.15 && hi >= 0.30 ? 1.5 : hi > 0 ? 3.0 : 6.0;     // never saw red: look longer
                    if (secs < 8) need = Math.Max(need, 5);
                    if (goneSince is DateTime g && (DateTime.UtcNow - g).TotalSeconds >= need) { dead = true; return; }
                    if (secs > FightTimeout) { AppLog.Add($"{name}: fight took too long, going on"); dead = true; return; }
                    try { await Task.Delay(200, watch.Token); } catch { return; }
                }
            });
            // 1.5.1: stop attacking the moment the bar is gone (half a second), not after the death has been made sure of: it kept
            // swinging for several seconds at a boss that was already dead. If the bar comes back, the attack goes on at once.
            bool GoneNow() => goneSince is DateTime gs && (DateTime.UtcNow - gs).TotalSeconds >= 0.5 && hi > 0;
            string? why = null;
            while (true)
            {
                why = await Runner.PressUntil(svc, fightLayout, () => dead || DateTime.UtcNow >= nextBuff || GoneNow(), ct);
                if (why != null || dead || DateTime.UtcNow >= nextBuff) break;
                // the bar is gone: wait, without attacking, until the watcher is sure (dead) or the bar is back
                var waitUntil = DateTime.UtcNow.AddSeconds(8);
                while (!dead && goneSince != null && DateTime.UtcNow < waitUntil) await Task.Delay(100, ct);
                if (dead || goneSince != null) break;
            }
            watch.Cancel();
            try { await watcher; } catch { /* stopped */ }
            if (why != null && !dead) throw new Exception(why);
            if (!dead && DateTime.UtcNow >= nextBuff)
            {
                await BuffAsync(svc, layout, nav, ct);
                nextBuff = NextBuff();
                continue;
            }
            if ((DateTime.UtcNow - start).TotalSeconds >= FightTimeout) break;
            // make sure: target once more and look again; a boss that is still there shows its bar or its name again.
            // 1.3.8: one look was not enough - a missed read made the run walk away from a living boss.
            // 1.4.0: after a boss that was nearly dead the check is one quick look (it is over: on to the next boss at once), else three
            bool nearlyDead = lo <= 0.15 && hi >= 0.30;
            await Press(svc, layout, BubbleKind.Target);
            await Task.Delay(nearlyDead ? 500 : 900, ct);
            bool alive = false, red = false; double hp2 = -1;
            for (int k = 0; k < (nearlyDead ? 2 : 3) && !alive; k++)
            {
                if (k > 0) await Task.Delay(400, ct);
                var (h2, t2) = await Screen.BossAsync(layout, true);
                if (h2 > 0 || t2) { alive = true; hp2 = h2; red = h2 > 0; }
            }
            // 1.4.0: only a name, no red, for a long time on a phone that reads the red well: the target key has picked something else
            // (yourself, a player) and the boss is gone - it stayed 2 minutes on a dead boss until the fight's time ran out
            double noRed = (DateTime.UtcNow - lastRed).TotalSeconds;
            if (alive && !red && redReads >= 8 && noRed > (lo <= 0.15 ? 25 : 15))
            {
                AppLog.Add($"{name}: only a name and no red bar for {noRed:0} s - counting it dead");
                alive = false;
            }
            if (alive)
            {
                if (red) lastRed = DateTime.UtcNow;
                if (++confirmMisses <= 3 || confirmMisses % 10 == 0)
                    AppLog.Add($"{name}: still there ({(hp2 > 0 ? Math.Round(hp2 * 100) + "%" : "no red")}), attacking on");
                continue;
            }
            break;
        }
        if (hunt) { await Press(svc, layout, BubbleKind.AutoHunt); AppLog.Add("auto hunt: off"); }
        AppLog.Add($"{name}: dead ({(int)(DateTime.UtcNow - start).TotalSeconds} s)");
    }

    static DateTime lastBuff = DateTime.MinValue;

    /// <summary>
    /// 1.5.2: the war break. Return scroll to town (it goes on if that fails), the Auto hunt bubble on - the game's auto hunt joins the
    /// war by itself (the war pop-up is never cancelled) - wait until the war is over (:49), Auto hunt off, and the run starts again.
    /// </summary>
    static async Task WarBreakAsync(TapService svc, BubbleLayout layout, Nav nav, CancellationToken ct)
    {
        Runner.OnWarBreak = true;
        try
        {
            Runner.SetPhase("war break: returning to town");
            try { await ReturnAsync(svc, layout, nav, ct, true); }
            catch (System.OperationCanceledException) { throw; }
            catch (Exception ex) { AppLog.Add("war break: " + ex.Message + " - going on"); }
            bool hunt = layout.Find(BubbleKind.AutoHunt) != null;
            if (hunt) { await Press(svc, layout, BubbleKind.AutoHunt); AppLog.Add("war break: auto hunt on - it joins the war"); }
            else AppLog.Add("war break: no Auto hunt bubble placed - waiting in town (place one in Bubbles to join the war)");
            var back = WarClock.BackAt(DateTime.UtcNow);
            while (DateTime.UtcNow < back)
            {
                var left = back - DateTime.UtcNow;
                Runner.ShowPhase($"war break · back to the run in {(int)left.TotalMinutes}:{left.Seconds:00}");
                await Task.Delay(1000, ct);
            }
            if (hunt) { await Press(svc, layout, BubbleKind.AutoHunt); AppLog.Add("war break: the war is over - auto hunt off"); }
            await Task.Delay(2500, ct);
            Runner.SetPhase("war break over: back to the run");
        }
        finally { Runner.OnWarBreak = false; }
    }

    static Place Target(bool den) => den ? Place.Den : Place.Labyrinth;
    static bool restartAtOne;

    /// <summary>Where the character is now (a few looks until one can be read).</summary>
    static async Task<Place> PlaceNowAsync(Nav nav, CancellationToken ct)
    {
        for (int i = 0; i < 4; i++)
        {
            var pl = Screen.PlaceOf(await nav.ReadAsync(ct, 1));
            if (pl != Place.Unknown) return pl;
            await Task.Delay(400, ct);
        }
        return Place.Unknown;
    }

    /// <summary>
    /// Kicked out of the dungeon (or died and woke up in town)? 1.3.7: from the place of each picture (its own mini-map title, else its
    /// coordinate - never an older title): three looks that say another place and none that says this dungeon, within about 4 s.
    /// </summary>
    static async Task<bool> OutsideAsync(Nav nav, bool den, CancellationToken ct)
    {
        int outside = 0;
        for (int i = 0; i < 6 && outside < 3; i++)
        {
            if (i > 0) await Task.Delay(700, ct);
            var pl = Screen.PlaceOf(await nav.ReadAsync(ct, 1));
            if (pl == Target(den)) return false;
            if (pl != Place.Unknown) outside++;
        }
        if (outside < 3) return false;
        AppLog.Add("where: " + Screen.PlaceText + " - not in " + (den ? "the Den" : "the Labyrinth") + " any more");
        return true;
    }

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

    /// <summary>Loot right after the kill (the drops fall at once): the Auto loot bubble every 400 ms for about 3 seconds.</summary>
    static int lootBase = -1;

    /// <summary>
    /// 1.5.1: loot only when something lies on the ground. The yellow item names around the character are counted and compared with
    /// the count at the start of the fight; nothing new = no loot taps (it used to tap Auto loot 6 times after every kill). While
    /// looting it looks again every second tap and stops once the names are gone. A screen that can't be seen loots as before.
    /// </summary>
    static async Task LootAsync(TapService svc, BubbleLayout layout, CancellationToken ct)
    {
        var loot = layout.Find(BubbleKind.AutoLoot);
        if (loot == null) return;
        await Task.Delay(500, ct);                           // the drops show up a moment after the death
        int now = await Screen.GroundYellowAsync();
        if (now < 0 || lootBase < 0) { Runner.SetPhase("looting"); await Runner.LootBurstAsync(svc, layout, ct); return; }
        int thr = 18 + lootBase / 4;
        if (now <= lootBase + thr)
        {
            await Task.Delay(400, ct);
            now = await Screen.GroundYellowAsync();          // one more look (the items may be a moment late)
            if (now <= lootBase + thr) { AppLog.Add("loot: nothing on the ground"); return; }
        }
        Runner.SetPhase("looting");
        var (w, h) = svc.ScreenSize();
        var (x, y) = layout.At(loot, w, h);
        for (int i = 1; i <= 10; i++)
        {
            await svc.PressAsync(x, y, 60);
            await Task.Delay(340, ct);
            if (i % 2 == 0 && (now = await Screen.GroundYellowAsync()) >= 0 && now <= lootBase + thr / 2) { AppLog.Add($"loot: picked up ({i} taps)"); return; }
        }
        AppLog.Add("loot: done (10 taps)");
    }

    /// <summary>
    /// The return scroll after the last boss. 1.4.1: the game asks to confirm first - the OPPO never got past that box, so the next run
    /// started in the Labyrinth again. The box's button (CONFIRM / OK / YES / USE) is found by its text and pressed, and the run checks it
    /// really is in town; once more if not.
    /// </summary>
    static async Task ReturnAsync(TapService svc, BubbleLayout layout, Nav nav, CancellationToken ct, bool more)
    {
        if (layout.Find(BubbleKind.Return) == null) { if (more) AppLog.Add("no Return bubble: teleporting straight on"); return; }
        Runner.SetPhase("return scroll");
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            await Press(svc, layout, BubbleKind.Return);
            await Task.Delay(1200, ct);
            await ConfirmAsync(svc, ct);
            var until = DateTime.UtcNow.AddSeconds(ReturnWait + 4);
            await Task.Delay(2500, ct);
            while (DateTime.UtcNow < until)
            {
                var pl = Screen.PlaceOf(await nav.ReadAsync(ct, 1));
                if (pl == Place.Town) { AppLog.Add("return scroll: in town (" + Screen.PlaceText + ")"); await Task.Delay(1500, ct); return; }
                await Task.Delay(800, ct);
            }
            AppLog.Add(attempt == 1 ? "return scroll: still not in town - once more" : "return scroll: still not in town");
        }
        throw new Exception("the return scroll didn't take the character to town (is the Return bubble on the scroll, and its confirm box on screen?)");
    }

    /// <summary>A confirm box in the middle of the screen: presses its CONFIRM / OK / YES / USE button (never Cancel). False when there is none.</summary>
    static async Task<bool> ConfirmAsync(TapService svc, CancellationToken ct)
    {
        string[] yes = { "confirm", "ok", "yes", "use", "accept", "확인", "ตกลง" };
        List<TextLine> seen = new();
        for (int look = 0; look < 3; look++)
        {
            if (look > 0) await Task.Delay(500, ct);
            var lines = await Screen.TextAsync(0.15, 0.20, 0.70, 0.70, Math.Min(svc.ScreenSize().w, svc.ScreenSize().h) < 1000 ? 2 : 1);
            var hit = lines.Where(l =>
            {
                var t = new string(l.Text.Where(ch => char.IsLetter(ch)).ToArray()).ToLowerInvariant();
                return t.Length > 0 && t.Length <= 8 && yes.Any(w => t == w);
            }).OrderByDescending(l => l.Y).FirstOrDefault();
            if (lines.Count > 0) seen = lines;
            if (hit.Text != null && hit.Text.Length > 0)
            {
                AppLog.Add("return scroll: pressing " + hit.Text.Trim());
                await svc.PressAsync(hit.Cx, hit.Y + hit.H / 2, 80);
                return true;
            }
        }
        // for the next log: what was on screen instead (a button with another word can be added)
        AppLog.Add("return scroll: no confirm button found" + (seen.Count > 0 ? " (saw: " + string.Join(" | ", seen.Take(8).Select(l => l.Text.Trim())) + ")" : ""));
        return false;
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
