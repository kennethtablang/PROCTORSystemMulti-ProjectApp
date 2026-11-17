using System.Text.Json;
using Microsoft.Maui.Storage;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>A lab PC this teacher device has seen before, remembered so it can be woken when it's off.</summary>
public sealed record KnownComputer
{
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public IReadOnlyList<string> MacAddresses { get; init; } = [];
    public DateTimeOffset LastSeen { get; init; }
}

/// <summary>
/// Remembered lab PCs per classroom server, stored only on this teacher device (the server keeps
/// nothing, AD-6). Entries not seen for 120 days are dropped; at most 500 per room.
/// </summary>
public static class KnownComputers
{
    private const int MaxPerRoom = 500;
    private static readonly TimeSpan Forget = TimeSpan.FromDays(120);
    private static readonly object Gate = new();

    public static IReadOnlyList<KnownComputer> For(string serverId)
    {
        lock (Gate) return Load(serverId);
    }

    public static void Remember(string serverId, string machineId, string machineName, IReadOnlyList<string> macs)
    {
        if (string.IsNullOrEmpty(machineId)) return;
        lock (Gate)
        {
            var list = Load(serverId).Where(k => k.MachineId != machineId).ToList();
            var previous = Load(serverId).FirstOrDefault(k => k.MachineId == machineId);
            list.Add(new KnownComputer
            {
                MachineId = machineId,
                MachineName = machineName,
                // Keep the last known MACs if this report had none.
                MacAddresses = macs.Count > 0 ? macs.ToArray() : previous?.MacAddresses ?? [],
                LastSeen = DateTimeOffset.UtcNow,
            });
            var keep = list.Where(k => DateTimeOffset.UtcNow - k.LastSeen < Forget)
                           .OrderByDescending(k => k.LastSeen).Take(MaxPerRoom).ToList();
            try { Preferences.Default.Set(Key(serverId), JsonSerializer.Serialize(keep)); } catch { }
        }
    }

    private static List<KnownComputer> Load(string serverId)
    {
        try
        {
            var json = Preferences.Default.Get(Key(serverId), "");
            return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<KnownComputer>>(json) ?? [];
        }
        catch { return []; }
    }

    private static string Key(string serverId) => "proctor.known." + serverId;
}
