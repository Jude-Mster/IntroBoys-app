using Android.OS;

namespace IntroBoys.App;

/// <summary>What Start does: auto attack on the spot, or a Labyrinth or Den run.</summary>
public enum RunMode { Attack, Labyrinth, Den }

/// <summary>
/// Starts and stops what the Assistant does. Auto attack presses the member's bubbles over and over: "Hold" bubbles (attack) are
/// pressed down for about a second at a time, again and again; "Every…" bubbles (target, skills, buffs, potion, auto loot) when their
/// time comes round, in between; "Once" bubbles at the start. One press at a time, so a press never cuts another short. Labyrinth and
/// Den runs are in RunEngine.cs. Everything stops by itself without a valid license.
/// </summary>
static class Runner
{
    static Android.Content.ISharedPreferences Prefs => TapService.Prefs;

    /// <summary>The chosen run (remembered).</summary>
    public static RunMode Mode
    {
        get => (RunMode)Math.Clamp(Prefs.GetInt("run_mode", 0), 0, 2);
        set { Prefs.Edit()!.PutInt("run_mode", (int)value)!.Apply(); Raise(); }
    }
    /// <summary>Runs in a row for Labyrinth / Den (0 = until stopped).</summary>
    public static int Runs
    {
        get => Math.Clamp(Prefs.GetInt("run_count", 0), 0, 999);
        set { Prefs.Edit()!.PutInt("run_count", Math.Clamp(value, 0, 999))!.Apply(); Raise(); }
    }
    /// <summary>All-time finished runs on this phone.</summary>
    public static int TotalLabyrinth => Prefs.GetInt("total_laby", 0);
    public static int TotalDen => Prefs.GetInt("total_den", 0);
    /// <summary>Called when a Labyrinth or Den run finishes.</summary>
    public static void CountRun(RunMode m)
    {
        string k = m == RunMode.Den ? "total_den" : "total_laby";
        Prefs.Edit()!.PutInt(k, Prefs.GetInt(k, 0) + 1)!.Apply();
        Raise();
    }
    public static string ModeName(RunMode m) => m switch { RunMode.Labyrinth => "Labyrinth", RunMode.Den => "Den (LOD)", _ => "Auto attack" };

    const int HoldMs = 900, TapMs = 70, GapMs = 45;

    public static bool Running { get; private set; }
    /// <summary>What the run is doing right now ("walking to boss 2", "fighting boss 2" ...), for the panel and the website.</summary>
    public static string Phase { get; private set; } = "";
    public static RunMode RunningMode { get; private set; }
    public static int Cycles { get; set; }
    public static int Kills { get; set; }
    public static void SetPhase(string p) { Phase = p; AppLog.Add(p); Raise(); }
    /// <summary>Changes what the panel shows without a log line (a countdown).</summary>
    public static void ShowPhase(string p) { Phase = p; Raise(); }
    public static string LastStop { get; private set; } = "";
    public static int Presses { get; private set; }
    /// <summary>Raised (any thread) when it starts, stops, or every few presses.</summary>
    public static event Action? Changed;
    /// <summary>Raised (any thread) with the index of the bubble just pressed.</summary>
    public static event Action<int>? Pressed;

    static CancellationTokenSource? cts;
    static long startedAt;
    static readonly object gate = new();

    /// <summary>Starts auto attack. Returns null when it started, otherwise why it can't.</summary>
    public static string? Start(RunMode? mode = null)
    {
        var m = mode ?? Mode;
        var svc = TapService.Instance;
        if (svc == null) return "Turn on the tap permission in the app first.";
        if (!License.Allowed()) return License.Why();
        var layout = BubbleLayout.Load();
        if (!layout.Bubbles.Any(b => b.Mode != PressMode.Off && !b.IsArea)) return "Place at least one bubble first (tap Bubbles).";
        if (m != RunMode.Attack && RunEngine.Missing(layout) is { } missing) return missing;
        lock (gate)
        {
            if (Running) return null;
            cts = new CancellationTokenSource();
            Running = true;
            RunningMode = m;
            Presses = 0; Cycles = 0; Kills = 0;
            LastStop = "";
            Phase = "";
            startedAt = SystemClock.ElapsedRealtime();
        }
        var ct = cts.Token;
        if (m == RunMode.Attack)
        {
            _ = Task.Run(() => Loop(svc, layout, ct));
            AppLog.Add("start: auto attack (" + layout.Bubbles.Count(b => b.Mode != PressMode.Off && !b.IsArea) + " bubbles)");
        }
        else
        {
            AppLog.Add("start: " + ModeName(m) + (Runs == 0 ? " until stopped" : ", " + Runs + " runs"));
            _ = Task.Run(async () =>
            {
                try { await RunEngine.RunAsync(svc, m, layout, Runs, ct); Stop("Done: " + Cycles + " " + ModeName(m) + " runs."); }
                catch (System.OperationCanceledException) { }
                catch (LicenseException ex) { Stop("Stopped: " + ex.Message); }
                catch (Exception ex) { Stop("Stopped: " + ex.Message); }
            });
        }
        Raise();
        return null;
    }

    public static void Stop(string why)
    {
        lock (gate)
        {
            if (!Running) return;
            Running = false;
            LastStop = why;
            cts?.Cancel();
            cts = null;
        }
        AppLog.Add(why.Length > 0 ? why : "stopped");
        Raise();
    }

    static void Raise()
    {
        try { Changed?.Invoke(); } catch { /* a listener's problem */ }
    }

    public static string StatusLine()
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, SystemClock.ElapsedRealtime() - startedAt));
        string time = t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : $"{t.Minutes}m {t.Seconds:D2}s";
        return RunningMode == RunMode.Attack
            ? $"running {time} · {Presses} presses\nLicense: {License.TimeLeftText()} left"
            : $"run {Cycles + 1} · {Kills} kills · {time}\nLicense: {License.TimeLeftText()} left";
    }

    static async Task Loop(TapService svc, BubbleLayout layout, CancellationToken ct)
    {
        try
        {
            // loot only when something died (with the Boss HP bar box placed: a target's red bar went away); no potion (the game does HP)
            var fight = layout.Copy();
            bool watchKills = fight.Find(BubbleKind.BossBar) != null && fight.Find(BubbleKind.AutoLoot) != null;
            fight.Bubbles.RemoveAll(b => b.Kind == BubbleKind.Potion || (watchKills && b.Kind == BubbleKind.AutoLoot));
            if (!watchKills) { string? w = await PressUntil(svc, fight, () => false, ct); Stop(w ?? "Done: every bubble was pressed."); return; }
            while (!ct.IsCancellationRequested)
            {
                bool died = false; double seen = 0; DateTime? gone = null;
                using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var watcher = Task.Run(async () =>
                {
                    while (!watch.IsCancellationRequested)
                    {
                        double hp = await Screen.BossHpAsync(layout);
                        if (hp > 0) { seen = Math.Max(seen, hp); gone = null; }
                        else if (seen > 0) { gone ??= DateTime.UtcNow; if ((DateTime.UtcNow - gone.Value).TotalSeconds >= 1.2) { died = true; return; } }
                        try { await Task.Delay(250, watch.Token); } catch { return; }
                    }
                });
                string? why = await PressUntil(svc, fight, () => died, ct);
                watch.Cancel();
                try { await watcher; } catch { }
                if (why != null) { Stop(why); return; }
                if (died) { Kills++; AppLog.Add("target died: looting"); await LootBurstAsync(svc, layout, ct); }
            }
        }
        catch (System.OperationCanceledException) { }
        catch (LicenseException ex) { Stop("Stopped: " + ex.Message); }
        catch (Exception ex) { Stop("Stopped: " + ex.Message); }
    }

    /// <summary>
    /// Presses the bubbles (as auto attack does) until <paramref name="done"/> says so. Returns null when done (or every bubble was
    /// pressed once), otherwise why it had to stop.
    /// </summary>
    /// <summary>Loot after a kill: the Auto loot bubble, every 400 ms, about 3 seconds.</summary>
    public static async Task LootBurstAsync(TapService svc, BubbleLayout layout, CancellationToken ct, int taps = 6)
    {
        var loot = layout.Find(BubbleKind.AutoLoot);
        if (loot == null) return;
        var (w, h) = svc.ScreenSize();
        var (x, y) = layout.At(loot, w, h);
        for (int i = 0; i < taps; i++)
        {
            ct.ThrowIfCancellationRequested();
            await svc.PressAsync(x, y, 60);
            await Task.Delay(340, ct);
        }
    }

    public static async Task<string?> PressUntil(TapService svc, BubbleLayout layout, Func<bool> done, CancellationToken ct)
    {
        // HP is drunk by the game itself: a Potion bubble from an older layout is never pressed
        var list = layout.Bubbles.Where(b => b.Kind != BubbleKind.Potion).ToList();
        var due = new long[list.Count];
        for (int i = 0; i < list.Count; i++) due[i] = list[i].Mode == PressMode.Off || list[i].IsArea ? long.MaxValue : 0;
        int failed = 0;
        {
            while (!ct.IsCancellationRequested && !done())
            {
                if (!License.Allowed()) return "Stopped: " + License.Why();

                // the bubble whose turn it is: the one waiting longest
                long now = SystemClock.UptimeMillis();
                int pick = -1;
                for (int i = 0; i < list.Count; i++)
                    if (due[i] <= now && (pick < 0 || due[i] < due[pick])) pick = i;
                if (pick < 0)
                {
                    long next = due.Min();
                    if (next == long.MaxValue) return null;
                    await Task.Delay((int)Math.Clamp(next - now, 10, 200), ct);
                    continue;
                }

                var b = list[pick];
                var (w, h) = svc.ScreenSize();
                var (x, y) = layout.At(b, w, h);
                bool ok = await svc.PressAsync(x, y, b.Mode == PressMode.Hold ? HoldMs : TapMs);
                ct.ThrowIfCancellationRequested();
                Pressed?.Invoke(pick);
                if (b.Mode != PressMode.Hold || Presses == 0) AppLog.Add((b.Mode == PressMode.Hold ? "attack: holding " : "press ") + b.Label + (ok ? "" : " (cancelled)"));

                long after = SystemClock.UptimeMillis();
                due[pick] = b.Mode switch
                {
                    PressMode.Hold => after,                                     // straight back down, after anything that is due
                    PressMode.Repeat => after + (long)(Math.Max(0.5, b.Every) * 1000),
                    _ => long.MaxValue                                           // Once
                };

                if (ok) { if (failed >= 12) AppLog.Add("taps: going through again"); failed = 0; Presses++; if (Presses % 5 == 0) Raise(); }
                else
                {
                    // 1.3.8: a cancelled tap (a finger on the screen, a game pop-up, the phone's game tools) no longer stops the run at once:
                    // after 12 in a row it says so and pauses a moment, and only stops after about a minute of nothing going through
                    failed++;
                    if (failed == 12) AppLog.Add("taps: Android keeps cancelling them (a finger on the screen, a pop-up over the game, or the phone's game tools blocking gestures) - trying on");
                    if (failed >= 12) { if (failed >= 80) return "Stopped: the taps keep being cancelled for a minute (is the screen off, or is something on top of the game?)."; await Task.Delay(600, ct); }
                }
                await Task.Delay(GapMs, ct);
            }
        }
        ct.ThrowIfCancellationRequested();
        return null;
    }
}
