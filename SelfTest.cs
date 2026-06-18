namespace PublicHouse28Scheduler;

/// <summary>
/// Headless sanity check for the data layer (no GUI, no API key needed).
/// Run with:  dotnet run -- --selftest
/// </summary>
public static class SelfTest
{
    public static void Run()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ph28_selftest_{Guid.NewGuid():N}.db");
        try
        {
            var svc = new SchedulerService(dbPath);

            Console.WriteLine("== Seeded employees (FOH listed before BOH) ==");
            foreach (var e in svc.ListEmployees())
                Console.WriteLine($"  {e.Name} [{e.Area}]");

            Console.WriteLine("\n== Seeded shifts ==");
            foreach (var s in svc.ListShifts())
                Console.WriteLine($"  #{s.Id} {s.Day} {s.StartTime}-{s.EndTime} {s.Role} -> {s.EmployeeName ?? "OPEN"}");

            Console.WriteLine("\n== Add a new assigned shift (simulating add_shift) ==");
            Console.WriteLine("  " + svc.AddShift("2026-06-20", "6:00pm", "Close", "server", "Marcus").Message);

            Console.WriteLine("\n== Assign an open shift (simulating assign_shift) ==");
            var open = svc.ListOpenShifts().FirstOrDefault();
            if (open is not null)
                Console.WriteLine("  " + svc.AssignShift(open.Id, "Tom").Message);

            Console.WriteLine("\n== Day-off + double-booking checks ==");
            // Sarah is seeded with bartender 5:00pm–Close on Friday 2026-06-19. Mark a specific date off.
            Console.WriteLine("  " + svc.MarkUnavailable("Sarah", "2026-06-22"));
            Console.WriteLine("  double-book (Fri 5pm-Close): " +
                svc.AddShift("2026-06-19", "5:00pm", "Close", "server", "Sarah").Message);
            Console.WriteLine("  day off (2026-06-22): " +
                svc.AddShift("2026-06-22", "5:00pm", "Close", "server", "Sarah").Message);
            Console.WriteLine("  same weekday, different week stays AVAILABLE (2026-06-29): " +
                svc.AddShift("2026-06-29", "5:00pm", "Close", "server", "Sarah").Message);
            Console.WriteLine("  override w/ force: " +
                svc.AddShift("2026-06-22", "5:00pm", "Close", "server", "Sarah", force: true).Message);

            Console.WriteLine("\n== Marking off frees an already-assigned shift ==");
            // Sarah holds the seeded Friday 2026-06-19 bartender shift. Tell her to take that day off.
            Console.WriteLine("  before: Sarah on Fri = " +
                string.Join(", ", svc.ShiftsForEmployee("Sarah").Where(s => s.Day == "2026-06-19").Select(s => $"#{s.Id}")));
            Console.WriteLine("  " + svc.MarkUnavailable("Sarah", "2026-06-19"));
            Console.WriteLine("  after:  Sarah on Fri = " +
                string.Join(", ", svc.ShiftsForEmployee("Sarah").Where(s => s.Day == "2026-06-19").Select(s => $"#{s.Id}")) + " (should be empty)");
            Console.WriteLine("  freed shift now appears as open: " +
                string.Join(", ", svc.ListOpenShifts().Where(s => s.Day == "2026-06-19").Select(s => $"#{s.Id} {s.Role}")));

            Console.WriteLine("\n== Swap shifts (simulating swap_shifts) ==");
            Console.WriteLine("  " + svc.SwapShifts(1, 2).Message);
            foreach (var s in svc.ListShifts("2026-06-19"))
                Console.WriteLine($"    #{s.Id} {s.Role} -> {s.EmployeeName ?? "OPEN"}");

            Console.WriteLine("\n== Hire & fire (add/remove employee) ==");
            svc.AddEmployee("Dana Cole", HouseArea.BOH);
            Console.WriteLine("  after hire:  " + string.Join(", ", svc.ListEmployees().Select(e => $"{e.Name} [{e.Area}]")));
            Console.WriteLine("  " + svc.SetEmployeeArea("Dana", HouseArea.FOH));
            Console.WriteLine("  after move:  " + string.Join(", ", svc.ListEmployees().Select(e => $"{e.Name} [{e.Area}]")));
            Console.WriteLine("  " + svc.RemoveEmployee("Tom"));
            Console.WriteLine("  after fire:  " + string.Join(", ", svc.ListEmployees().Select(e => e.Name)));

            Console.WriteLine("\n== Open shifts remaining ==");
            var remaining = svc.ListOpenShifts();
            Console.WriteLine(remaining.Count == 0 ? "  (none)" :
                string.Join("\n", remaining.Select(s => $"  #{s.Id} {s.Role}")));

            Console.WriteLine("\n== Shifts for Sarah ==");
            foreach (var s in svc.ShiftsForEmployee("Sarah"))
                Console.WriteLine($"  #{s.Id} {s.Day} {s.Role}");

            CheckExport(svc);

            CheckAssistantSchemas(svc);

            Console.WriteLine("\nSELFTEST OK");
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>Render the seeded week to PDF and PNG bytes and confirm both come out non-empty.</summary>
    private static void CheckExport(SchedulerService svc)
    {
        Console.WriteLine("\n== Export weekly schedule (PDF + PNG) ==");
        var week = new DateTime(2026, 6, 19); // the seeded Friday's week

        var (pdf, pdfExt) = SchedulePdfExporter.Render(svc, week, ScheduleExportFormat.Pdf);
        var (png, pngExt) = SchedulePdfExporter.Render(svc, week, ScheduleExportFormat.Png);
        Console.WriteLine($"  {pdfExt.ToUpperInvariant()}: {pdf.Length:n0} bytes");
        Console.WriteLine($"  {pngExt.ToUpperInvariant()}: {png.Length:n0} bytes");

        if (pdf.Length == 0 || png.Length == 0)
            throw new Exception("Export produced an empty file.");
    }

    /// <summary>
    /// Offline (no key, no network) check that BOTH providers convert the shared tool catalogue
    /// into their own schema without error, and that each provider constructs and degrades
    /// gracefully when no API key is set. Throws if a count doesn't match so --selftest fails loudly.
    /// </summary>
    private static void CheckAssistantSchemas(SchedulerService svc)
    {
        Console.WriteLine("\n== Assistant tool schemas build offline (no key/network) ==");

        int defs = SchedulerTools.Definitions.Count;
        int claudeCount = ClaudeAssistant.ToolSchemaCount();
        int geminiCount = GeminiAssistant.ToolSchemaCount();
        Console.WriteLine($"  shared definitions:     {defs}");
        Console.WriteLine($"  Claude tools built:     {claudeCount}");
        Console.WriteLine($"  Gemini functions built: {geminiCount}");

        if (claudeCount != defs || geminiCount != defs)
            throw new Exception($"Tool schema count mismatch (defs={defs}, claude={claudeCount}, gemini={geminiCount}).");

        // Constructing each provider with no key should succeed and simply report HasApiKey=false.
        var claude = new ClaudeAssistant(svc);
        var gemini = new GeminiAssistant(svc);
        Console.WriteLine($"  Claude:  provider={claude.ProviderName}, key={claude.ApiKeyEnvVar}, HasApiKey={claude.HasApiKey}");
        Console.WriteLine($"  Gemini:  provider={gemini.ProviderName}, key={gemini.ApiKeyEnvVar}, HasApiKey={gemini.HasApiKey}");

        // Show the Gemini function-call schema so the (new, unverified) wire format can be eyeballed.
        Console.WriteLine("  Gemini functionDeclarations JSON:");
        Console.WriteLine(GeminiAssistant.ToolSchemaJson());
    }
}
