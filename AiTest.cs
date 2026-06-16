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
}
