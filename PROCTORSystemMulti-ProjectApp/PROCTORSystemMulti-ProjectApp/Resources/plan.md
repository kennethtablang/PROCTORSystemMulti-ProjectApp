# ClassInsight — System Plan & Software Requirements Specification

**Document status:** Living document — the authoritative basis for the system from start to finish.
**Canonical location:** `PROCTORSystemMulti-ProjectApp/Resources/plan.md` (this file). Keep this copy current; any other copy (e.g. a WinUI `Assets` snapshot) is a convenience mirror.
**Version:** 0.24 (draft)
**Last updated:** 2026-11-16
**Owner:** Kenneth Tablang
**Applies to:** ClassInsight — the `ClassInsight` solution (Shared, Server, Agent) plus the .NET MAUI teacher app in `PROCTORSystemMulti-ProjectApp` (WinUI/Droid/Mac/iOS heads).

---

## 0. How to read this document

This is both the **project plan** (what we are building, in what order) and the
**Software Requirements Specification (SRS)** (the functional and non-functional
requirements the system must satisfy). Requirements are numbered and traceable:

- `FR-xxx-nn` — Functional requirement.
- `NFR-xxx-nn` — Non-functional requirement.

Each requirement uses **shall** (mandatory), **should** (recommended), or **may**
(optional) per RFC 2119 convention. Phase tags (`[P0]`…`[P5]`) map each requirement
to the delivery phase in §9.

**Status markers** on each requirement track delivery against the current build:

- ✅ **done** — implemented and verified in the code today.
- 🟡 **built, runtime-pending** — code complete and compiles, not yet verified at runtime.
- ⬜ **not started** — planned for a later phase (or not yet verifiable end-to-end).

A trailing *(italic note)* explains any requirement that is partly built.

---

## 1. Vision & scope

### 1.1 Purpose

ClassInsight is a **real-time classroom monitoring system** for school computer labs,
in the spirit of Faronics Insight / NetSupport School — **built from scratch, not a
clone** of any product's code, assets, or branding. A teacher sees a live grid of
every student screen as continuous video (not periodic screenshots), plus each
machine's running processes and live network bandwidth, and can focus on any single
student.

### 1.2 Deployment context (intended use)

ClassInsight is management software for **institution-owned machines on a local
network**, deployed by a school/IT administrator for **supervised classroom use**:

- Student agents run on school-owned **Windows** lab PCs.
- Teacher apps run on the teacher's **Windows, macOS, iOS, or Android** device.
- All devices sit on the **same LAN / lab VLAN**.

It is **not** consumer spyware and must only be deployed on machines the operator is
authorized to manage, under the institution's acceptable-use and privacy policy
(see §8.5 and NFR-PRIV).

### 1.3 In scope (this project)

- Zero-configuration LAN discovery and auto-connection.
- Continuous live screen streaming (WebRTC H.264), peer-to-peer.
- Live telemetry: CPU/memory, per-second bandwidth, top processes, foreground app.
- Teacher dashboard (live grid), focus view, processes view, bandwidth view.
- Cross-platform teacher app (one .NET MAUI codebase → 4 platforms).
- Role separation and optional teacher pairing code.

### 1.4 Out of scope (initial release) / future phases

- Remote control (keyboard/mouse takeover), screen blanking/locking, broadcast of the
  teacher's screen to students, chat/messaging, remote app/URL launch, web filtering,
  attendance, recording/playback — specified in §7 as **Phase 5 (future)**.
- Internet/WAN operation across NAT at scale (initial release targets a single LAN).
- Central multi-room management console / cloud backend.
- macOS/Linux **student agents** (initial agent is Windows-only, per requirements).

### 1.5 Primary users

| Role        | Device(s)                         | Needs                                              |
| ----------- | --------------------------------- | -------------------------------------------------- |
| Teacher     | Windows / macOS / iOS / Android   | See all screens live, drill into one, see activity |
| Student     | Windows lab PC                    | Nothing to do — agent runs automatically           |
| IT admin    | Windows                           | Deploy agents, set room name / teacher code        |

---

## 2. System overview & architecture

### 2.1 Component map

```
          UDP broadcast discovery (port 47800)
   ┌──────────────────────────────────────────────────┐
   │                                                    │
┌──┴───────────┐   SignalR    ┌──────────────────┐   SignalR   ┌─────────────┐
│ Student agent │◄────────────►│  ClassInsight    │◄───────────►│ Teacher app │
│  (Windows)    │  roster +    │  server          │  roster +   │ (MAUI)      │
│  WebRTC sender│  signalling  │  (SignalR hub)   │  signalling │ WebRTC recv │
└──────┬────────┘              └──────────────────┘             └──────┬──────┘
       │                                                                │
       │          WebRTC H.264 screen video (peer-to-peer)             │
       └────────────────────────────────────────────────────────────►─┘
            telemetry (CPU / memory / bandwidth / processes) via hub
```

### 2.2 Projects (the "multi-project app")

| Project                 | Type                     | Role                                                           | Status |
| ----------------------- | ------------------------ | -------------------------------------------------------------- | ------ |
| `ClassInsight.Shared`   | .NET 10 class library    | Wire contracts + protocol constants shared by all components   | ✅ Built |
| `ClassInsight.Server`   | ASP.NET Core (SignalR)   | Roster, **pair-gated** WebRTC signalling relay, telemetry fan-out, discovery, `/status`, optional STUN/TURN config. **Open teacher mode on by default** (trusted-VLAN deploy). Runtime-verified incl. two rooms on one LAN | ✅ Built |
| `ClassInsight.Agent`    | .NET 10 Windows app      | Discovery + registration + 1 Hz telemetry + multi-monitor GDI capture + **pure-managed VP8 WebRTC sender** (one peer per watching teacher), runtime-verified at thumbnail and focus quality. The Windows service is a session **supervisor** that runs the monitor in the student's desktop session (elevated install still to be run) | ✅ Built |
| `PROCTORSystemMulti-ProjectApp` | .NET MAUI app (WinUI/Droid/Mac/iOS heads) | **Teacher app:** room chooser + manual address, live **thumbnail video grid** with search/sort/density, focus view with display picker, Processes/Bandwidth tabs; one `StreamManager` owns every WebRTC stream. WinUI GUI-verified; all four heads compile (0 warnings). Android/iOS/macOS on-device runs pending | 🟡 Built, devices pending |
| `ClassInsight.Tests`    | xUnit test project       | 52 unit + in-process SignalR integration tests (discovery codec, roster/pairs, throttle, join code, telemetry math, capture sizing + DXGI mip choice, DTLS-SRTP, hub flows incl. injection cases) | ✅ Built, 52/52 pass |
| `installer` (WiX v5)    | MSI                      | `ClassInsight.Agent.msi`: self-contained agent, auto-start LocalSystem service with restart-on-failure, protected `%ProgramData%` state folder, in-place upgrade | ✅ Built (install run pending) |

> **Repository layout (0.12):** everything lives in the **PROCTORSystemMulti-ProjectApp** repo
> and solution. The MAUI teacher app (WinUI/Droid/Mac/iOS heads) is under
> `PROCTORSystemMulti-ProjectApp/`, and the back end is under `ClassInsight/`: `src/` (Shared,
> Server, Agent), `tests/` (ClassInsight.Tests) and `installer/` (agent MSI + `setup/` for
> `ClassInsight-Agent-Setup.exe`). The teacher app references `ClassInsight/src/ClassInsight.Shared`
> directly. (These projects briefly lived in the unrelated SENGEN repository; PROCTOR is their home.)

### 2.3 Key architectural decisions (locked)

| # | Decision | Rationale |
| - | -------- | --------- |
| AD-1 | **.NET MAUI** for the teacher app | One C#/XAML codebase targets Windows, macOS, iOS, Android, building on the existing `PROCTORSystemMulti-ProjectApp` multi-head app (WinUI/Droid/Mac/iOS). |
| AD-2 | **WebRTC H.264** for screen streaming | True low-latency video, hardware-encoded, lowest bandwidth at scale — not screenshots. |
| AD-3 | **Native Windows agent** (not web) | Only native code can capture the screen, enumerate processes, and measure bandwidth. |
| AD-4 | **Server is a coordinator/relay only** | SDP/ICE + roster + telemetry pass through; media flows peer-to-peer and never touches the server. |
| AD-5 | **UDP broadcast discovery** (port 47800) | Zero-config "absorb on launch"; no manual IP entry, no login for students. |
| AD-6 | **No database, no persistent accounts** | A machine exists in the roster only while its connection is live; matches the ephemeral lab model. |
| AD-7 | **SignalR** for the control plane | Mature real-time .NET-to-.NET transport for roster, signalling, and telemetry. |
| AD-8 | **SIPSorcery** (candidate) for WebRTC in .NET | Mature pure-C# WebRTC/H.264 stack usable from both the agent and the MAUI teacher app. (To be validated in P1/P2.) |

### 2.4 Technology stack

- **Language/runtime:** C# / .NET 10.
- **Server:** ASP.NET Core, SignalR, hosted `BackgroundService` for discovery.
- **Agent:** .NET 10 Windows; **DXGI Desktop Duplication** capture (GPU copy + GPU mip
  downsampling via Vortice.Direct3D11) per display, with GDI `StretchBlt` as automatic fallback,
  per-monitor DPI aware; Win32 (`GetSystemTimes`, `GlobalMemoryStatusEx`, NIC counters) and UI
  Automation for telemetry; SIPSorcery WebRTC + pure-managed `SIPSorcery.VP8` encode; a WPF
  "being viewed" notice; packaged as an MSI / Windows service that supervises an in-session
  monitor process.
- **Teacher:** .NET MAUI (WinUI/Droid/Mac/iOS heads); SignalR .NET client; SIPSorcery WebRTC
  receiver + managed VP8 decode; one SkiaSharp video surface (`LiveScreenView`) on every head.
- **Transport:** SignalR over WebSocket (control); WebRTC/SRTP (media); UDP (discovery).
- **Serialization:** System.Text.Json (web defaults) shared via `ClassInsight.Shared`.

---

## 3. Discovery & connection protocol

1. **Server** binds UDP `47800`, answers probes with a `DiscoveryResponse` carrying the
   exact hub URL, and emits periodic beacons (every 2 s).
2. **Client** (agent or teacher) broadcasts a `DiscoveryProbe` on launch; on receiving
   a response it opens a SignalR connection to `DiscoveryResponse.HubUrl`.
3. **Agent** calls `RegisterStudent(StudentInfo)`; **teacher** calls
   `RegisterTeacher(TeacherInfo)` (with join code if the server requires one).
4. Clients dial the **source address of the datagram** that answered — never a host or path
   carried inside it (NFR-SEC-05) — on the fixed hub path; only the port comes from the payload.
5. **Several rooms** on one network are several servers (FR-SRV-05). A teacher collects every
   reply for one beacon interval and picks a room (remembered for next time); an agent can be
   pinned to a room by name (`ClassInsight:RoomName`).
6. **Broadcast blocked?** A teacher can type the server address, and IT can set
   `ClassInsight:ServerAddress` on agents; the client reads `GET /status` to learn the room and
   protocol version, then connects exactly as if discovered (NFR-COMPAT-02).
7. On connection loss, clients re-probe and reconnect automatically.

All datagrams are JSON prefixed with the magic token `CLASSINSIGHT/1`. Protocol
version is carried in every message so mismatched clients can be rejected cleanly.

---

## 4. Screen streaming design (WebRTC)

- **Negotiation (pair-gated):** teacher `RequestStream(studentConnId, quality)` →
  server authorizes the pair and tells the student `StreamRequested(teacherConnId)` →
  the **student (media sender)** creates the SDP **offer** → relayed to the teacher →
  teacher `SendAnswer` → ICE candidates exchanged both ways → media flows P2P.
- **Quality hints:** `Thumbnail` (480 px wide, 5 fps) for the grid; `Focus` (1280 px wide,
  15 fps) for a single selected student, on a chosen display (`DisplayIndex`). Switching is a
  re-request: the agent replaces its sender and offers again; the teacher swaps peers in place.
- **Codec:** **VP8**, pure managed on both ends (`SIPSorcery.VP8`), so one decoder serves all
  four teacher platforms (supersedes the H.264 intent). Frame sizes are rounded to **multiples
  of 16** — the managed encoder only accepts whole macroblocks (it rejects e.g. 480×270) and
  can't change size, so it is recreated if a display's resolution changes. Unchanged frames
  aren't re-encoded (1 s keep-alive), and capture of frame N+1 overlaps encode of frame N.
- **Teacher side:** a single `StreamManager` keeps streams matched to what is on screen —
  thumbnails while the grid is visible, focus for the open student, nothing otherwise — with a
  1.5 s grace so navigation becomes an in-place switch, and a watchdog that retries failed
  streams.
- **Teardown:** teacher `StopStream`; when a student has no remaining viewers the agent
  stops encoding to save CPU/bandwidth, and the server returns the student to `Online`.
- **Relay fallback:** direct LAN connectivity by default. For segmented networks the server
  can hand both peers STUN/TURN servers (`ClassInsight:IceServers`, delivered via
  `GetIceServers` to registered clients only, since TURN entries carry credentials).

---

## 5. Telemetry design

- The agent samples on a fixed cadence (target 1 Hz) **independently of video** and
  pushes a `TelemetrySnapshot` via `PushTelemetry`; the server fans it out to teachers
  tagged with the student connection id.
- A snapshot contains: CPU %, memory used/total, **live bandwidth** (bytes/sec up/down,
  computed as a delta over the interval — not a raw counter), cumulative session
  totals, **top N processes** (by CPU/memory), the **foreground window title**, and
  optionally the **active browser URL/host**.
- Telemetry continues for every registered student regardless of whether their screen
  is currently being streamed, so the dashboard's processes/bandwidth views are always
  live.

---

## 6. Functional Requirements (SRS)

*Status: ✅ done · 🟡 built, runtime-pending · ⬜ not started. See §0 for the legend.*

### 6.1 Discovery & connectivity — `FR-DISC`

- ✅ **FR-DISC-01 [P0]** The server **shall** answer UDP discovery probes on port 47800
  with its reachable hub URL, server id, and room name.
- ✅ **FR-DISC-02 [P0]** The server **shall** emit periodic discovery beacons so passively
  listening clients can latch on without probing.
- ✅ **FR-DISC-03 [P1]** A student agent **shall** discover the server and connect
  automatically on launch, with no manual configuration and no user interaction.
- ✅ **FR-DISC-04 [P2]** A teacher app **shall** discover available server(s)/room(s) and
  connect; if multiple rooms respond it **shall** let the teacher choose. *(**GUI-verified**
  with two servers ("Classroom" :5080, "Lab B" :5081): the app showed a room chooser, joined the
  picked room, and on relaunch auto-joined the remembered room. "Switch room" re-opens the choice.)*
- ✅ **FR-DISC-05 [P1]** Clients **shall** automatically re-discover and reconnect after a
  network drop or server restart, restoring their roster entry. *(**Runtime-verified** both
  ways. Agent: a full discover→register loop (no sticky `WithAutomaticReconnect`) re-registers
  on every reconnection. Teacher: a `MaintainConnectionAsync` retry loop keeps re-discovering
  until a server appears (fixes "server not up at launch"), SignalR auto-reconnect handles
  transient drops with a `Reconnected` handler that **re-registers** to restore the roster
  entry, and a `Closed`/`ConnectionLost` path falls back to full re-discovery. Verified: with
  the server down, launching the teacher left it searching, then it connected on its own when
  the server came up ("Teacher joined"); and restarting the server under a connected teacher
  had both the student and teacher rejoin automatically. **Security:** on first connect the
  teacher pins the server endpoint (host+port+room) and an automatic reconnect only re-attaches
  to that same endpoint, so a rogue server appearing on the LAN after a drop can't harvest the
  join code — the restart test confirms a legitimately restarted server (new ServerId) still
  matches and reconnects. Full signed-discovery/TLS pinning remains NFR-SEC-06.)*
- ✅ **FR-DISC-06 [P0]** The server **shall** reject clients whose protocol version does
  not match its own. *(**Runtime-verified**: `StudentInfo`/`TeacherInfo` carry a
  `ProtocolVersion` defaulting to the shared `Protocol.Version` the client compiles
  against; `ClassroomHub` throws `HubException` on mismatch before any roster change. A
  v999 client got "Protocol version mismatch: client v999, server v1"; the teacher app
  surfaces this as `ConnectOutcome.Incompatible`, not a join-code prompt.)*

### 6.2 Student agent & monitoring — `FR-AGENT`

- ✅ **FR-AGENT-01 [P1]** The agent **shall** register with machine name, machine id,
  signed-in user, OS version, IP, screen resolution, and agent version.
- 🟡 **FR-AGENT-02 [P1]** The agent **shall** run on Windows as a background process that
  starts automatically (boot/login) without student action. *(Downgraded from ✅ in 0.11: a
  service runs in **session 0, which cannot see the student's desktop**, so the 0.6 design would
  have registered and sent telemetry but streamed a blank screen. The service is now a
  `SessionSupervisor` that launches the same exe with `--session-agent` **inside the active
  console session** as SYSTEM (token re-targeted with `TokenSessionId`, desktop
  `winsta0\default`), in a kill-on-close job, relaunching it on exit or user switch.
  `Install-Agent.ps1` registers it `Automatic`/`LocalSystem` with restart-on-failure. Builds
  clean; the console path is runtime-verified; the **elevated service install still has to be
  run** on a lab PC.)*
- ✅ **FR-AGENT-03 [P1]** The agent **shall** capture the primary display continuously for
  streaming when requested. *(**GUI-verified**: GDI `ScreenCapture` → scaled BGRA → VP8 encode
  loop in `ScreenStreamSender`, started on a teacher's stream request — the teacher's focus view
  rendered the student's live desktop continuously (real P2P WebRTC frames).)*
- ✅ **FR-AGENT-04 [P4]** The agent **should** support machines with multiple monitors,
  exposing each display to the teacher. *(**GUI-verified** on a two-monitor PC: the roster
  carries `Displays` (primary first), the focus view shows a display picker, and choosing
  "Display 2" re-negotiated and streamed the second monitor. Capture is per-monitor DPI aware.)*
- ✅ **FR-AGENT-05 [P1]** The agent **shall** collect and report CPU %, memory usage, live
  bandwidth, and top processes on a fixed cadence.
- ✅ **FR-AGENT-06 [P3]** The agent **shall** report the foreground application/window
  title; it **may** report the active browser URL. *(Title runtime-verified. Browser activity
  built: UI Automation reads the foreground Chrome/Edge/Firefox/Brave/Opera/Vivaldi address bar
  and reports the **host only** (never path or query, for privacy), shown on tiles and searchable.
  **Runtime-verified**: with Brave in the foreground the tile showed "🌐 claude.ai". Toggle:
  `ClassInsight:ReportBrowserActivity`.)*
- 🟡 **FR-AGENT-07 [P4]** The agent **should** resist casual termination by a standard
  student user (e.g. run as a service / restart on exit) within the lab trust model.
  *(Running as a `LocalSystem` service means a standard student can't stop it without admin;
  `sc failure` configures restart-on-failure (5s/5s/10s). Full verification on a locked-down
  student account still pending.)*
- ✅ **FR-AGENT-08 [P1]** The agent **shall** serve multiple concurrent teacher viewers of
  the same screen, or explicitly define and enforce a supported viewer limit. *(**Runtime-verified**:
  two teachers watched the same student at focus quality at once (A +58, B +57 frames over
  3 s); when A stopped, B kept streaming. No fixed limit — each viewer costs one encode on the
  agent (answers Q-1).)*

### 6.3 Screen streaming — `FR-STREAM`

- ✅ **FR-STREAM-01 [P2]** The system **shall** stream student screens as continuous
  WebRTC video — **not** periodic screenshots. *(**Runtime-verified** on 2026-11-13: a
  headless teacher requested a Focus stream and received 47 RTP frames → 46 decoded VP8
  frames at 1280×720 over ~15 s, sourced from the agent's **real GDI screen capture**
  (not a test pattern). Codec is **VP8** throughout, chosen for one pure-managed codec on
  all teacher platforms; the earlier "H.264" intent is superseded.)*
- ✅ **FR-STREAM-02 [P2]** Media **shall** flow peer-to-peer; the server **shall not**
  receive or relay video frames. *(Verified: frames arrived on the teacher's
  `RTCPeerConnection` media channel; the hub relays only SDP/ICE and has no video path.)*
- ✅ **FR-STREAM-03 [P2]** The teacher **shall** request a stream and receive it only for
  students; the agent **shall** respond with an SDP offer. *(Verified end-to-end:
  `RequestStream` → agent offer → teacher answer → ICE → `connected` → decoded frames,
  only for the pair the teacher authorized.)*
- ✅ **FR-STREAM-04 [P3]** The system **shall** support two quality levels — thumbnail
  (grid) and focus (single student) — and switch between them on demand. *(**Runtime + GUI
  verified**: thumbnails 480×272 at 5.0 fps, focus 1280×720 at 14.9 fps; thumbnail → focus →
  thumbnail switches renegotiate in place in ~0.1 s. **Bug found and fixed:** the managed VP8
  encoder rejects sizes that aren't multiples of 16, so the old 640×360 thumbnail had never sent
  a frame. Sizes are now macroblock-aligned and the first encode failure is logged as a warning.)*
- ✅ **FR-STREAM-05 [P2]** The server **shall** relay SDP/ICE **only** between a
  teacher↔student pair it authorized via `RequestStream` (pair-gated signalling).
- ✅ **FR-STREAM-06 [P3]** When a student has no remaining viewers, the agent **shall**
  stop encoding and the server **shall** mark the student `Online` (not `Streaming`).
  *(**Runtime-verified**: with two viewers the state stayed `Streaming` after the first stopped and
  went `Online` after the last; agent CPU fell to ~1.5 % of one core. A teacher that disconnects
  counts as stopping (the server tells the agent at once). In the app, leaving the Dashboard tab
  stops all thumbnails.)*

### 6.4 Telemetry: processes & bandwidth — `FR-TEL`

- ✅ **FR-TEL-01 [P3]** The dashboard **shall** show each student's live CPU and memory.
  *(**GUI-verified** in the running WinUI app: the focus detail panel showed live "CPU 95% /
  RAM 88%" and the Processes view "CPU 87%" for the connected student, updating in place.)*
- ✅ **FR-TEL-02 [P3]** The system **shall** display each student's **live bandwidth**
  (upload/download bytes per second) in real time. *(**GUI-verified**: the Bandwidth view
  showed live ↓9.8 KB/s / ↑5.7 KB/s with activity bars; the focus panel likewise.)*
- ✅ **FR-TEL-03 [P3]** The system **shall** display each student's running processes
  (name, PID, CPU, memory) and refresh them live. *(**GUI-verified**: both the focus detail
  panel and the classroom-wide Processes view rendered the live process list with real
  names/PIDs/CPU%/memory (PROCTOR.WinUI 13.5%/280 MB, ClassInsight.Agent 12.7%, sqlservr,
  explorer…), refreshing from the 1 Hz stream.)*
- ✅ **FR-TEL-04 [P3]** Telemetry **shall** update for all registered students regardless
  of whether their screen is being streamed. *(Verified: telemetry fanned out to the
  teacher continuously, before and independently of any stream request.)*
- ✅ **FR-TEL-05 [P3]** The teacher **should** be able to see session totals (cumulative
  bytes) in addition to the instantaneous rate. *(**GUI-verified**: the focus panel and the
  Bandwidth view both show "Session ↓ 1.7 GB ↑ 264.6 MB" alongside the live rate.)*

### 6.5 Teacher dashboard & views — `FR-TEACH`

- ✅ **FR-TEACH-01 [P2]** The dashboard **shall** show a live grid of student tiles, one
  per connected student, updating as students join/leave in real time. *(**GUI-verified**: the
  live roster rendered the connected student ("1 online", KEIKATSUNO · kenne) across the views,
  fed by the shared VM's observable `Students` collection. A tile now opens the focus view on an
  explicit tap (TapGestureRecognizer), not CollectionView selection, so the window gaining focus
  no longer pops a student's screen open. Since 0.11 each tile shows the student's **live
  thumbnail video** — GUI-verified.)*
- ✅ **FR-TEACH-02 [P2]** Selecting a tile **shall** open a focus view with a larger,
  higher-quality live stream of that student. *(**GUI-verified**: the focus view rendered the
  student's live desktop on the SkiaSharp canvas (real VP8/WebRTC frames, P2P) next to the live
  detail panel. The earlier NRE — `MaxReconstructedVideoFrameSize` set before `addTrack` — stays
  fixed.)*
- ✅ **FR-TEACH-03 [P2]** The app **shall** provide a navbar to switch views
  (Dashboard, Processes, Bandwidth, and — later — classroom control). *(**GUI-verified**: the
  Shell `TabBar` renders all three tabs and switches between them live; all bind to one
  DI-singleton `DashboardViewModel` (single connection/roster). Processes and Bandwidth views
  each rendered the connected student with live data.)*
- ✅ **FR-TEACH-04 [P2]** Each tile **shall** show student identity (machine/user) and
  connection/stream state (Online, Streaming, Idle, Offline). *(**GUI-verified**: identity
  "KEIKATSUNO · kenne" and the "Streaming" state badge (green) rendered in the views.)*
- 🟡 **FR-TEACH-05 [P2]** The app **shall** run natively on Windows, macOS, iOS, and
  Android from the single **.NET MAUI** codebase (`PROCTORSystemMulti-ProjectApp`). *(WinUI
  runs and is GUI-verified. The Android, iOS and Mac Catalyst heads **compile** with 0 warnings
  and carry their networking config (Android: cleartext LAN + Wi-Fi multicast lock for
  discovery; Apple: local-network usage text, `NSAllowsLocalNetworking`, the multicast
  entitlement — which Apple must grant — and Mac sandbox client/server). Not yet run on a
  device; iOS/Mac packaging needs a Mac.)*
- ✅ **FR-TEACH-06 [P3]** The app **should** let the teacher sort/filter students and
  adjust grid density. *(**GUI-verified**: search across name/user/foreground app/site ("0 of 1
  shown"), sort by Name/CPU/Memory/Bandwidth (live sorts re-order every 3 s so tiles don't
  jitter), and 1–8 tiles per row (default 1/2/3 for phone/tablet/desktop). Tiles are moved, not
  recreated, so their video keeps playing.)*

### 6.6 Server & coordination — `FR-SRV`

- ✅ **FR-SRV-01 [P0]** The server **shall** maintain an in-memory roster of connected
  students and teachers, with no persistent accounts or database.
- ✅ **FR-SRV-02 [P0]** The server **shall** push roster changes (join/leave/state) to all
  teachers in real time.
- ✅ **FR-SRV-03 [P0]** The server **shall** expose a status endpoint reporting server id,
  room, protocol version, and connected counts. *(Restored in 0.11 — the rebuilt server only
  had a bare `/`. Now `GET /status`; integration-tested and used by manual address entry.)*
- ✅ **FR-SRV-04 [P0]** On disconnect the server **shall** remove the client and purge any
  authorized viewing pairs involving it. *(Pairs restored in 0.11 — see FR-SEC-04; purging is
  unit- and integration-tested.)*
- ✅ **FR-SRV-05 [P4]** The room name **shall** be configurable; the server **should**
  support running several independent rooms on one network. *(**Runtime-verified**: two servers
  — even on one host, ports 5080/5081 — both answered discovery; the teacher chose between them,
  and an agent pinned with `RoomName` found only its room. Answers Q-2.)*

### 6.7 Security & access — `FR-SEC`

- ✅ **FR-SEC-01 [P0]** Students **shall** join with no code and no login (zero-friction,
  by design for lab deployment).
- ✅ **FR-SEC-02 [P0]** The teacher role **shall** be gated by a pairing code compared in
  constant time (SHA-256 of both normalised codes, `FixedTimeEquals`): the admin-set `ClassInsight:TeacherCode`, or — if none is set — a code
  the server **auto-generates and prints to the console** at startup. Teacher auth
  **fails closed**.
- ✅ **FR-SEC-03 [P0]** The server **shall** run fully open (no teacher authentication)
  only when the admin explicitly sets `ClassInsight:AllowOpenTeacherMode=true`, and
  **shall** log a prominent warning when it does.
- ✅ **FR-SEC-04 [P0]** The server **shall not** relay WebRTC signalling between
  connections that are not an authorized teacher↔student pair. *(**Regression found and fixed
  in 0.11:** the server rebuilt in 0.3 relayed SDP/ICE to *any* target. Pairs are now created
  only by a teacher's `RequestStream`; offers flow student → its teacher, answers teacher → its
  student, ICE either way within the pair; a connection holds one role. Integration tests cover
  the injection cases.)*
- ✅ **FR-SEC-05 [P5]** The system **should** offer a visible indicator on the student
  machine when monitoring/streaming is active, where policy or law requires notice. *(Delivered
  early (0.12): while any teacher views the screen the agent shows a click-through pill "Your
  screen is being viewed by your teacher" at the top of the display, hidden again when the last
  viewer leaves; it's excluded from capture so it never clutters the teacher's view. On by
  default, `ClassInsight:ShowViewingIndicator=false` to disable. **Verified** via the window
  manager: visible, topmost, no-activate, click-through, display affinity
  `WDA_EXCLUDEFROMCAPTURE`; gone after the teacher left.)*
- ✅ **FR-SEC-06 [P0]** The server **shall** throttle and lock out repeated failed teacher
  join-code attempts (per remote IP and globally) so a human-typable code cannot be
  brute-forced online. The auto-generated code **shall** be ≥ 64 bits of entropy. *(**Fixed in
  0.11:** the rebuilt server generated a 6-digit code (~20 bits) and throttled per IP only. Now a
  13-symbol Crockford base32 code (65 bits, e.g. `7KQ2-M9XD-4TR8B`) plus a global budget of 30
  failures/min → 1 min lockout. Unit- and integration-tested.)*
- ✅ **FR-SEC-07 [P1]** The agent's `MachineId` **shall** be a random per-install
  identifier (not derived from predictable machine attributes), and the agent **shall**
  connect only to the address that answered its discovery probe, on the fixed hub path.
  *(**Fixed in 0.11:** the rebuilt agent hashed machine name + MAC, and discovery followed the
  host named in the payload. Now a random 128-bit id in `%ProgramData%\ClassInsight\agent-id`
  (the installer protects the folder and discards a planted id), and `DiscoveryCodec` forces the
  source address and fixed hub path. Unit-tested.)*

### 6.8 Classroom control — `FR-CTRL` (Phase 5)

All commands are validated on the server and again on the agent, go only to registered
students (one, or the whole class), and are logged. In **open teacher mode** the strongest
controls (power, internet off, closing programs) are refused unless IT sets
`AllowStrongControlsInOpenMode`; with a join code they're always available. Fail-safes: locks
release when their teacher disconnects; room limits lift when the last teacher leaves; an agent
that loses the server unlocks and lifts everything itself.

- ✅ **FR-CTRL-01 [P5]** The teacher **may** blank/lock student screens. *(Full-screen "eyes on
  your teacher" cover on every display + low-level keyboard hook; 2 h auto-release; Ctrl+Alt+Del
  always works. **Live-verified** lock → unlock; lock badge on tiles; teacher-crash auto-unlock
  integration-tested.)*
- ✅ **FR-CTRL-02 [P5]** The teacher **may** broadcast their own screen to students. *("📺 Show my
  screen": **full screen** (students can't close it, kept on top) or **in a window**. The teacher is
  the WebRTC sender — one peer per student, each frame captured and VP8-encoded once (1280 px, 10 fps)
  and sent to all; media stays peer-to-peer (AD-4) and DTLS-SRTP encrypted. Broadcast signalling is
  pair-gated like viewing (only the broadcasting teacher ↔ its students); whole-class broadcasts
  include late joiners; a new broadcast replaces another teacher's; the viewer closes when the
  teacher stops or disconnects, or the agent loses the server. Teacher capture is **Windows-only**
  for now (other heads say so). **Live-verified**: WinUI teacher → real agent, "1 watching", the
  student window showed the teacher's screen live; Stop closed it.)*
- ✅ **FR-CTRL-03 [P5]** The teacher **may** send a message to one or all students. *(Centred
  card with OK; live-verified delivery.)*
- ✅ **FR-CTRL-04 [P5]** The teacher **may** remotely launch a URL/app on student machines.
  *(**Web pages only** (http/https, validated twice), opened as the signed-in student — never as
  SYSTEM. Launching arbitrary programs is deliberately not offered (remote code execution risk).
  Live-verified.)*
- 🟡 **FR-CTRL-05 [P5]** The teacher **may** take remote keyboard/mouse control of a
  focused student (with on-screen indication). *(Focus view → "🖱 Control" (Windows teacher app).
  Mouse over the picture and every key go to the student's PC as normalised positions and
  virtual-key codes, batched every ~30 ms and played with SendInput on the streamed display. A
  **strong control**: refused in open mode unless IT opts in, and **off on every PC unless IT
  sets `AllowRemoteControl=true` together with `ServerFingerprint`** (a strict key pin, NFR-SEC-06:
  a trust-on-first-use pin could have been captured by a rogue server, which must never be able
  to drive desktops). One controller per student (another teacher takes over; the first is
  told). Input is relayed only from the controlling teacher, validated (positions 0–1, real keys,
  ≤ 64 events/batch) and rate-limited. The student sees a red "Your teacher is controlling this
  computer" banner the whole time; the agent ignores input unless control was announced, and
  releases every held key/button when control ends, the teacher leaves or the connection drops.
  Leaving the focus view always hands control back. Hub tests; **needs a live check on a lab PC**
  (not exercised on the dev PC to avoid driving its user's desktop).)*
- 🟡 **FR-CTRL-06 [P5]** The teacher **may** apply a web-access allow/block policy. *(Room-wide
  limits: block-list or allow-only list of sites (sub-domains included); a blocked tab in front is
  closed with an explanation — rules unit-tested, live tab-close check pending (a Windows prompt held
  the foreground during testing). Also **blocked programs** (live-verified: Notepad closed on
  launch), **internet off** (firewall rule; needs the service's admin rights — run pending) and
  **mute** (live-verified). Pushed to late joiners; lifted when the last teacher leaves.)*
- 🟡 **FR-CTRL-07 [P5]** Students **should** be able to ask for help ("raise hand"). *(Tray icon on
  the student PC; teachers see a banner, amber tiles that float to the top, and can lower the hand.
  Server flow integration-tested; tray click not yet exercised on screen.)*
- 🟡 **FR-CTRL-08 [P5]** The teacher **may** log off / restart / shut down student PCs. *(30 s
  on-screen warning; refused in open mode without opt-in; agents can refuse locally with
  `AllowPowerCommands=false`. Relay and gating tested; not executed live, on purpose.)*
- ✅ **FR-CTRL-09 [P5]** The teacher **may** poll the class (yes/no, multiple choice with 2–6
  options, or a short written answer) and see results live. *(Students get an answer card — also if
  they join later — and may change their answer until the poll closes; teachers see bars, counts
  and who answered what, or, for an **anonymous** poll, totals plus unnamed written answers. One poll
  at a time, in memory only, closed automatically when the last teacher leaves. **Live-verified**:
  pressing "Yes" on the real student card reached the teacher as 1/1 answered, counts [1, 0].)*
- ✅ **FR-CTRL-14 [P5]** **File transfer.** *Send files*: whole class (⋯ More) or one student (focus
  view); they land in "Files from teacher" on the student's desktop. *Collect work*: the teacher
  asks (with a prompt), students tick files in Documents\Hand in from a window or the tray, and
  they're saved on the teacher's device under Documents\ClassInsight\Handed in\<date>\<student>.
  *(Relayed through the hub in 32 KB in-order chunks + SHA-256; nothing stored on the server. Max
  100 MB per file, 4 open transfers per sender, 10 hand-ins per student per collection. Names are
  sanitised (no paths, reserved names or bad characters). Executables/scripts: never handed in;
  sent by teachers only with a join code. Students can only hand in to the collecting teacher.
  The agent reads/writes student files **impersonating the signed-in student**, so a planted link
  can't make SYSTEM touch other files. Senders get per-PC receipts. Tests: names, assembler,
  routing, rules, hand-in. **Live-verified** send → agent save on desktop → receipt, and .exe
  refused in open mode (agent in console mode; the SYSTEM-impersonation path awaits an installed
  agent; hand-in awaits a GUI check).)*
- 🟡 **FR-CTRL-13 [P5]** Teacher ↔ student **chat**. *(Focus view → "💬 Chat"; students reply in a
  small window that opens without stealing their typing, or start one from the tray ("💬 Message my
  teacher"). Every teacher sees every conversation; a student sees only their own. Relayed, never
  stored; rate-limited (students 6 / 10 s, teachers 30 / 10 s); control characters stripped; unread
  badge on the tile. Hub tests; needs a GUI live check.)*
- 🟡 **FR-CTRL-12 [P5]** The teacher **may** show one student's screen to the rest of the class.
  *(Focus view → "📺 Show to class" (full screen or window). The teacher app relays the student's
  live stream it already receives (no extra capture, so it works from any teacher platform) through
  the FR-CTRL-02 broadcast pipeline. The shown student is never a viewer and is always told
  ("Your teacher is showing your screen to the class" / "…no longer…"); viewers' window says whose
  screen it is. The show ends if that student disconnects; while it runs, the teacher app keeps a
  full-quality stream from that student even after leaving the focus view. Hub tests; needs a
  two-PC live check.)*
- ✅ **FR-CTRL-11 [P5]** The teacher **may** switch lab PCs **on** with Wake-on-LAN.
  *(Agents report their physical adapters' MACs (wired first, max 4; validated server-side). The
  teacher app remembers PCs per server on its own device (120 days, max 500) and **Power… →
  Switch on offline computers** asks the server — which is on the lab network — to broadcast magic
  packets (limited + each subnet's directed broadcast, UDP 9 and 7). Allowed in open mode (waking
  is harmless); rate-limited server-wide to one request per 3 s, max 128 PCs. Needs WoL enabled in
  BIOS/NIC settings; see the agent deploy README. Unit + hub tests; actual wake-from-off not
  verifiable on the dev PC (Wi-Fi only).)*
- ✅ **FR-CTRL-10 [P5]** The teacher **may** run a **lesson timer** shown on every student screen.
  *(Optional label; students see a top-right countdown (amber in the last minute) and "Time's up!";
  counts down from each device's own clock; late joiners included; stops when the last teacher leaves.
  **Live-verified**: student pill "1:58 · Test timer", teacher banner with Stop.)*
- ✅ **FR-TEACH-07** Teacher tools (**⋯ More**): **random student picker** (no repeats until everyone
  has had a turn, optional "you've been picked" message), **snapshot all screens** (PNG per student),
  **screenshot** one student from the focus view, and **attendance export** (CSV: computer, user, IP,
  first/last seen, minutes online — written only on the teacher's device; formula-injection safe).
  Files go to `Documents\ClassInsight`. *(Live-verified: CSV and PNG written, picker shown.)*
- 🟡 **FR-SEC-08** The teacher app **may** be PIN-locked ("secure mode"): asked at launch and on
  "Lock this app now"; only a salted PBKDF2-SHA256 hash is stored; growing delay after wrong tries.
  *(Built; not exercised in the GUI so the teacher's own app wasn't left locked.)*
- ✅ **FR-STREAM-07** The teacher **should** be able to choose the stream resolution. *(Focus view:
  854 / 1280 / 1600 / 1920 / native; grid: 320 / 480 / 640 px thumbnails; remembered per device.
  The agent clamps to its IT ceiling `MaxStreamWidth` (default 1920) and never upscales.
  **Live-verified**: 320→320×176, 640→640×352, 854→848×480, 1600→1600×896, native→1920×1088.)*

---

## 7. Non-Functional Requirements (SRS)

*Status: ✅ done · ⬜ not started. Many NFRs can only be fully verified once the agent
and teacher app run end-to-end; those are marked ⬜ until measured.*

### 7.1 Performance — `NFR-PERF`

- ✅ **NFR-PERF-01** Live screen latency (capture→teacher render) **shall** be ≤ 1.5 s on
  a typical LAN under nominal load. *(Measured capture → decode by flashing a window and
  detecting its colour in decoded frames: median **114 ms** focus, **175 ms** thumbnail (which
  includes up to one 200 ms frame interval). Same-machine loopback; a LAN adds a few ms.)*
- ✅ **NFR-PERF-02** Thumbnail streams **shall** sustain ≥ 5 fps; focus streams **shall**
  sustain ≥ 15 fps where the source changes. *(Measured with continuously changing content:
  thumbnail **5.0 fps**, focus **14.9 fps** (150 frames / 10.0 s). Reaching 15 needed
  capture/encode pipelining and drift-free pacing — `Task.Delay`'s ~15.6 ms tick had capped
  focus at ~13 fps.)*
- ✅ **NFR-PERF-03** Telemetry **shall** update at ≥ 1 Hz with ≤ 2 s end-to-end delay.
  *(**Verified**: the agent samples and pushes every 1 s (`TelemetryInterval`), and the teacher
  UI was observed updating CPU/RAM/bandwidth/processes continuously and promptly in real time.)*
- ✅ **NFR-PERF-04** The agent **should** keep steady-state CPU overhead modest (target
  < 10 % of one core when streaming a thumbnail on typical lab hardware). *(0.12 moved capture to
  **DXGI Desktop Duplication**: the desktop is copied and mip-downsampled on the GPU and only the
  small mip is read back (~2.5 ms/frame vs 27–45 ms with GDI), and an unchanged screen costs
  nothing. Measured with continuously changing content: thumbnail **5.9 %** of one core (was
  10.1 % with GDI), ~1.3 % with no viewers; focus ~80 % (VP8 encode dominates). GDI remains the
  automatic fallback.)*

### 7.2 Scalability — `NFR-SCAL`

- ✅ **NFR-SCAL-01** A single room **shall** support at least **40 concurrent student
  agents** with all tiles streaming thumbnails. *(**Verified by simulation**: 40 simulated agents
  speaking the real protocol (registration, 1 Hz telemetry, real VP8/WebRTC thumbnail streams)
  against the real server and the WinUI teacher app: 40 online, 40 streams at ~197 frames/s
  aggregate (5 fps each), every tile live; teacher app **162 % of one core (~27 % of a 6-core
  PC)**, 392 MB. All on one machine, so a lab run on real PCs/network is still recommended.)*
- ✅ **NFR-SCAL-02** The server's per-student resource cost **shall** stay low because it
  relays control/signalling only, not media. *(Media never touches the server by design.)*

### 7.3 Reliability & availability — `NFR-REL`

- ✅ **NFR-REL-01** Clients **shall** auto-reconnect within a few seconds of a transient
  network loss without manual intervention. *(Satisfied by the FR-DISC-05 mechanism: SignalR
  auto-reconnect on both clients for transient drops, plus retry/re-discovery loops; verified
  reconnecting within seconds of a server restart with no user action.)*
- ✅ **NFR-REL-02** A student agent crash or reboot **shall not** affect other students or
  the teacher's session; the student re-appears automatically on return. *(By design: the server
  relays per-connection and drops a leaving student without touching others (NFR-REL-03); the
  agent's discover→register loop re-appears the student automatically on return (FR-DISC-05).)*
- ✅ **NFR-REL-03** The server **shall** tolerate abrupt client disconnects without leaking
  roster entries or authorized pairs.

### 7.4 Security & privacy — `NFR-SEC`, `NFR-PRIV`

- ✅ **NFR-SEC-01** The server **shall not** combine a wildcard CORS origin with
  credentials; no CORS policy is registered unless a browser client is added, and then
  only with an explicit origin allowlist.
- ✅ **NFR-SEC-02** Signalling **shall** be pair-gated (see FR-SEC-04) so a client cannot
  inject SDP/ICE into arbitrary connections.
- ✅ **NFR-SEC-03** WebRTC media **shall** be transported over SRTP (encrypted) as provided
  by the WebRTC stack. *(Verified: every offer uses the `UDP/TLS/RTP/SAVP(F)` profile with a DTLS
  `a=fingerprint:sha-256` — checked in a unit test and on each live offer.)*
- ✅ **NFR-SEC-04** The teacher pairing code **shall** be compared in constant time.
  *(Fixed in 0.11 — the rebuilt server used `string.Equals`.)*
- ✅ **NFR-SEC-05** The agent **shall not** follow a discovery response to an arbitrary
  host or path: it connects only to the datagram's source address on the fixed hub path.
  *(Fixed in 0.11 via the shared `DiscoveryCodec`, which teachers use too. Unit-tested.)*
- ✅ **NFR-SEC-06 [P4]** On a shared/untrusted network the system **should** authenticate
  the server to the agent (PSK/HMAC-signed discovery over a probe nonce), use TLS (`wss`)
  with a pinned certificate, and enrol agents with a per-agent key the server binds to
  `MachineId`. *(**TLS + server pinning done** (protocol v2): the server is TLS-only with a
  long-lived self-signed ECDSA P-256 certificate (or IT's own), created once and ACL-protected;
  clients pin the SHA-256 of its public key (SPKI). Agents: strict pin via
  `ServerFingerprint` (MSI `SERVERFINGERPRINT`), else trust-on-first-use remembered in the
  admin-only `HKLM\\SOFTWARE\\ClassInsight\\Agent` (students can't change or delete it, so it fails
  closed); discovery prefers the pinned key but the
  TLS handshake is what's enforced. Teachers: confirm the fingerprint on first connection (red
  warning if a known room/address presents a new key); the join code is only sent after the
  key is trusted. Remote control requires a strict agent pin. Discovery itself stays unsigned
  (a rogue reply can no longer be connected to, and can't hide the real server). **Agent
  enrolment:** each agent has its own ECDSA P-256 key (SYSTEM/Administrators-only file) and signs a
  single-use server nonce bound to the server's TLS fingerprint (a relayed or replayed signature is
  useless); the server binds each `MachineId` to its key on first registration (`agents.json`,
  tamper-checked) so no other program can register as that PC; `AgentEnrollment=Key` admits new PCs
  only with an HMAC made with the lab key (MSI `ENROLLMENTKEY`, kept in an admin-only registry key,
  hidden from logs). Tests: unsigned / wrong-server / replayed registrations refused, binding kept
  across a server restart, lab-key mode. **Live-verified**: agent key created locked down, PC
  enrolled then recognised after an agent restart, and refused by a key-mode server without the
  lab key. Unit tests for pinning; **live-verified** with
  the real server: plain HTTP refused, agent TOFU-pinned and connected over TLS, a wrong pin
  refused, a file sent over TLS, and the agent refusing a server restarted with another key.
  After security review: the certificate folder/file are created locked down and a certificate
  others could have planted is refused (verified); the agent's remembered pin moved to admin-only HKLM
  (an older pin file is imported only if students couldn't have written it); and the agent probes
  every discovered server — one candidate per source address, last good address first, up to 64
  in parallel — instead of trusting the advertised fingerprint.)*
- ✅ **NFR-PRIV-01** ClassInsight **shall** be deployable only on operator-authorized,
  institution-owned machines; documentation **shall** state the acceptable-use and
  notice obligations.
- ✅ **NFR-PRIV-02** No personal data **shall** be persisted server-side beyond the live
  in-memory roster.

### 7.5 Usability — `NFR-USE`

- ✅ **NFR-USE-01** A student agent **shall** require zero configuration and zero
  interaction — opening the app (or booting the PC) is sufficient to be monitored. *(Verified:
  started with no settings, it found the server and registered within a second.)*
- ✅ **NFR-USE-02** A teacher **shall** reach a live grid within seconds of opening the
  app on the lab network, with no manual IP entry. *(GUI-verified: launch → live thumbnail grid
  with no input when one room — or the remembered room — is present.)*
- 🟡 **NFR-USE-03** The teacher UI **shall** be legible on phone, tablet, and desktop form
  factors (responsive layout, 16 px minimum side gutters on mobile). *(Built: 16 px page
  padding, 1/2/3 default columns by idiom, wrapping toolbars, and the focus view stacks its
  detail panel under the video below 760 px wide. Not yet checked on a phone or tablet.)*

### 7.6 Portability — `NFR-PORT`

- 🟡 **NFR-PORT-01** The teacher app **shall** build and run on Windows, macOS, iOS, and
  Android from one **.NET MAUI** codebase. *(All four heads build; runs verified on Windows
  only — see FR-TEACH-05.)*
- ✅ **NFR-PORT-02** All components **shall** target .NET 10 and share `ClassInsight.Shared`
  for contracts to prevent protocol drift. *(Server, agent, tests and teacher app all reference
  it; discovery encoding is one shared `DiscoveryCodec`.)*

### 7.7 Maintainability — `NFR-MAIN`

- ✅ **NFR-MAIN-01** All wire contracts and method names **shall** live in
  `ClassInsight.Shared`; no component **shall** hard-code protocol strings.
- ✅ **NFR-MAIN-02** The protocol **shall** carry a version number and bump it on any
  breaking change (see FR-DISC-06).
- ✅ **NFR-MAIN-03** Code **shall** follow the existing solution conventions and build with
  zero warnings.

### 7.8 Compatibility — `NFR-COMPAT`

- ✅ **NFR-COMPAT-01** The system **shall** operate on a standard IPv4 LAN where UDP
  broadcast is permitted between the relevant devices. *(Full system verified: discovery →
  register → telemetry → thumbnail/focus video.)*
- ✅ **NFR-COMPAT-02** The system **should** degrade gracefully (and document the fix) on
  networks where broadcast is blocked, e.g. via a manually entered server address. *(**Verified**
  with the server's beacon switched off (`DiscoveryEnabled=false`): an agent with only
  `ServerAddress` set joined within ~4 s, and in the teacher app the "Enter its address" box
  appeared; typing the address connected straight to the live grid (remembered for next time).)*

---

## 8. Security model (summary)

1. **Students:** no login, no consent prompt — intentional for authorized lab devices.
2. **Teachers:** fail-closed pairing code (configured or auto-generated & printed),
   constant-time compared; fully open mode only via explicit opt-in flag, which warns.
   - **Discovery is always code-free.** Finding the server and student machines uses UDP
     broadcast with no code at all; the join code only gates *becoming a teacher*, never
     detection. The teacher app reflects this: on launch it **auto-connects with no
     code**, and reveals the code box **only if** the server rejects that attempt (i.e.
     the room runs with a teacher code). On a lab configured for open mode
     (`ClassInsight:AllowOpenTeacherMode=true`) the teacher never sees a code prompt —
     "open the app and the dashboard fills in". **Recommended only on an isolated/trusted
     lab VLAN**, since in open mode any device on that network can become a teacher.
3. **Signalling:** pair-gated; only server-authorized teacher↔student pairs are relayed.
4. **Media:** peer-to-peer, SRTP-encrypted, never through the server.
5. **Governance:** deploy only on authorized machines; honor institutional notice and
   acceptable-use policy; provide a monitoring indicator where required (FR-SEC-05).
6. **Discovery and transport:** agents and teachers connect only to the address that answered,
   on the fixed hub path (NFR-SEC-05), over TLS, and only if the server presents the pinned key
   (NFR-SEC-06): set by IT for agents, confirmed by the teacher on first connection.
7. **Agent process:** the service runs as LocalSystem from a protected `%ProgramFiles%` folder
   and launches its in-session monitor as SYSTEM, so a standard student can neither replace the
   binary nor end the monitor. Browser telemetry reports **hosts only**.

---

## 9. Delivery roadmap (start to finish)

| Phase | Goal | Key deliverables | Exit criteria |
| ----- | ---- | ---------------- | ------------- |
| **P0 — Foundations** ✅ | Contracts, server, discovery | `Shared` contracts; SignalR hub (roster, signalling relay, telemetry fan-out); UDP discovery; security hardening (pair-gating, optional teacher code, no CORS) | Server builds & runs; discovery verified end-to-end |
| **P1 — Student agent MVP** ✅ | Agent registers, streams one screen, reports telemetry | `ClassInsight.Agent`: discovery ✅, `RegisterStudent` ✅, 1 Hz telemetry ✅, WebRTC **VP8** sender ✅, auto-start service ✅ (elevated install run pending 🟡) | A real Windows PC appears in the roster (✅) and streams its screen on request (✅) |
| **P2 — Teacher app MVP** 🟡 | Live grid + focus view | Discovery + room choice ✅, live thumbnail grid ✅, focus view ✅, state badges ✅ on WinUI; Android/iOS/macOS heads compile ✅, on-device runs ⬜ | A teacher on any of the 4 platforms sees live student screens in a grid (Windows ✅; others need devices) |
| **P3 — Telemetry views** ✅ | Processes & bandwidth UI | Processes view ✅, live bandwidth view ✅, foreground app + browser host ✅, quality switching ✅, sort/filter/density ✅ | Teacher can inspect any student's processes and live bandwidth (✅) |
| **P4 — Hardening** ✅ | Production readiness | Reconnection ✅, multi-monitor ✅, STUN/TURN config ✅ (no TURN server tested), multi-room ✅, manual address ✅, PowerShell installer ✅, **MSI** ✅, DXGI capture ✅; code signing needs the institution's certificate | Stable 40-student room (✅ simulated; field run recommended); agent deployable by IT via installer (✅ MSI + script) |
| **P5 — Classroom control (future)** | Interactive control | Monitoring indicator ✅ (delivered early); blank/lock, broadcast, messaging, remote launch, optional remote control, web policy ⬜ | Per-feature acceptance; monitoring indicators in place (✅) |

**Cross-cutting (all phases):** automated tests (§10), zero-warning builds, and keeping
this document and `README.md` in sync with reality.

---

## 10. Testing strategy

`tests/ClassInsight.Tests` (xUnit, in the `ClassInsight` solution) — **52 tests, all passing**
(`dotnet test ClassInsight.slnx`):

- **Unit tests:** discovery codec (round trip, forced source host/path, bad magic/version/port,
  room-name sanitising), manual-address parsing, roster/pair authorization and teardown,
  per-source and global throttle (with a fake clock), join-code entropy and constant-time
  verification, bandwidth deltas and counter resets, browser host extraction, capture sizing
  (multiples of 16) and encodability, DTLS-SRTP in the offer.
- **Integration tests:** the real server on a `TestServer` over WebSockets — register → roster →
  telemetry fan-out; request → offer/answer/ICE within the pair and **dropped** for unpaired
  injections; viewer counting and teacher-disconnect teardown; role exclusivity; malformed
  targets; protocol mismatch; ICE servers only for registered clients; `/status`; closed-mode
  codes and lockout.
- **Runtime harness (manual, per release):** a headless teacher against a live server + agent
  measures fps, capture → decode latency (flash-window colour detection), agent CPU, quality
  switching, two concurrent viewers, and teardown.
- **Load simulation (manual):** a generator of N simulated agents (real protocol + real VP8
  streams) against the real server and teacher app — used for the 40-student check.
- **Still manual/field:** a 40-PC lab run on a real network, the elevated service/MSI install,
  and the phone/tablet/Mac heads.

---

## 11. Deployment & packaging

- **Server:** runs on the teacher's machine or a small lab server; configured via
  `appsettings.json` (`Port`, `RoomName`, `TeacherCode`); listens on `0.0.0.0`.
- **Agent:** `installer/` builds **`ClassInsight.Agent.msi`** (WiX v5, `dotnet build -c Release`)
  for GPO software installation / Intune / SCCM / imaging (`msiexec /i … /qn`); the equivalent
  `deploy/Install-Agent.ps1` remains for script-based deployment. Both install the
  self-contained agent as an auto-start LocalSystem service with restart-on-failure and a
  protected state folder; no per-machine configuration (discovery handles connection). Optional
  per-lab settings in `appsettings.json` (kept across MSI upgrades): `RoomName`,
  `ServerAddress`, `ReportBrowserActivity`, `ShowViewingIndicator`, `CaptureBackend`. Sign the
  MSI with the institution's code-signing certificate before distribution.
- **Teacher app:** distributed per platform (MSIX/Store/TestFlight/APK) from the MAUI
  (`PROCTORSystemMulti-ProjectApp`) build.
- **Networking:** allow UDP 47800 and the hub TCP port between lab devices, plus UDP between
  teacher and student for WebRTC media. Where broadcast is blocked, type the server address in
  the teacher app or set `ServerAddress` on agents. Across segmented networks, configure
  `ClassInsight:IceServers` with a TURN server.

---

## 12. Risks & open questions

| ID | Risk / question | Mitigation / decision needed |
| -- | ---------------- | ---------------------------- |
| R-1 | WebRTC video maturity in .NET from MAUI (esp. iOS/Android video rendering) | Retired on Windows: real screen capture → managed VP8 → WebRTC → managed decode → SkiaSharp is measured end to end (5 / 14.9 fps, 114–175 ms). Codec constraints learned: frame sizes must be multiples of 16, and an encoder can't change size (recreate it). Remaining: the decode cost of 40 thumbnails on the teacher, and on-device runs of the mobile heads. |
| R-2 | School networks that block UDP broadcast | Provide manual server-address entry (NFR-COMPAT-02). |
| R-3 | CPU cost of many simultaneous encodes on older lab PCs | Encode only when viewed (FR-STREAM-06, verified), skip unchanged frames, pipelined capture/encode, DXGI GPU capture: thumbnail 5.9 % of one core (NFR-PERF-04). Each extra viewer of the same student is one more encode. |
| R-4 | Legal/consent notice requirements vary by jurisdiction | FR-SEC-05 monitoring indicator; document admin responsibilities (NFR-PRIV). |
| R-5 | Rogue device registers as teacher and views screens | Resolved: fail-closed teacher auth — code enforced by default (configured or auto-generated 64-bit), open mode only via explicit `AllowOpenTeacherMode=true`, and failed `RegisterTeacher` attempts are throttled/locked out per IP and globally (FR-SEC-06). Future: QR-code pairing carrying a high-entropy secret. |
| R-6 | Rogue server on a shared LAN harvests telemetry; plaintext `ws` transport | Mitigated: TLS-only transport with server key pinning (NFR-SEC-06) — strict when IT sets the agents' `ServerFingerprint`, trust-on-first-use otherwise; teachers confirm the key. Agents are authenticated too (per-PC keys bound to their PC id; optional lab enrolment key). Remaining: unsigned discovery, which can no longer redirect or hide anything. |
| R-7 | A Windows service (session 0) can't capture the student's desktop (found in 0.11) | Resolved by design: the service supervises an in-session SYSTEM monitor. Needs one elevated install test on a lab PC before rollout. |
| Q-1 | Max concurrent viewers per student screen? | Answered: no fixed limit; each viewer is one encode on the agent (two verified). Revisit if labs routinely have several teachers. |
| Q-2 | Multi-room support in initial release or P4? | Answered: delivered (FR-SRV-05, FR-DISC-04). |

---

## 13. Glossary

- **Agent** — the ClassInsight program on a student's Windows PC.
- **Room** — one server instance = one classroom/lab; all its clients share a roster.
- **Roster** — the live set of connected students and teachers.
- **Signalling** — the SDP/ICE exchange that sets up a WebRTC connection.
- **Focus view** — a single enlarged, higher-quality student stream.
- **Open mode** — server running with no teacher pairing code (any device may become a
  teacher); logs a warning.

---

## 14. Change log

Newest first. Each entry records what moved and why, so the status markers above stay
traceable to real work.

- **0.24 — 2026-11-16**
  - **Agent setup .exe per lab** (P4): `installer\Build-AgentSetup.ps1` builds
    `ClassInsight-Agent-Setup.exe` (agent 0.3.0, self-contained) with a lab's server fingerprint,
    address, room, enrolment key and remote-control setting built in, so IT just double-clicks it
    on each PC; the setup also forwards those settings from its command line to the MSI. Output in
    `ClassInsight\dist\` (not committed). A setup with an enrolment key built in must be kept private.

- **0.23 — 2026-11-16**
  - **Agent enrolment** completes NFR-SEC-06 (✅): per-PC agent keys, signed single-use challenge
    bound to the server key, PC id bound to its key, optional lab enrolment key (MSI
    `ENROLLMENTKEY`). Discovery hardened against reply floods. Tests: 152.

- **0.22 — 2026-11-16**
  - **Encryption + server authentication** (NFR-SEC-06 🟡): TLS-only server, key pinning for agents
    (strict or trust-on-first-use) and teachers (confirm on first connection), protocol v2,
    MSI properties `SERVERFINGERPRINT` / `SERVERADDRESS` / `ROOMNAME` / `ALLOWREMOTECONTROL`.
    Remote control now needs a strict agent pin. Tests: 146.

- **0.21 — 2026-11-16**
  - **Remote control** (FR-CTRL-05 🟡; opt-in per PC, pinned server only — security review). File-transfer hardening after security review: wider
    blocked-type list, Mark-of-the-Web on saved files, no hijacking another teacher's collection.
    Tests: 143.

- **0.20 — 2026-11-16**
  - **File transfer**: send files + collect work (FR-CTRL-14 ✅). Tests: 127.

- **0.19 — 2026-11-16**
  - **Chat** (FR-CTRL-13 🟡). Tests: 114.

- **0.18 — 2026-11-16**
  - **Show a student's screen to the class** (FR-CTRL-12 🟡, built; awaiting a two-PC live check). Tests: 112.

- **0.17 — 2026-11-16**
  - **Wake-on-LAN** (FR-CTRL-11 ✅): switch offline lab PCs on from the teacher app. Tests: 110.

- **0.16 — 2026-11-15**
  - **Quick wins:** lesson timer (FR-CTRL-10 ✅), random picker / snapshot all / screenshot /
    attendance CSV (FR-TEACH-07 ✅), teacher PIN lock (FR-SEC-08 🟡). Student IPs now recorded as
    plain IPv4. Tests: 98, all passing.

- **0.15 — 2026-11-15**
  - **Show my screen (FR-CTRL-02 ✅):** teacher-to-class screen broadcast (full screen or window),
    encode-once/send-to-all over peer-to-peer WebRTC, pair-gated signalling, late joiners,
    stop-on-disconnect. Windows teacher capture; live-verified. Tests: 94, all passing.

- **0.14 — 2026-11-15**
  - **Polls / voting (FR-CTRL-09 ✅):** teacher "📊 Poll" page with live results; student answer
    card; anonymous mode; late joiners included; closes when the last teacher leaves. Live-verified
    through the real student UI. Tests: 89, all passing.

- **0.13 — 2026-11-15**
  - **Repository move:** the back end (Shared, Server, Agent, Tests, installers) now lives in
    this repo under `ClassInsight/` (it had been in the unrelated SENGEN repository).
  - **Agent setup.exe:** `ClassInsight/installer/setup` builds `ClassInsight-Agent-Setup.exe`
    (WiX Burn bundle around the MSI): double-click install, or `/quiet /norestart`.
  - **Classroom control (P5):** lock/unlock, messages, open web page, log off/restart/shut down,
    room limits (web block/allow lists, blocked programs, internet off, mute), raise hand — with
    server + agent validation, open-mode gating of the strong controls, and fail-safes.
    FR-CTRL-01/03/04 ✅, FR-CTRL-06/07/08 🟡 (see §6.8).
  - **Stream resolution control (FR-STREAM-07 ✅):** teacher-chosen focus resolution and thumbnail
    size, clamped by the agent's `MaxStreamWidth`.
  - **Security follow-ups:** VP8 keyframe size checked before decoding; agent pinned to
    `ServerAddress` skips broadcast discovery; power commands refusable per agent. Open teacher
    mode remains the shipped default (trusted lab VLAN) — switching to a join code is recommended
    for any shared network.
  - Tests: 85, all passing.

- **0.12 — 2026-11-15**
  - **DXGI Desktop Duplication capture (NFR-PERF-04 → ✅).** GPU copy + GPU mip downsampling,
    only the small mip read back (~2.5 ms/frame), unchanged screens free; GDI is the automatic
    fallback (secure desktop, mode change, unsupported driver). Thumbnail agent CPU 10.1 % → 5.9 %.
  - **40-student scale check (NFR-SCAL-01 → ✅, simulated).** 40 simulated agents on the real
    protocol + real WebRTC/VP8 against the real server and WinUI teacher: all tiles live at 5 fps,
    teacher ~27 % of a 6-core PC.
  - **Monitoring notice (FR-SEC-05 → ✅, delivered early from P5)** — click-through, capture-excluded.
  - **Manual address fallback GUI-verified (NFR-COMPAT-02 → ✅)** with the beacon switched off;
    **browser host verified live (FR-AGENT-06)**.
  - **Agent MSI** (`installer/`, WiX v5): service, restart actions and protected state folder
    checked in the MSI tables; install run pending (needs admin).
  - Security follow-ups from automated review: join code bound to the server that asked for it;
    decoded frame size capped by requested quality.

- **0.11 — 2026-11-15**
  - **Finished the remaining P1–P4 work and re-checked every "done" item against the code.**
    The audit found that the server and agent rebuilt in 0.3 had **silently lost** features
    this plan marked ✅. All are restored, with tests that pin them: pair-gated signalling
    (FR-SEC-04 — the hub relayed SDP/ICE to anyone), the ≥ 64-bit join code and global throttle
    (FR-SEC-06 — it was 6 digits, per-IP only), constant-time comparison (NFR-SEC-04), a random
    install id (FR-SEC-07 — it was a hash of name + MAC), source-address-only discovery
    (NFR-SEC-05 — it followed the payload's host), and `/status` (FR-SRV-03).
  - **Live thumbnail grid + quality switching (FR-STREAM-04 ✅).** New teacher `StreamManager`
    (one stream per student, matched to what's on screen, 1.5 s grace, watchdog), `FrameBuffer`
    and `LiveScreenView` (one Skia surface for tiles and focus). **Bug found:** the managed VP8
    encoder rejects sizes that aren't multiples of 16, so thumbnails (640×360) had never sent a frame.
  - **Performance (NFR-PERF-01/02 ✅, -04 🟡).** Single-pass GDI StretchBlt capture with the
    cursor, unchanged-frame skipping, capture/encode pipelining and drift-free pacing: thumbnail
    5.0 fps, focus 14.9 fps, latency 114–175 ms; thumbnail CPU ≈ 10 % of one core (boundary).
  - **Multi-monitor (FR-AGENT-04 ✅)**, **multi-room + room chooser (FR-SRV-05, FR-DISC-04 ✅)**,
    **manual server address (NFR-COMPAT-02 🟡)**, **STUN/TURN config (P4)**, **search / sort /
    density (FR-TEACH-06 ✅)**, **browser host telemetry (FR-AGENT-06)**, responsive focus view.
  - **Agent service supervisor (FR-AGENT-02 → 🟡).** A Windows service can't see the student's
    desktop (session 0), so the 0.6 service would have streamed a blank screen. The service now
    launches the monitor inside the console session; the installer protects the state folder.
  - **Mobile/Apple heads (FR-TEACH-05 🟡):** networking permissions, multicast lock and
    local-network entitlements; all four heads compile with 0 warnings.
  - **`ClassInsight.Tests` (47 tests, all pass)** and a runtime harness; GUI re-verified on WinUI
    (room chooser, live grid, focus + display 2, search/density).

- **0.10 — 2026-11-13**
  - **Auto re-discover & reconnect (FR-DISC-05 → ✅).** The teacher app gained a
    `MaintainConnectionAsync` retry loop (re-discovers every few seconds until a server appears,
    fixing "server not up at launch"), a SignalR `Reconnected` handler that **re-registers** to
    restore the roster entry after a transient drop, and a `Closed`/`ConnectionLost` fallback to
    full re-discovery. The agent already did a full discover→register loop, so it re-registers on
    every reconnection. **Runtime-verified both ways:** with the server down, the launched teacher
    sat searching then joined on its own when the server came up; and restarting the server under
    a connected teacher had both student and teacher rejoin automatically. This also satisfies
    **NFR-REL-01/02**.
  - **Checked off items confirmed by the running system:** FR-AGENT-03 (continuous capture —
    the focus view streamed the live desktop) and NFR-PERF-03 (telemetry at 1 Hz, observed
    updating promptly) to ✅; FR-DISC-04 note updated (connect GUI-verified; only multi-room
    selection remains).

- **0.9 — 2026-11-13**
  - **Custom window chrome (title bar) + usability pass.** Replaced the default OS title bar with
    integrated app chrome via MAUI's `Window.TitleBar`: a dark branded bar carrying the purple
    "ClassInsight" wordmark, keeping the native minimize/maximize/close buttons and the drag/resize
    behaviour, so the app reads like a real product rather than a default-chrome window.
    **GUI-verified** on screen. Also hid the Shell nav bar (`Shell.NavBarIsVisible="False"`) on the
    three tab pages so the oversized page title no longer repeats what the active tab already says —
    the window now reads: branded title bar, tab strip, content. (The focus view keeps its nav bar
    for the back button.)
  - **Fixed the focus-view auto-open quirk.** The dashboard tile now opens the focus view on an
    explicit tap (`TapGestureRecognizer`) instead of `CollectionView` selection, so WinUI
    auto-selecting the first item when the window gains focus no longer pops a student's screen
    open unbidden.

- **0.8 — 2026-11-13**
  - **End-to-end GUI verification — the whole teacher app, running, with live data.** Launched
    the real WinUI app against a live server + agent (this machine as the student) and captured
    the rendered window. Confirmed on screen: the Dashboard navbar and empty state; the focus
    view rendering the student's **live desktop** on the SkiaSharp canvas (real VP8/WebRTC, P2P)
    beside a live detail panel (CPU/RAM, live + session bandwidth, foreground app, top processes
    with real PIDs); the classroom-wide **Processes** view (full live process list, "1 online");
    and the **Bandwidth** view (live ↓/↑ rates with activity bars + session totals). Promoted to
    ✅ on the strength of the visual confirmation: FR-TEL-01/02/03/05 and FR-TEACH-01/02/03/04.
  - **Observed UX quirk (tracked):** on WinUI the dashboard CollectionView auto-advances into the
    focus view when the window first gains focus (no click). To be addressed in the usability
    pass alongside the custom title bar.

- **0.7 — 2026-11-13**
  - **Navbar + classroom-wide Processes/Bandwidth views (FR-TEACH-03 → 🟡).** `AppShell` is now
    a `TabBar` with Dashboard / Processes / Bandwidth tabs. The crux is a shared connection:
    `DashboardViewModel` is a DI singleton injected into all three pages (and the focus view's
    client), so one server link and one live roster feed every tab — no duplicate connections.
    New `ProcessesPage` shows each student's live top processes (BindableLayout inner list to
    avoid nested-scroll conflicts); new `BandwidthPage` shows per-student live down/up rates with
    relative activity bars and session totals. Added `DownloadRateText`/`UploadRateText`/
    `DownloadBarFraction`/`UploadBarFraction` to `StudentTile`. WinUI head builds 0/0 with
    compiled bindings (so every binding path is validated); on-screen render still to be confirmed.
  - **Agent install hardening (security review follow-ups).** The agent-as-service installer was
    hardened in three rounds against binary-path privilege escalation: install into a protected
    `%ProgramFiles%\ClassInsight` (ACL: Admins/SYSTEM full, Users read+execute); wipe any
    pre-existing dir to kill planted DLLs; allowlist-based ACL/owner verification over the whole
    tree; create the dir already holding the protected DACL (no TOCTOU window); and verify every
    installed file against the source bundle by SHA-256. Validator/mechanics spot-checked.

- **0.6 — 2026-11-13**
  - **Agent as an auto-start Windows service (FR-AGENT-02 → ✅).** The agent executable now
    hosts under the Windows Service Control Manager (`Microsoft.Extensions.Hosting.WindowsServices`:
    `AddWindowsService` + Event Log sink when running as a service), while remaining a normal
    `dotnet run` console app for development — the service wiring is a no-op unless the SCM
    launched the process. Added `deploy/Install-Agent.ps1` / `Uninstall-Agent.ps1` / `README.md`:
    install registers `ClassInsightAgent` as `Automatic` start under `LocalSystem` with
    restart-on-failure, so a managed lab PC auto-joins the classroom at boot with no student
    action and no per-machine config (discovery finds the server). Agent builds 0/0; console
    startup verified ("ClassInsight agent starting on …").
  - **Casual-termination resistance (FR-AGENT-07 → 🟡).** Falls out of the above: a
    `LocalSystem` service can't be stopped by a standard student without admin, and `sc failure`
    restarts it on exit. Full verification on a locked-down account still pending.

- **0.5 — 2026-11-13**
  - **Protocol-version gate (FR-DISC-06 → ✅).** `StudentInfo`/`TeacherInfo` now carry a
    `ProtocolVersion` that defaults to the shared `Protocol.Version` each client is built
    against, so no client plumbing was needed; `ClassroomHub` rejects a mismatch with a
    `HubException` before touching the roster. Runtime-verified (v999 → rejected, v1 →
    accepted). The teacher app distinguishes this from an auth failure via a new
    `ConnectOutcome.Incompatible` instead of wrongly prompting for a join code.
  - **Live processes + bandwidth detail panel (FR-TEL-03 → 🟡).** `StudentTile` now
    projects the telemetry's top processes into a `ProcessRow` list (name, PID, CPU%,
    memory) and session-total bandwidth; `FocusPage` gained a side panel bound to the live
    tile that shows CPU/RAM, live + session bandwidth, the foreground app and the process
    list, all refreshing in place from the 1 Hz stream while the screen streams alongside.
    WinUI head builds 0/0; on-screen render still to be confirmed in the running app.

- **0.4 — 2026-11-13**
  - **Video + telemetry pipeline runtime-verified end-to-end.** With the server and agent
    running, a throwaway **headless teacher** (discover → register open-mode → request a
    Focus stream) exercised the whole path on one machine: **46 decoded VP8 frames at
    1280×720** from the agent's real GDI screen capture, plus **1 Hz telemetry** carrying
    real CPU, memory, live bandwidth, process counts and the foreground window title.
    Promoted **FR-STREAM-01/02/03** and **FR-TEL-04** to ✅; strengthened the notes on
    FR-STREAM-04, FR-TEL-01/02 and FR-TEACH-02 (data path proven; GUI render still 🟡).
  - **Bug caught by that verification — focus view would have crashed.**
    `ScreenStreamReceiver` set `MaxReconstructedVideoFrameSize` **before** `addTrack`,
    which throws `NullReferenceException` (the setter dereferences the not-yet-created
    video stream). Reordered to set it after `addTrack`; this was the reason the first
    harness run received an offer but produced no frames.
  - **Security hardening — decoder frame bounds.** Automated review flagged that
    `ScreenStreamReceiver.OnVideoFrame` sized its BGRA buffer with 32-bit `w*h*4`, which a
    hostile/buggy sender could overflow into a tiny/negative allocation and drive an
    out-of-bounds write. Now clamps width/height to ≤ 8192 and computes sizes in 64-bit
    before allocating (NFR-SEC, defense-in-depth).
  - **Acknowledged, not changed — plaintext teacher-code over discovery.** The same review
    flagged that auto-connect trusts a discovered server and (in closed mode) would send
    the join code over `ws://` without authenticating the server. This is within the
    documented **trusted-lab-VLAN** threat model (open mode sends no code at all) and is
    already tracked as **NFR-SEC-06 [P4]** (pin server key / HMAC challenge) for any
    shared-network deployment; no code change now.

- **0.3 — 2026-11-13**
  - **Server + agent restored and running end-to-end.** `ClassInsight.Server` and
    `ClassInsight.Agent` (lost from disk earlier, see 0.2) were rebuilt from the shared
    wire contract and both client implementations, then verified at runtime on one machine:
    the agent auto-discovered the server over UDP, registered as a student with **no code**,
    and the server logged the join (with the real socket IP) and a clean leave. Both build
    0 warnings / 0 errors; a new `ClassInsight.slnx` ties Shared + Server + Agent together.
  - **Open teacher mode is the shipped default.** `appsettings.json` sets
    `ClassInsight:AllowOpenTeacherMode=true`, so teachers join with no code on the trusted
    lab VLAN (FR-SEC-03); `ServerIdentity` logs a prominent warning that this is VLAN-only
    and, in closed mode, prints a generated 6-digit join code to the console.
  - **Server internals** (reconstructed): `ClassroomHub` (roster + pair-gated SDP/ICE relay
    + telemetry fan-out), `RoomRegistry` (thread-safe roster), `ServerIdentity` (LAN-IP
    resolution, open/closed decision, code generation), `TeacherAuthThrottle` (fail-closed
    brute-force lockout, FR-SEC-06), and `DiscoveryBeaconService` (UDP responder + beacon).
  - **Agent internals** (new): `AgentWorker` (discover → register → 1 Hz telemetry →
    serve streams, with self-reconnect/re-register), `SystemTelemetryCollector` (CPU via
    `GetSystemTimes` deltas, memory via `GlobalMemoryStatusEx`, live bandwidth from NIC
    stats, top-15 processes, foreground window), `ScreenCapture` (GDI → scaled even-dim
    BGRA), and `ScreenStreamSender` (VP8 WebRTC offerer, one per teacher). Advanced
    **FR-AGENT-03/08** and **FR-STREAM-04/06** to 🟡 (built, runtime-pending).
  - **This retires R-1 on the agent side too:** encode is the same pure-managed
    `SIPSorcery.VP8` codec the teacher decodes with — no native media dependency anywhere.

- **0.2 — 2026-11-13**
  - **Teacher app — WebRTC receiver + focus view (P2 slice 2).** Added
    `ScreenStreamReceiver` (teacher-side WebRTC answerer: `RequestStream` → answer the
    agent's SDP offer → ICE exchange → per-frame VP8 decode) and `FocusPage` (tap a tile
    to open a focus view that renders the decoded stream on a SkiaSharp canvas). Moved
    **FR-STREAM-01/03** and **FR-TEACH-02** to 🟡 (built, runtime-pending). WinUI head
    builds clean, 0 warnings / 0 errors.
  - **Codec decision for the teacher side.** Decode uses **`SIPSorcery.VP8`** — a
    pure-managed VP8 codec (no native binaries, no PInvoke) — so the receive+render path
    is identical on Windows, macOS, iOS and Android from one codebase. This **largely
    retires R-1**; the open items are the agent's real screen-capture → H.264 source and
    measuring the managed decoder's cost at scale.
  - **`ClassInsight.Shared` rebuilt in place.** The sibling `ClassInsight` solution was
    lost from disk during development. `Protocol.cs` and the four contract files
    (`Discovery`, `Roster`, `Signaling`, `Telemetry`) were restored verbatim;
    `Discovery/LanDiscoveryClient.cs` was reconstructed from its call sites and §3 (noted
    in a header comment on that file). `ClassInsight.Server` and `ClassInsight.Agent` were
    **restored in 0.3** (see above).
  - **Zero-friction teacher connect.** The teacher app now auto-connects on launch with
    no join code and only shows the code box if the server rejects it (see §8). Paired
    with open teacher mode on a trusted VLAN, this delivers the "just open the app, the
    dashboard fills in" experience; `TeacherClient.ConnectAsync` now reports a
    `ConnectOutcome` (Connected / NeedsJoinCode / NoServer / Error) to drive this.
  - **Canonical plan relocated** to `PROCTORSystemMulti-ProjectApp/Resources/plan.md`.

- **0.1 — 2026-11-12**
  - Initial plan + SRS. P0 foundations (contracts, SignalR hub, UDP discovery, security
    hardening) and P1 agent MVP (discovery, registration, 1 Hz telemetry, WebRTC VP8
    sender self-test) built; teacher app P2 slice 1 (discovery + connect + live roster
    grid with telemetry and state badges) built against the WinUI head.

---

*This plan is the basis of the ClassInsight system. Update it as decisions change —
requirement IDs are stable references for tests, commits, and reviews.*
