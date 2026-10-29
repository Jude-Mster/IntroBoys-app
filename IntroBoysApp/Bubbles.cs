using System.Text;
using System.Text.Json;
using Android.Content;

namespace IntroBoys.App;

/// <summary>How a bubble is pressed while the Assistant runs.</summary>
public enum PressMode { Hold, Repeat, Once, Off }

/// <summary>What a bubble sits on in the game.</summary>
public enum BubbleKind { Attack, Target, Skill, Buff, Potion, AutoLoot, Other, Teleport, Return, Camera, MovePad, BossBar, AutoHunt }

/// <summary>One step of the camera fix after a teleport: drag the view one way, or zoom.</summary>
public enum CamOp { Left, Right, Up, Down, ZoomIn, ZoomOut }

public sealed class CamStep
{
    public CamOp Op;
    /// <summary>How far: part of the camera area (0.1 .. 1.0) for a drag, the pinch size for a zoom.</summary>
    public double Amount = 0.5;
    public string Describe() => Op switch
    {
        CamOp.Left => "drag left", CamOp.Right => "drag right", CamOp.Up => "drag up", CamOp.Down => "drag down",
        CamOp.ZoomIn => "zoom in", _ => "zoom out"
    } + " " + (int)Math.Round(Amount * 100) + "%";
}

/// <summary>One bubble: a spot on the screen (screen pixels) and how often to press it.</summary>
public sealed class Bubble
{
    public BubbleKind Kind;
    public float X, Y;
    /// <summary>Camera area: width and height (screen pixels). Move pad: W is how far the stick reaches from the centre.</summary>
    public float W, H;
    public PressMode Mode;
    /// <summary>Seconds between presses (Repeat).</summary>
    public double Every;
    /// <summary>A short label: ATK, SK1, BUFF2 ...</summary>
    public string Label = "";

    public static string Short(BubbleKind k) => k switch
    {
        BubbleKind.Attack => "ATK",
        BubbleKind.Target => "TGT",
        BubbleKind.Skill => "SK",
        BubbleKind.Buff => "BUFF",
        BubbleKind.Potion => "HP",
        BubbleKind.AutoLoot => "LOOT",
        BubbleKind.Teleport => "TP",
        BubbleKind.Return => "RET",
        BubbleKind.Camera => "CAM",
        BubbleKind.MovePad => "PAD",
        BubbleKind.BossBar => "BAR",
        BubbleKind.AutoHunt => "HUNT",
        _ => "BTN"
    };

    public static string Name(BubbleKind k) => k switch
    {
        BubbleKind.Attack => "Attack",
        BubbleKind.Target => "Target",
        BubbleKind.Skill => "Skill",
        BubbleKind.Buff => "Buff",
        BubbleKind.Potion => "Potion",
        BubbleKind.AutoLoot => "Auto loot",
        BubbleKind.Teleport => "Teleport",
        BubbleKind.Return => "Return",
        BubbleKind.Camera => "Camera area",
        BubbleKind.MovePad => "Move pad",
        BubbleKind.BossBar => "Boss HP bar",
        BubbleKind.AutoHunt => "Auto hunt",
        _ => "Other"
    };

    /// <summary>The colour the bubble is drawn in (same as the preview).</summary>
    public static string Color(BubbleKind k) => k switch
    {
        BubbleKind.Attack => "#D61F26",
        BubbleKind.Target => "#E0841A",
        BubbleKind.Skill => "#C2185B",
        BubbleKind.Buff => "#7B4FD6",
        BubbleKind.Potion => "#2E9E4F",
        BubbleKind.AutoLoot => "#C9A227",
        BubbleKind.Teleport => "#1F8FD6",
        BubbleKind.Return => "#1FB5A8",
        BubbleKind.Camera => "#78BEFF",
        BubbleKind.MovePad => "#5EE08F",
        BubbleKind.BossBar => "#FF5A5F",
        BubbleKind.AutoHunt => "#00A884",
        _ => "#5C6BC0"
    };

    /// <summary>A new bubble's settings: attack is held, the rest are pressed every few seconds.</summary>
    public static Bubble New(BubbleKind k, float x, float y) => k switch
    {
        BubbleKind.Attack => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Hold, Every = 1 },
        BubbleKind.Target => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Repeat, Every = 3 },
        BubbleKind.Skill => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Repeat, Every = 2 },
        BubbleKind.Buff => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Repeat, Every = 60 },
        BubbleKind.Potion => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Repeat, Every = 15 },
        BubbleKind.AutoLoot => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Repeat, Every = 3 },
        // used by Labyrinth and Den runs, never pressed by auto attack
        BubbleKind.Teleport or BubbleKind.Return => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Off, Every = 5 },
        BubbleKind.Camera => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Off, Every = 5 },
        BubbleKind.MovePad => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Off, Every = 5 },
        BubbleKind.BossBar => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Off, Every = 5 },
        // the game's auto-hunt switch: on when a fight starts, off again after the kill
        BubbleKind.AutoHunt => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Once, Every = 5 },
        _ => new Bubble { Kind = k, X = x, Y = y, Mode = PressMode.Repeat, Every = 5 }
    };

    public Bubble Copy() => (Bubble)MemberwiseClone();

    /// <summary>Areas, not buttons: the camera area and the move pad are never tapped by auto attack.</summary>
    public bool IsArea => Kind is BubbleKind.Camera or BubbleKind.MovePad or BubbleKind.BossBar;
    /// <summary>A box (camera area, boss HP bar), sized by width and height.</summary>
    public bool IsBox => Kind is BubbleKind.Camera or BubbleKind.BossBar;

    public string Describe() => IsArea ? "used for moving and the camera" : Mode switch
    {
        PressMode.Hold => "held down",
        PressMode.Repeat => "every " + Secs(Every),
        PressMode.Once => "once at the start",
        _ => "off"
    };

    public static string Secs(double s) => s >= 60 && s % 60 == 0 ? (s / 60) + " min" : s.ToString("0.#") + " s";
}

/// <summary>The member's bubble layout, kept in the app's private storage. Positions are screen pixels of the screen size it was made on.</summary>
public sealed class BubbleLayout
{
    public int ScreenW, ScreenH;
    public List<Bubble> Bubbles = new();
    /// <summary>The camera fix after a teleport (the one route to edit): steps done in order on the camera area.</summary>
    public List<CamStep> CamFix = new();

    public Bubble? Find(BubbleKind k) => Bubbles.FirstOrDefault(b => b.Kind == k);

    public static event Action? Saved;

    static ISharedPreferences Prefs => Android.App.Application.Context.GetSharedPreferences("layout", FileCreationMode.Private)!;

    public BubbleLayout Copy() => new()
    {
        ScreenW = ScreenW, ScreenH = ScreenH, Bubbles = Bubbles.Select(b => b.Copy()).ToList(),
        CamFix = CamFix.Select(x => new CamStep { Op = x.Op, Amount = x.Amount }).ToList()
    };

    /// <summary>Gives each bubble its label: ATK, SK1, SK2, BUFF1 ... (a number only when there is more than one of the kind).</summary>
    public void Relabel()
    {
        foreach (var g in Bubbles.GroupBy(b => b.Kind))
        {
            int n = 0, count = g.Count();
            foreach (var b in g) b.Label = Bubble.Short(g.Key) + (count > 1 ? (++n).ToString() : "");
        }
    }

    /// <summary>Where to tap for a bubble on a screen of this size (the layout is stretched if the screen size changed, e.g. turned).</summary>
    public (float x, float y) At(Bubble b, int w, int h)
    {
        if (ScreenW <= 0 || ScreenH <= 0 || (ScreenW == w && ScreenH == h)) return (b.X, b.Y);
        return (b.X * w / ScreenW, b.Y * h / ScreenH);
    }

    /// <summary>Width and height of an area on a screen of this size.</summary>
    public (float w, float h) SizeAt(Bubble b, int w, int h)
    {
        if (ScreenW <= 0 || ScreenH <= 0 || (ScreenW == w && ScreenH == h)) return (b.W, b.H);
        return (b.W * w / ScreenW, b.H * h / ScreenH);
    }

    public static BubbleLayout Load()
    {
        var l = new BubbleLayout();
        try
        {
            string json = Prefs.GetString("json", "") ?? "";
            if (json.Length == 0) return l;
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            l.ScreenW = r.TryGetProperty("w", out var w) ? w.GetInt32() : 0;
            l.ScreenH = r.TryGetProperty("h", out var h) ? h.GetInt32() : 0;
            if (r.TryGetProperty("bubbles", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (!Enum.TryParse<BubbleKind>(e.GetProperty("kind").GetString(), out var k)) continue;
                    Enum.TryParse<PressMode>(e.TryGetProperty("mode", out var m) ? m.GetString() : "", out var mode);
                    l.Bubbles.Add(new Bubble
                    {
                        Kind = k,
                        X = (float)e.GetProperty("x").GetDouble(),
                        Y = (float)e.GetProperty("y").GetDouble(),
                        Mode = mode,
                        Every = Math.Clamp(e.TryGetProperty("every", out var ev) ? ev.GetDouble() : 2, 0.5, 600),
                        W = e.TryGetProperty("w", out var bw) ? (float)bw.GetDouble() : 0,
                        H = e.TryGetProperty("h", out var bh) ? (float)bh.GetDouble() : 0
                    });
                    if (l.Bubbles[^1].IsArea) l.Bubbles[^1].Mode = PressMode.Off;
                }
            }
            if (r.TryGetProperty("camfix", out var cf) && cf.ValueKind == JsonValueKind.Array)
                foreach (var e in cf.EnumerateArray())
                    if (Enum.TryParse<CamOp>(e.GetProperty("op").GetString(), out var op))
                        l.CamFix.Add(new CamStep { Op = op, Amount = Math.Clamp(e.TryGetProperty("amt", out var a) ? a.GetDouble() : 0.5, 0.1, 1.0) });
        }
        catch { l.Bubbles.Clear(); l.CamFix.Clear(); }
        l.Relabel();
        return l;
    }

    public void Save()
    {
        Relabel();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("w", ScreenW);
            w.WriteNumber("h", ScreenH);
            w.WriteStartArray("bubbles");
            foreach (var b in Bubbles)
            {
                w.WriteStartObject();
                w.WriteString("kind", b.Kind.ToString());
                w.WriteNumber("x", Math.Round(b.X, 1));
                w.WriteNumber("y", Math.Round(b.Y, 1));
                w.WriteString("mode", b.Mode.ToString());
                w.WriteNumber("every", b.Every);
                if (b.IsArea) { w.WriteNumber("w", Math.Round(b.W, 1)); w.WriteNumber("h", Math.Round(b.H, 1)); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("camfix");
            foreach (var st in CamFix)
            {
                w.WriteStartObject();
                w.WriteString("op", st.Op.ToString());
                w.WriteNumber("amt", Math.Round(st.Amount, 2));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        Prefs.Edit()!.PutString("json", Encoding.UTF8.GetString(ms.ToArray()))!.Apply();
        try { Saved?.Invoke(); } catch { /* a listener's problem */ }
    }
}
