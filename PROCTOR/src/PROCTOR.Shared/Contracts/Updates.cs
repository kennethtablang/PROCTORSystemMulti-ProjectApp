namespace PROCTOR.Shared.Contracts;

/// <summary>
/// Server → agent: the student-agent package this server carries (installed with the teacher
/// setup, so it's always the teacher's version). An older agent downloads it from
/// <see cref="Protocol.AgentUpdatePath"/> and installs it silently: student PCs follow the
/// teacher's PC without anyone visiting them.
/// </summary>
public sealed record AgentUpdateInfo
{
    /// <summary>"0.4.1".</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>SHA-256 of the MSI, lower-case hex: checked before it is run.</summary>
    public string Sha256 { get; init; } = string.Empty;

    public long Size { get; init; }
}
