using System.Security.Cryptography;
using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace ClassInsight.Tests;

/// <summary>Agent authentication (NFR-SEC-06): signed registration, PC id bound to its key, optional lab key.</summary>
public class EnrollmentTests
{
    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static StudentInfo Pc(string id, string name = "LAB-01") => new() { MachineId = id, MachineName = name };

    [Fact]
    public async Task Unsigned_replayed_or_wrong_server_registrations_are_refused()
    {
        await using var factory = new ServerFactory();
        await using var peer = await factory.ConnectAsync();
        var id = Guid.NewGuid().ToString("N");
        var key = NewKey();

        // No challenge asked for / no signature at all.
        await Assert.ThrowsAsync<HubException>(() => peer.Hub.InvokeAsync(Protocol.ServerMethods.RegisterStudent, Pc(id)));

        // Signed for a different server's key (what a rogue server relaying the challenge would get).
        var challenge = await peer.Hub.InvokeAsync<EnrollmentChallenge>(Protocol.ServerMethods.GetEnrollmentChallenge);
        var spki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var forOtherServer = Pc(id) with
        {
            PublicKey = spki,
            Signature = Convert.ToBase64String(AgentEnrollment.Sign(key, new string('0', 64), challenge.Nonce, id)),
        };
        await Assert.ThrowsAsync<HubException>(() => peer.Hub.InvokeAsync(Protocol.ServerMethods.RegisterStudent, forOtherServer));

        // A correct registration works once; replaying the same signed message needs a fresh challenge and fails.
        challenge = await peer.Hub.InvokeAsync<EnrollmentChallenge>(Protocol.ServerMethods.GetEnrollmentChallenge);
        var signed = Pc(id) with
        {
            PublicKey = spki,
            Signature = Convert.ToBase64String(AgentEnrollment.Sign(key, peer.ServerFingerprint, challenge.Nonce, id)),
        };
        await peer.Hub.InvokeAsync(Protocol.ServerMethods.RegisterStudent, signed);
        await using var replayer = await factory.ConnectAsync();
        await replayer.Hub.InvokeAsync<EnrollmentChallenge>(Protocol.ServerMethods.GetEnrollmentChallenge);
        await Assert.ThrowsAsync<HubException>(() => replayer.Hub.InvokeAsync(Protocol.ServerMethods.RegisterStudent, signed));
    }

    [Fact]
    public async Task A_pc_id_stays_bound_to_its_key_even_after_a_server_restart()
    {
        var store = Path.Combine(Path.GetTempPath(), "ci-agents-" + Guid.NewGuid().ToString("N") + ".json");
        var id = Guid.NewGuid().ToString("N");
        var genuine = NewKey();
        try
        {
            await using (var factory = new ServerFactory(("ClassInsight:AgentStorePath", store)))
            {
                await using var pc = await factory.ConnectAsync();
                await pc.RegisterStudentAsync(Pc(id), genuine);

                // Someone else claiming the same PC id with their own key is refused.
                await using var impostor = await factory.ConnectAsync();
                await Assert.ThrowsAsync<HubException>(() => impostor.RegisterStudentAsync(Pc(id, "FAKE"), NewKey()));
            }

            await using (var restarted = new ServerFactory(("ClassInsight:AgentStorePath", store)))
            {
                await using var impostor = await restarted.ConnectAsync();
                await Assert.ThrowsAsync<HubException>(() => impostor.RegisterStudentAsync(Pc(id, "FAKE"), NewKey()));
                await using var pc = await restarted.ConnectAsync();
                await pc.RegisterStudentAsync(Pc(id), genuine);   // the real PC still gets in
            }
        }
        finally
        {
            File.Delete(store);
        }
    }

    [Fact]
    public async Task New_enrolments_are_rate_limited_so_made_up_pcs_cannot_fill_the_server()
    {
        await using var factory = new ServerFactory(("ClassInsight:EnrollmentsPerAddressPerHour", "3"));
        for (var i = 0; i < 3; i++)
        {
            await using var pc = await factory.ConnectAsync();
            await pc.RegisterStudentAsync(Pc(Guid.NewGuid().ToString("N")));
        }
        await using var flood = await factory.ConnectAsync();
        var ex = await Assert.ThrowsAsync<HubException>(() => flood.RegisterStudentAsync(Pc(Guid.NewGuid().ToString("N"))));
        Assert.Contains("Too many new PCs", ex.Message);
    }

    [Fact]
    public async Task With_a_lab_key_only_pcs_installed_with_it_can_enrol()
    {
        const string labKey = "correct horse battery staple";
        await using var factory = new ServerFactory(("ClassInsight:AgentEnrollment", "Key"), ("ClassInsight:EnrollmentKey", labKey));
        await using var rogue = await factory.ConnectAsync();
        var challenge = await rogue.Hub.InvokeAsync<EnrollmentChallenge>(Protocol.ServerMethods.GetEnrollmentChallenge);
        Assert.True(challenge.KeyRequired);

        await Assert.ThrowsAsync<HubException>(() => rogue.RegisterStudentAsync(Pc(Guid.NewGuid().ToString("N"))));
        await Assert.ThrowsAsync<HubException>(() => rogue.RegisterStudentAsync(Pc(Guid.NewGuid().ToString("N")), NewKey(), "wrong key"));

        var id = Guid.NewGuid().ToString("N");
        var key = NewKey();
        await using var lab = await factory.ConnectAsync();
        await lab.RegisterStudentAsync(Pc(id), key, labKey);

        // Once enrolled, the PC registers with its own key alone.
        await using var again = await factory.ConnectAsync();
        await again.RegisterStudentAsync(Pc(id), key);
    }
}
