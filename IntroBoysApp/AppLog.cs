namespace IntroBoys.App;

/// <summary>The live log: what the Assistant just did, shown in the floating panel, the app and the guild website's live view.</summary>
static class AppLog
{
    static readonly object gate = new();
    static readonly List<string> lines = new();
    const int Keep = 500;

    /// <summary>Raised (any thread) after a line is added.</summary>
    public static event Action? Changed;

    public static void Add(string text)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + " " + text;
        lock (gate)
        {
            lines.Add(line);
            if (lines.Count > Keep) lines.RemoveRange(0, lines.Count - Keep);
        }
        try { Changed?.Invoke(); } catch { /* a listener's problem */ }
    }

    /// <summary>Everything kept (the last 500 lines), for "Share the log".</summary>
    public static string All() { lock (gate) return string.Join("\n", lines); }

    /// <summary>The last <paramref name="n"/> lines, oldest first.</summary>
    public static string[] Last(int n)
    {
        lock (gate) return lines.Skip(Math.Max(0, lines.Count - n)).ToArray();
    }
}
