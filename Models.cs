namespace PublicHouse28Scheduler;

/// <summary>A staff member at Public House 28.</summary>
public class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
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

