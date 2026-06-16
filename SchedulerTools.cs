using System.Text.Json;

namespace PublicHouse28Scheduler;

/// <summary>One scheduling tool the model can call: its name, description, and JSON-schema parameters.</summary>
public sealed record ToolDef(string Name, string Description, Dictionary<string, JsonElement> Properties, string[] Required);

/// <summary>
/// Provider-agnostic scheduling tools. Both the Claude and Gemini assistants share these
/// definitions, the system prompt, and the execution logic — only the wire format and the HTTP
/// loop differ per provider. Every tool maps to a <see cref="SchedulerService"/> call, so OUR
/// code performs every change to the schedule no matter which model is driving.
/// </summary>
public sealed class SchedulerTools
{
    private readonly SchedulerService _svc;

    public SchedulerTools(SchedulerService svc) => _svc = svc;

    /// <summary>The system prompt, shared across providers. Recomputed each call so "today" stays current.</summary>
    public static string SystemPrompt() =>
        $"""
        You are the scheduling assistant for a bar called Public House 28. You help the manager
        build and adjust the EMPLOYEE shift schedule by calling the provided tools.

        Today is {DateTime.Today:dddd, MMMM d, yyyy} ({DateTime.Today:yyyy-MM-dd}).
        When the user names a day like "Friday" or "next Tuesday", resolve it to a concrete
        calendar date and pass it to tools as an ISO date string (yyyy-MM-dd).

        Times can be written naturally — keep them as the user says them (e.g. "5pm", "Close",
        "11:00pm"). Common roles at this bar: bartender, server, host, kitchen.

        Every employee belongs to an area: FOH (front of house — bar, servers, hosts) or BOH
        (back of house — kitchen). Set it when hiring with add_employee, or change it later with
        set_employee_area. Kitchen staff are BOH; bartenders, servers and hosts are FOH.

        Employees are available every day by default. Days off are tied to SPECIFIC calendar
        dates, not recurring weekdays. Mark a day off with mark_unavailable — ONE date per call,
        passed as an ISO date (yyyy-MM-dd). When the user names a weekday like "Saturday" without
        a week, default to THIS week's occurrence (the one in the week currently being scheduled).
        Only cover more weeks if the user asks (e.g. "off the next three Saturdays" → three calls
        with the next three Saturday dates; "off Wed through Sat" → one call per date in that range).
        Undo with mark_available for the specific date. Marking someone off also frees up any
        shift they were already assigned that day (it becomes OPEN and needs coverage) — tell the
        manager which shifts opened up so they can reassign or remove them. The add_shift and
        assign_shift tools automatically REFUSE to double-book someone (two overlapping shifts)
        or to schedule them on a date they're off, and return the reason.

        Guidance:
        - Use the tools to actually make changes; don't just describe what you would do.
        - For minor choices, make a reasonable decision and note it rather than asking.
        - After making changes, reply with a short, plain-English confirmation of what you did.
        - If a tool reports a conflict, explain it plainly and ask whether to schedule anyway.
          Only retry with force=true if the user clearly confirms they want to override.
        - If something is genuinely ambiguous (e.g. two employees match a name), ask briefly.
        """;

    /// <summary>Run a tool by name against the schedule and return a plain-text result for the model.</summary>
    public string Execute(string name, JsonElement args)
    {
        try
        {
            switch (name)
            {
                case "add_shift":
                    return _svc.AddShift(
                        Str(args, "day"), Str(args, "start_time"), Str(args, "end_time"),
                        Str(args, "role"), StrOrNull(args, "employee_name"), Bool(args, "force")).Message;
                case "assign_shift":
                    return _svc.AssignShift(Int(args, "shift_id"), Str(args, "employee_name"), Bool(args, "force")).Message;
                case "mark_unavailable":
                    return _svc.MarkUnavailable(Str(args, "employee_name"), Str(args, "date"));
                case "mark_available":
                    return _svc.MarkAvailable(Str(args, "employee_name"), Str(args, "date"));
                case "get_availability":
                {
                    var off = _svc.GetDaysOff(Str(args, "employee_name"));
                    return off.Count == 0
                        ? "No upcoming days off — available every day."
                        : "Off on: " + string.Join(", ", off);
                }
                case "remove_shift":
                    return _svc.RemoveShift(Int(args, "shift_id"));
                case "swap_shifts":
                    return _svc.SwapShifts(Int(args, "shift_id_a"), Int(args, "shift_id_b"), Bool(args, "force")).Message;
                case "list_shifts":
                    return Format(_svc.ListShifts(StrOrNull(args, "day")));
                case "list_open_shifts":
                    return Format(_svc.ListOpenShifts());
                case "shifts_for_employee":
                    return Format(_svc.ShiftsForEmployee(Str(args, "employee_name")));
                case "add_employee":
                {
                    var area = HouseAreaExtensions.ParseArea(StrOrNull(args, "area"));
                    _svc.AddEmployee(Str(args, "name"), area);
                    return $"Added employee {Str(args, "name")} ({area.LongName()}).";
                }
                case "set_employee_area":
                    return _svc.SetEmployeeArea(Str(args, "name"),
                        HouseAreaExtensions.ParseArea(Str(args, "area")));
                case "remove_employee":
                    return _svc.RemoveEmployee(Str(args, "name"));
                case "list_employees":
                    var emps = _svc.ListEmployees();
                    return emps.Count == 0 ? "No employees yet."
                        : string.Join("\n", emps.Select(e => $"- {e.Name} ({e.Area})"));
                default:
                    return $"Unknown tool: {name}";
            }
        }
        catch (Exception ex)
        {
            return $"Error running {name}: {ex.Message}";
        }
    }

    private static string Format(List<Shift> shifts) =>
        shifts.Count == 0 ? "No matching shifts." : string.Join("\n", shifts.Select(Describe));

    private static string Describe(Shift s) =>
        $"shift #{s.Id}: {s.Day} {s.StartTime}–{s.EndTime} · {s.Role} · {s.EmployeeName ?? "OPEN"}";

    // ---------- tool definitions ----------

    private static JsonElement P(string type, string description) =>
        JsonSerializer.SerializeToElement(new { type, description });

    /// <summary>The full tool catalogue. Each provider converts these to its own schema format.</summary>
    public static IReadOnlyList<ToolDef> Definitions { get; } = new List<ToolDef>
    {
        new("add_shift",
            "Create a shift. Optionally assign an employee by name; if omitted or unknown, the shift is left open. Refuses to double-book or violate availability unless force is true.",
            new()
            {
                ["day"] = P("string", "ISO date, yyyy-MM-dd"),
                ["start_time"] = P("string", "Start time as written, e.g. 5pm"),
                ["end_time"] = P("string", "End time as written, e.g. Close or 11:00pm"),
                ["role"] = P("string", "Role for the shift: bartender, server, host, or kitchen"),
                ["employee_name"] = P("string", "Optional employee to assign"),
                ["force"] = P("boolean", "Set true ONLY to override a reported availability/double-booking conflict after the user confirms"),
            },
            new[] { "day", "start_time", "end_time", "role" }),
        new("assign_shift",
            "Assign (or reassign) an existing shift to an employee. Refuses to double-book or violate availability unless force is true.",
            new()
            {
                ["shift_id"] = P("integer", "The shift id"),
                ["employee_name"] = P("string", "Employee to assign"),
                ["force"] = P("boolean", "Set true ONLY to override a reported availability/double-booking conflict after the user confirms"),
            },
            new[] { "shift_id", "employee_name" }),
        new("remove_shift",
            "Delete a shift from the schedule.",
            new() { ["shift_id"] = P("integer", "The shift id") },
            new[] { "shift_id" }),
        new("swap_shifts",
            "Swap the assigned employees between two shifts. Refuses if it would double-book or violate availability unless force is true.",
            new()
            {
                ["shift_id_a"] = P("integer", "First shift id"),
                ["shift_id_b"] = P("integer", "Second shift id"),
                ["force"] = P("boolean", "Set true ONLY to override a reported conflict after the user confirms"),
            },
            new[] { "shift_id_a", "shift_id_b" }),
        new("list_shifts",
            "List shifts, optionally only those on a given ISO day (yyyy-MM-dd).",
            new() { ["day"] = P("string", "Optional ISO date filter") },
            Array.Empty<string>()),
        new("list_open_shifts",
            "List all shifts that have no employee assigned.",
            new(),
            Array.Empty<string>()),
        new("shifts_for_employee",
            "List all shifts assigned to a particular employee.",
            new() { ["employee_name"] = P("string", "Employee name") },
            new[] { "employee_name" }),
        new("add_employee",
            "Add (hire) a new staff member. Defaults to front-of-house if area is omitted.",
            new()
            {
                ["name"] = P("string", "Full name"),
                ["area"] = P("string", "Where they work: FOH (front of house — bar, servers, hosts) or BOH (back of house — kitchen)"),
            },
            new[] { "name" }),
        new("set_employee_area",
            "Move an existing employee between front-of-house (FOH) and back-of-house (BOH).",
            new()
            {
                ["name"] = P("string", "Employee name"),
                ["area"] = P("string", "FOH or BOH"),
            },
            new[] { "name", "area" }),
        new("list_employees",
            "List all staff members with their area (FOH or BOH).",
            new(),
            Array.Empty<string>()),
        new("remove_employee",
            "Remove (fire) a staff member. Their assigned shifts become open and their availability is cleared.",
            new() { ["name"] = P("string", "Employee name") },
            new[] { "name" }),
        new("mark_unavailable",
            "Mark an employee as OFF (unavailable) on ONE specific calendar date. Days off apply only to the exact date given, not every week. For several dates (e.g. multiple weeks, or a range), call this once per date. Employees are available every day until marked off. If they were already assigned a shift that day, it is automatically freed up (set OPEN) so it can be covered — the result lists any such shifts.",
            new()
            {
                ["employee_name"] = P("string", "Employee name"),
                ["date"] = P("string", "The specific date to mark off, as an ISO date (yyyy-MM-dd)"),
            },
            new[] { "employee_name", "date" }),
        new("mark_available",
            "Clear a day off — make the employee available again on one specific date.",
            new()
            {
                ["employee_name"] = P("string", "Employee name"),
                ["date"] = P("string", "The specific date to clear, as an ISO date (yyyy-MM-dd)"),
            },
            new[] { "employee_name", "date" }),
        new("get_availability",
            "Show an employee's upcoming days off (specific dates).",
            new() { ["employee_name"] = P("string", "Employee name") },
            new[] { "employee_name" }),
    };

    // ---------- argument helpers (robust to string/number JSON) ----------

    public static JsonElement ToElement(object? input) => JsonSerializer.SerializeToElement(input);

    private static string Str(JsonElement o, string key) => StrOrNull(o, key) ?? "";

    private static string? StrOrNull(JsonElement o, string key)
    {
        if (o.ValueKind != JsonValueKind.Object
            || !o.TryGetProperty(key, out var v)
            || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static int Int(JsonElement o, string key)
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var m)) return m;
        }
        return 0;
    }

    private static bool Bool(JsonElement o, string key)
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
        }
        return false;
    }
}
