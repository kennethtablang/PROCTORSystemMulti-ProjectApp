# Deploying the PROCTOR agent to lab PCs

The agent runs as an **auto-start Windows service** (FR-AGENT-02). Once installed it starts at
boot, before any student logs in, finds the classroom server over the LAN (UDP discovery), and
joins with no code and no per-machine configuration.

> Deploy only on institution-owned machines you are authorized to manage, on the lab network.
> See `plan.md` for the deployment and trust model.

## 1. Publish a self-contained build

From the `PROCTOR.Agent` project folder (produces an exe with no .NET install required on
the lab PC):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o .\deploy\agent
```

This drops `PROCTOR.Agent.exe` into `deploy\agent\`, where `Install-Agent.ps1` looks by default.

## Easiest — one setup .exe per lab

`PROCTOR-Agent-Setup.exe` installs everything (the agent is self-contained, so lab PCs need
no .NET install). Build it from `PROCTOR\installer`, with your lab's settings built in so that
on each PC you only double-click it:

```powershell
cd PROCTOR\installer
# The fingerprint is printed by the classroom server at start-up.
.\Build-AgentSetup.ps1 -ServerFingerprint "47E5 EF43 ... 49B9" -RoomName "Lab 3"
# -> PROCTOR\dist\PROCTOR-Agent-Setup-Lab3.exe
```

| Parameter | Effect |
| --------- | ------ |
| `-ServerFingerprint` | Pin the agents to your server (recommended; needed for remote control). |
| `-ServerAddress` | `host` or `host:port`, when broadcast discovery is blocked. |
| `-RoomName` | Join only this room. |
| `-EnrollmentKey` | Lab enrolment key (server `AgentEnrollment=Key`). Anyone holding a setup built with it can read the key out of the file, so **keep that setup like a password**; or leave it out and pass `ENROLLMENTKEY=...` at install time. |
| `-AllowRemoteControl` | Allow teacher remote control (requires `-ServerFingerprint`). |
| `-HideViewingIndicator` | No effect: student PCs don't show a "being viewed by your teacher" notice. Make sure your acceptable-use/privacy policy tells students the lab PCs are monitored. |

`.\Build-AgentSetup.ps1` with no parameters builds a generic setup (`dist\PROCTOR-Agent-Setup.exe`)
that finds the server on the LAN and trusts the first one it meets.

On each PC, as an administrator: double-click the setup, or run it silently (GPO, Intune, a
script). The same settings can be given (or overridden) on the command line, names in upper case:

```powershell
PROCTOR-Agent-Setup.exe /quiet /norestart
PROCTOR-Agent-Setup.exe /quiet /norestart SERVERFINGERPRINT="47E5 EF43 ... 49B9" ENROLLMENTKEY="..."
PROCTOR-Agent-Setup.exe /uninstall /quiet      # remove (or Settings > Apps > PROCTOR Agent)
```

Running a newer setup upgrades in place and keeps the settings the PC was installed with unless the
new setup sets them. Sign the setup with your institution's code-signing certificate before
distributing it, or Windows SmartScreen will warn on each PC.

## Option A — MSI (recommended for GPO / Intune / SCCM / imaging)

```powershell
cd ..\..\..\installer
dotnet build -c Release          # publishes the agent self-contained, then links the MSI
# -> installer\bin\x64\Release\en-us\PROCTOR.Agent.msi
msiexec /i PROCTOR.Agent.msi /qn

# Recommended: pin the agents to your classroom server (fingerprint printed by the server at start-up)
msiexec /i PROCTOR.Agent.msi SERVERFINGERPRINT="1056 91B5 BB03 ... 17C5" /qn
```

MSI properties (all optional; each is written to `proctor.ini` next to the agent, which
overrides `appsettings.json`, and is remembered so later upgrades keep it):

| Property | Setting |
| -------- | ------- |
| `SERVERFINGERPRINT` | `PROCTOR:ServerFingerprint` — the server's TLS key; the agent talks to no other server. |
| `SERVERADDRESS` | `PROCTOR:ServerAddress` — `host` or `host:port` when broadcast discovery is blocked. |
| `ROOMNAME` | `PROCTOR:RoomName` — join only this room. |
| `ENROLLMENTKEY` | The lab enrolment key, when the server runs with `AgentEnrollment=Key`. Stored only in an admin-only registry key (never a settings file) and hidden from installer logs. |
| `ALLOWREMOTECONTROL` | `PROCTOR:AllowRemoteControl` — `true` to allow remote control (also needs `SERVERFINGERPRINT`). |
| `SHOWVIEWINGINDICATOR` | No effect (the notice was removed); accepted so older install commands still work. |

The MSI installs into `%ProgramFiles%\PROCTOR`, registers the same `PROCTORAgent` service
(Automatic, LocalSystem, restart on failure 5 s / 5 s / 5 s, reset daily), creates
`%ProgramData%\PROCTOR` with a protected ACL, starts the service, and upgrades in place.
An admin's edits to `appsettings.json` survive upgrades. Sign the MSI with your institution's
code-signing certificate before distribution (`signtool sign /fd sha256 /a PROCTOR.Agent.msi`).

## Option B — PowerShell installer

## 2. Install the service (elevated)

```powershell
# Run from an Administrator PowerShell
.\Install-Agent.ps1
# ...or install from a specific published build:
.\Install-Agent.ps1 -BinaryPath 'C:\build\agent\PROCTOR.Agent.exe'
```

The installer **copies the agent into `%ProgramFiles%\PROCTOR`** (override with `-InstallDir`)
and **locks that folder's ACL** - Administrators/SYSTEM get full control, standard Users get only
read & execute - then registers the service to run from there. This matters: the service runs as
`LocalSystem`, so if the binary lived in a student-writable folder a student could swap the exe and
run code as SYSTEM at boot. The installer **refuses to proceed if the installed binary is writable
by non-admins**.

The service is registered as `PROCTORAgent`, **Automatic** start, running as `LocalSystem`,
with **restart-on-failure** (5s, 5s, then every 10s) so it recovers if it exits (FR-AGENT-07).

### How it runs

Windows services live in **session 0**, which can't see the student's desktop. So the service is
a small **supervisor**: it launches the same `PROCTOR.Agent.exe --session-agent` *inside the
active console session* (the signed-in student's desktop, `winsta0\default`), running as SYSTEM.
That session agent does the discovery, telemetry and screen streaming. The supervisor:

- relaunches it within seconds if it exits, and moves it when the console user changes
  (log off / switch user);
- holds it in a kill-on-close job object, so it can never outlive the service;
- runs it as SYSTEM, so a standard student can't end it from Task Manager (FR-AGENT-07).

At the Windows logon screen there is no user desktop to capture; the agent stays registered
(telemetry continues) and the stream resumes once someone signs in.

### Optional per-lab settings

`appsettings.json` next to the installed exe (none are needed on a normal lab LAN):

| Setting | Use |
| ------- | --- |
| `PROCTOR:RoomName` | Join only the server with this room name, when several rooms share one network. |
| `PROCTOR:ServerFingerprint` | The classroom server's TLS key fingerprint, exactly as the server prints it at start-up. **Recommended.** With it the agent connects to that server only; without it the agent trusts the first server it reaches and remembers it as `TrustedServerFingerprint` under `HKLM\SOFTWARE\PROCTOR\Agent`, which students can't change or delete (then refuses any other). |
| `PROCTOR:ServerAddress` | `host` or `host:port` to try when UDP broadcast discovery is blocked. |
| `PROCTOR:ReportBrowserActivity` | Report the foreground browser tab's **host** (domain only). Default `true`. |
| `PROCTOR:ShowViewingIndicator` | Removed: student PCs show no "being viewed" notice. |
| `PROCTOR:EnableClassroomControl` | Messages, lock, polls, timer, chat, limits and the tray icon. Default `true`. |
| `PROCTOR:AllowPowerCommands` | Obey log off / restart / shut down from the teacher. Default `true`. |
| `PROCTOR:AllowRemoteControl` | Let a teacher control this PC's keyboard and mouse (the student sees a red banner while it happens). **Off by default.** It only takes effect when `ServerFingerprint` is also set (the agent is pinned to your classroom server's key, so a rogue server can't drive the PC), and the server also needs a teacher join code or `AllowStrongControlsInOpenMode`. Default `false`. |
| `PROCTOR:AllowFileTransfer` | Accept files from the teacher (saved to *Files from teacher* on the student's desktop) and let students hand work in from *Documents\Hand in*. Files are read and written as the signed-in student, never as SYSTEM. Default `true`. |
| `PROCTOR:CaptureBackend` | `DXGI` (GPU capture, default) or `GDI`. GDI is the automatic fallback; force it only for diagnostics. |

The agent's identity is a random per-install id kept in `%ProgramData%\PROCTOR\agent-id`
(the installer protects that folder).

### Switching PCs on from the teacher app (Wake-on-LAN)

The teacher app can switch on lab PCs it has seen before (**Power… → Switch on offline
computers**). The PROCTOR server sends the wake-up packet, so it must be on the same network
(VLAN) as the lab PCs, or the routers must forward directed broadcasts. On each PC:

1. **BIOS/UEFI:** enable *Wake on LAN* / *Power on by PCI-E* (and disable *Deep Sleep / ErP*).
2. **Network adapter** (Device Manager → adapter → *Power Management* / *Advanced*): enable
   *Allow this device to wake the computer*, *Only allow a magic packet*, and *Wake on Magic Packet*.
3. **Windows Fast Startup** often stops wake-from-shutdown; turn it off for lab images:
   `powercfg /hibernate off` (or Control Panel → Power Options → *Turn on fast startup* off).

Wired Ethernet is needed; Wi-Fi adapters generally can't wake a PC that is switched off.

### Encryption and server identity

Every connection — agents and teachers to the server — is **TLS-encrypted** (the server has no
plain-HTTP port), and screen video between PCs is DTLS-SRTP encrypted by WebRTC. On first start
the server creates its own certificate in `%ProgramData%\PROCTOR\Server\server-certificate.pfx`
(readable only by SYSTEM, Administrators and the account running the server; or supply one with
`PROCTOR:CertificatePath`/`CertificatePassword`) and prints its **fingerprint**:

```
  Server fingerprint (pin this in agents as PROCTOR:ServerFingerprint; ...):
    1056 91B5 BB03 3A30 18C7 F4CE 5F2A A846 9AD6 787E A260 1404 3F0F E5ED 615D 17C5
```

* **Agents** check the server presents that key. Set it with the `SERVERFINGERPRINT` MSI property
  (best), or let each agent trust the first server it reaches.
* **Teachers** are shown the fingerprint the first time they connect to a server and confirm it;
  if a server's key ever changes they get a red warning and must confirm again.
* **Tamper checks:** the server refuses to use a certificate file (or folder) that accounts other
  than SYSTEM, Administrators and its own account can change. Agents keep a first-use pin in
  HKLM, which only Administrators and SYSTEM can write (an agent run from a console without admin
  rights just keeps it in memory). Agents probe every server that answers discovery (one per
  address, the last good one first), so fake replies can't hide the real server.
* **Back up the certificate file.** Replacing it changes the fingerprint: agents will refuse the
  server until you re-pin them (re-run the MSI with the new `SERVERFINGERPRINT`, or delete
  the `TrustedServerFingerprint` registry value under `HKLM\SOFTWARE\PROCTOR\Agent` on PCs
  that trusted on first use).

### Which PCs may join (agent enrolment)

Every agent has its own key (`%ProgramData%\PROCTOR\agent-key`, readable only by SYSTEM and
Administrators) and proves it at each registration by signing a single-use challenge bound to the
server's fingerprint. The server remembers which key each PC enrolled with (`agents.json` beside its
certificate), so no other program can ever register as that PC.

* **Open** (default, zero-config): a PC the server hasn't seen is enrolled the first time it registers.
* **Key**: set the server's `AgentEnrollment` to `Key` and `EnrollmentKey` to a long random secret, and
  install agents with `ENROLLMENTKEY="<that secret>"`. A program without it can't join as a student.
* **Flood protection:** at most 5 new PCs per address per hour and 200 per 10 minutes may enrol
  (`EnrollmentsPerAddressPerHour` / `EnrollmentsPerTenMinutes` on the server), so a rogue program
  can't fill the server with made-up PCs (IPv6 counted per /64 network). PCs installed with the lab
  enrolment key skip the per-address limit but share a budget of 1,000 per 10 minutes
  (`KeyEnrollmentsPerTenMinutes`), in case the key ever leaks. PCs not seen for 180 days are forgotten only if the server's list of
  20,000 PCs is full.
* **Re-imaged PC** (new agent key, same PC id): the server refuses it and logs why. Delete that PC's
  entry from `agents.json` (or the file) and restart the server, and it enrols again.

## 3. Verify / manage

```powershell
Get-Service PROCTORAgent            # status
Get-EventLog -LogName Application -Source 'PROCTOR Agent' -Newest 20   # diagnostics
```

Under the service, diagnostics go to the Windows **Event Log** (Event Viewer â†’ Windows Logs â†’
Application, source *PROCTOR Agent*) since there's no console.

## 4. Uninstall (elevated)

```powershell
.\Uninstall-Agent.ps1            # removes the service and %ProgramFiles%\PROCTOR
.\Uninstall-Agent.ps1 -KeepFiles # removes only the service, leaves the files
```

## Mass deployment

- **GPO:** run `Install-Agent.ps1` as a *Computer â†’ Startup* script, with the published `agent\`
  folder staged on a share or copied locally.
- **Imaging:** publish once, copy the exe into the image, and run `Install-Agent.ps1` in the
  build/sysprep step.

The same `PROCTOR.Agent.exe` also runs interactively for development (`dotnet run`): the
Windows-service wiring is a no-op unless the Service Control Manager launched the process, and
the agent then monitors the current session directly.
