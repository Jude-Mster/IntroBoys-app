using System.Text.RegularExpressions;
using Android.Content;

namespace IntroBoys.App;

/// <summary>Where the character is: the map name and the numbers under the mini-map ("[The Labyrinth (Adept)]" / "3595 , 34153").</summary>
public readonly record struct Position(bool Ok, int X, int Y, string Map)
{
    public bool InLabyrinth => Map.Contains("Labyrinth", StringComparison.OrdinalIgnoreCase) || Map.Contains("Labyr", StringComparison.OrdinalIgnoreCase);
    public bool InDen => Map.Contains("Rebirth", StringComparison.OrdinalIgnoreCase) || Map.Contains("Den", StringComparison.Ordinal);
    public double Dist(int x, int y) => Math.Sqrt((double)(X - x) * (X - x) + (double)(Y - y) * (Y - y));
    public override string ToString() => Ok ? $"{X} , {Y}" : "–";
}

/// <summary>
/// Reading the phone game's screen (through the tap service, or screen sharing): the position under the mini-map, the map name, and the boss HP bar in the
/// box the member placed over it. The latest readings are kept for the floating panel and the website.
/// </summary>
static class Screen
{
    static readonly Regex Coord = new(@"(\d{2,5})\s*[,.;:]\s*(\d{2,5})", RegexOptions.Compiled);
    static Context C => Android.App.Application.Context;

    public static Position LastPos { get; private set; }
    public static double LastHp { get; private set; } = -1;
    public static string LastMap { get; private set; } = "";
    /// <summary>When the picture of the last reading was taken (a reading from the same picture again is no news).</summary>
    public static long LastFrameAt { get; private set; }

    /// <summary>The position and map name (Ok false when they can't be read: sharing off, a loading screen, the mini-map closed).</summary>
    public static async Task<Position> PositionAsync()
    {
        var f = await Capture.SnapAsync(C);
        if (f == null) return default;
        LastFrameAt = f.At;
        // the mini-map title and the numbers under it: the top right corner
        int x = (int)(f.W * 0.80), y = (int)(f.H * 0.035), w = f.W - x, h = (int)(f.H * 0.10);
        var lines = await Ocr.ReadAsync(f, x, y, w, h, 2);
        string map = "";
        Position pos = default;
        foreach (var l in lines.OrderBy(l => l.Y))
        {
            var m = Coord.Match(l.Text);
            if (m.Success && !pos.Ok && int.TryParse(m.Groups[1].Value, out int px) && int.TryParse(m.Groups[2].Value, out int py))
                pos = new Position(true, px, py, "");
            // the map name is the title in brackets ("[The Labyrinth (Adept)]"); the ping / FPS line above it is not a map
            else if (l.Text.Contains('[') && l.Text.Count(char.IsLetter) >= 4) map = l.Text.Trim();
        }
        if (map.Length > 0) LastMap = map;
        if (pos.Ok) { pos = pos with { Map = map.Length > 0 ? map : LastMap }; LastPos = pos; }
        return pos;
    }

    /// <summary>The map name only (Ok false when nothing is readable).</summary>
    public static async Task<string> MapAsync() { await PositionAsync(); return LastMap; }

    /// <summary>
    /// The boss HP bar, read in the box the member placed over it (Bubbles, + Boss HP bar): the share of the bar that is filled red,
    /// 0..1, or -1 when there is no red. Returns -2 when there is no box or no picture.
    /// </summary>
    public static async Task<double> BossHpAsync(BubbleLayout layout) => Hp(layout, await Capture.SnapAsync(C));

    /// <summary>
    /// Is a boss targeted? Red in the bar, or (the bar can read empty while the boss lives) the target's name just above the bar.
    /// </summary>
    public static async Task<(double hp, bool there)> BossAsync(BubbleLayout layout, bool lookForName)
    {
        var f = await Capture.SnapAsync(C);
        double hp = Hp(layout, f);
        if (hp > 0 || !lookForName || f == null) return (hp, hp > 0);
        var box = layout.Find(BubbleKind.BossBar);
        if (box == null) return (hp, false);
        var (cx, cy) = layout.At(box, f.W, f.H);
        var (bw, bh) = layout.SizeAt(box, f.W, f.H);
        int x = (int)(cx - bw * 0.8), y = (int)(cy - bh / 2 - Math.Max(bh * 4, f.H * 0.05)), w = (int)(bw * 1.6), h = (int)(cy + bh * 1.5) - y;
        var lines = await Ocr.ReadAsync(f, x, y, w, h, 2);
        bool name = lines.Any(l => l.Text.Count(char.IsLetter) >= 3);
        return (hp, name);
    }

    public static double BossHp(BubbleLayout layout) => Hp(layout, Capture.Snap(C));

    static readonly Queue<(long at, double hp)> recent = new();

    /// <summary>
    /// 1.3.6: how much of the bar is red. Only the middle rows of the box are looked at (not its edges), a darker red counts too, and
    /// the red must start at the left end and run on without a real gap (a damage number or a red effect elsewhere in the box is not
    /// HP). The last three readings within a second are combined (the middle one), so one odd picture doesn't count.
    /// </summary>
    static double Hp(BubbleLayout layout, Frame? f)
    {
        var box = layout.Find(BubbleKind.BossBar);
        if (box == null || f == null) { LastHp = -1; return -2; }
        var (cx, cy) = layout.At(box, f.W, f.H);
        var (bw, bh) = layout.SizeAt(box, f.W, f.H);
        int x0 = (int)(cx - bw / 2), x1 = (int)(cx + bw / 2);
        int yTop = (int)(cy - bh / 2), yBot = (int)(cy + bh / 2);
        int y0 = yTop + (yBot - yTop) / 4, y1 = Math.Max(y0 + 1, yBot - (yBot - yTop) / 4);
        int cols = x1 - x0;
        if (cols < 8) return -2;
        var red = new bool[cols];
        for (int i = 0; i < cols; i++)
        {
            int n = 0, hit = 0;
            for (int y = y0; y < y1; y++)
            {
                var (r, g, b) = f.Rgb(x0 + i, y);
                n++;
                if (r >= 90 && r >= g * 1.5 && r >= b * 1.5) hit++;
            }
            red[i] = hit * 2 >= n;
        }
        // the red has to start near the left end of the bar ...
        int start = -1;
        for (int i = 0; i < Math.Max(2, cols * 15 / 100); i++) if (red[i]) { start = i; break; }
        double hp;
        if (start < 0) hp = -1;
        else
        {
            // ... and runs on to where it stops (gaps up to 3 % of the bar are bridged: a dark line or a number on the bar)
            int gapMax = Math.Max(2, cols * 3 / 100), gap = 0, end = start;
            for (int i = start; i < cols; i++)
            {
                if (red[i]) { end = i; gap = 0; }
                else if (++gap > gapMax) break;
            }
            hp = (end - 0 + 1) / (double)cols;
            if (end - start < 2 && hp > 0.05) hp = -1;
        }
        // the middle of the last three readings (within a second)
        long now = System.Environment.TickCount64;
        lock (recent)
        {
            recent.Enqueue((now, hp));
            while (recent.Count > 3 || (recent.Count > 0 && now - recent.Peek().at > 1000)) recent.Dequeue();
            if (recent.Count == 3)
            {
                var v = recent.Select(x => x.hp).OrderBy(x => x).ToArray();
                hp = v[1];
            }
        }
        LastHp = hp;
        return hp;
    }

    /// <summary>Lines of text in a part of the screen given as fractions of its size (the teleport list, buttons ...).</summary>
    public static async Task<List<TextLine>> TextAsync(double fx, double fy, double fw, double fh, int scale = 1)
    {
        var f = await Capture.SnapAsync(C);
        if (f == null) return new();
        return await Ocr.ReadAsync(f, (int)(f.W * fx), (int)(f.H * fy), (int)(f.W * fw), (int)(f.H * fh), scale);
    }
}
