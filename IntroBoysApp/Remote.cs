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
            // 1.6.0: a report every 8 s (was 20: the site counts an app offline after 25 s without one, so one slow or lost report made it
            // flicker offline); a failed report is tried again after 4 s; commands run on their own (a long one - the phone check - no
            // longer holds up the reports); the report goes out whenever the app is signed in, not only while the license check is fresh
            int wait = 8000;
            try
            {
                if (License.State != LicenseState.SignedOut)
                {
                    var body = new Dictionary<string, object?> { ["state"] = State(), ["log"] = AppLog.Last(20) };
                    List<Dictionary<string, object?>> done;
                    lock (gate) done = results.ToList();
                    if (done.Count > 0) body["results"] = done;
                    double now = Environment.TickCount64 / 1000.0;
                    if (Watching && now - lastPic >= 4)
                    {
                        lastPic = now;
                        var (pic, why) = await Capture.JpegAsync(c);
                        if (pic != null) body["picture"] = pic; else body["picture_error"] = why;
                    }
                    var r = await License.CallSignedInAsync("remotePush", body, CancellationToken.None);
                    if (r is { } root)
                    {
                        lock (gate) foreach (var x in done) results.Remove(x);
                        Watching = root.TryGetProperty("watching", out var w) && w.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
                            foreach (var cmd in cmds.EnumerateArray()) { var one = cmd.Clone(); _ = Task.Run(() => Handle(one)); }
                        wait = Watching ? 3000 : 8000;
                    }
                    else wait = 4000;
                }
                else Watching = false;
            }
            catch { wait = 4000; }
            await Task.Delay(wait);
        }
    }

    /// <summary>What the live view shows (the same fields as the PC app, plus platform).</summary>
    static Dictionary<string, object?> State() => new()
    {
        ["platform"] = "android",
        ["running"] = Runner.Running,
        ["state"] = !Runner.Running ? "READY" : Runner.RunningMode == RunMode.Attack ? "AUTO ATTACK · LIVE" : (Runner.Phase.Length > 0 ? Runner.Phase : "RUNNING"),
        ["boss"] = Runner.Running && Runner.Phase.StartsWith("fighting") ? Runner.Phase.Substring(9) : "",
        ["hp"] = Runner.Running ? Math.Round(Screen.LastHp, 3) : -1,
        ["position"] = Screen.LastPos.ToString(),
        ["map"] = Screen.LastPlace == Place.Unknown ? Screen.LastMap : Screen.PlaceText,
        ["place"] = Screen.LastPlace.ToString().ToLowerInvariant(),
        ["war_break"] = Runner.WarBreak,
        ["on_war_break"] = Runner.OnWarBreak,
        ["war_next"] = new DateTimeOffset(WarClock.NextBreak(DateTime.UtcNow)).ToUnixTimeMilliseconds(),
        ["war_back"] = new DateTimeOffset(WarClock.BackAt(DateTime.UtcNow)).ToUnixTimeMilliseconds(),
        ["dungeon"] = Runner.Mode switch { RunMode.Den => "den", RunMode.Labyrinth => "labyrinth", _ => "attack" },
        ["runs"] = Runner.Runs,
        ["game"] = Capture.CanSee,
        ["cycles"] = Runner.Cycles,
        ["kills"] = Runner.Kills,
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
                // 1.5.2: the website may also set the runs and the war break with Start
                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("runs", out var rr) && rr.ValueKind == JsonValueKind.Number) Runner.Runs = rr.GetInt32();
                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("war_break", out var wb) && (wb.ValueKind == JsonValueKind.True || wb.ValueKind == JsonValueKind.False)) Runner.WarBreak = wb.GetBoolean();
                if (mode != RunMode.Attack) Runner.Mode = mode;
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
            // 1.5.2: more from the website
            case "set_mode":
            {
                var aa = c.TryGetProperty("args", out var x1) ? x1 : default;
                string dm = aa.ValueKind == JsonValueKind.Object && aa.TryGetProperty("dungeon", out var d1) ? d1.GetString() ?? "" : "";
                if (Runner.Running) { ok = false; msg = "stop the run first"; break; }
                Runner.Mode = dm == "den" ? RunMode.Den : dm == "attack" ? RunMode.Attack : RunMode.Labyrinth;
                ok = true; msg = "mode: " + Runner.ModeName(Runner.Mode);
                AppLog.Add("website: " + msg);
                break;
            }
            case "set_runs":
            {
                var aa = c.TryGetProperty("args", out var x2) ? x2 : default;
                int n = aa.ValueKind == JsonValueKind.Object && aa.TryGetProperty("runs", out var r1) && r1.ValueKind == JsonValueKind.Number ? r1.GetInt32() : 0;
                Runner.Runs = n;
                ok = true; msg = "runs in a row: " + (n == 0 ? "until stopped" : n.ToString());
                AppLog.Add("website: " + msg);
                break;
            }
            case "war_break":
            {
                var aa = c.TryGetProperty("args", out var x3) ? x3 : default;
                bool on = aa.ValueKind == JsonValueKind.Object && aa.TryGetProperty("on", out var o1) && o1.ValueKind == JsonValueKind.True;
                Runner.WarBreak = on;
                ok = true; msg = "war break " + (on ? "on" : "off") + (Runner.Running && on ? " (from the next run; at :27 it leaves the run that is going)" : "");
                break;
            }
            case "auto_hunt":
            {
                var s3 = TapService.Instance;
                var hb = BubbleLayout.Load().Find(BubbleKind.AutoHunt);
                if (s3 == null || hb == null) { ok = false; msg = hb == null ? "no Auto hunt bubble placed" : "the tap permission is off"; break; }
                if (Runner.Running) { ok = false; msg = "stop the run first"; break; }
                var (w3, h3) = s3.ScreenSize();
                var (x3b, y3b) = BubbleLayout.Load().At(hb, w3, h3);
                ok = await s3.PressAsync(x3b, y3b, 80);
                msg = ok ? "auto hunt pressed" : "the tap did not go through";
                AppLog.Add("website: auto hunt pressed");
                break;
            }
            case "check_phone":
            {
                var s4 = TapService.Instance;
                if (s4 == null) { ok = false; msg = "the tap permission is off"; break; }
                if (Runner.Running) { ok = false; msg = "stop the run first"; break; }
                AppLog.Add("website: check phone");
                string res = await PhoneCheck.RunAsync(s4, CancellationToken.None);
                ok = res.StartsWith("This phone is ready");
                msg = res.Replace("\n", " · ");
                if (msg.Length > 190) msg = msg.Substring(0, 190) + "…";
                break;
            }
            default:
                ok = false; msg = "this app version doesn't know \"" + cmd + "\"";
                break;
        }
        if (id.Length > 0) lock (gate) results.Add(new Dictionary<string, object?> { ["id"] = id, ["ok"] = ok, ["message"] = msg });
    }
}
