using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Dashboard selection (act on some students only) and the lab layout view (student screens laid
/// out like the computers in the room).
/// </summary>
public sealed partial class DashboardViewModel
{
    // ---- selection ------------------------------------------------------------------------

    /// <summary>Students ticked on the dashboard; the toolbar's actions apply to them only.</summary>
    public IReadOnlyList<StudentTile> SelectedStudents => Students.Where(s => s.IsSelected).ToList();
    public int SelectedCount => Students.Count(s => s.IsSelected);
    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => SelectedCount switch
    {
        0 => "",
        1 => "1 student selected · the toolbar acts on this student only",
        var n => $"{n} students selected · the toolbar acts on them only",
    };

    /// <summary>The selected students' connections, or null for the whole class.</summary>
    public IReadOnlyList<string>? SelectedTargets =>
        HasSelection ? Students.Where(s => s.IsSelected).Select(s => s.ConnectionId).ToArray() : null;

    /// <summary>"the whole class", "PC05" or "3 selected students", for prompts.</summary>
    public string TargetsWho => SelectedStudents switch
    {
        [] => "the whole class",
        [var one] => one.MachineName,
        var many => $"{many.Count} selected students",
    };

    /// <summary>Tick every student on screen (in the lab layout: every connected student).</summary>
    public void SelectAll()
    {
        foreach (var t in LayoutMode ? Students : VisibleStudents) t.IsSelected = true;
    }

    public void ClearSelection()
    {
        foreach (var t in Students) t.IsSelected = false;
    }

    private void OnTileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StudentTile.IsSelected)) RaiseSelection();
    }

    private void RaiseSelection()
    {
        Raise(nameof(SelectedCount));
        Raise(nameof(HasSelection));
        Raise(nameof(SelectionText));
    }

    // ---- lab layout -----------------------------------------------------------------------

    private const string PrefLayoutMode = "proctor.layoutmode";
    public const int MaxLayoutSide = 20;

    private LabLayout? _layout;
    private string? _layoutServer;
    private bool _layoutMode = LoadPref(PrefLayoutMode) == "1";

    /// <summary>Seats of the lab layout in reading order: a <see cref="StudentTile"/> or a <see cref="SeatSlot"/>.</summary>
    public ObservableCollection<object> SeatItems { get; } = new();

    /// <summary>What the dashboard grid shows: the lab layout, or the searched/sorted list.</summary>
    public IEnumerable GridItems => LayoutMode && _layout is not null ? SeatItems : VisibleStudents;

    /// <summary>Show students where they sit in the lab (rows × computers per row).</summary>
    public bool LayoutMode
    {
        get => _layoutMode && _layout is not null;
        set
        {
            if (_layoutMode == value) return;
            _layoutMode = value;
            SavePref(PrefLayoutMode, value ? "1" : "0");
            if (!value) IsArranging = false;
            RebuildSeats();
            RaiseLayout();
        }
    }

    public bool HasLayout => _layout is not null;
    public int LayoutRows => _layout?.Rows ?? 0;
    public int LayoutColumns => _layout?.Columns ?? 0;

    /// <summary>Search, sort and the size slider only apply to the normal grid.</summary>
    public bool ShowListControls => !LayoutMode;

    private bool _isArranging;
    /// <summary>Rearranging seats: tap a computer, then the seat it should move to (they swap).</summary>
    public bool IsArranging
    {
        get => _isArranging;
        set
        {
            if (_isArranging == value) return;
            _isArranging = value;
            ClearSwapSource();
            Raise();
            Raise(nameof(ArrangeText));
        }
    }

    public string ArrangeText => _swapFrom is { } from && from < (_layout?.Seats.Count ?? 0)
        ? $"Now tap the seat for {SeatName(from)} (the two swap)."
        : "Rearranging: tap a computer, then the seat it should move to. Empty seats are aisles or spare places.";

    private int? _swapFrom;

    private void RaiseLayout()
    {
        Raise(nameof(LayoutMode));
        Raise(nameof(HasLayout));
        Raise(nameof(LayoutRows));
        Raise(nameof(LayoutColumns));
        Raise(nameof(ShowListControls));
        Raise(nameof(GridItems));
        Raise(nameof(ArrangeText));
    }

    /// <summary>Make a new layout: rows × computers per row, filled in computer-name order.</summary>
    public void CreateLayout(int rows, int columns)
    {
        _layoutServer = _client.CurrentServer?.ServerId;   // the room this layout belongs to
        _layout = new LabLayout { Rows = Clamp(rows), Columns = Clamp(columns) };
        _layoutMode = true;
        SavePref(PrefLayoutMode, "1");
        AutoArrange();
    }

    /// <summary>Change the size, keeping every computer at the same row and position where it still fits.</summary>
    public void ResizeLayout(int rows, int columns)
    {
        if (_layout is null) { CreateLayout(rows, columns); return; }
        rows = Clamp(rows); columns = Clamp(columns);
        var seats = new string?[rows * columns];
        var overflow = new List<string>();
        for (var i = 0; i < _layout.Seats.Count; i++)
        {
            if (_layout.Seats[i] is not { } key) continue;
            int r = i / _layout.Columns, c = i % _layout.Columns;
            if (r < rows && c < columns) seats[r * columns + c] = key;
            else overflow.Add(key);
        }
        foreach (var key in overflow)
        {
            var free = Array.IndexOf(seats, null);
            if (free < 0) break;   // no room: connected ones get a new row when they're placed
            seats[free] = key;
        }
        _layout = new LabLayout { Rows = rows, Columns = columns, Seats = seats.ToList() };
        SaveLayout();
        SyncLayout();
    }

    /// <summary>Fill the seats in computer-name order (PC1, PC2 … PC10), connected and known ones.</summary>
    public void AutoArrange()
    {
        if (_layout is null) return;
        var names = Students.Select(s => s.SeatKey)
            .Concat(KnownNames().Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, NaturalComparer.Instance)
            .ToList();
        var rows = Math.Max(_layout.Rows, (names.Count + _layout.Columns - 1) / _layout.Columns);
        var seats = new string?[rows * _layout.Columns];
        for (var i = 0; i < names.Count && i < seats.Length; i++) seats[i] = names[i];
        _layout = _layout with { Rows = rows, Seats = seats.ToList() };
        SaveLayout();
        SyncLayout();
    }

    /// <summary>Forget the layout for this classroom (on this device).</summary>
    public void ClearLayout()
    {
        if (_layoutServer is { } server) SavePref(LayoutKey(server), "");
        _layout = null;
        IsArranging = false;
        RebuildSeats();
        RaiseLayout();
    }

    /// <summary>The seat a connected student sits at, or -1.</summary>
    public int SeatIndexOf(StudentTile tile) => _layout?.Seats.IndexOf(tile.SeatKey) ?? -1;

    /// <summary>Rearranging: first tap picks a computer, the second tap swaps it with that seat.</summary>
    public void SeatTapped(int index)
    {
        if (_layout is null || index < 0 || index >= _layout.Seats.Count) return;
        if (_swapFrom is not { } from)
        {
            if (_layout.Seats[index] is null) return;   // nothing to move from an empty seat
            _swapFrom = index;
            MarkSwapSource(index, true);
            Raise(nameof(ArrangeText));
            return;
        }

        ClearSwapSource();
        if (from != index)
        {
            var seats = _layout.Seats.ToList();
            (seats[from], seats[index]) = (seats[index], seats[from]);
            _layout = _layout with { Seats = seats };
            SaveLayout();
            RebuildSeats();
        }
        Raise(nameof(ArrangeText));
    }

    private void ClearSwapSource()
    {
        if (_swapFrom is { } from) MarkSwapSource(from, false);
        _swapFrom = null;
    }

    private void MarkSwapSource(int index, bool on)
    {
        if (index < 0 || index >= SeatItems.Count) return;
        switch (SeatItems[index])
        {
            case StudentTile t: t.IsSwapSource = on; break;
            case SeatSlot s: s.IsSwapSource = on; break;
        }
    }

    /// <summary>
    /// Roster changed or a classroom was joined: load that room's layout, give newly seen
    /// computers the first free seat (a new row when full), and rebuild the seats.
    /// </summary>
    private void SyncLayout()
    {
        var server = _client.CurrentServer?.ServerId;
        if (server != _layoutServer)
        {
            _layoutServer = server;
            _layout = server is null ? null : LoadLayout(server);
            IsArranging = false;
        }

        if (_layout is not null)
        {
            var seats = _layout.Seats.ToList();
            var placed = new HashSet<string>(seats.OfType<string>(), StringComparer.Ordinal);
            var changed = false;
            foreach (var tile in Students.OrderBy(s => s.SeatKey, NaturalComparer.Instance))
            {
                if (!placed.Add(tile.SeatKey)) continue;
                var free = seats.IndexOf(null);
                if (free < 0)
                {
                    free = seats.Count;
                    seats.AddRange(new string?[_layout.Columns]);
                }
                seats[free] = tile.SeatKey;
                changed = true;
            }
            if (changed)
            {
                _layout = _layout with { Rows = seats.Count / _layout.Columns, Seats = seats };
                SaveLayout();
            }
        }

        RebuildSeats();
        RaiseLayout();
    }

    /// <summary>Update <see cref="SeatItems"/> in place, so live tiles keep their video surfaces.</summary>
    private void RebuildSeats()
    {
        if (_layout is null || !_layoutMode)
        {
            if (SeatItems.Count > 0) SeatItems.Clear();
            return;
        }

        var online = new Dictionary<string, StudentTile>(StringComparer.Ordinal);
        foreach (var t in Students) online.TryAdd(t.SeatKey, t);
        var known = KnownNames();

        for (var i = 0; i < _layout.Seats.Count; i++)
        {
            var key = _layout.Seats[i];
            object item;
            if (key is not null && online.TryGetValue(key, out var tile))
                item = tile;
            else if (i < SeatItems.Count && SeatItems[i] is SeatSlot same && same.Key == key)
                item = same;
            else
                item = new SeatSlot(i, key, key is null ? "" : known.GetValueOrDefault(key, key));

            if (i < SeatItems.Count)
            {
                if (!ReferenceEquals(SeatItems[i], item)) SeatItems[i] = item;
            }
            else
            {
                SeatItems.Add(item);
            }
        }
        while (SeatItems.Count > _layout.Seats.Count) SeatItems.RemoveAt(SeatItems.Count - 1);
        foreach (var slot in SeatItems.OfType<SeatSlot>()) slot.Index = SeatItems.IndexOf(slot);
    }

    private string SeatName(int index) => _layout?.Seats[index] is { } key
        ? KnownNames().GetValueOrDefault(key, Students.FirstOrDefault(s => s.SeatKey == key)?.MachineName ?? key)
        : "that seat";

    /// <summary>Computers seen in this room before (on this device): seat key → display name.</summary>
    private Dictionary<string, string> KnownNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_layoutServer is { } server)
            foreach (var k in KnownComputers.For(server))
                if (!string.IsNullOrWhiteSpace(k.MachineName)) names.TryAdd(k.MachineName.ToUpperInvariant(), k.MachineName);
        foreach (var s in Students) names[s.SeatKey] = s.MachineName;
        return names;
    }

    private void SaveLayout()
    {
        if (_layoutServer is null || _layout is null) return;
        try { SavePref(LayoutKey(_layoutServer), JsonSerializer.Serialize(_layout)); } catch { }
    }

    private static LabLayout? LoadLayout(string server)
    {
        try
        {
            var json = LoadPref(LayoutKey(server));
            if (string.IsNullOrEmpty(json)) return null;
            var layout = JsonSerializer.Deserialize<LabLayout>(json);
            if (layout is null || layout.Columns < 1 || layout.Rows < 1) return null;
            var seats = layout.Seats.Take(layout.Rows * layout.Columns).ToList();
            while (seats.Count < layout.Rows * layout.Columns) seats.Add(null);
            return layout with { Seats = seats };
        }
        catch
        {
            return null;
        }
    }

    private static string LayoutKey(string server) => "proctor.layout." + server;
    private static int Clamp(int v) => Math.Clamp(v, 1, MaxLayoutSide);
}

/// <summary>A lab's seat map: <see cref="Seats"/> in reading order, each a computer name (upper-case) or null.</summary>
public sealed record LabLayout
{
    public int Rows { get; init; }
    public int Columns { get; init; }
    public List<string?> Seats { get; init; } = [];
}

/// <summary>A seat in the lab layout with no connected student: empty, or a computer that is off.</summary>
public sealed class SeatSlot(int index, string? key, string name) : INotifyPropertyChanged
{
    public int Index { get; set; } = index;
    public string? Key { get; } = key;
    public bool IsEmpty => Key is null;
    public string Title => IsEmpty ? "" : name;
    public string Detail => IsEmpty ? "Empty seat" : "Offline";

    private bool _isSwapSource;
    public bool IsSwapSource
    {
        get => _isSwapSource;
        set { if (_isSwapSource != value) { _isSwapSource = value; Raise(); Raise(nameof(BorderColor)); } }
    }

    public Color BorderColor => _isSwapSource ? Color.FromArgb("#66BB6A") : Color.FromArgb(IsEmpty ? "#2A2D34" : "#3A3E47");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Orders "PC2" before "PC10" (numbers compared as numbers).</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a is null || b is null) return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                var c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
