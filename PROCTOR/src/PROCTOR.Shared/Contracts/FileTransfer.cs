using System.Security.Cryptography;

namespace PROCTOR.Shared.Contracts;

/// <summary>
/// Announces a file. Teacher → students ("send files"), or student → the collecting teacher
/// (<see cref="HandIn"/> = true). The server relays it and fills <see cref="TransferId"/> and
/// <see cref="FromName"/>; the bytes follow as <see cref="FileChunk"/>s and the transfer ends
/// with a <see cref="FileFinish"/> carrying the SHA-256. The server stores nothing (AD-6).
/// </summary>
public sealed record FileOffer
{
    public string TransferId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public long Size { get; init; }

    /// <summary>Teacher's send: these students (empty = whole class). Ignored for hand-ins.</summary>
    public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();

    public bool HandIn { get; init; }

    /// <summary>
    /// Teacher's send: once saved, the student PC opens "Files from teacher" with the file selected.
    /// (Older agents ignore it and just save the file.)
    /// </summary>
    public bool OpenOnArrival { get; init; }

    /// <summary>Set by the server: who it's from (computer · user for students).</summary>
    public string? FromName { get; init; }

    /// <summary>Set by the server: the sender's connection, so receivers can report back.</summary>
    public string? FromConnectionId { get; init; }
}

public sealed record FileChunk
{
    public string TransferId { get; init; } = string.Empty;
    public int Index { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
}

public sealed record FileFinish
{
    public string TransferId { get; init; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the whole file.</summary>
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>Receiver → sender (via the server): the file was saved, or why not.</summary>
public sealed record FileReceipt
{
    public string TransferId { get; init; } = string.Empty;
    public bool Ok { get; init; }
    public string? Error { get; init; }

    /// <summary>Set by the server: who is reporting.</summary>
    public string? FromConnectionId { get; init; }
    public string? FromName { get; init; }
}

/// <summary>Teacher → students: please hand in work (empty targets = whole class).</summary>
public sealed record HandInRequest
{
    public string? Prompt { get; init; }
    public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();
}

/// <summary>Server → student: a teacher is collecting work (<see cref="Open"/> false = no longer).</summary>
public sealed record HandInInfo
{
    public string TeacherConnectionId { get; init; } = string.Empty;
    public string? Prompt { get; init; }
    public bool Open { get; init; } = true;
}

public static class FileLimits
{
    public const long MaxSize = 100L * 1024 * 1024;   // 100 MB per file
    public const int ChunkSize = 32 * 1024;           // well under the hub's 1 MB message limit
    public const int MaxHandInFilesPerStudent = 10;
    public const int MaxOpenTransfersPerSender = 4;
    public const int MaxNameLength = 120;

    /// <summary>
    /// Types that run code when opened: programs and scripts, installers, shortcuts, macro-enabled
    /// Office files, compiled help, and disk images/containers (which can hide their contents from
    /// Mark-of-the-Web). Teachers can send them only with a join code (not in open mode), and
    /// students can never hand them in. Saved files are also marked as downloaded (Zone 3) so
    /// SmartScreen and Office Protected View still apply to everything else.
    /// </summary>
    public static readonly IReadOnlySet<string> Executable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // programs, libraries, installers
        ".exe", ".com", ".scr", ".pif", ".dll", ".ocx", ".sys", ".drv", ".cpl", ".msc",
        ".msi", ".msp", ".mst", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller",
        ".application", ".appref-ms", ".xbap", ".gadget", ".vsix", ".jar", ".jnlp",
        // scripts
        ".bat", ".cmd", ".ps1", ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2", ".psd1", ".psm1", ".msh", ".msh1", ".msh2",
        ".mshxml", ".msh1xml", ".msh2xml", ".vb", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ws", ".wsc", ".sct",
        ".hta", ".py", ".pyw", ".pl", ".rb", ".sh",
        // shortcuts and shell / settings files
        ".lnk", ".url", ".website", ".scf", ".shb", ".shs", ".library-ms", ".searchconnector-ms", ".settingcontent-ms",
        ".inf", ".ins", ".isp", ".reg", ".cer", ".crt", ".der", ".diagcab", ".diagpkg", ".mof",
        // help files that run script
        ".chm", ".hlp",
        // macro-enabled Office and Access databases
        ".docm", ".dotm", ".xlsm", ".xltm", ".xlsb", ".xlam", ".xll", ".pptm", ".potm", ".ppam", ".ppsm", ".sldm",
        ".mdb", ".mde", ".accdb", ".accde", ".accda", ".ade", ".adp", ".one", ".onepkg",
        // disk images / containers (can strip Mark-of-the-Web from what's inside)
        ".iso", ".img", ".vhd", ".vhdx", ".udf",
    };

    /// <summary>The Zone.Identifier stream content that marks a file as from the internet (Mark-of-the-Web).</summary>
    public const string InternetZoneMark = "[ZoneTransfer]\r\nZoneId=3\r\n";

    /// <summary>Mark a saved file as downloaded so SmartScreen / Protected View apply. Best effort (NTFS only).</summary>
    public static void MarkAsDownloaded(string path)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", InternetZoneMark); } catch { }
    }

    public static bool IsExecutable(string fileName) => Executable.Contains(Path.GetExtension(fileName));

    /// <summary>
    /// A safe leaf file name: no folders, no characters Windows rejects, no reserved device names,
    /// no trailing dots/spaces, at most <see cref="MaxNameLength"/> characters. Null if nothing usable is left.
    /// </summary>
    public static string? CleanFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = new HashSet<char>("<>:\"/\\|?*");
        var chars = name.Select(c => char.IsControl(c) || invalid.Contains(c) ? '_' : c).ToArray();
        var clean = new string(chars).Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean.All(c => c == '.' || c == '_')) return null;
        var stem = Path.GetFileNameWithoutExtension(clean).ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                             "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (reserved.Contains(stem)) clean = "_" + clean;
        if (clean.Length > MaxNameLength)
        {
            var ext = Path.GetExtension(clean);
            if (ext.Length > 20) ext = "";
            clean = clean[..(MaxNameLength - ext.Length)] + ext;
        }
        return clean;
    }

    /// <summary>A name that doesn't exist yet in <paramref name="folder"/>: "report.docx", "report (2).docx", …</summary>
    public static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }

    public static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}

/// <summary>
/// Receives one file: chunks must arrive in order and never exceed the announced size, the bytes
/// go to a temporary file, and <see cref="Finish"/> checks length and SHA-256 before handing it over.
/// </summary>
public sealed class FileAssembler : IDisposable
{
    private readonly FileStream _temp;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _next;

    public FileAssembler(FileOffer offer, string tempPath)
    {
        Offer = offer;
        TempPath = tempPath;
        _temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
    }

    public FileOffer Offer { get; }
    public string TempPath { get; }
    public long Received { get; private set; }

    /// <summary>Add the next chunk; false (and the transfer should be dropped) if it's out of order or too big.</summary>
    public bool Append(FileChunk chunk)
    {
        if (chunk.Index != _next || chunk.Data.Length > FileLimits.ChunkSize || Received + chunk.Data.Length > Offer.Size) return false;
        _temp.Write(chunk.Data);
        _hash.AppendData(chunk.Data);
        Received += chunk.Data.Length;
        _next++;
        return true;
    }

    /// <summary>
    /// Verify the whole file arrived intact. On success returns the temp stream rewound to the start
    /// (it's deleted when disposed, so copy it out first); null when the size or hash is wrong.
    /// </summary>
    public Stream? Finish(string sha256)
    {
        if (Received != Offer.Size) return null;
        if (!string.Equals(FileLimits.Hex(_hash.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase)) return null;
        _temp.Flush();
        _temp.Position = 0;
        return _temp;
    }

    public void Dispose()
    {
        _temp.Dispose();
        _hash.Dispose();
    }
}
