using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace PublicHouse28Scheduler;

public partial class MainWindow : Window
{
    private readonly SchedulerService _svc;
    private readonly IAssistant _assistant;

    public MainWindow()
    {
        InitializeComponent();

        // schedule.db lives next to the executable.
        var dbPath = System.IO.Path.Combine(AppContext.BaseDirectory, "schedule.db");
        _svc = new SchedulerService(dbPath);
        // Hand the assistant a live read of the week on screen, so "export the schedule in view" works.
        _assistant = AssistantFactory.Create(_svc, () => _weekStart);

        SendButton.Click += (_, _) => Send();
        RefreshButton.Click += (_, _) => RefreshSchedule();
        PrevWeekButton.Click += (_, _) => ShowWeek(_weekStart.AddDays(-7));
        NextWeekButton.Click += (_, _) => ShowWeek(_weekStart.AddDays(7));
        TodayButton.Click += (_, _) => ShowWeek(WeekStart(DateTime.Today));
        ChatBubbleButton.Click += (_, _) => ToggleChat(true);
        ChatCollapseButton.Click += (_, _) => ToggleChat(false);
        InputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Send();
            }
        };

        RefreshSchedule();

        AddBubble("assistant",
            _assistant.HasApiKey
                ? $"Hi! I manage the Public House 28 schedule (powered by {_assistant.ProviderName}). "
                  + "Try: \"Put Sarah on bar Friday 5pm to close\" or \"Who's working Friday?\""
                : $"⚠️  No API key detected for {_assistant.ProviderName}. Set the {_assistant.ApiKeyEnvVar} environment "
                  + "variable and restart to enable the assistant. The schedule on the left still works.");
    }

    // ---------- weekly grid (days as rows, roles as columns) ----------

    private DateTime _weekStart = WeekStart(DateTime.Today);

    /// <summary>Monday of the week containing <paramref name="d"/>.</summary>
    private static DateTime WeekStart(DateTime d)
    {
        int daysSinceMonday = ((int)d.DayOfWeek + 6) % 7;
        return d.Date.AddDays(-daysSinceMonday);
    }

    private void ShowWeek(DateTime weekStart)
    {
        _weekStart = weekStart;
        RefreshSchedule();
    }

    private void ToggleChat(bool open)
    {
        ChatPanelBorder.IsVisible = open;
        ChatBubbleButton.IsVisible = !open;
        if (open) InputBox.Focus();
    }

    private void RefreshSchedule()
    {
        SchedulePanel.Children.Clear();

        var weekEnd = _weekStart.AddDays(6);
        WeekLabel.Text = $"{_weekStart:MMM d} – {weekEnd:MMM d, yyyy}";

        // Only this week's shifts, paired with their parsed date.
        var weekShifts = _svc.ListShifts()
            .Select(s => (shift: s, date: ParseDay(s.Day)))
            .Where(x => x.date is DateTime d && d >= _weekStart && d <= weekEnd)
            .ToList();

        // Columns are the current employees (dynamic — hiring/firing changes them), plus an Open column.
        var employees = _svc.ListEmployees();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                          // day-label column
        foreach (var _ in employees)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star))); // one per employee

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // area-group row
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // employee-name row
        for (int i = 0; i < 7; i++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // Row 0: FOH/BOH grouping header, each spanning that area's contiguous columns.
        // (ListEmployees orders FOH first, then BOH, so each area is one unbroken run.)
        AddCell(grid, 0, 0, HeaderText(""), header: true, rowSpan: 2);
        for (int start = 0; start < employees.Count; )
        {
            var area = employees[start].Area;
            int span = 1;
            while (start + span < employees.Count && employees[start + span].Area == area) span++;
            AddCell(grid, 0, start + 1, HeaderText(area.LongName()), header: true, colSpan: span, areaHeader: true);
            start += span;
        }

        // Row 1: one cell per employee name.
        for (int c = 0; c < employees.Count; c++)
            AddCell(grid, 1, c + 1, HeaderText(employees[c].Name), header: true);

        // One row per day, Monday → Sunday.
        for (int r = 0; r < 7; r++)
        {
            var day = _weekStart.AddDays(r);
            var dayIso = day.ToString("yyyy-MM-dd");
            bool isToday = day == DateTime.Today;

            AddCell(grid, r + 2, 0, new TextBlock
            {
                Text = day.ToString("ddd\nMMM d", CultureInfo.InvariantCulture),
                FontWeight = isToday ? FontWeight.Bold : FontWeight.SemiBold,
                Foreground = Brushes.Black,
            }, dayLabel: true, today: isToday);

            // One cell per employee — their shifts that day (time + role; the name is the column).
            for (int c = 0; c < employees.Count; c++)
            {
                var emp = employees[c];
                var shifts = weekShifts
                    .Where(x => x.date == day && x.shift.EmployeeId == emp.Id)
                    .Select(x => x.shift)
                    .ToList();

                bool unavailable = !_svc.IsAvailableOn(emp.Id, dayIso);

                var cell = new StackPanel { Spacing = 4 };
                if (shifts.Count > 0)
                {
                    foreach (var s in shifts)
                        cell.Children.Add(ShiftBlock(s));
                }
                else if (unavailable)
                {
                    cell.Children.Add(new TextBlock
                    {
                        Text = "off",
                        FontSize = 11,
                        FontStyle = FontStyle.Italic,
                        Foreground = Brushes.Gray,
                    });
                }

                AddCell(grid, r + 2, c + 1, cell, today: isToday, unavailable: unavailable);
            }
        }

        SchedulePanel.Children.Add(grid);
    }

    /// <summary>A shift rendered as time + role (+ id). No employee name — that's the column header.</summary>
    private static TextBlock ShiftBlock(Shift s) => new()
    {
        Text = $"{s.StartTime}–{s.EndTime}\n{Capitalize(s.Role)}",
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = Brushes.Black,
    };

    private static TextBlock HeaderText(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = 13,
        Foreground = Brushes.Black,
    };

    private static void AddCell(Grid grid, int row, int col, Control content,
        bool header = false, bool dayLabel = false, bool today = false, bool unavailable = false,
        int colSpan = 1, int rowSpan = 1, bool areaHeader = false)
    {
        string bg = areaHeader  ? "#E4E4E4" // FOH/BOH grouping band, distinct from the white name row
                  : header      ? "#FFFFFF"
                  : unavailable ? "#D9D9D9"
                  : today       ? "#ECECEC"
                  : (row % 2 == 0 ? "#FFFFFF" : "#F7F7F7");

        if (areaHeader && content is TextBlock tb)
            tb.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse(bg)),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(8, 6),
            MinWidth = dayLabel ? 78 : 0, // 0 lets the star columns shrink to fit the window
            Child = content,
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        if (colSpan > 1) Grid.SetColumnSpan(border, colSpan);
        if (rowSpan > 1) Grid.SetRowSpan(border, rowSpan);
        grid.Children.Add(border);
    }

    private static DateTime? ParseDay(string iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ---------- assistant chat ----------

    private async void Send()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        InputBox.Text = "";
        AddBubble("user", text);

        SendButton.IsEnabled = false;
        var thinking = AddBubble("assistant", "…");

        try
        {
            var reply = await _assistant.SendAsync(text);
            thinking.Text = string.IsNullOrWhiteSpace(reply) ? "(no response)" : reply;
        }
        catch (Exception ex)
        {
            thinking.Text = $"Error: {ex.Message}";
            thinking.Foreground = Brushes.IndianRed;
        }
        finally
        {
            SendButton.IsEnabled = true;
            RefreshSchedule();        // the assistant may have changed the schedule
            InputBox.Focus();
        }
    }

    /// <summary>Adds a chat bubble and returns the inner TextBlock so callers can update it later.</summary>
    private TextBlock AddBubble(string sender, string text)
    {
        bool isUser = sender == "user";
        var inner = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black };

        var bubble = new Border
        {
            Background = new SolidColorBrush(Color.Parse(isUser ? "#E6E6E6" : "#F5F5F5")),
            BorderBrush = new SolidColorBrush(Color.Parse("#CCCCCC")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Child = inner,
            HorizontalAlignment = isUser ? Avalonia.Layout.HorizontalAlignment.Right
                                         : Avalonia.Layout.HorizontalAlignment.Left,
            MaxWidth = 300,
        };

        ChatPanel.Children.Add(bubble);
        Dispatcher.UIThread.Post(
            () => ChatScroll.Offset = new Vector(0, ChatScroll.Extent.Height),
            DispatcherPriority.Background);
        return inner;
    }
}
