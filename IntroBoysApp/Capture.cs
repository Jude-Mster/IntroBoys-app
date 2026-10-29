using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using Android.Views;

namespace IntroBoys.App;

/// <summary>
/// "See the game": Android's screen capture (the member allows it each time the app starts; Android shows its own notice while it is
/// on). The app keeps only the latest picture, in memory: for the live view on the guild website now, and for reading the screen
/// (boss HP bar, position) once that is set up for the phone. Nothing is saved or sent anywhere else.
/// </summary>
static class Capture
{
    static readonly object gate = new();
    static MediaProjection? projection;
    static VirtualDisplay? display;
    static ImageReader? reader;
    static int capW, capH;

    public static bool On { get { lock (gate) return projection != null; } }
    /// <summary>Raised (any thread) when capture starts or stops.</summary>
    public static event System.Action? Changed;

    const int LongSide = 1280;      // pictures at most 1280 px on the long side: enough to read text, light on the phone

    internal static void Begin(Context c, MediaProjection mp)
    {
        lock (gate)
        {
            StopLocked();
            projection = mp;
            mp.RegisterCallback(new Ended(), new Handler(Looper.MainLooper!));
            MakeDisplayLocked(c);
        }
        AppLog.Add("screen: Android is sharing the screen with the Assistant");
        Raise();
    }

    public static void Stop()
    {
        bool was;
        lock (gate) { was = projection != null; StopLocked(); }
        if (was) { AppLog.Add("screen: sharing stopped"); Raise(); }
    }

    static void StopLocked()
    {
        try { display?.Release(); } catch { /* gone */ }
        try { reader?.Close(); } catch { /* gone */ }
        try { projection?.Stop(); } catch { /* gone */ }
        display = null; reader = null; projection = null;
    }

    static void Raise() { try { Changed?.Invoke(); } catch { /* a listener's problem */ } }

    static (int w, int h, int dpi) Size(Context c)
    {
        var wm = c.GetSystemService(Context.WindowService)!.JavaCast<IWindowManager>()!;
        int w, h;
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) { var b = wm.CurrentWindowMetrics.Bounds; w = b.Width(); h = b.Height(); }
        else
        {
            var p = new Android.Graphics.Point();
#pragma warning disable CA1422, CS0618
            wm.DefaultDisplay!.GetRealSize(p);
#pragma warning restore CA1422, CS0618
            w = p.X; h = p.Y;
        }
        double k = Math.Min(1.0, LongSide / (double)Math.Max(w, h));
        int dpi = (int)((int)c.Resources!.DisplayMetrics!.DensityDpi * k);
        return ((int)(w * k) & ~1, (int)(h * k) & ~1, Math.Max(120, dpi));
    }

    static void MakeDisplayLocked(Context c)
    {
        if (projection == null) return;
        var (w, h, dpi) = Size(c);
        capW = w; capH = h;
        var r = ImageReader.NewInstance(w, h, (ImageFormatType)1 /* RGBA_8888 */, 2);
        if (display == null)
            display = projection.CreateVirtualDisplay("IntroBoys", w, h, dpi, (DisplayFlags)VirtualDisplayFlags.AutoMirror, r.Surface, null, null);
        else
        {
            display.Resize(w, h, dpi);
            display.Surface = r.Surface;
        }
        try { reader?.Close(); } catch { /* gone */ }
        reader = r;
    }

    /// <summary>
    /// The latest picture of the screen (null when capture is off or nothing has been drawn yet). It is in capture pixels, a smaller
    /// copy of the screen; <paramref name="scale"/> is capture pixels per screen pixel. The caller owns (and recycles) the bitmap.
    /// </summary>
    public static Bitmap? Latest(Context c, out double scale)
    {
        scale = 1;
        lock (gate)
        {
            if (projection == null || reader == null) return null;
            // the phone was turned: capture the new way round
            var (w, h, _) = Size(c);
            if ((w > h) != (capW > capH)) { MakeDisplayLocked(c); return null; }
            Image? img = null;
            try
            {
                img = reader.AcquireLatestImage();
                if (img == null) return null;
                var plane = img.GetPlanes()![0];
                int pixelStride = plane.PixelStride, rowStride = plane.RowStride;
                int padW = rowStride / pixelStride;
                var full = Bitmap.CreateBitmap(padW, img.Height, Bitmap.Config.Argb8888!)!;
                full.CopyPixelsFromBuffer(plane.Buffer!);
                var bmp = padW == img.Width ? full : Bitmap.CreateBitmap(full, 0, 0, img.Width, img.Height)!;
                if (!ReferenceEquals(bmp, full)) full.Recycle();
                scale = capW / (double)Math.Max(1, ScreenW(c));
                return bmp;
            }
            catch { return null; }
            finally { img?.Close(); }
        }
    }

    static int ScreenW(Context c)
    {
        var wm = c.GetSystemService(Context.WindowService)!.JavaCast<IWindowManager>()!;
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) return wm.CurrentWindowMetrics.Bounds.Width();
        var p = new Android.Graphics.Point();
#pragma warning disable CA1422, CS0618
        wm.DefaultDisplay!.GetRealSize(p);
#pragma warning restore CA1422, CS0618
        return p.X;
    }

    /// <summary>A small JPEG of the screen for the live view (base64), or null with the reason.</summary>
    public static (string? b64, string why) Jpeg(Context c, int width = 640, int quality = 55)
    {
        if (!On) return (null, "the phone is not sharing its screen with the Assistant (tap Allow on \"See the game\" in the app)");
        var bmp = Latest(c, out _);
        if (bmp == null) return (null, "no picture from the screen yet");
        try
        {
            int h = (int)(bmp.Height * (width / (double)bmp.Width));
            using var small = Bitmap.CreateScaledBitmap(bmp, width, Math.Max(1, h), true)!;
            using var ms = new MemoryStream();
            small.Compress(Bitmap.CompressFormat.Jpeg!, quality, ms);
            return (Convert.ToBase64String(ms.ToArray()), "");
        }
        catch (Exception ex) { return (null, "the picture failed: " + ex.Message); }
        finally { bmp.Recycle(); }
    }

    sealed class Ended : MediaProjection.Callback
    {
        public override void OnStop()
        {
            lock (gate) { display?.Release(); reader?.Close(); display = null; reader = null; projection = null; }
            AppLog.Add("screen: Android stopped the screen sharing");
            Raise();
            CaptureService.StopSelfNow();
        }
    }
}

/// <summary>
/// The foreground service Android requires while the screen is shared (it shows a small notification). Started by
/// <see cref="CaptureActivity"/> with the member's permission.
/// </summary>
[Service(Name = "com.blacklistintroboys.guild.CaptureService", Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public class CaptureService : Service
{
    const string Channel = "capture";
    static CaptureService? running;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        running = this;
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        nm.CreateNotificationChannel(new NotificationChannel(Channel, "Screen sharing for the Assistant", NotificationImportance.Low));
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(AssistantActivity)), PendingIntentFlags.Immutable)!;
        var n = new Notification.Builder(this, Channel)
            .SetContentTitle("IntroBoys Assistant sees the game")!
            .SetContentText("For the live view and screen reading. Stop it in the app.")!
            .SetSmallIcon(Resource.Drawable.logo_s)!
            .SetContentIntent(open)!
            .SetOngoing(true)!
            .Build()!;
        StartForeground(41, n, ForegroundService.TypeMediaProjection);

        int code = intent?.GetIntExtra("code", 0) ?? 0;
#pragma warning disable CA1422, CS0618
        var data = intent?.GetParcelableExtra("data") as Intent;
#pragma warning restore CA1422, CS0618
        if (data == null) { StopSelfNow(); return StartCommandResult.NotSticky; }
        try
        {
            var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            var mp = mpm.GetMediaProjection(code, data);
            if (mp == null) { StopSelfNow(); return StartCommandResult.NotSticky; }
            Capture.Begin(this, mp);
        }
        catch (Exception ex) { AppLog.Add("screen: could not start (" + ex.Message + ")"); StopSelfNow(); }
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        if (running == this) running = null;
        Capture.Stop();
        base.OnDestroy();
    }

    internal static void StopSelfNow()
    {
        var s = running;
        if (s == null) return;
        try { s.StopForeground(StopForegroundFlags.Remove); } catch { /* not foreground */ }
        s.StopSelf();
    }

    public static void StopSharing(Context c)
    {
        Capture.Stop();
        StopSelfNow();
    }
}

/// <summary>Asks Android for the screen (its own "start sharing" dialog), then hands the answer to <see cref="CaptureService"/>.</summary>
[Activity(Name = "com.blacklistintroboys.guild.CaptureActivity", Exported = false, Theme = "@android:style/Theme.Translucent.NoTitleBar",
          ExcludeFromRecents = true, TaskAffinity = "com.blacklistintroboys.guild.capture", LaunchMode = LaunchMode.SingleTop)]
public class CaptureActivity : Activity
{
    const int Ask = 7;

    public static void Request(Context c)
    {
        var i = new Intent(c, typeof(CaptureActivity));
        i.AddFlags(ActivityFlags.NewTask);
        c.StartActivity(i);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (savedInstanceState != null) return;
        var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        StartActivityForResult(mpm.CreateScreenCaptureIntent(), Ask);
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == Ask && resultCode == Result.Ok && data != null)
        {
            var i = new Intent(this, typeof(CaptureService));
            i.PutExtra("code", (int)resultCode);
            i.PutExtra("data", data);
            StartForegroundService(i);
        }
        else AppLog.Add("screen: sharing was not allowed");
        Finish();
    }
}
