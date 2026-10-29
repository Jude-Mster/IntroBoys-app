namespace IntroBoys.App;

/// <summary>
/// The live log: what the Assistant just did, shown in the floating panel, the app and the guild website's live view. Also written to
/// a file in the app's own storage, so "Share the log" still has it after the app was closed or crashed.
/// </summary>
static class AppLog
{
    static readonly object gate = new();
    static readonly List<string> lines = new();
    const int Keep = 500;
    static bool loaded;

    /// <summary>Raised (any thread) after a line is added.</summary>
    public static event Action? Changed;

    static string FilePath => Path.Combine(Android.App.Application.Context.FilesDir!.AbsolutePath, "assistant-log.txt");

    static void LoadLocked()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (File.Exists(FilePath))
            {
                var old = File.ReadAllLines(FilePath);
                lines.AddRange(old.Skip(Math.Max(0, old.Length - Keep)));
            }
        }
        catch { /* no old log */ }
    }

    public static void Add(string text)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + " " + text;
        lock (gate)
        {
            LoadLocked();
            lines.Add(line);
            if (lines.Count > Keep) lines.RemoveRange(0, lines.Count - Keep);
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 400_000) File.WriteAllLines(FilePath, lines);      // keep the file small
                else File.AppendAllText(FilePath, line + "\n");
            }
            catch { /* the log on screen still works */ }
        }
        try { Changed?.Invoke(); } catch { /* a listener's problem */ }
    }

    /// <summary>Everything kept (the last 500 lines), for "Share the log".</summary>
    public static string All() { lock (gate) { LoadLocked(); return string.Join("\n", lines); } }

    /// <summary>The last <paramref name="n"/> lines, oldest first.</summary>
    public static string[] Last(int n)
    {
        lock (gate) { LoadLocked(); return lines.Skip(Math.Max(0, lines.Count - n)).ToArray(); }
    }

    /// <summary>1.3.8: the phone, its Android and its screen (for logs from different phones).</summary>
    public static string Device()
    {
        try
        {
            var dm = Android.App.Application.Context.Resources!.DisplayMetrics!;
            var (w, h) = TapService.Instance?.ScreenSize() ?? (dm.WidthPixels, dm.HeightPixels);
            return $"{Android.OS.Build.Manufacturer} {Android.OS.Build.Model}, Android {Android.OS.Build.VERSION.Release} (SDK {(int)Android.OS.Build.VERSION.SdkInt}), screen {w} x {h}, {dm.DensityDpi} dpi";
        }
        catch { return "unknown"; }
    }

    /// <summary>1.3.8: the whole log as a file (the shared text was cut short by some apps), with the phone at the top. Null when it can't be written.</summary>
    public static Java.IO.File? AsFile(string version)
    {
        try
        {
            var dir = new Java.IO.File(Android.App.Application.Context.CacheDir, "logs");
            dir.Mkdirs();
            var f = new Java.IO.File(dir, "introboys-log.txt");
            File.WriteAllText(f.AbsolutePath, "IntroBoys Assistant " + version + "\nphone: " + Device() + "\n" + All() + "\n");
            return f;
        }
        catch { return null; }
    }

    static int hooked;
    /// <summary>Writes any crash into the log (with where it happened), so it can be shared and fixed.</summary>
    public static void CatchCrashes()
    {
        if (Interlocked.Exchange(ref hooked, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Add("CRASH: " + Short(e.ExceptionObject as Exception));
        TaskScheduler.UnobservedTaskException += (_, e) => { Add("error (background): " + Short(e.Exception)); e.SetObserved(); };
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Add("CRASH: " + Short(e.Exception));
    }

    static string Short(Exception? ex)
    {
        if (ex == null) return "unknown";
        var inner = ex is AggregateException ag && ag.InnerException != null ? ag.InnerException : ex;
        string where = (inner.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("IntroBoys")) ?? "";
        return inner.GetType().Name + ": " + inner.Message + (where.Length > 0 ? " @ " + where : "");
    }
}
