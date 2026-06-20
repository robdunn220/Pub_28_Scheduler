namespace PublicHouse28Scheduler;

/// <summary>
/// One-shot live test of the real assistant + tool-calling loop (makes real API calls).
/// Run with:  dotnet run -- --ai-test "your request"
/// </summary>
public static class AiTest
{
    public static void Run(string[] args)
    {
        int i = Array.IndexOf(args, "--ai-test");
        string prompt = (i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            ? args[i + 1]
            : "Set Marcus available Saturdays 5pm to close, then schedule him as bartender this Saturday 6pm to close.";

        var dbPath = Path.Combine(Path.GetTempPath(), $"ph28_aitest_{Guid.NewGuid():N}.db");
        try
        {
            var svc = new SchedulerService(dbPath);
            var assistant = AssistantFactory.Create(svc);

            if (!assistant.HasApiKey)
            {
                Console.WriteLine($"No {assistant.ApiKeyEnvVar} in the environment — can't run the live test ({assistant.ProviderName}).");
                return;
            }

            Console.WriteLine($"PROVIDER: {assistant.ProviderName}");
            Console.WriteLine($"PROMPT: {prompt}\n");
            var reply = assistant.SendAsync(prompt).GetAwaiter().GetResult();
            Console.WriteLine($"ASSISTANT:\n{reply}\n");

            Console.WriteLine("RESULTING SHIFTS:");
            foreach (var s in svc.ListShifts())
                Console.WriteLine($"  #{s.Id} {s.Day} {s.StartTime}-{s.EndTime} {s.Role} -> {s.EmployeeName ?? "OPEN"}");

            Console.WriteLine("\nDAYS OFF:");
            foreach (var e in svc.ListEmployees())
            {
                var off = svc.GetDaysOff(e.Name);
                if (off.Count == 0) continue;
                Console.WriteLine($"  {e.Name}: off " + string.Join(", ", off));
            }
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Live battery focused on the model's weakest job: turning natural-language weekdays into
    /// concrete ISO dates (single day, multi-week enumeration, and a weekday range). Each case runs
    /// against a fresh DB and prints the days off the model produced next to the rule-correct dates
    /// computed here in C#, so you can eyeball whether the model resolved them. Makes real API calls.
    /// Run with:  dotnet run -- --date-test
    /// </summary>
    public static void RunDateTests()
    {
        // The three cases mirror the examples in SchedulerTools.SystemPrompt():
        //   "this week's occurrence", "the next three Saturdays", and "Wed through Sat".
        var cases = new (string Prompt, DateTime[] Expected)[]
        {
            ("Mark Pat off this Saturday.",
                new[] { ThisWeekDate(DayOfWeek.Saturday) }),
            ("Pat is off the next three Saturdays.",
                new[] { UpcomingDate(DayOfWeek.Saturday),
                        UpcomingDate(DayOfWeek.Saturday).AddDays(7),
                        UpcomingDate(DayOfWeek.Saturday).AddDays(14) }),
            ("Pat is off Wednesday through Saturday this week.",
                new[] { ThisWeekDate(DayOfWeek.Wednesday), ThisWeekDate(DayOfWeek.Thursday),
                        ThisWeekDate(DayOfWeek.Friday), ThisWeekDate(DayOfWeek.Saturday) }),
        };

        Console.WriteLine($"Today is {DateTime.Today:dddd, yyyy-MM-dd}.\n");

        for (int c = 0; c < cases.Length; c++)
        {
            var (prompt, expected) = cases[c];
            var dbPath = Path.Combine(Path.GetTempPath(), $"ph28_datetest_{Guid.NewGuid():N}.db");
            try
            {
                var svc = new SchedulerService(dbPath);
                svc.AddEmployee("Pat Tester", HouseArea.FOH); // a known target, independent of seed data
                var assistant = AssistantFactory.Create(svc);

                if (!assistant.HasApiKey)
                {
                    Console.WriteLine($"No {assistant.ApiKeyEnvVar} in the environment — can't run the live test ({assistant.ProviderName}).");
                    return;
                }

                if (c == 0) Console.WriteLine($"PROVIDER: {assistant.ProviderName}\n");

                Console.WriteLine($"CASE {c + 1}: {prompt}");
                var reply = assistant.SendAsync(prompt).GetAwaiter().GetResult();

                // GetDaysOff only returns dates from today onward, so compare against the same window.
                var expectedIso = expected.Where(d => d.Date >= DateTime.Today)
                                          .Select(d => d.ToString("yyyy-MM-dd")).OrderBy(s => s).ToList();
                var actualIso = svc.GetDaysOff("Pat").OrderBy(s => s).ToList();
                bool match = expectedIso.SequenceEqual(actualIso);

                Console.WriteLine($"  expected: {string.Join(", ", expectedIso)}");
                Console.WriteLine($"  actual:   {(actualIso.Count == 0 ? "(none)" : string.Join(", ", actualIso))}");
                Console.WriteLine($"  {(match ? "MATCH" : "DIFF — eyeball the reply below")}");
                if (!match) Console.WriteLine($"  reply: {reply.Replace("\n", " ")}");
                Console.WriteLine();
            }
            finally
            {
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }
    }

    /// <summary>The given weekday within the current Monday-start week (mirrors the app's week convention).</summary>
    private static DateTime ThisWeekDate(DayOfWeek dow)
    {
        var monday = SchedulePdfExporter.WeekStart(DateTime.Today);
        return monday.AddDays(((int)dow + 6) % 7); // Monday=0 … Sunday=6
    }

    /// <summary>The soonest upcoming occurrence of the weekday (today if today is that weekday).</summary>
    private static DateTime UpcomingDate(DayOfWeek dow) =>
        DateTime.Today.AddDays(((int)dow - (int)DateTime.Today.DayOfWeek + 7) % 7);
}
