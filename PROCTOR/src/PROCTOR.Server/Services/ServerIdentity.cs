using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;

namespace PROCTOR.Server.Services;

/// <summary>
/// The server's stable identity and deployment configuration, resolved once at startup
/// and shared by the hub, the discovery beacon and teacher auth. Carries the room name,
/// the LAN address clients should dial, the open/closed teacher-mode decision and the
/// effective teacher code.
///
/// Implements the plan's security model for teacher pairing (FR-SEC-02/03/06, NFR-SEC-04).
/// </summary>
public sealed class ServerIdentity
{
    public string ServerId { get; }
    public string RoomName { get; }
    public int Port { get; }

    /// <summary>LAN IPv4 address clients dial (also embedded in discovery responses).</summary>
    public string Host { get; }

    /// <summary>When true the server runs "open": teachers join with no code (FR-SEC-03).</summary>
    public bool AllowOpenTeacherMode { get; }

    /// <summary>
    /// The code a teacher must present when <see cref="AllowOpenTeacherMode"/> is false.
    /// Empty only when open. In closed mode with no configured code, one is generated.
    /// </summary>
    public string TeacherCode { get; }

    /// <summary>
    /// In open teacher mode, whether teachers may still use the strongest controls (log off / restart /
    /// shut down, internet off, closing programs). Off by default: without a join code anyone on the
    /// network can be a teacher.
    /// </summary>
    public bool AllowStrongControlsInOpenMode { get; }

    /// <summary>When false the UDP discovery beacon is not started (tests, or broadcast-hostile networks).</summary>
    public bool DiscoveryEnabled { get; }

    /// <summary>
    /// STUN/TURN servers handed to both WebRTC peers (relay fallback for segmented networks, P4).
    /// Empty on a flat lab LAN, where peers connect directly. Only given to registered clients,
    /// since TURN entries carry credentials.
    /// </summary>
    public IReadOnlyList<IceServerConfig> IceServers { get; }

    /// <summary>The TLS key fingerprint clients pin (NFR-SEC-06).</summary>
    public string Fingerprint { get; }

    public ServerIdentity(IConfiguration configuration, ServerCertificate certificate, ILogger<ServerIdentity> logger)
    {
        var section = configuration.GetSection("PROCTOR");
        Fingerprint = certificate.Fingerprint;

        ServerId = Guid.NewGuid().ToString("N");
        RoomName = section.GetValue<string>("RoomName") ?? "Classroom";
        Port = section.GetValue<int?>("Port") ?? Protocol.DefaultServerPort;
        AllowOpenTeacherMode = section.GetValue<bool?>("AllowOpenTeacherMode") ?? false;
        Host = ResolveLanAddress();
        DiscoveryEnabled = section.GetValue<bool?>("DiscoveryEnabled") ?? true;
        AllowStrongControlsInOpenMode = section.GetValue<bool?>("AllowStrongControlsInOpenMode") ?? false;
        IceServers = section.GetSection("IceServers").Get<IceServerConfig[]>()?
            .Where(s => !string.IsNullOrWhiteSpace(s.Urls))
            .ToArray() ?? [];

        var configured = section.GetValue<string>("TeacherCode");
        if (AllowOpenTeacherMode)
        {
            TeacherCode = string.Empty;
            logger.LogWarning(
                "Teacher mode is OPEN: any teacher on the LAN can connect without a code. " +
                "This is intended for a trusted lab VLAN only. Set PROCTOR:AllowOpenTeacherMode=false to require a code.");
        }
        else if (!string.IsNullOrWhiteSpace(configured))
        {
            TeacherCode = configured.Trim();
            logger.LogInformation("Teacher mode is CLOSED. Teachers must present the configured join code.");
        }
        else
        {
            TeacherCode = GenerateCode();
            logger.LogInformation("Teacher mode is CLOSED. Generated one-time join code for this run.");
        }

        logger.LogInformation(
            "PROCTOR server \"{Room}\" ({ServerId}) listening on {Host}:{Port}{HubPath}",
            RoomName, ServerId, Host, Port, Protocol.HubPath);

        // The fingerprint is how agents and teachers know they reached THIS server: IT copies it
        // into the agents' ServerFingerprint setting, and teachers compare it when they first connect.
        Console.WriteLine();
        Console.WriteLine("  Server fingerprint (pin this in agents as PROCTOR:ServerFingerprint;");
        Console.WriteLine("  teachers check it the first time they connect):");
        Console.WriteLine("    " + ServerTrust.Format(Fingerprint));
        logger.LogInformation("Server TLS fingerprint: {Fingerprint}", ServerTrust.Format(Fingerprint));

        if (!AllowOpenTeacherMode)
        {
            // Printed prominently so the teacher at the podium can read it off the server console.
            Console.WriteLine();
            Console.WriteLine("  ┌───────────────────────────────────────────┐");
            Console.WriteLine($"  │  Teacher join code:  {TeacherCode,-21}│");
            Console.WriteLine("  └───────────────────────────────────────────┘");
            Console.WriteLine();
        }
    }

    /// <summary>Build the discovery reply advertised to probing clients.</summary>
    public DiscoveryResponse ToDiscoveryResponse() => new()
    {
        Kind = "response",
        ServerId = ServerId,
        RoomName = RoomName,
        Host = Host,
        Port = Port,
        HubPath = Protocol.HubPath,
        ProtocolVersion = Protocol.Version,
        Fingerprint = Fingerprint,
    };

    // Crockford base32: no I/L/O/U, so a code read off the console can't be mistyped as 1/0.
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// A one-time pairing code with 65 bits of entropy (13 base32 symbols), grouped for reading
    /// aloud, e.g. <c>7KQ2-M9XD-4TR8B</c>. FR-SEC-06 requires ≥ 64 bits so that, together with
    /// <see cref="TeacherAuthThrottle"/>, online guessing is infeasible.
    /// </summary>
    public static string GenerateCode()
    {
        Span<char> raw = stackalloc char[13];
        for (var i = 0; i < raw.Length; i++)
            raw[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        var s = new string(raw);
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    /// <summary>Canonical form for comparison: separators/whitespace dropped, upper-cased.</summary>
    public static string NormalizeCode(string? code) =>
        new string((code ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray())
            .ToUpperInvariant();

    /// <summary>
    /// Constant-time check of a presented teacher code (NFR-SEC-04). Both sides are normalised
    /// and hashed first, so neither the content nor the length of the real code leaks through
    /// timing. Always false when no code is configured.
    /// </summary>
    public bool VerifyTeacherCode(string? presented)
    {
        if (string.IsNullOrEmpty(TeacherCode)) return false;
        var a = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(NormalizeCode(presented)));
        var b = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(NormalizeCode(TeacherCode)));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// The LAN IPv4 to advertise: the adapter on the network actually in use (default gateway),
    /// not a VPN tap or virtual switch. Same choice the teacher app shows (<see cref="LanAddresses"/>).
    /// </summary>
    private static string ResolveLanAddress() => LanAddresses.Best()?.Address.ToString() ?? "127.0.0.1";
}
