using System.Text.RegularExpressions;

namespace PublicHouse28Scheduler;

/// <summary>
/// Best-effort parsing of the free-text shift times ("5pm", "5:00pm", "17:00", "11pm",
/// "Close") into minutes-since-midnight, so we can detect overlapping shifts. When a time
/// can't be understood, callers treat the comparison as indeterminate rather than guessing.
/// </summary>
internal static class ShiftTime
{
    /// <summary>"Close" (and friends) — an open-ended late end-of-night.</summary>
    public static bool IsClose(string s) =>
        s.Trim().TrimEnd('.').ToLowerInvariant() is "close" or "closing" or "late";

    /// <summary>Parse a clock time to minutes since midnight. Does NOT handle "Close" (see TryWindow).</summary>
    public static bool TryParse(string s, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var t = s.Trim().ToLowerInvariant().Replace(" ", "");
        var m = Regex.Match(t, @"^(\d{1,2})(?::(\d{2}))?(am|pm)?$");
        if (!m.Success) return false;

        int hour = int.Parse(m.Groups[1].Value);
        int min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (hour > 23 || min > 59) return false;

        string mer = m.Groups[3].Value;
        if (mer == "pm" && hour < 12) hour += 12;
        else if (mer == "am" && hour == 12) hour = 0;

        minutes = hour * 60 + min;
        return true;
    }

    /// <summary>
    /// Turn a (start, end) pair into a numeric [start, end) window. "Close" becomes a late
    /// sentinel (30:00). End times at/after midnight roll into the next day so e.g.
    /// 5pm–2am is contiguous. Returns false if the times can't be parsed.
    /// </summary>
    public static bool TryWindow(string start, string end, out int startMin, out int endMin)
    {
        startMin = 0;
        endMin = 0;
        if (!TryParse(start, out startMin)) return false;

        if (IsClose(end))
        {
            endMin = 30 * 60; // 6:00am-ish "end of night" — always after any evening start
            return true;
        }
        if (!TryParse(end, out endMin)) return false;

        if (endMin <= startMin) endMin += 24 * 60; // crosses midnight
        return true;
    }

    /// <summary>True/false if the two shifts overlap; null if either can't be parsed.</summary>
    public static bool? Overlaps(string start1, string end1, string start2, string end2)
    {
        if (!TryWindow(start1, end1, out int a1, out int b1)) return null;
        if (!TryWindow(start2, end2, out int a2, out int b2)) return null;
        return a1 < b2 && a2 < b1;
    }
}
