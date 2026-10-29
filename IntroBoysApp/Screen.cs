using System.Text.RegularExpressions;
using Android.Content;

namespace IntroBoys.App;

/// <summary>Where the character is, in words: in town (or any map that is not a dungeon), the Labyrinth or the Den.</summary>
public enum Place { Unknown, Town, Labyrinth, Den }

/// <summary>
/// Where the character is: the map name and the numbers under the mini-map ("[The Labyrinth (Adept)]" / "3595 , 34153").
/// Map falls back on the last title read (for showing); Title is only what THIS picture showed (empty when it couldn't be read).
/// </summary>
public readonly record struct Position(bool Ok, int X, int Y, string Map, string Title = "")
{
    // 1.4.0: an empty reading (default) has null strings: "Object reference not set" after a kill, when the screen couldn't be read
    public bool InLabyrinth => (Map ?? "").Contains("Labyr", StringComparison.OrdinalIgnoreCase);
    public bool InDen => (Map ?? "").Contains("Rebirth", StringComparison.OrdinalIgnoreCase) || (Map ?? "").Contains("Den", StringComparison.Ordinal);
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
    /// <summary>1.3.7: where the character was at the last reading (town, Labyrinth, Den), and since when it has been there.</summary>
    public static Place LastPlace { get; private set; }
    public static string PlaceText => LastPlace switch
    {
        Place.Town => "Town" + (LastTitle.Length > 0 && !LastTitle.Contains("Labyr", StringComparison.OrdinalIgnoreCase) ? " " + LastTitle : ""),
        Place.Labyrinth => "Labyrinth",
        Place.Den => "Den of Rebirth",
        _ => "–",
    };
    static string LastTitle = "";
    /// <summary>When the picture of the last reading was taken (a reading from the same picture again is no news).</summary>
    public static long LastFrameAt { get; private set; }

    /// <summary>
    /// 1.3.7: which place a reading is. The mini-map title of THIS picture decides (a title that is no dungeon = town); without a title, the
    /// coordinate: the Labyrinth runs x 2600-4700, y 800-36000 and the Den x 700-1700, y 1000-3000 - anything else is outside them.
    /// The title is never taken from an older picture: after a death or a kick the last title still said "Labyrinth", so the run walked
    /// on in town.
    /// </summary>
    public static Place PlaceOf(Position p)
    {
        // 1.4.4: "town" from the numbers alone only with two whole 4-5 digit numbers: a dropped first digit ("595 , 34153" read off a
        // tiny screen) made the cloud phone think it was in town and start the run over
        Place byXy = !p.Ok ? Place.Unknown
                   : p.X >= 2600 && p.X <= 4700 && p.Y >= 800 && p.Y <= 36000 ? Place.Labyrinth
                   : p.X >= 700 && p.X <= 1700 && p.Y >= 1000 && p.Y <= 3000 ? Place.Den
                   : p.X >= 1000 && p.Y >= 1000 ? Place.Town
                   : Place.Unknown;
        string t = p.Title ?? "";
        if (t.Length == 0) return byXy;
        bool Has(string w) => t.Contains(w, StringComparison.OrdinalIgnoreCase);
        // a word or two is enough (the text reader drops letters: "[The Labyrinth (Ade", "[The Labyrinth (AdeX")
        if (Has("Labyr") || Has("rinth") || Has("Adept")) return Place.Labyrinth;
        if (Has("Rebirth") || Has("Rebir") || Has("Ultimate") || t.Contains("Den ", StringComparison.Ordinal) || t.Contains("Den]", StringComparison.Ordinal)) return Place.Den;
        // another title, but the numbers are those of a dungeon: a misread title - not sure
        if (byXy is Place.Labyrinth or Place.Den) return Place.Unknown;
        return Place.Town;
    }

    /// <summary>The position and map name (Ok false when they can't be read: sharing off, a loading screen, the mini-map closed).</summary>
    public static async Task<Position> PositionAsync()
    {
        var f = await Capture.SnapAsync(C);
        if (f == null) return new Position(false, 0, 0, LastMap, "");
        LastFrameAt = f.At;
        // 1.4.0: where the mini-map numbers are on THIS phone. The game draws its corner pieces by the screen's HEIGHT, so the part that
        // is read is as wide as 46 % of the height (was 20 % of the width: on a wider or narrower screen the numbers fell outside it).
        // A place found once is remembered (Prefs "pos_box"); when nothing is found there, the whole top-right quarter is searched.
        var (x, y, w, h) = PosBox(f);
        // 1.4.3: a small screen (720p: the numbers are ~10 px high) is read at 3 times the size
        int sc = Math.Min(f.W, f.H) <= 800 ? 3 : 2;
        var (pos, map) = ReadPos(await Ocr.ReadAsync(f, x, y, w, h, sc));
        // 1.4.4: a small screen that didn't read: once more at the other size before anything else
        if (!pos.Ok && sc == 3) { var (pa, ma) = ReadPos(await Ocr.ReadAsync(f, x, y, w, h, 2)); if (pa.Ok) { pos = pa; if (ma.Length > 0) map = ma; } }
        if (!pos.Ok)
        {
            if (++posMisses >= 3 && System.Environment.TickCount64 - lastWide > 4000)
            {
                lastWide = System.Environment.TickCount64;
                int wx = f.W / 2, ww = f.W - wx, wh = (int)(f.H * 0.32);
                var wide = await Ocr.ReadAsync(f, wx, 0, ww, wh, 2);
                var (p2, m2) = ReadPos(wide, true);
                if (p2.Ok)
                {
                    pos = p2; if (m2.Length > 0) map = m2;
                    Learn(f, wide.Where(l => (l.Text.Contains('[') && l.Text.Count(char.IsLetter) >= 4)
                                          || (Clean(l.Text).Contains(p2.X.ToString()) && Clean(l.Text).Contains(p2.Y.ToString()))).ToList());
                }
            }
        }
        else posMisses = 0;
        if (map.Length > 0) LastMap = map;
        pos = pos with { Map = map.Length > 0 ? map : LastMap, Title = map };
        if (pos.Ok) LastPos = pos;
        var place = PlaceOf(pos);
        if (place != Place.Unknown) { LastPlace = place; LastTitle = map.Length > 0 ? map : ""; }
        return pos;
    }

    static int posMisses;
    static long lastWide = -100000;
    static Android.Content.ISharedPreferences Prefs => C.GetSharedPreferences("screen", Android.Content.FileCreationMode.Private)!;

    /// <summary>The part of the screen with the mini-map title and numbers: the one found before, or the default for this screen shape.</summary>
    static (int x, int y, int w, int h) PosBox(Frame f)
    {
        string key = $"pos_box_{f.W}x{f.H}";
        var saved = Prefs.GetString(key, "") ?? "";
        var parts = saved.Split(',');
        if (parts.Length == 4 && parts.All(q => int.TryParse(q, out _)))
        {
            int[] v = parts.Select(int.Parse).ToArray();
            if (v[2] > 20 && v[3] > 10 && v[0] + v[2] <= f.W && v[1] + v[3] <= f.H) return (v[0], v[1], v[2], v[3]);
        }
        int w = Math.Min(f.W / 2, Math.Max((int)(f.W * 0.20), (int)(f.H * 0.46)));
        return (f.W - w, (int)(f.H * 0.035), w, (int)(f.H * 0.10));
    }

    /// <summary>The numbers (and title) were found in the wide search: remember that place, with room around it.</summary>
    static void Learn(Frame f, List<TextLine> lines)
    {
        var num = lines.Where(l => !l.Text.Contains('[')).ToList();
        if (num.Count == 0) return;
        var c0 = num[0];
        var hit = num.Take(1).Concat(lines.Where(l => l.Text.Contains('[') && c0.Y - l.Y >= 0 && c0.Y - l.Y < l.H * 6)).ToList();
        float x0 = hit.Min(l => l.X), y0 = hit.Min(l => l.Y), x1 = hit.Max(l => l.X + l.W), y1 = hit.Max(l => l.Y + l.H);
        float padX = Math.Max(f.H * 0.08f, (x1 - x0) * 0.5f), padY = Math.Max(f.H * 0.02f, (y1 - y0) * 0.6f);
        int bx = (int)Math.Max(0, x0 - padX), by = (int)Math.Max(0, y0 - padY);
        int bw = (int)Math.Min(f.W - bx, x1 - x0 + 2 * padX), bh = (int)Math.Min(f.H - by, y1 - y0 + 2 * padY);
        Prefs.Edit()!.PutString($"pos_box_{f.W}x{f.H}", $"{bx},{by},{bw},{bh}")!.Apply();
        AppLog.Add($"screen: found the mini-map numbers at {bx},{by} ({bw} x {bh}) on this {f.W} x {f.H} screen - remembered");
        posMisses = 0;
    }

    /// <summary>Letters the reader mistakes for digits in the numbers line ("3595 , 34l53", "O").</summary>
    static string Clean(string t)
    {
        int digits = t.Count(char.IsDigit);
        if (digits < 4 || digits < t.Count(char.IsLetter) * 2) return t;
        return t.Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1').Replace('S', '5').Replace('B', '8');
    }

    static (Position pos, string map) ReadPos(List<TextLine> lines, bool strict = false)
    {
        string map = "";
        TextLine? title = null;
        foreach (var l in lines.OrderBy(l => l.Y))
            if (l.Text.Contains('[') && l.Text.Count(char.IsLetter) >= 4) { map = l.Text.Trim(); title ??= l; }
        // the numbers: the first "x , y" line (wide search: 3-5 digits each, the one right under the title when there is one -
        // other numbers on the screen, like "53.550", are not a position)
        var cands = new List<(TextLine l, int x, int y)>();
        foreach (var l in lines.OrderBy(l => l.Y))
        {
            var m = Coord.Match(Clean(l.Text));
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int px) || !int.TryParse(m.Groups[2].Value, out int py)) continue;
            if (strict && (m.Groups[1].Value.Length < 3 || m.Groups[2].Value.Length < 3)) continue;
            cands.Add((l, px, py));
        }
        if (cands.Count == 0) return (new Position(false, 0, 0, "", ""), map);
        var pick = cands[0];
        if (strict && title is TextLine t)
        {
            var below = cands.Where(c => c.l.Y >= t.Y && c.l.Y - t.Y < t.H * 6).OrderBy(c => Math.Abs(c.l.Cx - t.Cx) + (c.l.Y - t.Y)).ToList();
            if (below.Count > 0) pick = below[0];
            else return (new Position(false, 0, 0, "", ""), map);
        }
        return (new Position(true, pick.x, pick.y, "", ""), map);
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
                // 1.3.8: a darker / less saturated red counts too (another phone's screen and the game's effects over the bar)
                if (r >= 75 && r >= g * 1.35 && r >= b * 1.35 && r - Math.Max(g, b) >= 35) hit++;
            }
            red[i] = hit * 3 >= n;
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
