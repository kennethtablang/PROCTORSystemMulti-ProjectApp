using System.Globalization;
using System.Text;
using System.Text.Json;
using PROCTOR.Shared;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Saved student groups ("Group A", "Row 1"): named sets of computers, remembered on this device
/// for each classroom. Picking a group selects its computers, so the toolbar acts on them. Also
/// the PC inventory export (hardware, Windows, disk space, agent version).
/// </summary>
public sealed partial class DashboardViewModel
{
    public const int MaxGroups = 50;
    public const int MaxGroupName = 40;

    /// <summary>This classroom's groups, in name order (empty when not connected).</summary>
    public IReadOnlyList<StudentGroup> Groups => _client.CurrentServer?.ServerId is { Length: > 0 } server ? LoadGroups(server) : [];

    /// <summary>How many of the group's computers are connected now.</summary>
    public int OnlineCount(StudentGroup group)
    {
        var members = group.Members.ToHashSet(StringComparer.Ordinal);
        return Students.Count(s => members.Contains(s.SeatKey));
    }

    /// <summary>Select the group's connected computers (replacing the selection, or adding to it).</summary>
    public int SelectGroup(StudentGroup group, bool add = false)
    {
        var members = group.Members.ToHashSet(StringComparer.Ordinal);
        var n = 0;
        foreach (var s in Students)
        {
            var inGroup = members.Contains(s.SeatKey);
            if (inGroup) n++;
            if (inGroup || !add) s.IsSelected = inGroup;
        }
        return n;
    }

    /// <summary>Save the selected students as a group (a group with the same name is replaced). Returns an error, or null.</summary>
    public string? SaveGroup(string name)
    {
        if (_client.CurrentServer?.ServerId is not { Length: > 0 } server) return "Connect to the classroom first.";
        name = CleanGroupName(name);
        if (name.Length == 0) return "Type a name for the group.";
        var members = SelectedStudents.Select(s => s.SeatKey).Distinct(StringComparer.Ordinal).ToList();
        if (members.Count == 0) return "Select the students first (tick their screens).";

        var groups = LoadGroups(server).Where(g => !g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (groups.Count >= MaxGroups) return $"There are already {MaxGroups} groups. Delete one first.";
        groups.Add(new StudentGroup { Name = name, Members = members });
        SaveGroups(server, groups);
        Raise(nameof(Groups));
        return null;
    }

    public void DeleteGroup(StudentGroup group)
    {
        if (_client.CurrentServer?.ServerId is not { Length: > 0 } server) return;
        SaveGroups(server, LoadGroups(server).Where(g => !g.Name.Equals(group.Name, StringComparison.OrdinalIgnoreCase)).ToList());
        Raise(nameof(Groups));
    }

    /// <summary>Show a message in the status pill (bottom right).</summary>
    public void ShowStatus(string text) => Status = text;

    public static string CleanGroupName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > MaxGroupName ? clean[..MaxGroupName].Trim() : clean;
    }

    private static List<StudentGroup> LoadGroups(string server)
    {
        try
        {
            var json = LoadPref(GroupsKey(server));
            if (string.IsNullOrEmpty(json)) return [];
            return (JsonSerializer.Deserialize<List<StudentGroup>>(json) ?? [])
                .Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Name))
                .Select(g => g with { Name = CleanGroupName(g.Name), Members = (g.Members ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).ToList() })
                .OrderBy(g => g.Name, NaturalComparer.Instance)
                .Take(MaxGroups)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void SaveGroups(string server, List<StudentGroup> groups)
    {
        try { SavePref(GroupsKey(server), JsonSerializer.Serialize(groups.OrderBy(g => g.Name, NaturalComparer.Instance).ToList())); } catch { }
    }

    private static string GroupsKey(string server) => "proctor.groups." + server;

    // ---- PC inventory ---------------------------------------------------------------------

    /// <summary>The newest agent version among the connected PCs (older ones are flagged).</summary>
    public Version? NewestAgentVersion => Students
        .Select(s => ProductVersion.Parse(s.Inventory?.AgentVersion))
        .OfType<Version>()
        .DefaultIfEmpty()
        .Max();

    /// <summary>Save the connected PCs' inventory as CSV; returns the file.</summary>
    public string ExportInventoryCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Computer,User,IP address,MAC address,Manufacturer,Model,Processor,Cores,Memory (GB),Disk (GB),Disk free (GB),Windows,Started,Agent version");
        foreach (var t in Students.OrderBy(s => s.MachineName, NaturalComparer.Instance))
        {
            var i = t.Inventory;
            sb.AppendLine(string.Join(",",
                Csv(t.MachineName), Csv(t.UserName ?? ""), Csv(t.IpAddress), Csv(string.Join(" ", t.MacAddresses)),
                Csv(i?.Manufacturer ?? ""), Csv(i?.Model ?? ""), Csv(i?.CpuName ?? ""),
                i is null ? "" : i.CpuCores.ToString(CultureInfo.InvariantCulture),
                Gb(i?.MemoryTotalBytes), Gb(i?.DiskTotalBytes), Gb(i?.DiskFreeBytes),
                Csv(i?.Windows ?? ""),
                i?.BootedAt is { } b ? Csv(b.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)) : "",
                Csv(i?.AgentVersion ?? "")));
        }
        var room = _client.CurrentServer?.RoomName ?? "Classroom";
        return TeacherFiles.Save("Inventory", $"Inventory_{Safe(room)}_{DateTime.Now:yyyy-MM-dd_HHmm}.csv",
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray());
    }

    private static string Gb(long? bytes) =>
        bytes is { } b && b > 0 ? (b / 1024d / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) : "";
}

/// <summary>A named set of computers (seat keys: upper-case computer names).</summary>
public sealed record StudentGroup
{
    public string Name { get; init; } = "";
    public List<string> Members { get; init; } = [];
}
