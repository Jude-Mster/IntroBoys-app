using System.Text.Json;

namespace IntroBoys.App;

/// <summary>
/// The guild website's live view and remote control, like the PC Assistant: every 20 seconds (every 3 while someone watches on the
/// site) the app reports what it is doing, its live log and, while watched, a small picture of the screen; the site answers with
/// commands from a fixed list (start, stop, return to town).
/// </summary>
static class Remote
{
    static int started;
    static readonly object gate = new();
    static readonly List<Dictionary<string, object?>> results = new();
    public static bool Watching { get; private set; }

    public static void Begin()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        _ = Task.Run(LoopAsync);
    }

    static async Task LoopAsync()
    {
        var c = Android.App.Application.Context;
        double lastPic = 0;
        while (true)
        {
            int wait = 20000;
            try
            {
                if (License.State == LicenseState.Licensed)
                {
                    var body = new Dictionary<string, object?> { ["state"] = State(), ["log"] = AppLog.Last(20) };
                    List<Dictionary<string, object?>> done;
                    lock (gate) done = results.ToList();
                    if (done.Count > 0) body["results"] = done;
                    double now = Environment.TickCount64 / 1000.0;
                    if (Watching && now - lastPic >= 4)
                    {
                        lastPic = now;
                        var (pic, why) = Capture.Jpeg(c);
                        if (pic != null) body["picture"] = pic; else body["picture_error"] = why;
                    }
                    var r = await License.CallSignedInAsync("remotePush", body, CancellationToken.None);
                    if (r is { } root)
                    {
                        lock (gate) foreach (var x in done) results.Remove(x);
                        Watching = root.TryGetProperty("watching", out var w) && w.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
                            foreach (var cmd in cmds.EnumerateArray()) await Handle(cmd);
                    }
                    wait = Watching ? 3000 : 20000;
                }
                else Watching = false;
            }
            catch { wait = 15000; }
            await Task.Delay(wait);
        }
    }

    /// <summary>What the live view shows (the same fields as the PC app, plus platform).</summary>
    static Dictionary<string, object?> State() => new()
    {
        ["platform"] = "android",
        ["running"] = Runner.Running,
        ["state"] = Runner.Running ? Runner.ModeName(RunMode.Attack).ToUpperInvariant() + " · LIVE" : "READY",
        ["boss"] = "",
        ["hp"] = -1,
        ["dungeon"] = Runner.Mode switch { RunMode.Den => "den", RunMode.Labyrinth => "labyrinth", _ => "attack" },
        ["runs"] = Runner.Runs,
        ["game"] = Capture.On,
        ["cycles"] = 0,
        ["kills"] = 0,
        ["presses"] = Runner.Presses,
        ["total_labyrinth"] = Runner.TotalLabyrinth,
        ["total_den"] = Runner.TotalDen,
        ["time_left"] = License.TimeLeftText(),
        ["version"] = LicenseConfig.AppVersion,
        ["locked"] = false
    };

    static async Task Handle(JsonElement c)
    {
        string id = c.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
        string cmd = c.TryGetProperty("cmd", out var k) ? k.GetString() ?? "" : "";
        bool ok; string msg;
        switch (cmd)
        {
            case "start":
                var a = c.TryGetProperty("args", out var args) ? args : default;
                string d = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("dungeon", out var dd) ? dd.GetString() ?? "" : "";
                var mode = d == "den" ? RunMode.Den : d == "labyrinth" ? RunMode.Labyrinth : RunMode.Attack;
                string? why = null;
                var tcs = new TaskCompletionSource();
                var svc = TapService.Instance;
                if (svc == null) why = "the tap permission is off on the phone";
                else svc.Main.Post(() => { why = Runner.Start(mode); tcs.TrySetResult(); });
                if (svc != null) await tcs.Task;
                ok = why == null; msg = ok ? "started from the website" : why!;
                AppLog.Add("website: start " + Runner.ModeName(mode) + (ok ? "" : " - " + why));
                break;
            case "stop":
                Runner.Stop("Stopped from the website.");
                ok = true; msg = "stopped";
                break;
            case "return_town":
                var s2 = TapService.Instance;
                var ret = BubbleLayout.Load().Find(BubbleKind.Return);
                if (s2 == null || ret == null) { ok = false; msg = ret == null ? "no Return bubble placed" : "the tap permission is off"; break; }
                Runner.Stop("Returning to town (website).");
                var (w, h) = s2.ScreenSize();
                var (x, y) = BubbleLayout.Load().At(ret, w, h);
                ok = await s2.PressAsync(x, y, 80);
                msg = ok ? "return scroll pressed" : "the tap did not go through";
                AppLog.Add("website: return to town" + (ok ? "" : " failed"));
                break;
            default:
                ok = false; msg = "this app version doesn't know \"" + cmd + "\"";
                break;
        }
        if (id.Length > 0) lock (gate) results.Add(new Dictionary<string, object?> { ["id"] = id, ["ok"] = ok, ["message"] = msg });
    }
}
