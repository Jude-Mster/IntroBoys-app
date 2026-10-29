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
        try { StateChanged?.Invoke(); } catch { /* a listener's problem */ }
        // asked from the app before the switch was on: show the floating S now
        if (Prefs.GetBoolean("show_control", false)) Overlay.ShowControl();
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
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
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

    sealed class Result : GestureResultCallback
    {
        readonly TaskCompletionSource<bool> done;
        public Result(TaskCompletionSource<bool> done) { this.done = done; }
        public override void OnCompleted(GestureDescription? gestureDescription) => done.TrySetResult(true);
        public override void OnCancelled(GestureDescription? gestureDescription) => done.TrySetResult(false);
    }

    /// <summary>Show or hide the floating S (also remembered for when the service starts).</summary>
    public static void SetControlVisible(bool show)
    {
        Prefs.Edit()!.PutBoolean("show_control", show)!.Apply();
        var s = Instance;
        if (s == null) return;
        s.Main.Post(() =>
        {
            if (show) s.Overlay?.ShowControl();
            else { Runner.Stop("Stopped."); s.Overlay?.CloseAll(); }
        });
    }
}
