namespace IntroBoys.App;

/// <summary>
/// 1.5.2: the one-tap check for a new phone (the floating panel's "Check phone"): can the Assistant see the screen, read the mini-map
/// numbers and the map name, are the bubbles a run needs placed, does the move pad move the character (and which way the camera looks),
/// and does a drag on the camera area turn the view. Each line gets a ✓ or ✗ with what to do; nothing else is changed except what
/// the run would learn anyway (push length, drag size). The character takes one short step and steps back; the view turns and turns back.
/// </summary>
static class PhoneCheck
{
    public static bool Running { get; private set; }

    public static async Task<string> RunAsync(TapService svc, CancellationToken ct)
    {
        if (Running) return "A check is already running.";
        Running = true;
        var lines = new List<string>();
        void Ok(string t) { lines.Add("✓ " + t); AppLog.Add("check: ✓ " + t); }
        void Bad(string t) { lines.Add("✗ " + t); AppLog.Add("check: ✗ " + t); }
        try
        {
            AppLog.Add("check: " + AppLog.Device());
            var layout = BubbleLayout.Load();

            // 1. seeing the screen
            if (!Capture.CanSee)
            {
                AppLog.Add("check: asking Android to share the screen (Android 9 / 10)");
                CaptureActivity.Request(Android.App.Application.Context);
                var until = DateTime.UtcNow.AddSeconds(40);
                while (!Capture.CanSee && DateTime.UtcNow < until) await Task.Delay(400, ct);
            }
            if (!Capture.CanSee) { Bad("can't see the screen: allow the screen sharing (Live record) - nothing else can be checked"); return string.Join("\n", lines); }
            Ok(OperatingSystem.IsAndroidVersionAtLeast(30) && !Capture.On ? "sees the screen (through the taps permission)" : "sees the screen (Live record)");

            // 2. the mini-map numbers and the map name
            Position p = default;
            for (int i = 0; i < 6 && !p.Ok; i++) { p = await Screen.PositionAsync(); if (!p.Ok) await Task.Delay(500, ct); }
            if (p.Ok) Ok($"reads the position {p} · where: {Screen.PlaceText}");
            else Bad("can't read the numbers under the mini-map: open the mini-map (top right) so its title and numbers show");

            // 3. the bubbles
            var need = new (BubbleKind k, string what)[]
            {
                (BubbleKind.Attack, "Attack"), (BubbleKind.Target, "Target"), (BubbleKind.MovePad, "Move pad"), (BubbleKind.Camera, "Camera area"),
                (BubbleKind.BossBar, "Boss HP bar box"), (BubbleKind.Teleport, "Teleport (scroll)"), (BubbleKind.Return, "Return (scroll)"),
                (BubbleKind.AutoLoot, "Auto loot"), (BubbleKind.Buff, "Buff")
            };
            var missing = need.Where(n => layout.Find(n.k) == null).Select(n => n.what).ToList();
            if (missing.Count == 0) Ok("all the bubbles a run uses are placed");
            else Bad("not placed yet: " + string.Join(", ", missing) + " (Bubbles)");
            if (layout.Find(BubbleKind.BossBar) != null)
            {
                double hp = await Screen.BossHpAsync(layout);
                Ok(hp > 0 ? $"boss bar box placed (reads {Math.Round(hp * 100)}% now)" : "boss bar box placed (target a monster or boss to see it read)");
            }

            // 4. the move pad and 5. the camera
            if (p.Ok && layout.Find(BubbleKind.MovePad) != null)
            {
                var nav = new Nav(svc, layout);
                if (await nav.CalibrateAsync(ct)) Ok($"the move pad moves the character (the camera looks {(int)nav.Forward}° on the map){(nav.Slow ? " · slow phone: longer pushes" : "")}");
                else Bad("the move pad didn't move the character: put the Move pad bubble on the game's stick, stand on open ground");
                if (layout.Find(BubbleKind.Camera) != null && nav.Known)
                {
                    var turned = await nav.TestTurnAsync(45, ct);
                    if (turned == null) Bad("couldn't measure a camera turn");
                    else if (Math.Abs(turned.Value) < 5) Bad("a drag on the camera area didn't turn the view: put the Camera area on open ground (no buttons under it)");
                    else Ok($"a drag on the camera area turns the view (asked 45°, turned {(int)turned.Value}°; the run corrects the rest)");
                }
            }
            bool all = lines.All(l => l.StartsWith("✓"));
            lines.Insert(0, all ? "This phone is ready for runs." : "Fix the ✗ lines, then check again.");
            return string.Join("\n", lines);
        }
        catch (System.OperationCanceledException) { return "Check stopped."; }
        catch (Exception ex) { return "Check stopped: " + ex.Message; }
        finally { Running = false; }
    }
}
