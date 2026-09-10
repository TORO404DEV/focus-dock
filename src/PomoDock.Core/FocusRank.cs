namespace PomoDock.Core;

/// <summary>
/// Where the user stands, measured in hours of real focus. Ranks are wide on purpose: this is a
/// record of work already done, not a bar to grind, so the early ones arrive fast and the late
/// ones take months.
/// </summary>
public sealed record FocusRank(int Level, string Name, string Detail, double Hours, double Earned, double Span)
{
    /// <summary>Progress inside the current rank, 0 to 1.</summary>
    public double Share => Span <= 0 ? 1 : Math.Clamp(Earned / Span, 0, 1);
    public double ToNext => Span <= 0 ? 0 : Math.Max(0, Span - Earned);
    public bool IsHighest => Span <= 0;
}

public static class FocusProfile
{
    /// <summary>Hours where each rank begins, with the key of the name it grants.</summary>
    private static readonly (double Hours, string Key)[] Ladder =
    [
        (0, "rank.step"), (1, "rank.apprentice"), (5, "rank.steady"), (15, "rank.focused"),
        (40, "rank.artisan"), (80, "rank.veteran"), (150, "rank.master"), (300, "rank.legend")
    ];

    /// <summary>Only focus counts. Breaks are rest, not work.</summary>
    public static double Hours(IEnumerable<Session> sessions) =>
        sessions.Where(session => session.Phase == Phase.Focus).Sum(session => session.Seconds) / 3600;

    public static FocusRank Of(IEnumerable<Session> sessions) => At(Hours(sessions));

    public static FocusRank At(double hours)
    {
        hours = Math.Max(0, hours);
        int index = 0;
        for (int step = 0; step < Ladder.Length; step++) if (hours >= Ladder[step].Hours) index = step;
        var current = Ladder[index];
        bool highest = index == Ladder.Length - 1;
        double span = highest ? 0 : Ladder[index + 1].Hours - current.Hours;
        return new FocusRank(index + 1, L.T(current.Key), L.T(current.Key + "Detail"), hours, hours - current.Hours, span);
    }

    /// <summary>The name of the rank that comes next, for the "what am I working towards" line.</summary>
    public static string NextName(FocusRank rank) => rank.Level < Ladder.Length ? L.T(Ladder[rank.Level].Key) : rank.Name;
}
