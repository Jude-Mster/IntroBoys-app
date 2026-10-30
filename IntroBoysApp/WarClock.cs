namespace IntroBoys.App;

/// <summary>
/// 1.5.2: Regular Battle (the war), as on wiki.wuxen2.com and the guild site: every 2 hours on the even UTC hours (00:00, 02:00 …):
/// countdown :20, entrance :30-:33, battle :33-:48. The war break leaves the dungeon at :27 and goes back after :48 (+1 min).
/// </summary>
static class WarClock
{
    public const int BreakMin = 27, EndMin = 48, BackAfterMin = 49;

    static DateTime BlockStart(DateTime utc) => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour - utc.Hour % 2, 0, 0, DateTimeKind.Utc);

    /// <summary>Between :27 and :49 of a war hour.</summary>
    public static bool InBreak(DateTime utc)
    {
        var b = BlockStart(utc);
        return utc >= b.AddMinutes(BreakMin) && utc < b.AddMinutes(BackAfterMin);
    }

    /// <summary>The next :27 of a war hour (now, if it is exactly that).</summary>
    public static DateTime NextBreak(DateTime utc)
    {
        var at = BlockStart(utc).AddMinutes(BreakMin);
        return at > utc ? at : at.AddHours(2);
    }

    /// <summary>When the run goes back after the war that is on now (or the next one).</summary>
    public static DateTime BackAt(DateTime utc)
    {
        var b = BlockStart(utc);
        var back = b.AddMinutes(BackAfterMin);
        return utc < back ? back : back.AddHours(2);
    }
}
