using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Views.Accessibility;

namespace IntroBoys.App;

/// <summary>
/// The only part of the app that touches the game: Android's accessibility "gestures" press the screen where the member's bubbles are,
/// like a finger would. It also hosts the floating S button and the bubble editor (accessibility overlays need no extra permission).
/// It reads no window content and nothing from other apps. Every press goes through <see cref="PressAsync"/>, which refuses without
/// a valid license ticket.
/// </summary>
[Service(Name = "com.blacklistintroboys.guild.TapService", Label = "@string/tap_service_label",
         Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/tap_service")]
public class TapService : AccessibilityService
{
    /// <summary>The running service, or null while the member has not switched it on.</summary>
    public static TapService? Instance { get; private set; }
    /// <summary>Raised on the main thread when the service is switched on or off.</summary>
    public static event System.Action? StateChanged;

    public Handler Main { get; } = new(Looper.MainLooper!);
    internal IWindowManager Wm { get; private set; } = null!;
    internal Overlay? Overlay { get; private set; }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Wm = GetSystemService(WindowService)!.JavaCast<IWindowManager>()!;
        Instance = this;
        Overlay = new Overlay(this);
        License.Begin();
        Remote.Begin();
        AppLog.CatchCrashes();
        try { StateChanged?.Invoke(); } catch { /* a listener's problem */ }
        // Nothing shows by itself: not when the phone starts, not when the app opens. The floating S appears only when the
        // member taps "Show the floating S" in the app.
        ControlWanted = false;
        License.Changed += OnLicense;
        screen = new ScreenWatch(this);
        var f = new IntentFilter(Intent.ActionScreenOff);
        f.AddAction(Intent.ActionUserPresent);
        RegisterReceiver(screen, f);
    }

    /// <summary>The license ended (ran out, switched off, signed out): the floating S goes away.</summary>
    void OnLicense()
    {
        if (ControlWanted && License.State is LicenseState.SignedOut or LicenseState.NotMember or LicenseState.NoLicense
                or LicenseState.Expired or LicenseState.Revoked or LicenseState.PhoneOff or LicenseState.OtherDevice)
            Main.Post(() => SetControlVisible(false));
    }

    /// <summary>True while the member wants the floating S (this session only: never remembered across a restart).</summary>
    public static bool ControlWanted { get; private set; }
    ScreenWatch? screen;

    /// <summary>
    /// Screen off: everything over the game goes away at once (auto attack stops, the editor closes without saving), so nothing
    /// is ever left over the lock screen. Unlocked again: the floating S comes back if it was showing.
    /// </summary>
    sealed class ScreenWatch : BroadcastReceiver
    {
        readonly TapService s;
        public ScreenWatch(TapService s) { this.s = s; }
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == Intent.ActionScreenOff)
            {
                Runner.Stop("Stopped: the screen turned off.");
                s.Overlay?.CloseAll();
            }
            else if (intent?.Action == Intent.ActionUserPresent && ControlWanted) s.Overlay?.ShowControl();
        }
    }

    public override bool OnUnbind(Intent? intent)
    {
        Cleanup();
        return base.OnUnbind(intent);
    }

    public override void OnDestroy()
    {
        Cleanup();
        base.OnDestroy();
    }

    void Cleanup()
    {
        if (Instance != this) return;
        Runner.Stop("The tap permission was switched off.");
        if (screen != null) { try { UnregisterReceiver(screen); } catch { /* not registered */ } screen = null; }
        License.Changed -= OnLicense;
        ControlWanted = false;
        try { Overlay?.CloseAll(); } catch { /* already gone */ }
        Overlay = null;
        Instance = null;
        try { StateChanged?.Invoke(); } catch { /* a listener's problem */ }
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e) { }
    public override void OnInterrupt() { }

    internal static ISharedPreferences Prefs => Android.App.Application.Context.GetSharedPreferences("app", FileCreationMode.Private)!;

    /// <summary>The whole screen in pixels as it is turned right now.</summary>
    public (int w, int h) ScreenSize()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var b = Wm.CurrentWindowMetrics.Bounds;
            return (b.Width(), b.Height());
        }
        var p = new Android.Graphics.Point();
#pragma warning disable CA1422, CS0618
        Wm.DefaultDisplay!.GetRealSize(p);
#pragma warning restore CA1422, CS0618
        return (p.X, p.Y);
    }

    /// <summary>
    /// Presses the screen at (x, y) for <paramref name="ms"/> milliseconds. True when Android finished the press, false when it was
    /// cancelled (for example the member touched the screen) or refused. Throws <see cref="LicenseException"/> without a valid license.
    /// </summary>
    long coveredSaid = -100000;

    public Task<bool> PressAsync(float x, float y, long ms)
    {
        License.Require();
        var (w, h) = ScreenSize();
        x = Math.Clamp(x, 0, Math.Max(0, w - 1));
        y = Math.Clamp(y, 0, Math.Max(0, h - 1));
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Main.Post(() =>
        {
            try
            {
                // 1.4.0: never press the Assistant's own floating S / panel (that pressed its Stop button)
                if (Overlay?.Covers(x, y) == true)
                {
                    if (System.Environment.TickCount64 - coveredSaid > 30000) { coveredSaid = System.Environment.TickCount64; AppLog.Add("a bubble is under the floating S / its panel: not pressed (move the S, or tap it to fold the panel)"); }
                    done.TrySetResult(true);
                    return;
                }
                var path = new Android.Graphics.Path();
                path.MoveTo(x, y);
                var stroke = new GestureDescription.StrokeDescription(path, 0, Math.Max(1, ms));
                var gesture = new GestureDescription.Builder().AddStroke(stroke)!.Build()!;
                if (!DispatchGesture(gesture, new Result(done), null)) done.TrySetResult(false);
            }
            catch { done.TrySetResult(false); }
        });
        // Android always answers, but never wait forever
        _ = Task.Delay((int)ms + 2000).ContinueWith(_ => done.TrySetResult(false));
        return done.Task;
    }

    /// <summary>Drags from one point to another over <paramref name="ms"/> milliseconds (the camera, the move pad).</summary>
    public Task<bool> SwipeAsync(float x1, float y1, float x2, float y2, long ms) =>
        Strokes(ms, (x1, y1, x2, y2));

    /// <summary>Two fingers from <paramref name="from"/> apart to <paramref name="to"/> apart around a point: a pinch (zoom).</summary>
    public Task<bool> PinchAsync(float cx, float cy, float from, float to, long ms) =>
        Strokes(ms, (cx - from / 2, cy, cx - to / 2, cy), (cx + from / 2, cy, cx + to / 2, cy));

    Task<bool> Strokes(long ms, params (float x1, float y1, float x2, float y2)[] lines)
    {
        License.Require();
        var (w, h) = ScreenSize();
        float X(float v) => Math.Clamp(v, 0, Math.Max(0, w - 1));
        float Y(float v) => Math.Clamp(v, 0, Math.Max(0, h - 1));
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Main.Post(() =>
        {
            try
            {
                var b = new GestureDescription.Builder();
                foreach (var l in lines)
                {
                    var path = new Android.Graphics.Path();
                    path.MoveTo(X(l.x1), Y(l.y1));
                    path.LineTo(X(l.x2), Y(l.y2));
                    b.AddStroke(new GestureDescription.StrokeDescription(path, 0, Math.Max(1, ms)));
                }
                if (!DispatchGesture(b.Build()!, new Result(done), null)) done.TrySetResult(false);
            }
            catch { done.TrySetResult(false); }
        });
        _ = Task.Delay((int)ms + 2000).ContinueWith(_ => done.TrySetResult(false));
        return done.Task;
    }

    /// <summary>
    /// The camera fix after a teleport: each step on the camera area, in order. Returns false (and says why) when there is no camera
    /// area or nothing to do.
    /// </summary>
    public async Task<bool> CameraFixAsync(BubbleLayout layout, CancellationToken ct = default)
    {
        var cam = layout.Find(BubbleKind.Camera);
        if (cam == null) { AppLog.Add("camera fix: no camera area placed (Bubbles, + Camera area)"); return false; }
        if (layout.CamFix.Count == 0) { AppLog.Add("camera fix: no steps yet (Bubbles, tap the camera area)"); return false; }
        var (sw, sh) = ScreenSize();
        var (cx, cy) = layout.At(cam, sw, sh);
        var (aw, ah) = layout.SizeAt(cam, sw, sh);
        AppLog.Add("camera fix: " + string.Join(", ", layout.CamFix.Select(x => x.Describe())));
        foreach (var st in layout.CamFix)
        {
            ct.ThrowIfCancellationRequested();
            float dx = (float)(aw / 2 * st.Amount), dy = (float)(ah / 2 * st.Amount);
            switch (st.Op)
            {
                case CamOp.Left: await SwipeAsync(cx + dx, cy, cx - dx, cy, 450); break;
                case CamOp.Right: await SwipeAsync(cx - dx, cy, cx + dx, cy, 450); break;
                case CamOp.Up: await SwipeAsync(cx, cy + dy, cx, cy - dy, 450); break;
                case CamOp.Down: await SwipeAsync(cx, cy - dy, cx, cy + dy, 450); break;
                case CamOp.ZoomIn: await PinchAsync(cx, cy, Math.Min(aw, ah) * 0.15f, (float)(Math.Min(aw, ah) * (0.15 + 0.7 * st.Amount)), 500); break;
                case CamOp.ZoomOut: await PinchAsync(cx, cy, (float)(Math.Min(aw, ah) * (0.15 + 0.7 * st.Amount)), Math.Min(aw, ah) * 0.15f, 500); break;
            }
            await Task.Delay(250, ct);
        }
        return true;
    }

    /// <summary>Sends a gesture built by the caller (the move pad's held stick, Nav.cs). False when it was cancelled or refused.</summary>
    /// <summary>1.5.6: folds the floating panel if it lies over (x, y), before a touch of the run starts there.</summary>
    public void Uncover(float x, float y) => Main.Post(() => Overlay?.FoldIfCovers(x, y));

    public Task<bool> DispatchAsync(Func<GestureDescription> build, long ms)
    {
        License.Require();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Main.Post(() =>
        {
            try { if (!DispatchGesture(build(), new Result(done), null)) done.TrySetResult(false); }
            catch { done.TrySetResult(false); }
        });
        _ = Task.Delay((int)ms + 2000).ContinueWith(_ => done.TrySetResult(false));
        return done.Task;
    }

    // ------------------------------------------------------------------ looking at the screen (Android 11+): no screen sharing needed
    long shotAt;
    Frame? shot;
    readonly SemaphoreSlim shooting = new(1, 1);

    /// <summary>
    /// A picture of the whole screen through the accessibility service (Android allows about 3 a second, so a picture under 0.35 s old is
    /// given again). Null on Android 9 and 10 or when Android refuses.
    /// </summary>
    internal async Task<Frame?> ShotAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return null;
        await shooting.WaitAsync();
        try
        {
            if (shot != null && System.Environment.TickCount64 - shotAt < 120) return shot;
            long wait = 340 - (System.Environment.TickCount64 - shotAt);
            if (wait > 0) await Task.Delay((int)wait);
            Frame? f = null;
            for (int attempt = 0; attempt < 3 && f == null; attempt++)
            {
                if (attempt > 0) await Task.Delay(150);
                var done = new TaskCompletionSource<Frame?>(TaskCreationOptions.RunContinuationsAsynchronously);
                Main.Post(() =>
                {
                    // 1.5.7: the picture is copied on its own thread (it was the main thread: copying a whole screen 3 times a second held up
                    // the move pad's touch pieces, the touch broke and the character ran in stops and starts)
                    try { TakeScreenshot(0, ShotThread, new Shot(done)); }
                    catch { done.TrySetResult(null); }
                });
                f = await Task.WhenAny(done.Task, Task.Delay(2500)) == done.Task ? await done.Task : null;
            }
            if (f != null) { shot = f; shotAt = System.Environment.TickCount64; }
            return f ?? shot;
        }
        finally { shooting.Release(); }
    }

    static Java.Nio.ByteBuffer? copyBuf;
    static readonly Java.Util.Concurrent.IExecutorService ShotThread = Java.Util.Concurrent.Executors.NewSingleThreadExecutor()!;

    sealed class Shot : Java.Lang.Object, ITakeScreenshotCallback
    {
        readonly TaskCompletionSource<Frame?> done;
        public Shot(TaskCompletionSource<Frame?> done) { this.done = done; }
        public void OnFailure(int errorCode) => done.TrySetResult(null);
        public void OnSuccess(ScreenshotResult screenshot)
        {
            try
            {
                // 1.3.4: every native buffer is given back at once (they are big: a whole screen each), and one copy buffer is reused,
                // so memory stays flat during a long fight
                var hb = screenshot.HardwareBuffer;
                Android.Graphics.Bitmap? hw = null, bmp = null;
                try
                {
                    hw = Android.Graphics.Bitmap.WrapHardwareBuffer(hb, screenshot.ColorSpace);
                    if (hw == null) { done.TrySetResult(null); return; }
                    bmp = hw.Copy(Android.Graphics.Bitmap.Config.Argb8888!, false)!;
                    int w = bmp.Width, h = bmp.Height, n = w * h * 4;
                    if (copyBuf == null || copyBuf.Capacity() != n) copyBuf = Java.Nio.ByteBuffer.AllocateDirect(n)!;
                    copyBuf.Rewind();
                    bmp.CopyPixelsToBuffer(copyBuf);
                    copyBuf.Rewind();
                    var bytes = new byte[n];
                    copyBuf.Get(bytes);
                    done.TrySetResult(new Frame(bytes, w, h, w * 4, System.Environment.TickCount64));
                }
                finally
                {
                    try { bmp?.Recycle(); bmp?.Dispose(); } catch { }
                    try { hw?.Recycle(); hw?.Dispose(); } catch { }
                    try { hb.Close(); hb.Dispose(); } catch { }
                }
            }
            catch { done.TrySetResult(null); }
        }
    }

    sealed class Result : GestureResultCallback
    {
        readonly TaskCompletionSource<bool> done;
        public Result(TaskCompletionSource<bool> done) { this.done = done; }
        public override void OnCompleted(GestureDescription? gestureDescription) => done.TrySetResult(true);
        public override void OnCancelled(GestureDescription? gestureDescription) => done.TrySetResult(false);
    }

    /// <summary>Show or hide the floating S. Needs the tap permission; never remembered across a restart.</summary>
    public static void SetControlVisible(bool show)
    {
        if (show && !License.Allowed()) show = false;     // only with an active license
        var s = Instance;
        ControlWanted = show && s != null;
        if (s == null) return;
        s.Main.Post(() =>
        {
            if (show) s.Overlay?.ShowControl();
            else { Runner.Stop("Stopped."); s.Overlay?.CloseAll(); }
        });
    }
}
