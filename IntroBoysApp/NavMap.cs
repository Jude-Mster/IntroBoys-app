using System.Text.Json;

namespace IntroBoys.App;

/// <summary>
/// 1.6.11: the PC Assistant's map of the dungeons (navmap.json, learned on the PC): ~2500 stretches the character really walked, and the
/// spots where it was blocked. The phone plans its way over it (A* on a grid of 80-unit cells: the walked stretches and a cell either side
/// are open, the blocked spots are shut), so a walk without a recorded way - or one that got stuck - goes the way the PC knows works
/// instead of guessing with detours.
/// </summary>
static class NavMap
{
    const int Cell = 80;
    static HashSet<(int, int)>? core, open;
    static readonly object gate = new();

    static (int, int) C(double x, double y) => ((int)Math.Floor(x / Cell), (int)Math.Floor(y / Cell));

    static void Load()
    {
        if (open != null) return;
        var c = new HashSet<(int, int)>(); var o = new HashSet<(int, int)>();
        try
        {
            using var st = Android.App.Application.Context.Assets!.Open("navmap.json");
            using var doc = JsonDocument.Parse(st);
            foreach (var w in doc.RootElement.GetProperty("Walks").EnumerateArray())
            {
                double x1 = w[0].GetDouble(), y1 = w[1].GetDouble(), x2 = w[2].GetDouble(), y2 = w[3].GetDouble();
                int n = Math.Max(1, (int)(Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)) / (Cell / 2)));
                for (int i = 0; i <= n; i++) c.Add(C(x1 + (x2 - x1) * i / n, y1 + (y2 - y1) * i / n));
            }
            foreach (var (cx, cy) in c) for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) o.Add((cx + dx, cy + dy));
            // a blocked spot: [x, y, dx, dy] = going from (x, y) toward (dx, dy) ran into something - the cell ahead is shut
            foreach (var b in doc.RootElement.GetProperty("Blocks").EnumerateArray())
            {
                double x = b[0].GetDouble(), y = b[1].GetDouble(), dx = b[2].GetDouble(), dy = b[3].GetDouble();
                double l = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                var k = C(x + dx / l * Cell, y + dy / l * Cell);
                if (!c.Contains(k)) o.Remove(k);
            }
        }
        catch (Exception ex) { AppLog.Add("map: couldn't read the PC's map (" + ex.Message + ")"); }
        lock (gate) { core = c; open = o; }
    }

    public static bool Ready { get { Load(); return open!.Count > 0; } }

    /// <summary>A way from (fx, fy) to (tx, ty) over what the PC walked, as map points about 80 apart. Null when there is none.</summary>
    public static List<(int x, int y)>? Plan(int fx, int fy, int tx, int ty)
    {
        Load();
        if (open!.Count == 0) return null;
        var s = Snap(C(fx, fy)); var g = Snap(C(tx, ty));
        if (s == null || g == null) return null;
        var start = s.Value; var goal = g.Value;
        var dist = new Dictionary<(int, int), double> { [start] = 0 };
        var from = new Dictionary<(int, int), (int, int)>();
        var pq = new PriorityQueue<(int, int), double>();
        pq.Enqueue(start, H(start, goal));
        int guard = 0;
        while (pq.Count > 0 && guard++ < 200000)
        {
            var cur = pq.Dequeue();
            if (cur == goal) break;
            double dc = dist[cur];
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                var nb = (cur.Item1 + dx, cur.Item2 + dy);
                if (!open.Contains(nb)) continue;
                // the walked cells cost less: the way keeps to where the character really went
                double step = (dx != 0 && dy != 0 ? 1.414 : 1) * (core!.Contains(nb) ? 1 : 1.6);
                double nd = dc + step;
                if (dist.TryGetValue(nb, out var od) && od <= nd) continue;
                dist[nb] = nd; from[nb] = cur;
                pq.Enqueue(nb, nd + H(nb, goal));
            }
        }
        if (!dist.ContainsKey(goal)) return null;
        var cells = new List<(int, int)>();
        for (var k = goal; ; k = from[k]) { cells.Add(k); if (k == start) break; if (!from.ContainsKey(k)) return null; }
        cells.Reverse();
        var pts = cells.Select(k => (k.Item1 * Cell + Cell / 2, k.Item2 * Cell + Cell / 2)).ToList();
        pts.Add((tx, ty));
        return pts;
    }

    static double H((int, int) a, (int, int) b) => Math.Sqrt(Math.Pow(a.Item1 - b.Item1, 2) + Math.Pow(a.Item2 - b.Item2, 2));

    /// <summary>The nearest open cell within 5 cells (400 units).</summary>
    static (int, int)? Snap((int, int) k)
    {
        if (open!.Contains(k)) return k;
        (int, int)? best = null; double bd = double.MaxValue;
        for (int dx = -5; dx <= 5; dx++) for (int dy = -5; dy <= 5; dy++)
        {
            var q = (k.Item1 + dx, k.Item2 + dy);
            if (!core!.Contains(q)) continue;
            double d = dx * dx + dy * dy;
            if (d < bd) { bd = d; best = q; }
        }
        return best;
    }
}
