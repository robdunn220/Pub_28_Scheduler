namespace PublicHouse28Scheduler;

/// <summary>Which part of the house a staff member works: Front (bar/servers/hosts) or Back (kitchen).</summary>
public enum HouseArea { FOH, BOH }

/// <summary>A staff member at Public House 28.</summary>
public class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Front-of-house or back-of-house. Defaults to FOH.</summary>
    public HouseArea Area { get; set; } = HouseArea.FOH;
}

/// <summary>Display helpers for <see cref="HouseArea"/>.</summary>
public static class HouseAreaExtensions
{
    public static string LongName(this HouseArea area) =>
        area == HouseArea.BOH ? "Back of House" : "Front of House";

    /// <summary>Parse "FOH"/"BOH" (or longer spellings); defaults to FOH when unclear.</summary>
    public static HouseArea ParseArea(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return HouseArea.FOH;
        if (s.StartsWith("B", StringComparison.OrdinalIgnoreCase)) return HouseArea.BOH;
        return HouseArea.FOH;
    }
}

/// <summary>A single shift on the schedule. May be unassigned (open).</summary>
public class Shift
{
    public int Id { get; set; }

    /// <summary>ISO date, yyyy-MM-dd.</summary>
    public string Day { get; set; } = "";

    /// <summary>Free text so bar hours like "5pm" or "Close" work naturally.</summary>
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";

    /// <summary>Role required for this shift, e.g. "bartender".</summary>
    public string Role { get; set; } = "";

    public int? EmployeeId { get; set; }

    /// <summary>Joined from the employees table for display; not stored on the shift row.</summary>
    public string? EmployeeName { get; set; }
}

/// <summary>Outcome of a create/assign attempt. <see cref="Shift"/> is null when the action was refused.</summary>
public record ShiftResult(Shift? Shift, string Message, bool Conflict);

