using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;
using Microsoft.Maui.Storage;

namespace PROCTORSystemMulti_ProjectApp.ClassInsight;

/// <summary>
/// Teacher side of file transfer: send files to students, and collect ("hand in") work from them.
/// Handed-in files are saved only on this device, under Documents\ClassInsight\Handed in.
/// </summary>
public sealed partial class DashboardViewModel
{
    // ---- sending ------------------------------------------------------------

    private sealed class Outgoing
    {
        public required string Name { get; init; }
        public int Receivers;
        public int Ok;
        public readonly List<string> Failed = new();
    }

    private readonly Dictionary<string, Outgoing> _outgoing = new(StringComparer.Ordinal);
    private string _transferText = "";

    /// <summary>Progress / outcome of the latest file send or the running collection, for the banner.</summary>
    public string TransferText
    {
        get => _transferText;
        private set { _transferText = value; Raise(); Raise(nameof(HasTransferText)); }
    }

    public bool HasTransferText => !string.IsNullOrEmpty(_transferText) || IsCollecting;

    /// <summary>Send files to students (null targets = whole class). Returns an error to show, or null.</summary>
    public async Task<string?> SendFilesAsync(IEnumerable<Microsoft.Maui.Storage.FileResult> files, IReadOnlyList<string>? targets)
    {
        foreach (var file in files)
        {
            var name = FileLimits.CleanFileName(file.FileName) ?? "file";
            try
            {
                await using var stream = await file.OpenReadAsync();
                Stream source = stream;
                string? temp = null;
                if (!stream.CanSeek)
                {
                    // Some pickers hand back a forward-only stream: buffer it so the size is known.
                    temp = Path.Combine(FileSystem.CacheDirectory, Guid.NewGuid().ToString("N"));
                    await using (var copy = File.Create(temp)) await stream.CopyToAsync(copy);
                    source = File.OpenRead(temp);
                }
                try
                {
                    var size = source.Length;
                    if (size > FileLimits.MaxSize) return $"“{name}” is bigger than {FileLimits.MaxSize / (1024 * 1024)} MB.";
                    OnUi(() => TransferText = $"📁 Sending “{name}”…");
                    var progress = new Progress<long>(sent =>
                        TransferText = $"📁 Sending “{name}”… {(size == 0 ? 100 : sent * 100 / size)}%");
                    var accepted = await FileSender.SendAsync(
                        _client.OfferFileAsync, _client.SendFileChunkAsync, _client.FinishFileAsync, _client.CancelFileAsync,
                        source,
                        new FileOffer { FileName = name, Size = size, Targets = targets ?? [] },
                        progress);
                    OnUi(() =>
                    {
                        _outgoing[accepted.TransferId] = new Outgoing { Name = name, Receivers = accepted.Targets.Count };
                        TransferText = $"📁 “{name}” sent — waiting for {accepted.Targets.Count} PC(s) to save it…";
                    });
                }
                finally
                {
                    if (temp is not null) { await source.DisposeAsync(); try { File.Delete(temp); } catch { } }
                }
            }
            catch (Exception ex)
            {
                OnUi(() => TransferText = $"📁 “{name}” wasn't sent.");
                return ex.Message;
            }
        }
        return null;
    }

    private void OnFileResult(FileReceipt r)
    {
        if (!_outgoing.TryGetValue(r.TransferId, out var o)) return;
        if (r.Ok) o.Ok++;
        else o.Failed.Add($"{r.FromName}: {r.Error ?? "failed"}");
        var text = $"📁 “{o.Name}” saved on {o.Ok} of {o.Receivers} PC(s)";
        if (o.Failed.Count > 0) text += $" · {o.Failed.Count} failed ({string.Join("; ", o.Failed.Take(3))}{(o.Failed.Count > 3 ? "…" : "")})";
        TransferText = text;
        if (o.Ok + o.Failed.Count >= o.Receivers) _outgoing.Remove(r.TransferId);
    }

    // ---- collecting work ----------------------------------------------------

    private readonly Dictionary<string, FileAssembler> _handIns = new(StringComparer.Ordinal);
    private string? _collectFolder;
    private int _collectedFiles;
    private readonly HashSet<string> _collectedFrom = new(StringComparer.Ordinal);

    public bool IsCollecting { get; private set; }

    /// <summary>Where this collection's files are saved.</summary>
    public string? CollectFolder => _collectFolder;

    public async Task<string?> StartCollectingAsync(string? prompt, IReadOnlyList<string>? targets = null)
    {
        try
        {
            var asked = await _client.RequestHandInAsync(new HandInRequest { Prompt = prompt, Targets = targets ?? [] });
            _collectFolder = Path.Combine(TeacherFiles.Root, "Handed in", $"{DateTime.Now:yyyy-MM-dd HHmm}");
            _collectedFiles = 0;
            _collectedFrom.Clear();
            IsCollecting = true;
            Raise(nameof(IsCollecting));
            TransferText = $"📥 Collecting work from {asked} student(s) · nothing handed in yet";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public async Task StopCollectingAsync()
    {
        try { await _client.EndHandInAsync(); } catch { }
        IsCollecting = false;
        Raise(nameof(IsCollecting));
        TransferText = _collectedFiles == 0 ? "" : $"📥 Collected {_collectedFiles} file(s) from {_collectedFrom.Count} student(s).";
    }

    private void OnFileOffered(FileOffer offer)
    {
        if (!offer.HandIn) return;
        if (!IsCollecting || _collectFolder is null || FileLimits.CleanFileName(offer.FileName) is null)
        {
            _ = Report(offer.TransferId, "The teacher isn't collecting work any more.");
            return;
        }
        try
        {
            var dir = Path.Combine(FileSystem.CacheDirectory, "incoming");
            Directory.CreateDirectory(dir);
            _handIns[offer.TransferId] = new FileAssembler(offer, Path.Combine(dir, offer.TransferId + ".part"));
        }
        catch (Exception ex) { _ = Report(offer.TransferId, ex.Message); }
    }

    private void OnFileChunk(FileChunk chunk)
    {
        if (!_handIns.TryGetValue(chunk.TransferId, out var a) || a.Append(chunk)) return;
        _handIns.Remove(chunk.TransferId);
        a.Dispose();
        _ = Report(chunk.TransferId, "The file arrived out of order.");
    }

    private void OnFileFinished(FileFinish finish)
    {
        if (!_handIns.Remove(finish.TransferId, out var a)) return;
        using (a)
        {
            var data = a.Finish(finish.Sha256);
            if (data is null) { _ = Report(finish.TransferId, "The file arrived damaged."); return; }
            try
            {
                var student = FileLimits.CleanFileName(a.Offer.FromName) ?? "Student";
                var dir = Path.Combine(_collectFolder!, student);
                Directory.CreateDirectory(dir);
                var target = FileLimits.UniquePath(dir, FileLimits.CleanFileName(a.Offer.FileName)!);
                using (var output = new FileStream(target, FileMode.CreateNew))
                    data.CopyTo(output);
                FileLimits.MarkAsDownloaded(target);   // a student's file: SmartScreen / Protected View treat it as untrusted
                _collectedFiles++;
                _collectedFrom.Add(a.Offer.FromConnectionId ?? student);
                TransferText = $"📥 Collecting work · {_collectedFiles} file(s) from {_collectedFrom.Count} student(s) · latest: {a.Offer.FromName}";
                _ = Report(finish.TransferId, null);
            }
            catch (Exception ex) { _ = Report(finish.TransferId, "It couldn't be saved: " + ex.Message); }
        }
    }

    private void OnFileCancelled(string transferId)
    {
        if (_handIns.Remove(transferId, out var a)) a.Dispose();
    }

    private async Task Report(string transferId, string? error)
    {
        try { await _client.ReportFileResultAsync(new FileReceipt { TransferId = transferId, Ok = error is null, Error = error }); }
        catch { }
    }

    private void ResetFiles()
    {
        foreach (var a in _handIns.Values) a.Dispose();
        _handIns.Clear();
        _outgoing.Clear();
        if (IsCollecting) { IsCollecting = false; Raise(nameof(IsCollecting)); }
        TransferText = "";
    }
}
