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

            Console.WriteLine("== Seeded employees ==");
            foreach (var e in svc.ListEmployees())
                Console.WriteLine($"  {e.Name}");

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
            // Sarah is seeded with bartender 5:00pm–Close on Friday 2026-06-19. 2026-06-22 is a Monday.
            Console.WriteLine("  " + svc.MarkUnavailable("Sarah", (int)DayOfWeek.Monday));
            Console.WriteLine("  double-book (Fri 5pm-Close): " +
                svc.AddShift("2026-06-19", "5:00pm", "Close", "server", "Sarah").Message);
            Console.WriteLine("  day off (Mon): " +
                svc.AddShift("2026-06-22", "5:00pm", "Close", "server", "Sarah").Message);
            Console.WriteLine("  override w/ force: " +
                svc.AddShift("2026-06-22", "5:00pm", "Close", "server", "Sarah", force: true).Message);

            Console.WriteLine("\n== Swap shifts (simulating swap_shifts) ==");
            Console.WriteLine("  " + svc.SwapShifts(1, 2).Message);
            foreach (var s in svc.ListShifts("2026-06-19"))
                Console.WriteLine($"    #{s.Id} {s.Role} -> {s.EmployeeName ?? "OPEN"}");

            Console.WriteLine("\n== Hire & fire (add/remove employee) ==");
            svc.AddEmployee("Dana Cole");
            Console.WriteLine("  after hire:  " + string.Join(", ", svc.ListEmployees().Select(e => e.Name)));
            Console.WriteLine("  " + svc.RemoveEmployee("Tom"));
            Console.WriteLine("  after fire:  " + string.Join(", ", svc.ListEmployees().Select(e => e.Name)));

            Console.WriteLine("\n== Open shifts remaining ==");
            var remaining = svc.ListOpenShifts();
            Console.WriteLine(remaining.Count == 0 ? "  (none)" :
                string.Join("\n", remaining.Select(s => $"  #{s.Id} {s.Role}")));

            Console.WriteLine("\n== Shifts for Sarah ==");
            foreach (var s in svc.ShiftsForEmployee("Sarah"))
                Console.WriteLine($"  #{s.Id} {s.Day} {s.Role}");

            Console.WriteLine("\nSELFTEST OK");
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
