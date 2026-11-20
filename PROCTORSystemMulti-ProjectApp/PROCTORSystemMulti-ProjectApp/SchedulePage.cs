using PROCTOR.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// The class schedule: classes by weekday and time that turn limits or an exam on at the start and
/// off at the end. Saved on this device for this classroom; runs while this app is open and connected.
/// </summary>
public sealed class SchedulePage : ContentPage
{
    private static readonly string[] Actions = ["Turn on limits", "Start an exam"];
    private readonly DashboardViewModel _vm;
    private readonly VerticalStackLayout _list = new() { Spacing = 6 };
    private readonly Label _running = ModalUi.Text("", 13, Color.FromArgb("#A5D6A7"));
    private readonly Button _endNow = ModalUi.Danger("End it now");

    // Editor
    private readonly Entry _name = new() { Placeholder = "Class name, e.g. Programming 1 (BSIT 2A)", MaxLength = 60, MinimumWidthRequest = 260 };
    private readonly CheckBox[] _days = new CheckBox[7];
    private readonly TimePicker _start = new() { Time = new TimeSpan(8, 0, 0), WidthRequest = 110 };
    private readonly TimePicker _end = new() { Time = new TimeSpan(9, 30, 0), WidthRequest = 110 };
    private readonly Picker _action = new() { ItemsSource = Actions, SelectedIndex = 0, WidthRequest = 170 };
    private readonly CheckBox _takeLimits = new() { IsChecked = true, Color = ModalUi.Accent, VerticalOptions = LayoutOptions.Center };
    private readonly Label _limitsInfo = ModalUi.Text("", 12, ModalUi.Muted);
    private readonly Label _error = ModalUi.Text("", 12, Color.FromArgb("#EF9A9A"));
    private readonly Label _editorTitle = ModalUi.Text("Add a class", 13, ModalUi.Accent);
    private ScheduleEntry? _editing;

    public SchedulePage(DashboardViewModel vm)
    {
        _vm = vm;
        BackgroundColor = ModalUi.Overlay;

        // Days, Monday first.
        var dayRow = new List<View>();
        foreach (var d in new[] { 1, 2, 3, 4, 5, 6, 0 })
        {
            var box = new CheckBox { Color = ModalUi.Accent, VerticalOptions = LayoutOptions.Center, IsChecked = d is >= 1 and <= 5 };
            _days[d] = box;
            var label = ModalUi.Text(ScheduleEntry.DayNames[d], 13, Colors.White);
            label.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => box.IsChecked = !box.IsChecked) });
            dayRow.Add(new HorizontalStackLayout { Spacing = 0, Children = { box, label } });
        }

        _action.SelectedIndexChanged += (_, _) => UpdateLimitsInfo();
        _takeLimits.CheckedChanged += (_, _) => UpdateLimitsInfo();
        var takeLabel = ModalUi.Text("Use the limits that are on now", 13, Colors.White);
        takeLabel.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => _takeLimits.IsChecked = !_takeLimits.IsChecked) });

        var save = ModalUi.Primary("Save class");
        save.Clicked += (_, _) => Save();
        var clear = ModalUi.Secondary("New");
        clear.Clicked += (_, _) => Edit(null);

        var editor = new Border
        {
            Stroke = Color.FromArgb("#3A3E47"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(12, 8),
            Content = new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    _editorTitle,
                    _name,
                    ModalUi.Row(false, dayRow.ToArray()),
                    ModalUi.Row(false, ModalUi.Text("From", 13, Colors.White), _start, ModalUi.Text("to", 13, Colors.White), _end, _action),
                    ModalUi.Row(false, new HorizontalStackLayout { Spacing = 0, Children = { _takeLimits, takeLabel } }, _limitsInfo),
                    _error,
                    ModalUi.Row(true, clear, save),
                },
            },
        };

        _endNow.Clicked += async (_, _) =>
        {
            if (_vm.ActiveSchedule is not { } a) return;
            if (!await DisplayAlertAsync($"End {a.Name} now?", "What was on before it comes back. It starts again on its next day.", "End now", "Cancel")) return;
            _vm.SkipActiveSchedule();
            await Task.Delay(500);
            Refresh();
        };

        var close = ModalUi.Secondary("Close");
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        Content = ModalUi.Page(this, "Class schedule",
            "At the start of each class its limits (websites, programs, internet, flash drives, sound) or an exam turn on by themselves, " +
            "and at the end what was on before comes back. Times use this PC's clock. Keep PROCTOR open on this PC: the schedule runs in this app.",
            860,
            (ModalUi.Row(false, _running, _endNow), false),
            (editor, false),
            (_list, false),
            (ModalUi.Row(true, close), false));

        Edit(null);
        Refresh();
    }

    private void UpdateLimitsInfo()
    {
        var exam = _action.SelectedIndex == (int)ScheduleAction.Exam;
        _takeLimits.IsEnabled = !exam;
        if (exam)
        {
            var e = ExamPage.Saved;
            _limitsInfo.Text = "Uses your last exam settings (Exam button)" + (e.Title is { Length: > 0 } t ? $": {t}" : "");
        }
        else if (_takeLimits.IsChecked || _editing?.Limits is null)
        {
            _limitsInfo.Text = "Now: " + _vm.PolicySummary.Replace("Limits: ", "");
        }
        else
        {
            _limitsInfo.Text = "Keeps: " + _editing.ActionText.Replace("Limits: ", "");
        }
    }

    private void Edit(ScheduleEntry? e)
    {
        _editing = e;
        _error.Text = "";
        _editorTitle.Text = e is null ? "Add a class" : $"Edit {e.Name}";
        _name.Text = e?.Name ?? "";
        for (var d = 0; d < 7; d++) _days[d].IsChecked = e is null ? d is >= 1 and <= 5 : e.OnDay((DayOfWeek)d);
        _start.Time = TimeSpan.FromMinutes(e?.StartMinute ?? 8 * 60);
        _end.Time = TimeSpan.FromMinutes(e?.EndMinute ?? 9 * 60 + 30);
        _action.SelectedIndex = (int)(e?.Action ?? ScheduleAction.Limits);
        _takeLimits.IsChecked = e is null;
        UpdateLimitsInfo();
    }

    private void Save()
    {
        var days = 0;
        for (var d = 0; d < 7; d++) if (_days[d].IsChecked) days |= 1 << d;
        var action = (ScheduleAction)Math.Max(0, _action.SelectedIndex);
        var current = _vm.Policy with { ExamMode = false, ExamTitle = null };
        var limits = action == ScheduleAction.Limits ? (_takeLimits.IsChecked || _editing?.Limits is null ? current : _editing.Limits) : null;
        if (action == ScheduleAction.Limits && (limits is null || limits.IsEmpty))
        {
            _error.Text = "No limits are on right now. Set them first (Websites and Limits buttons), then save the class.";
            return;
        }
        var exam = action == ScheduleAction.Exam ? ExamPage.Saved : null;
        if (exam is not null && string.IsNullOrWhiteSpace(exam.Title)) exam = exam with { Title = _name.Text?.Trim() };

        var entry = (_editing ?? new ScheduleEntry()) with
        {
            Name = _name.Text ?? "",
            Days = days,
            StartMinute = (int)(_start.Time?.TotalMinutes ?? 0),
            EndMinute = (int)(_end.Time?.TotalMinutes ?? 0),
            Action = action,
            Limits = limits,
            Exam = exam,
        };
        if (_vm.SaveScheduleEntry(entry) is { } error) { _error.Text = error; return; }
        Edit(null);
        Refresh();
    }

    private void Refresh()
    {
        _running.Text = _vm.ActiveSchedule is { } a ? $"● Running now: {a.Name} until {a.EndText}" : "No class running now.";
        _endNow.IsVisible = _vm.ActiveSchedule is not null;

        _list.Children.Clear();
        var entries = _vm.Schedule;
        if (entries.Count == 0)
        {
            _list.Children.Add(ModalUi.Text("No classes yet. Set the limits you want (or your exam settings), then add a class above.", 12, ModalUi.Muted));
            return;
        }
        foreach (var e in entries)
        {
            var text = new VerticalStackLayout
            {
                Spacing = 1,
                Children =
                {
                    new Label { Text = e.Name, FontSize = 13, FontAttributes = FontAttributes.Bold, TextColor = e.Enabled ? Colors.White : ModalUi.Muted },
                    ModalUi.Text($"{e.DaysText} · {e.StartText}–{e.EndText}" + (e.Enabled ? "" : " · paused"), 12, ModalUi.Body),
                    ModalUi.Text(e.ActionText, 11, ModalUi.Muted),
                },
            };
            var edit = ModalUi.Secondary("Edit");
            edit.Clicked += (_, _) => Edit(e);
            var pause = ModalUi.Secondary(e.Enabled ? "Pause" : "Resume");
            pause.Clicked += (_, _) =>
            {
                if (_vm.SaveScheduleEntry(e with { Enabled = !e.Enabled }) is { } error) { _error.Text = error; return; }
                Refresh();
            };
            var delete = ModalUi.Danger("Delete");
            delete.Clicked += async (_, _) =>
            {
                if (!await DisplayAlertAsync($"Delete {e.Name}?", "It's removed from the schedule.", "Delete", "Cancel")) return;
                _vm.DeleteScheduleEntry(e);
                if (_editing?.Id == e.Id) Edit(null);
                Refresh();
            };
            // Text above the buttons: a wrapping row must never sit in an auto-width column.
            var row = new VerticalStackLayout { Spacing = 4, Children = { text, ModalUi.Row(false, edit, pause, delete) } };
            _list.Children.Add(new Border
            {
                Stroke = Color.FromArgb("#3A3E47"),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(12, 6),
                Content = row,
            });
        }
    }

    protected override bool OnBackButtonPressed() => ModalUi.Back(this);
}
