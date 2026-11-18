using PROCTOR.Server.Hubs;
using PROCTOR.Server.Services;
using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;

// Runs as a console app (F5, dotnet run) or as the "PROCTORServer" Windows service installed by
// the PROCTOR Teacher setup, which starts at boot on the lab's teacher PC so no one has to keep a
// window open. A service starts in System32, so its content root (appsettings.json) is the exe's folder.
var asService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = asService ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = "PROCTORServer");

// The appsettings.json next to the exe holds the shipped defaults and is replaced by every update.
// An administrator's own settings (teacher code, room name...) go in %ProgramData%\PROCTOR\Server\
// appsettings.json, which no setup touches, read on top. Only when no other account can write it
// (same rule as the server key beside it): such a file could otherwise open the room to anyone.
var localSettings = Path.Combine(Path.GetDirectoryName(ServerCertificate.DefaultPath)!, "appsettings.json");
string? localSettingsNote = null;
if (File.Exists(localSettings))
{
    if (!OperatingSystem.IsWindows() || ServerCertificate.IsTrustedPath(localSettings))
    {
        builder.Configuration.AddJsonFile(localSettings, optional: true, reloadOnChange: false);
        localSettingsNote = $"Using this PC's settings from {localSettings}.";
    }
    else
    {
        localSettingsNote = $"Ignoring {localSettings}: it is writable by an account other than SYSTEM or Administrators.";
    }
}

// Bind Kestrel to the configured PROCTOR port on all interfaces so lab machines on
// the LAN can reach the hub. (Discovery hands clients the resolved LAN address.)
// TLS only (NFR-SEC-06): every connection is encrypted with the server's own long-lived
// certificate, whose key fingerprint agents and teachers pin. There is no plain-HTTP listener.
var port = builder.Configuration.GetValue<int?>("PROCTOR:Port") ?? Protocol.DefaultServerPort;
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
builder.Services.AddSingleton<AgentStore>();
builder.Services.AddSingleton<WebLimitsStore>();
builder.Services.AddSingleton<AgentUpdatePackage>();
builder.Services.AddHostedService<DiscoveryBeaconService>();

var app = builder.Build();

// Website limits outlast lessons and restarts: start the room with the saved class whitelist /
// blacklist (each PC's own list is applied as it joins).
{
    var room = app.Services.GetRequiredService<RoomRegistry>();
    room.Policy = new ClassPolicy().WithWeb(app.Services.GetRequiredService<WebLimitsStore>().Class);
}
if (localSettingsNote is not null)
{
    if (localSettingsNote.StartsWith("Ignoring", StringComparison.Ordinal)) app.Logger.LogWarning("{Note}", localSettingsNote);
    else app.Logger.LogInformation("{Note}", localSettingsNote);
}

// Force ServerIdentity to resolve now so its startup banner (and any generated join code)
// prints before the first client connects.
_ = app.Services.GetRequiredService<ServerIdentity>();
_ = app.Services.GetRequiredService<AgentStore>();

app.MapHub<ClassroomHub>(Protocol.HubPath);
app.MapGet("/", () => Results.Ok(new { service = "PROCTOR", status = "ok" }));

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

// Agent auto-update: the student-agent MSI installed with the teacher setup. Public (it's the same
// file as the GitHub release); agents fetch it over TLS pinned to this server and check its SHA-256.
app.MapGet(Protocol.AgentUpdatePath, (AgentUpdatePackage package) =>
    package.FilePath is { } path
        ? Results.File(path, "application/x-msi", "PROCTOR.Agent.msi")
        : Results.NotFound());

app.Run();

/// <summary>Exposed so integration tests can host the server in-process (WebApplicationFactory).</summary>
public partial class Program;
