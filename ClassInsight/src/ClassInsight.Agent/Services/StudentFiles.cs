using System.IO;
using System.Runtime.Versioning;
using ClassInsight.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace ClassInsight.Agent.Services;

/// <summary>
/// Student side of file transfer. Files from the teacher are assembled in the agent's own temp
/// folder, checked (size + SHA-256), then copied into "Files from teacher" on the student's desktop
/// <b>as the student</b>. Hand-in reads files from "Hand in" in the student's Documents, also as the
/// student — the agent's SYSTEM rights are never used on student-controlled paths.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class StudentFiles : IDisposable
{
    public const string ReceivedFolderName = "Files from teacher";
    public const string HandInFolderName = "Hand in";
    private const int MaxIncoming = 8;

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, FileAssembler> _incoming = new(StringComparer.Ordinal);
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ClassInsight", "incoming");

    public StudentFiles(ILogger logger) => _logger = logger;

    /// <summary>A teacher announced a file. Returns an error to report straight back, or null.</summary>
    public string? Offered(FileOffer offer)
    {
        if (offer.HandIn) return "Not a file for students.";
        if (FileLimits.CleanFileName(offer.FileName) is null || offer.Size is < 0 or > FileLimits.MaxSize) return "The file was refused.";
        lock (_gate)
        {
            if (_incoming.Count >= MaxIncoming) return "Too many files at once.";
            try
            {
                Directory.CreateDirectory(_tempDir);
                _incoming[offer.TransferId] = new FileAssembler(offer, Path.Combine(_tempDir, offer.TransferId + ".part"));
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't start receiving a file.");
                return "The PC couldn't store the file.";
            }
        }
    }

    /// <summary>A piece arrived. Returns false (and drops the transfer) if it's bad.</summary>
    public bool Chunk(FileChunk chunk)
    {
        lock (_gate)
        {
            if (!_incoming.TryGetValue(chunk.TransferId, out var a)) return true;   // unknown/already dropped: ignore
            if (a.Append(chunk)) return true;
            _incoming.Remove(chunk.TransferId);
            a.Dispose();
            return false;
        }
    }

    /// <summary>All bytes arrived: verify and save. Returns (saved path, error).</summary>
    public (string? Path, string? FileName, string? Error) Finished(FileFinish finish)
    {
        FileAssembler? a;
        lock (_gate)
        {
            if (!_incoming.Remove(finish.TransferId, out a)) return (null, null, null);
        }
        using (a)
        {
            var name = FileLimits.CleanFileName(a.Offer.FileName)!;
            var data = a.Finish(finish.Sha256);
            if (data is null) return (null, name, "The file arrived damaged.");
            try
            {
                var path = UserSession.AsStudent(UserSession.DesktopFolder, desktop =>
                {
                    var dir = Path.Combine(desktop, ReceivedFolderName);
                    Directory.CreateDirectory(dir);
                    var target = FileLimits.UniquePath(dir, name);
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    data.CopyTo(output);
                    return target;
                });
                _logger.LogInformation("Saved a file from the teacher: {Path}", path);
                return (path, name, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't save a file from the teacher.");
                return (null, name, "It couldn't be saved on the student's desktop.");
            }
        }
    }

    public void Cancelled(string transferId)
    {
        lock (_gate)
        {
            if (_incoming.Remove(transferId, out var a)) a.Dispose();
        }
    }

    /// <summary>Connection lost: drop everything half-received.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var a in _incoming.Values) a.Dispose();
            _incoming.Clear();
        }
    }

    // ---- hand-in ------------------------------------------------------------

    /// <summary>The student's hand-in folder (created if needed), as the student.</summary>
    public static string HandInFolder() => UserSession.AsStudent(UserSession.DocumentsFolder, docs =>
    {
        var dir = Path.Combine(docs, HandInFolderName);
        Directory.CreateDirectory(dir);
        return dir;
    });

    /// <summary>Files directly in the hand-in folder (no sub-folders, no links), as the student.</summary>
    public static IReadOnlyList<(string Name, long Size)> HandInFiles() => UserSession.AsStudent(UserSession.DocumentsFolder, docs =>
    {
        var dir = new DirectoryInfo(Path.Combine(docs, HandInFolderName));
        if (!dir.Exists) dir.Create();
        return (IReadOnlyList<(string, long)>)dir.EnumerateFiles()
            .Where(f => (f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Where(f => FileLimits.CleanFileName(f.Name) == f.Name && !FileLimits.IsExecutable(f.Name))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(50)
            .Select(f => (f.Name, f.Length))
            .ToList();
    });

    /// <summary>Open one hand-in file for reading, as the student (the handle stays usable afterwards).</summary>
    public static FileStream OpenHandInFile(string name) => UserSession.AsStudent(UserSession.DocumentsFolder, docs =>
    {
        if (FileLimits.CleanFileName(name) != name) throw new IOException("Bad file name.");
        var path = Path.Combine(docs, HandInFolderName, name);
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Links can't be handed in.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    });

    public void Dispose() => Clear();
}
