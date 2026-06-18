using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PublicHouse28Scheduler;

/// <summary>The file format a schedule can be exported to.</summary>
public enum ScheduleExportFormat { Pdf, Png }

/// <summary>
/// Renders one week of the schedule to a PDF or PNG laid out like the on-screen grid — days down
/// the left, one column per employee, with FOH and BOH grouped. It reads everything it needs from
/// <see cref="SchedulerService"/>, so it's pure data → file and safe to run off the UI thread.
/// </summary>
public static class SchedulePdfExporter
{
    // QuestPDF requires a license to be declared once before first use. The Community license is
    // free for individuals and small companies, which covers this app.
    static SchedulePdfExporter() => QuestPDF.Settings.License = LicenseType.Community;

    /// <summary>Monday of the week containing <paramref name="d"/>. Mirrors MainWindow.WeekStart.</summary>
    public static DateTime WeekStart(DateTime d)
    {
        int daysSinceMonday = ((int)d.DayOfWeek + 6) % 7;
        return d.Date.AddDays(-daysSinceMonday);
    }

    /// <summary>
    /// Build the weekly schedule for the week containing <paramref name="anyDayInWeek"/> in the
    /// requested <paramref name="format"/> and save it to the user's Downloads folder. Returns the
    /// full path of the written file.
    /// </summary>
    public static string Export(SchedulerService svc, DateTime anyDayInWeek, ScheduleExportFormat format = ScheduleExportFormat.Pdf)
    {
        var weekStart = WeekStart(anyDayInWeek);
        var (bytes, ext) = Render(svc, weekStart, format);
        var path = DownloadPath($"PublicHouse28-Schedule-{weekStart:yyyy-MM-dd}.{ext}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// Render the week (whose Monday is <paramref name="weekStart"/>) to file bytes plus the matching
    /// file extension. No file I/O — this is the part the self-test exercises.
    /// </summary>
    public static (byte[] Bytes, string Extension) Render(SchedulerService svc, DateTime weekStart, ScheduleExportFormat format = ScheduleExportFormat.Pdf)
    {
        weekStart = weekStart.Date;
        var weekEnd = weekStart.AddDays(6);
        var employees = svc.ListEmployees();

        // This week's shifts, paired with their parsed date — same filter the grid uses.
        var weekShifts = svc.ListShifts()
            .Select(s => (shift: s, date: ParseDay(s.Day)))
            .Where(x => x.date is DateTime d && d >= weekStart && d <= weekEnd)
            .ToList();

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(16);
                // WrapAnywhere lets long unbreakable tokens (e.g. "5:00pm–11:00pm") wrap inside a
                // narrow cell instead of overflowing it.
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Black).WrapAnywhere());

                page.Header().Column(col =>
                {
                    col.Item().Text("Public House 28 — Weekly Schedule")
                        .FontSize(18).Bold();
                    col.Item().Text($"{weekStart:MMM d} – {weekEnd:MMM d, yyyy}")
                        .FontSize(12).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(8);
                });

                page.Content().Element(content => BuildTable(content, employees, weekShifts, svc, weekStart));

                page.Footer().AlignRight().Text(t =>
                {
                    t.Span($"Generated {DateTime.Now:MMM d, yyyy h:mm tt}  ·  ")
                        .FontSize(8).FontColor(Colors.Grey.Medium);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                    t.Span(" / ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        });

        if (format == ScheduleExportFormat.Png)
        {
            // One landscape page → one PNG. Render at 2x for a crisp image.
            var png = doc.GenerateImages(new ImageGenerationSettings
            {
                ImageFormat = ImageFormat.Png,
                RasterDpi = 144,
            }).First();
            return (png, "png");
        }

        return (doc.GeneratePdf(), "pdf");
    }

    private static void BuildTable(
        IContainer content,
        List<Employee> employees,
        List<(Shift shift, DateTime? date)> weekShifts,
        SchedulerService svc,
        DateTime weekStart)
    {
        if (employees.Count == 0)
        {
            content.Text("No staff on the roster yet — add employees before exporting a schedule.")
                .Italic().FontColor(Colors.Grey.Darken1);
            return;
        }

        content.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(58);                       // day-label column
                foreach (var _ in employees) columns.RelativeColumn();
            });

            // Header row 1: FOH/BOH grouping bands. Header row 2: employee names.
            // (ListEmployees orders FOH first, then BOH, so each area is one unbroken run.)
            table.Header(header =>
            {
                header.Cell().RowSpan(2).Element(GroupHeaderCell).Text("");

                for (int start = 0; start < employees.Count;)
                {
                    var area = employees[start].Area;
                    uint span = 1;
                    while (start + span < employees.Count && employees[start + (int)span].Area == area) span++;
                    header.Cell().ColumnSpan(span).Element(GroupHeaderCell)
                        .AlignCenter().Text(area.LongName()).Bold();
                    start += (int)span;
                }

                foreach (var emp in employees)
                    header.Cell().Element(NameHeaderCell).AlignCenter().Text(emp.Name).SemiBold();
            });

            // One row per day, Monday → Sunday.
            for (int r = 0; r < 7; r++)
            {
                var day = weekStart.AddDays(r);
                var dayIso = day.ToString("yyyy-MM-dd");
                bool isToday = day == DateTime.Today;

                table.Cell().Element(c => DayCell(c, isToday)).Text(t =>
                {
                    t.Line(day.ToString("ddd", CultureInfo.InvariantCulture)).Bold();
                    t.Span(day.ToString("MMM d", CultureInfo.InvariantCulture)).FontSize(9);
                });

                foreach (var emp in employees)
                {
                    var shifts = weekShifts
                        .Where(x => x.date == day && x.shift.EmployeeId == emp.Id)
                        .Select(x => x.shift)
                        .ToList();
                    bool unavailable = !svc.IsAvailableOn(emp.Id, dayIso);

                    table.Cell().Element(c => BodyCell(c, isToday, unavailable && shifts.Count == 0)).Column(cell =>
                    {
                        cell.Spacing(3);
                        if (shifts.Count > 0)
                        {
                            foreach (var s in shifts)
                                cell.Item().Text(t =>
                                {
                                    t.Line($"{s.StartTime}–{s.EndTime}");
                                    t.Span(Capitalize(s.Role)).FontSize(9).FontColor(Colors.Grey.Darken2);
                                });
                        }
                        else if (unavailable)
                        {
                            cell.Item().Text("off").Italic().FontSize(9).FontColor(Colors.Grey.Medium);
                        }
                    });
                }
            }
        });
    }

    // ---------- cell styling ----------

    private static IContainer GroupHeaderCell(IContainer c) =>
        c.Border(0.5f).BorderColor(Colors.Grey.Lighten1).Background(Colors.Grey.Lighten2).Padding(5);

    private static IContainer NameHeaderCell(IContainer c) =>
        c.Border(0.5f).BorderColor(Colors.Grey.Lighten1).Background(Colors.Grey.Lighten3).Padding(5);

    private static IContainer DayCell(IContainer c, bool today) =>
        c.Border(0.5f).BorderColor(Colors.Grey.Lighten1)
            .Background(today ? Colors.Grey.Lighten2 : Colors.White).Padding(5);

    private static IContainer BodyCell(IContainer c, bool today, bool unavailable) =>
        c.Border(0.5f).BorderColor(Colors.Grey.Lighten1)
            .Background(unavailable ? Colors.Grey.Lighten2 : today ? Colors.Grey.Lighten4 : Colors.White)
            .Padding(5).MinHeight(34);

    // ---------- helpers ----------

    private static DateTime? ParseDay(string iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A path in the user's Downloads folder (falling back to the home folder), name made unique if needed.</summary>
    private static string DownloadPath(string fileName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, "Downloads");
        if (!Directory.Exists(dir)) dir = home;

        var path = Path.Combine(dir, fileName);
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}");
        return path;
    }
}
