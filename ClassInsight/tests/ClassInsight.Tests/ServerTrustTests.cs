using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClassInsight.Server.Services;
using ClassInsight.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassInsight.Tests;

public class ServerTrustTests
{
    private static ServerCertificate Cert(string path) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ClassInsight:CertificatePath"] = path }).Build(),
        NullLogger<ServerCertificate>.Instance);

    [Fact]
    public void Fingerprints_normalise_and_format()
    {
        var fp = new string('a', 32) + new string('0', 32);
        Assert.Equal(fp, ServerTrust.Normalize(ServerTrust.Format(fp)));
        Assert.Equal(fp, ServerTrust.Normalize(fp.ToUpperInvariant().Replace("AA", "AA:")));
        Assert.Null(ServerTrust.Normalize("not a fingerprint"));
        Assert.Null(ServerTrust.Normalize(new string('a', 63)));
        Assert.Null(ServerTrust.Normalize(new string('a', 64) + "zz"));
        Assert.Equal("AAAA AAAA AAAA AAAA", ServerTrust.Short(fp));
    }

    [Fact]
    public void The_server_identity_is_created_once_and_kept()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pfx");
        try
        {
            var first = Cert(path).Fingerprint;
            Assert.True(File.Exists(path));
            Assert.Equal(first, Cert(path).Fingerprint);           // a restart keeps the same identity
            Assert.NotEqual(first, Cert(path + ".other").Fingerprint);
            Assert.NotNull(ServerTrust.Normalize(first));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".other");
        }
    }

    [Fact]
    public async Task Probe_reads_the_key_and_the_pin_refuses_any_other()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pfx");
        var cert = Cert(path);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // A tiny TLS server presenting the server certificate to every client.
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                try
                {
                    await using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsServerAsync(cert.Certificate);
                }
                catch { /* the pinned client may hang up */ }
            }
        });

        try
        {
            Assert.Equal(cert.Fingerprint, await ServerTrust.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), default));

            // Pinned to the right key: the handshake succeeds.
            using (var ok = new TcpClient())
            {
                await ok.ConnectAsync(IPAddress.Loopback, port);
                await using var ssl = new SslStream(ok.GetStream(), false, ServerTrust.Validator(cert.Fingerprint));
                await ssl.AuthenticateAsClientAsync("127.0.0.1");
            }

            // Pinned to a different key: refused.
            var other = Convert.ToHexString(SHA256.HashData([1, 2, 3])).ToLowerInvariant();
            using (var bad = new TcpClient())
            {
                await bad.ConnectAsync(IPAddress.Loopback, port);
                await using var ssl = new SslStream(bad.GetStream(), false, ServerTrust.Validator(other));
                await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() => ssl.AuthenticateAsClientAsync("127.0.0.1"));
            }
            await serving.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            listener.Stop();
            File.Delete(path);
        }
    }
}
