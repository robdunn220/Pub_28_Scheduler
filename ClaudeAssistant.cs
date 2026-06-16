using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace PublicHouse28Scheduler;

/// <summary>
/// Wraps the Anthropic API. The user types plain English; Claude decides which scheduling
/// tool to call; we execute it against <see cref="SchedulerService"/> and feed the result back.
/// This is a manual tool-use loop, so OUR code performs every change to the schedule.
/// </summary>
public class ClaudeAssistant
{
    private readonly SchedulerService _svc;
    private readonly AnthropicClient? _client;
    private readonly List<ToolUnion> _tools;
    private readonly List<MessageParam> _messages = new();

    public bool HasApiKey { get; }

    public ClaudeAssistant(SchedulerService svc)
    {
        _svc = svc;
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        HasApiKey = !string.IsNullOrWhiteSpace(apiKey);
        if (HasApiKey)
            _client = new AnthropicClient { ApiKey = apiKey };
        _tools = BuildTools();
    }

    /// <summary>Send one user message, run any tool calls to completion, return the assistant's reply text.</summary>
    public async Task<string> SendAsync(string userMessage)
    {
        if (!HasApiKey || _client is null)
            return "⚠️  No API key found. Set the ANTHROPIC_API_KEY environment variable, then restart the app. " +
                   "You can get a key from https://console.anthropic.com/.";

        _messages.Add(new MessageParam { Role = Role.User, Content = userMessage });

        var finalText = new StringBuilder();

        try
        {
        for (int iteration = 0; iteration < 10; iteration++)
        {
            var parameters = new MessageCreateParams
            {
                Model = Model.ClaudeSonnet4_6,
                MaxTokens = 4096,
                System = SystemPrompt(),
                Tools = _tools,
                Messages = _messages,
            };

            Message response = await _client.Messages.Create(parameters);

            var assistantContent = new List<ContentBlockParam>();
            var toolResults = new List<ContentBlockParam>();
            bool calledTool = false;

            foreach (ContentBlock block in response.Content)
            {
                if (block.TryPickText(out TextBlock? text))
                {
                    assistantContent.Add(new TextBlockParam { Text = text!.Text });
                    finalText.AppendLine(text.Text);
                }
                else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                {
                    calledTool = true;
                    assistantContent.Add(new ToolUseBlockParam
                    {
                        ID = toolUse!.ID,
                        Name = toolUse.Name,
                        Input = toolUse.Input,
                    });

                    string result = ExecuteTool(toolUse.Name, ToElement(toolUse.Input));
                    toolResults.Add(new ToolResultBlockParam
                    {
                        ToolUseID = toolUse.ID,
                        Content = result,
                    });
                }
            }

            // Echo the assistant turn back into history.
            _messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });

            if (!calledTool)
                return finalText.ToString().Trim();

            // Feed tool results back and let the model continue.
            _messages.Add(new MessageParam { Role = Role.User, Content = toolResults });
            finalText.Clear(); // keep only the model's final narrative, not interim "let me..." text
        }

        return finalText.Length > 0 ? finalText.ToString().Trim()
                                    : "(Stopped after several tool calls without a final answer.)";
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
                return "⚠️  The Anthropic account is out of credit. Add credits at "
                     + "console.anthropic.com → Plans & Billing, then try again.";
            return $"⚠️  Couldn't reach the assistant: {ex.Message}";
        }
    }

    private string SystemPrompt() =>
        $"""
        You are the scheduling assistant for a bar called Public House 28. You help the manager
        build and adjust the EMPLOYEE shift schedule by calling the provided tools.

        Today is {DateTime.Today:dddd, MMMM d, yyyy} ({DateTime.Today:yyyy-MM-dd}).
        When the user names a day like "Friday" or "next Tuesday", resolve it to a concrete
        calendar date and pass it to tools as an ISO date string (yyyy-MM-dd).

        Times can be written naturally — keep them as the user says them (e.g. "5pm", "Close",
        "11:00pm"). Common roles at this bar: bartender, server, host, kitchen.

        Employees are available every day by default. Mark a day off with mark_unavailable —
        ONE weekday per call. For a range like "Wednesday to Saturday", call it once for each
        day in the range (Wednesday, Thursday, Friday, Saturday). Undo with mark_available.
        The add_shift and assign_shift tools automatically REFUSE to double-book someone (two
        overlapping shifts) or to schedule them on a day they're off, and return the reason.

        Guidance:
        - Use the tools to actually make changes; don't just describe what you would do.
        - For minor choices, make a reasonable decision and note it rather than asking.
        - After making changes, reply with a short, plain-English confirmation of what you did.
        - If a tool reports a conflict, explain it plainly and ask whether to schedule anyway.
          Only retry with force=true if the user clearly confirms they want to override.
        - If something is genuinely ambiguous (e.g. two employees match a name), ask briefly.
        """;

    private string ExecuteTool(string name, JsonElement args)
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
                    if (!TryDay(Str(args, "day_of_week"), out int offDay))
                        return $"Didn't recognize day of week \"{Str(args, "day_of_week")}\".";
                    return _svc.MarkUnavailable(Str(args, "employee_name"), offDay);
                case "mark_available":
                    if (!TryDay(Str(args, "day_of_week"), out int onDay))
                        return $"Didn't recognize day of week \"{Str(args, "day_of_week")}\".";
                    return _svc.MarkAvailable(Str(args, "employee_name"), onDay);
                case "get_availability":
                {
                    var off = _svc.GetDaysOff(Str(args, "employee_name"));
                    return off.Count == 0
                        ? "Available every day."
                        : "Off on: " + string.Join(", ", off.Select(SchedulerService.DayName));
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
                    _svc.AddEmployee(Str(args, "name"));
                    return $"Added employee {Str(args, "name")}.";
                case "remove_employee":
                    return _svc.RemoveEmployee(Str(args, "name"));
                case "list_employees":
                    var emps = _svc.ListEmployees();
                    return emps.Count == 0 ? "No employees yet."
                        : string.Join("\n", emps.Select(e => $"- {e.Name}"));
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

    // ---------- tool schemas ----------

    private static List<ToolUnion> BuildTools()
    {
        static JsonElement P(string type, string description) =>
            JsonSerializer.SerializeToElement(new { type, description });

        var tools = new List<ToolUnion>
        {
            new Tool
            {
                Name = "add_shift",
                Description = "Create a shift. Optionally assign an employee by name; if omitted or unknown, the shift is left open. Refuses to double-book or violate availability unless force is true.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["day"] = P("string", "ISO date, yyyy-MM-dd"),
                        ["start_time"] = P("string", "Start time as written, e.g. 5pm"),
                        ["end_time"] = P("string", "End time as written, e.g. Close or 11:00pm"),
                        ["role"] = P("string", "Role for the shift: bartender, server, host, or kitchen"),
                        ["employee_name"] = P("string", "Optional employee to assign"),
                        ["force"] = P("boolean", "Set true ONLY to override a reported availability/double-booking conflict after the user confirms"),
                    },
                    Required = ["day", "start_time", "end_time", "role"],
                },
            },
            new Tool
            {
                Name = "assign_shift",
                Description = "Assign (or reassign) an existing shift to an employee. Refuses to double-book or violate availability unless force is true.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["shift_id"] = P("integer", "The shift id"),
                        ["employee_name"] = P("string", "Employee to assign"),
                        ["force"] = P("boolean", "Set true ONLY to override a reported availability/double-booking conflict after the user confirms"),
                    },
                    Required = ["shift_id", "employee_name"],
                },
            },
            new Tool
            {
                Name = "remove_shift",
                Description = "Delete a shift from the schedule.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement> { ["shift_id"] = P("integer", "The shift id") },
                    Required = ["shift_id"],
                },
            },
            new Tool
            {
                Name = "swap_shifts",
                Description = "Swap the assigned employees between two shifts. Refuses if it would double-book or violate availability unless force is true.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["shift_id_a"] = P("integer", "First shift id"),
                        ["shift_id_b"] = P("integer", "Second shift id"),
                        ["force"] = P("boolean", "Set true ONLY to override a reported conflict after the user confirms"),
                    },
                    Required = ["shift_id_a", "shift_id_b"],
                },
            },
            new Tool
            {
                Name = "list_shifts",
                Description = "List shifts, optionally only those on a given ISO day (yyyy-MM-dd).",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement> { ["day"] = P("string", "Optional ISO date filter") },
                    Required = [],
                },
            },
            new Tool
            {
                Name = "list_open_shifts",
                Description = "List all shifts that have no employee assigned.",
                InputSchema = new() { Properties = new Dictionary<string, JsonElement>(), Required = [] },
            },
            new Tool
            {
                Name = "shifts_for_employee",
                Description = "List all shifts assigned to a particular employee.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement> { ["employee_name"] = P("string", "Employee name") },
                    Required = ["employee_name"],
                },
            },
            new Tool
            {
                Name = "add_employee",
                Description = "Add (hire) a new staff member.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement> { ["name"] = P("string", "Full name") },
                    Required = ["name"],
                },
            },
            new Tool
            {
                Name = "list_employees",
                Description = "List all staff members and the roles they can work.",
                InputSchema = new() { Properties = new Dictionary<string, JsonElement>(), Required = [] },
            },
            new Tool
            {
                Name = "remove_employee",
                Description = "Remove (fire) a staff member. Their assigned shifts become open and their availability is cleared.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement> { ["name"] = P("string", "Employee name") },
                    Required = ["name"],
                },
            },
            new Tool
            {
                Name = "mark_unavailable",
                Description = "Mark an employee as OFF (unavailable) on a weekday. For a range of days, call this once per day. Employees are available every day until marked off.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["employee_name"] = P("string", "Employee name"),
                        ["day_of_week"] = P("string", "Weekday name: Sunday, Monday, Tuesday, Wednesday, Thursday, Friday, or Saturday"),
                    },
                    Required = ["employee_name", "day_of_week"],
                },
            },
            new Tool
            {
                Name = "mark_available",
                Description = "Clear a day off — make the employee available again on that weekday.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["employee_name"] = P("string", "Employee name"),
                        ["day_of_week"] = P("string", "Weekday name"),
                    },
                    Required = ["employee_name", "day_of_week"],
                },
            },
            new Tool
            {
                Name = "get_availability",
                Description = "Show which weekdays an employee is off.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement> { ["employee_name"] = P("string", "Employee name") },
                    Required = ["employee_name"],
                },
            },
        };
        return tools;
    }

    // ---------- argument helpers (robust to string/number JSON) ----------

    private static JsonElement ToElement(object? input) => JsonSerializer.SerializeToElement(input);

    private static string Str(JsonElement o, string key) => StrOrNull(o, key) ?? "";

    private static string? StrOrNull(JsonElement o, string key)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static int Int(JsonElement o, string key)
    {
        if (o.TryGetProperty(key, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var m)) return m;
        }
        return 0;
    }

    private static bool Bool(JsonElement o, string key)
    {
        if (o.TryGetProperty(key, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
        }
        return false;
    }

    private static readonly Dictionary<string, int> DayAbbrev = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sun"] = 0, ["mon"] = 1, ["tue"] = 2, ["tues"] = 2, ["wed"] = 3,
        ["thu"] = 4, ["thur"] = 4, ["thurs"] = 4, ["fri"] = 5, ["sat"] = 6,
    };

    private static bool TryDay(string s, out int dayOfWeek)
    {
        dayOfWeek = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (Enum.TryParse<DayOfWeek>(s, ignoreCase: true, out var dow))
        {
            dayOfWeek = (int)dow;
            return true;
        }
        if (DayAbbrev.TryGetValue(s, out var v))
        {
            dayOfWeek = v;
            return true;
        }
        return false;
    }
}
