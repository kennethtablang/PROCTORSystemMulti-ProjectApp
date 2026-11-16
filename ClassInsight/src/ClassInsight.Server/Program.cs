using ClassInsight.Server.Hubs;
using ClassInsight.Server.Services;
using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

// Bind Kestrel to the configured ClassInsight port on all interfaces so lab machines on
// the LAN can reach the hub. (Discovery hands clients the resolved LAN address.)
// TLS only (NFR-SEC-06): every connection is encrypted with the server's own long-lived
// certificate, whose key fingerprint agents and teachers pin. There is no plain-HTTP listener.
var port = builder.Configuration.GetValue<int?>("ClassInsight:Port") ?? Protocol.DefaultServerPort;
builder.Services.AddSingleton<ServerCertificate>();
builder.WebHost.ConfigureKestrel(options =>
{
    var certificate = options.ApplicationServices.GetRequiredService<ServerCertificate>().Certificate;
    options.ListenAnyIP(port, listen => listen.UseHttps(certificate));
});

builder.Services.AddSignalR(options =>
{
    // Screen SDP offers and telemetry can be larger than the default 32 KB cap.
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1 MB
});

builder.Services.AddSingleton<ServerIdentity>();
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddSingleton<TeacherAuthThrottle>();
builder.Services.AddSingleton<PollBox>();
builder.Services.AddSingleton<BroadcastState>();
builder.Services.AddSingleton<TimerState>();
builder.Services.AddSingleton<WakeSender>();
builder.Services.AddSingleton<ChatThrottle>();
builder.Services.AddSingleton<FileTransfers>();
builder.Services.AddSingleton<RemoteControlState>();
builder.Services.AddHostedService<DiscoveryBeaconService>();

var app = builder.Build();

// Force ServerIdentity to resolve now so its startup banner (and any generated join code)
// prints before the first client connects.
_ = app.Services.GetRequiredService<ServerIdentity>();

app.MapHub<ClassroomHub>(Protocol.HubPath);
app.MapGet("/", () => Results.Ok(new { service = "ClassInsight", status = "ok" }));

// Status endpoint (FR-SRV-03): id, room, protocol version and live counts. Also how a client
// resolves a manually entered server address when broadcast is blocked (NFR-COMPAT-02).
app.MapGet(Protocol.StatusPath, (ServerIdentity identity, RoomRegistry room) => Results.Ok(new ServerStatus
{
    ServerId = identity.ServerId,
    RoomName = identity.RoomName,
    ProtocolVersion = Protocol.Version,
    Students = room.StudentCount,
    Teachers = room.TeacherCount,
}));

app.Run();

/// <summary>Exposed so integration tests can host the server in-process (WebApplicationFactory).</summary>
public partial class Program;
