using System.Security.Cryptography;
using System.Text;

namespace ClassInsight.Shared;

/// <summary>Server → agent: sign this to prove which PC you are (NFR-SEC-06 agent enrolment).</summary>
public sealed record EnrollmentChallenge
{
    /// <summary>Random, single-use, base64.</summary>
    public string Nonce { get; init; } = string.Empty;

    /// <summary>
    /// Whether the server requires new PCs to present the lab enrolment key
    /// (<see cref="AgentEnrollment.Proof"/>). Informational: the agent always sends it when it has one.
    /// </summary>
    public bool KeyRequired { get; init; }
}

/// <summary>
/// Agent authentication (NFR-SEC-06). Every agent has its own ECDSA P-256 key, kept where only
/// SYSTEM and Administrators can read it. At registration it signs a single-use server nonce,
/// bound to the server's TLS key fingerprint, so a signature captured or relayed by a rogue server
/// is useless at the real one. The server binds each PC's <c>MachineId</c> to its public key the
/// first time it sees it; from then on only that key can register as that PC. Optionally the
/// server only admits <i>new</i> PCs that also present an HMAC made with the lab's enrolment key.
/// </summary>
public static class AgentEnrollment
{
    private const string SignContext = "ClassInsight-agent-register-v1";
    private const string ProofContext = "ClassInsight-agent-enrol-v1";

    /// <summary>The exact bytes an agent signs when registering.</summary>
    public static byte[] Payload(string serverFingerprint, string nonce, string machineId) =>
        Encoding.UTF8.GetBytes($"{SignContext}\n{serverFingerprint}\n{nonce}\n{machineId}");

    /// <summary>Lower-case hex SHA-256 of a public key (SPKI), used as the agent key's id.</summary>
    public static string KeyId(byte[] subjectPublicKeyInfo) =>
        Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo)).ToLowerInvariant();

    /// <summary>Enrolment proof: HMAC-SHA256(lab key, key id + machine id), lower-case hex.</summary>
    public static string Proof(string enrollmentKey, string keyId, string machineId) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(enrollmentKey.Trim()),
            Encoding.UTF8.GetBytes($"{ProofContext}\n{keyId}\n{machineId}"))).ToLowerInvariant();

    public static bool ProofMatches(string enrollmentKey, string keyId, string machineId, string? proof) =>
        proof is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Proof(enrollmentKey, keyId, machineId)),
            Encoding.ASCII.GetBytes(proof.Trim().ToLowerInvariant()));

    /// <summary>Verify an agent's registration signature.</summary>
    public static bool Verify(byte[] subjectPublicKeyInfo, byte[] signature, string serverFingerprint, string nonce, string machineId)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var read);
            if (read != subjectPublicKeyInfo.Length || key.KeySize != 256) return false;
            return key.VerifyData(Payload(serverFingerprint, nonce, machineId), signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Sign a registration (agent side).</summary>
    public static byte[] Sign(ECDsa key, string serverFingerprint, string nonce, string machineId) =>
        key.SignData(Payload(serverFingerprint, nonce, machineId), HashAlgorithmName.SHA256);
}
