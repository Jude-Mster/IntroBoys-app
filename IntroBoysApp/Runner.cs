using Android.OS;

namespace IntroBoys.App;

/// <summary>
/// Auto attack: presses the member's bubbles over and over. "Hold" bubbles (attack) are pressed down for about a second at a time,
/// again and again; "Every…" bubbles (target, skills, buffs, potion, auto loot) when their time comes round, in between; "Once"
/// bubbles at the start. One press at a time, so a press never cuts another short. Stops by itself without a valid license.
/// </summary>
static class Runner
{
    const int HoldMs = 900, TapMs = 70, GapMs = 45;

    public static bool Running { get; private set; }
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
    public static string? Start()
    {
        var svc = TapService.Instance;
        if (svc == null) return "Turn on the tap permission in the app first.";
        if (!License.Allowed()) return License.Why();
        var layout = BubbleLayout.Load();
        if (!layout.Bubbles.Any(b => b.Mode != PressMode.Off)) return "Place at least one bubble first (tap Bubbles).";
        lock (gate)
        {
            if (Running) return null;
            cts = new CancellationTokenSource();
            Running = true;
            Presses = 0;
            LastStop = "";
            startedAt = SystemClock.ElapsedRealtime();
        }
        var ct = cts.Token;
        _ = Task.Run(() => Loop(svc, layout, ct));
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
        return $"running {time} · {Presses} presses\nLicense: {License.TimeLeftText()} left";
    }

    static async Task Loop(TapService svc, BubbleLayout layout, CancellationToken ct)
    {
        var list = layout.Bubbles;
        var due = new long[list.Count];
        for (int i = 0; i < list.Count; i++) due[i] = list[i].Mode == PressMode.Off ? long.MaxValue : 0;
        int failed = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!License.Allowed()) { Stop("Stopped: " + License.Why()); return; }

                // the bubble whose turn it is: the one waiting longest
                long now = SystemClock.UptimeMillis();
                int pick = -1;
                for (int i = 0; i < list.Count; i++)
                    if (due[i] <= now && (pick < 0 || due[i] < due[pick])) pick = i;
                if (pick < 0)
                {
                    long next = due.Min();
                    if (next == long.MaxValue) { Stop("Done: every bubble was pressed."); return; }
                    await Task.Delay((int)Math.Clamp(next - now, 10, 200), ct);
                    continue;
                }

                var b = list[pick];
                var (w, h) = svc.ScreenSize();
                var (x, y) = layout.At(b, w, h);
                bool ok = await svc.PressAsync(x, y, b.Mode == PressMode.Hold ? HoldMs : TapMs);
                if (ct.IsCancellationRequested) return;
                Pressed?.Invoke(pick);

                long after = SystemClock.UptimeMillis();
                due[pick] = b.Mode switch
                {
                    PressMode.Hold => after,                                     // straight back down, after anything that is due
                    PressMode.Repeat => after + (long)(Math.Max(0.5, b.Every) * 1000),
                    _ => long.MaxValue                                           // Once
                };

                if (ok) { failed = 0; Presses++; if (Presses % 5 == 0) Raise(); }
                else if (++failed >= 12) { Stop("Stopped: the taps keep being cancelled (is the screen off, or is something on top of the game?)."); return; }
                await Task.Delay(GapMs, ct);
            }
        }
        catch (System.OperationCanceledException) { }
        catch (LicenseException ex) { Stop("Stopped: " + ex.Message); }
        catch (Exception ex) { Stop("Stopped: " + ex.Message); }
    }
}
