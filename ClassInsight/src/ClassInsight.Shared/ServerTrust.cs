using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClassInsight.Shared;

/// <summary>
/// Server authentication by certificate pinning (NFR-SEC-06). Lab servers have no public
/// certificate, so the server's identity is the SHA-256 of its certificate's public key (SPKI):
/// stable across certificate renewals with the same key, and what clients pin — configured by IT
/// (agents), or trusted on first use after the teacher confirms it. Normal chain/hostname checks
/// are replaced by "is this exactly the pinned key".
/// </summary>
public static class ServerTrust
{
    /// <summary>Lower-case hex SHA-256 of the certificate's SubjectPublicKeyInfo.</summary>
    public static string Fingerprint(X509Certificate certificate)
    {
        var cert = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        try
        {
            return Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        }
        finally
        {
            if (!ReferenceEquals(cert, certificate)) cert.Dispose();
        }
    }

    /// <summary>A fingerprint in any spacing/case ("AB12 CD34 …", "ab12:cd34…") as 64 lower-case hex chars; null if invalid.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        var separatorsOnly = text.All(c => Uri.IsHexDigit(c) || c is ' ' or ':' or '-');
        return hex.Length == 64 && separatorsOnly ? hex : null;
    }

    /// <summary>For people to compare: upper-case groups of four, e.g. "AB12 CD34 …".</summary>
    public static string Format(string fingerprint)
    {
        var hex = (Normalize(fingerprint) ?? fingerprint).ToUpperInvariant();
        return string.Join(" ", Enumerable.Range(0, (hex.Length + 3) / 4).Select(i => hex.Substring(i * 4, Math.Min(4, hex.Length - i * 4))));
    }

    /// <summary>The first 4 groups, for compact display ("AB12 CD34 EF56 7890").</summary>
    public static string Short(string fingerprint) => string.Join(" ", Format(fingerprint).Split(' ').Take(4));

    public static bool Matches(string pin, X509Certificate? certificate) =>
        certificate is not null && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(Fingerprint(certificate)),
            System.Text.Encoding.ASCII.GetBytes(pin));

    /// <summary>TLS validation for WebSockets / SslStream: accept only the pinned key.</summary>
    public static RemoteCertificateValidationCallback Validator(string pin) =>
        (_, certificate, _, _) => Matches(pin, certificate);

    /// <summary>TLS validation for HttpClientHandler: accept only the pinned key.</summary>
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool> HttpValidator(string pin) =>
        (_, certificate, _, _) => Matches(pin, certificate);

    /// <summary>An HttpClient that talks only to the server holding the pinned key.</summary>
    public static HttpClient PinnedHttpClient(string pin, TimeSpan timeout) =>
        new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpValidator(pin) }) { Timeout = timeout };

    /// <summary>
    /// Connect to <paramref name="host"/>:<paramref name="port"/>, do a TLS handshake (accepting any
    /// certificate) and return the server's fingerprint — what the server <i>claims</i> to be, to
    /// compare with a pin or show a teacher for confirmation. Nothing is sent. Null if unreachable.
    /// </summary>
    public static async Task<string?> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            string? seen = null;
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
            {
                if (cert is not null) seen = Fingerprint(cert);
                return true;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
            return seen;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or AuthenticationException)
        {
            return null;
        }
    }
}
