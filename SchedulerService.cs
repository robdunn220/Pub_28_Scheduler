using Microsoft.Data.Sqlite;

namespace PublicHouse28Scheduler;

/// <summary>
/// All schedule data lives here, backed by a local SQLite file. Every operation the
/// Claude assistant can perform maps to a public method on this class, so the assistant
/// never touches the database directly — it only asks this service to do things.
/// </summary>
public class SchedulerService
{
    private readonly string _connectionString;

    public SchedulerService(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        EnsureSchema();
        SeedIfEmpty();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS employees (
                Id    INTEGER PRIMARY KEY AUTOINCREMENT,
                Name  TEXT NOT NULL,
                Area  TEXT NOT NULL DEFAULT 'FOH'
            );
            CREATE TABLE IF NOT EXISTS shifts (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                Day        TEXT NOT NULL,
                StartTime  TEXT NOT NULL,
                EndTime    TEXT NOT NULL,
                Role       TEXT NOT NULL,
                EmployeeId INTEGER NULL REFERENCES employees(Id) ON DELETE SET NULL
            );
            CREATE TABLE IF NOT EXISTS time_off (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                EmployeeId INTEGER NOT NULL,
                Date       TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        MigrateTimeOffToDates(conn);
        MigrateEmployeesAddArea(conn);
    }

    /// <summary>Add the Area column to employees databases created before FOH/BOH existed. Existing staff default to FOH.</summary>
    private static void MigrateEmployeesAddArea(SqliteConnection conn)
    {
        using var info = conn.CreateCommand();
        info.CommandText = "PRAGMA table_info(employees)";
        bool hasArea = false;
        using (var r = info.ExecuteReader())
            while (r.Read())
                if (string.Equals(r.GetString(1), "Area", StringComparison.OrdinalIgnoreCase))
                    hasArea = true;

        if (hasArea) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE employees ADD COLUMN Area TEXT NOT NULL DEFAULT 'FOH'";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Older databases stored time off as a recurring weekday (DayOfWeek 0–6), which meant
    /// "off Saturday" applied to every Saturday forever. Time off is now per specific date, so
    /// the old rows can't be carried over meaningfully — drop and rebuild the table with the new shape.
    /// </summary>
    private static void MigrateTimeOffToDates(SqliteConnection conn)
    {
        using var info = conn.CreateCommand();
        info.CommandText = "PRAGMA table_info(time_off)";
        bool hasOldColumn = false;
        using (var r = info.ExecuteReader())
            while (r.Read())
                if (string.Equals(r.GetString(1), "DayOfWeek", StringComparison.OrdinalIgnoreCase))
                    hasOldColumn = true;

        if (!hasOldColumn) return;

        using var migrate = conn.CreateCommand();
        migrate.CommandText = """
            DROP TABLE time_off;
            CREATE TABLE time_off (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                EmployeeId INTEGER NOT NULL,
                Date       TEXT NOT NULL
            );
            """;
        migrate.ExecuteNonQuery();
    }

    /// <summary>Give a brand-new database a little starting data so the app isn't empty on first run.</summary>
    private void SeedIfEmpty()
    {
        using var conn = Open();
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM employees";
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

        AddEmployee("Sarah Quinn", HouseArea.FOH);
        AddEmployee("Marcus Lee", HouseArea.FOH);
        AddEmployee("Priya Patel", HouseArea.FOH);
        AddEmployee("Tom Becker", HouseArea.BOH);

        // A couple of shifts for the coming Friday so the grid shows something.
        var friday = NextWeekday(DayOfWeek.Friday).ToString("yyyy-MM-dd");
        AddShift(friday, "5:00pm", "Close", "bartender", "Sarah Quinn", force: true);
        AddShift(friday, "5:00pm", "11:00pm", "server", "Priya Patel", force: true);
        AddShift(friday, "4:00pm", "Close", "kitchen", null, force: true); // intentionally open
    }

    private static DateTime NextWeekday(DayOfWeek target)
    {
        var today = DateTime.Today;
        int delta = ((int)target - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(delta == 0 ? 7 : delta);
    }

    // ---------- Employees ----------

    public int AddEmployee(string name, HouseArea area = HouseArea.FOH)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO employees (Name, Area) VALUES ($n, $a); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$a", area.ToString());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Move an employee between front- and back-of-house.</summary>
    public string SetEmployeeArea(string employeeName, HouseArea area)
    {
        var emp = FindEmployee(employeeName);
        if (emp is null) return $"No employee matching \"{employeeName}\".";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE employees SET Area = $a WHERE Id = $id";
        cmd.Parameters.AddWithValue("$a", area.ToString());
        cmd.Parameters.AddWithValue("$id", emp.Id);
        cmd.ExecuteNonQuery();
        return $"{emp.Name} is now {area.LongName()} ({area}).";
    }

    /// <summary>Employees ordered front-of-house first, then by name, so the two areas stay grouped.</summary>
    public List<Employee> ListEmployees()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Area FROM employees
            ORDER BY CASE Area WHEN 'FOH' THEN 0 ELSE 1 END, Name
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<Employee>();
        while (r.Read())
            list.Add(new Employee { Id = r.GetInt32(0), Name = r.GetString(1), Area = HouseAreaExtensions.ParseArea(r.GetString(2)) });
        return list;
    }

    /// <summary>Resolve a name to an employee: exact (case-insensitive) match preferred, else a partial match.</summary>
    public Employee? FindEmployee(string name)
    {
        var all = ListEmployees();
        name = name.Trim();
        return all.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Remove (fire) an employee: their shifts become open and their availability is cleared.</summary>
    public string RemoveEmployee(string employeeName)
    {
        var emp = FindEmployee(employeeName);
        if (emp is null) return $"No employee matching \"{employeeName}\".";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM time_off WHERE EmployeeId = $id;
            UPDATE shifts SET EmployeeId = NULL WHERE EmployeeId = $id;
            DELETE FROM employees WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", emp.Id);
        cmd.ExecuteNonQuery();
        return $"Removed {emp.Name}. Any shifts they had are now open.";
    }

    // ---------- Shifts ----------

    /// <summary>
    /// Add a shift. If <paramref name="employeeName"/> is given and matches a known employee,
    /// the shift is assigned; otherwise it's created as an open shift.
    /// </summary>
    public ShiftResult AddShift(string day, string startTime, string endTime, string role, string? employeeName, bool force = false)
    {
        Employee? emp = null;
        string note = "";
        if (!string.IsNullOrWhiteSpace(employeeName))
        {
            emp = FindEmployee(employeeName);
            if (emp is null)
                note = $" No employee matching \"{employeeName}\", so it's left OPEN.";
        }

        if (emp is not null && !force)
        {
            var conflicts = FindConflicts(emp.Id, day, startTime, endTime);
            if (conflicts.Count > 0)
                return new ShiftResult(null,
                    $"Did NOT create the shift — {emp.Name} can't take {day} {startTime}–{endTime}: "
                    + string.Join("; ", conflicts) + ". Tell me to schedule anyway if you want to override.",
                    Conflict: true);
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO shifts (Day, StartTime, EndTime, Role, EmployeeId)
            VALUES ($d, $s, $e, $r, $emp);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$d", day.Trim());
        cmd.Parameters.AddWithValue("$s", startTime.Trim());
        cmd.Parameters.AddWithValue("$e", endTime.Trim());
        cmd.Parameters.AddWithValue("$r", role.Trim());
        cmd.Parameters.AddWithValue("$emp", (object?)emp?.Id ?? DBNull.Value);
        int id = Convert.ToInt32(cmd.ExecuteScalar());

        var shift = GetShift(id)!;
        return new ShiftResult(shift, $"Created {Describe(shift)}.{note}", Conflict: false);
    }

    public ShiftResult AssignShift(int shiftId, string employeeName, bool force = false)
    {
        var shift = GetShift(shiftId);
        if (shift is null) return new ShiftResult(null, $"No shift with id {shiftId}.", false);
        var emp = FindEmployee(employeeName);
        if (emp is null) return new ShiftResult(null, $"No employee matching \"{employeeName}\".", false);

        if (!force)
        {
            var conflicts = FindConflicts(emp.Id, shift.Day, shift.StartTime, shift.EndTime, excludeShiftId: shiftId);
            if (conflicts.Count > 0)
                return new ShiftResult(shift,
                    $"Did NOT assign — {emp.Name} can't take shift #{shiftId} ({shift.Day} {shift.StartTime}–{shift.EndTime}): "
                    + string.Join("; ", conflicts) + ". Tell me to assign anyway if you want to override.",
                    Conflict: true);
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE shifts SET EmployeeId = $emp WHERE Id = $id";
        cmd.Parameters.AddWithValue("$emp", emp.Id);
        cmd.Parameters.AddWithValue("$id", shiftId);
        cmd.ExecuteNonQuery();
        return new ShiftResult(GetShift(shiftId), $"Assigned {emp.Name} to shift #{shiftId}.", false);
    }

    /// <summary>Exchange the assigned employees of two shifts. Conflict-checked unless forced.</summary>
    public ShiftResult SwapShifts(int shiftIdA, int shiftIdB, bool force = false)
    {
        var a = GetShift(shiftIdA);
        var b = GetShift(shiftIdB);
        if (a is null) return new ShiftResult(null, $"No shift with id {shiftIdA}.", false);
        if (b is null) return new ShiftResult(null, $"No shift with id {shiftIdB}.", false);
        if (a.EmployeeId is null && b.EmployeeId is null)
            return new ShiftResult(null, $"Both #{shiftIdA} and #{shiftIdB} are open — nothing to swap.", false);

        var exclude = new[] { a.Id, b.Id };
        if (!force)
        {
            var conflicts = new List<string>();
            if (b.EmployeeId is int) // b's person moves onto shift A
                conflicts.AddRange(FindConflicts(b.EmployeeId.Value, a.Day, a.StartTime, a.EndTime, exclude)
                    .Select(r => $"{b.EmployeeName} → #{a.Id}: {r}"));
            if (a.EmployeeId is int) // a's person moves onto shift B
                conflicts.AddRange(FindConflicts(a.EmployeeId.Value, b.Day, b.StartTime, b.EndTime, exclude)
                    .Select(r => $"{a.EmployeeName} → #{b.Id}: {r}"));
            if (conflicts.Count > 0)
                return new ShiftResult(null,
                    $"Did NOT swap — {string.Join("; ", conflicts)}. Tell me to swap anyway if you want to override.",
                    Conflict: true);
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE shifts SET EmployeeId = $eb WHERE Id = $a;
            UPDATE shifts SET EmployeeId = $ea WHERE Id = $b;
            """;
        cmd.Parameters.AddWithValue("$eb", (object?)b.EmployeeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ea", (object?)a.EmployeeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", a.Id);
        cmd.Parameters.AddWithValue("$b", b.Id);
        cmd.ExecuteNonQuery();

        return new ShiftResult(GetShift(a.Id),
            $"Swapped: #{a.Id} is now {b.EmployeeName ?? "OPEN"}, #{b.Id} is now {a.EmployeeName ?? "OPEN"}.", false);
    }

    public string RemoveShift(int shiftId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM shifts WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", shiftId);
        int n = cmd.ExecuteNonQuery();
        return n > 0 ? $"Removed shift #{shiftId}." : $"No shift with id {shiftId}.";
    }

    public Shift? GetShift(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = ShiftSelect + " WHERE s.Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadShift(r) : null;
    }

    /// <summary>List shifts, optionally only those on a given ISO day. Ordered by day then start time.</summary>
    public List<Shift> ListShifts(string? day = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = ShiftSelect + (day is null ? "" : " WHERE s.Day = $d") + " ORDER BY s.Day, s.StartTime";
        if (day is not null) cmd.Parameters.AddWithValue("$d", day.Trim());
        using var r = cmd.ExecuteReader();
        var list = new List<Shift>();
        while (r.Read()) list.Add(ReadShift(r));
        return list;
    }

    public List<Shift> ListOpenShifts()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = ShiftSelect + " WHERE s.EmployeeId IS NULL ORDER BY s.Day, s.StartTime";
        using var r = cmd.ExecuteReader();
        var list = new List<Shift>();
        while (r.Read()) list.Add(ReadShift(r));
        return list;
    }

    public List<Shift> ShiftsForEmployee(string employeeName)
    {
        var emp = FindEmployee(employeeName);
        if (emp is null) return new List<Shift>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = ShiftSelect + " WHERE s.EmployeeId = $emp ORDER BY s.Day, s.StartTime";
        cmd.Parameters.AddWithValue("$emp", emp.Id);
        using var r = cmd.ExecuteReader();
        var list = new List<Shift>();
        while (r.Read()) list.Add(ReadShift(r));
        return list;
    }

    private List<Shift> ShiftsForEmployeeId(int employeeId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = ShiftSelect + " WHERE s.EmployeeId = $emp ORDER BY s.Day, s.StartTime";
        cmd.Parameters.AddWithValue("$emp", employeeId);
        using var r = cmd.ExecuteReader();
        var list = new List<Shift>();
        while (r.Read()) list.Add(ReadShift(r));
        return list;
    }

    // ---------- availability (days off) ----------

    /// <summary>
    /// Mark an employee OFF on a specific calendar date (ISO yyyy-MM-dd). No-op if already off.
    /// Any shift they were assigned that day is freed up (set OPEN) so the gap is visible and
    /// can be covered — the shift slot is kept, never silently deleted.
    /// </summary>
    public string MarkUnavailable(string employeeName, string isoDate)
    {
        var emp = FindEmployee(employeeName);
        if (emp is null) return $"No employee matching \"{employeeName}\".";
        if (!TryNormalizeDate(isoDate, out var date))
            return $"Didn't recognize the date \"{isoDate}\". Use an ISO date like 2026-06-20.";

        // Shifts they're already on that day — these get freed up below.
        var affected = ShiftsForEmployeeId(emp.Id).Where(s => s.Day == date).ToList();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO time_off (EmployeeId, Date)
            SELECT $emp, $date
            WHERE NOT EXISTS (SELECT 1 FROM time_off WHERE EmployeeId = $emp AND Date = $date);

            UPDATE shifts SET EmployeeId = NULL WHERE EmployeeId = $emp AND Day = $date;
            """;
        cmd.Parameters.AddWithValue("$emp", emp.Id);
        cmd.Parameters.AddWithValue("$date", date);
        cmd.ExecuteNonQuery();

        var msg = $"{emp.Name} is now OFF on {FriendlyDate(date)}.";
        if (affected.Count > 0)
            msg += $" Freed up {affected.Count} shift(s) that day — now OPEN and needing coverage: "
                 + string.Join("; ", affected.Select(s => $"#{s.Id} {s.StartTime}–{s.EndTime} ({s.Role})")) + ".";
        return msg;
    }

    /// <summary>Clear a day off — make the employee available again on that specific date.</summary>
    public string MarkAvailable(string employeeName, string isoDate)
    {
        var emp = FindEmployee(employeeName);
        if (emp is null) return $"No employee matching \"{employeeName}\".";
        if (!TryNormalizeDate(isoDate, out var date))
            return $"Didn't recognize the date \"{isoDate}\". Use an ISO date like 2026-06-20.";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM time_off WHERE EmployeeId = $emp AND Date = $date";
        cmd.Parameters.AddWithValue("$emp", emp.Id);
        cmd.Parameters.AddWithValue("$date", date);
        cmd.ExecuteNonQuery();
        return $"{emp.Name} is available on {FriendlyDate(date)} again.";
    }

    /// <summary>ISO dates (yyyy-MM-dd) the employee is marked off, today onward, sorted.</summary>
    public List<string> GetDaysOff(string employeeName)
    {
        var emp = FindEmployee(employeeName);
        return emp is null ? new List<string>() : GetDaysOffById(emp.Id);
    }

    private List<string> GetDaysOffById(int employeeId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Date FROM time_off WHERE EmployeeId = $emp AND Date >= $today ORDER BY Date";
        cmd.Parameters.AddWithValue("$emp", employeeId);
        cmd.Parameters.AddWithValue("$today", DateTime.Today.ToString("yyyy-MM-dd"));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>True unless the employee is marked off on that specific ISO date.</summary>
    public bool IsAvailableOn(int employeeId, string isoDay)
    {
        if (!TryNormalizeDate(isoDay, out var date)) return true;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM time_off WHERE EmployeeId = $emp AND Date = $date LIMIT 1";
        cmd.Parameters.AddWithValue("$emp", employeeId);
        cmd.Parameters.AddWithValue("$date", date);
        return cmd.ExecuteScalar() is null;
    }

    /// <summary>Parse a date into canonical ISO form (yyyy-MM-dd), so storage and lookups always match.</summary>
    private static bool TryNormalizeDate(string input, out string iso)
    {
        iso = "";
        if (DateTime.TryParse(input?.Trim(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt))
        {
            iso = dt.ToString("yyyy-MM-dd");
            return true;
        }
        return false;
    }

    private static string FriendlyDate(string isoDate) =>
        DateTime.TryParse(isoDate, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt)
            ? dt.ToString("dddd, MMM d", System.Globalization.CultureInfo.InvariantCulture)
            : isoDate;

    // ---------- conflict detection ----------

    /// <summary>
    /// Reasons the employee can't take this shift: marked off on that specific date, or
    /// overlapping a shift they already have. Empty list = no conflict. Employees are
    /// available on any date they have not been explicitly marked off.
    /// </summary>
    public List<string> FindConflicts(int employeeId, string day, string startTime, string endTime, int? excludeShiftId = null)
        => FindConflicts(employeeId, day, startTime, endTime,
            excludeShiftId is int id ? new[] { id } : Array.Empty<int>());

    public List<string> FindConflicts(int employeeId, string day, string startTime, string endTime, IEnumerable<int> excludeShiftIds)
    {
        var exclude = new HashSet<int>(excludeShiftIds);
        var reasons = new List<string>();

        // 1) Day off (this specific calendar date)
        if (TryNormalizeDate(day, out var iso) && !IsAvailableOn(employeeId, iso))
            reasons.Add($"off on {FriendlyDate(iso)}");

        // 2) Double-booking (overlapping shift the same day)
        foreach (var s in ShiftsForEmployeeId(employeeId).Where(s => s.Day == day.Trim() && !exclude.Contains(s.Id)))
        {
            var overlap = ShiftTime.Overlaps(startTime, endTime, s.StartTime, s.EndTime);
            if (overlap == true)
                reasons.Add($"already booked #{s.Id} {s.StartTime}–{s.EndTime} ({s.Role})");
            else if (overlap is null)
                reasons.Add($"already has shift #{s.Id} {s.StartTime}–{s.EndTime} that day (times unclear)");
        }

        return reasons;
    }

    private static string Describe(Shift s) =>
        $"shift #{s.Id}: {s.Day} {s.StartTime}–{s.EndTime} · {s.Role} · {s.EmployeeName ?? "OPEN"}";

    private const string ShiftSelect = """
        SELECT s.Id, s.Day, s.StartTime, s.EndTime, s.Role, s.EmployeeId, e.Name
        FROM shifts s
        LEFT JOIN employees e ON e.Id = s.EmployeeId
        """;

    private static Shift ReadShift(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Day = r.GetString(1),
        StartTime = r.GetString(2),
        EndTime = r.GetString(3),
        Role = r.GetString(4),
        EmployeeId = r.IsDBNull(5) ? null : r.GetInt32(5),
        EmployeeName = r.IsDBNull(6) ? null : r.GetString(6),
    };
}
