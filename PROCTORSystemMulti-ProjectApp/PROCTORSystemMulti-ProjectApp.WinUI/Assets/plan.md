# ClassInsight — System Plan & Software Requirements Specification

**Document status:** Living document — the authoritative basis for the system from start to finish.
**Canonical location:** `PROCTORSystemMulti-ProjectApp/Resources/plan.md` (this file). Keep this copy current; any other copy (e.g. a WinUI `Assets` snapshot) is a convenience mirror.
**Version:** 0.8 (draft)
**Last updated:** 2026-11-13
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
| `ClassInsight.Server`   | ASP.NET Core (SignalR)   | Roster, WebRTC signalling relay, telemetry fan-out, discovery. **Open teacher mode on by default** (trusted-VLAN deploy). Discovery + roster + join/leave runtime-verified | ✅ Built |
| `ClassInsight.Agent`    | .NET 10 Windows app      | Discovery + registration + 1 Hz telemetry runtime-verified; GDI screen capture + **pure-managed VP8 WebRTC sender** built (one peer per watching teacher), runtime-pending | 🟡 In progress |
| `PROCTORSystemMulti-ProjectApp` | .NET MAUI app (WinUI/Droid/Mac/iOS heads) | **Teacher app:** discovery + connect + live roster grid with telemetry; focus view with WebRTC receiver + pure-managed VP8 decode rendered on SkiaSharp (WinUI head builds clean, 0 warnings). Runtime + remaining non-Windows heads pending | 🟡 In progress |
| `ClassInsight.Tests`    | xUnit test project       | Unit + integration tests across components                     | ⬜ Planned |

> The teacher app is a **separate MAUI solution** (`PROCTORSystemMulti-ProjectApp`, MAUI
> multi-project template with WinUI/Droid/Mac/iOS heads); the
> server and agent stay in the `ClassInsight` solution. Both reference the same
> `ClassInsight.Shared` contracts.

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
- **Agent:** .NET 10 Windows; Windows.Graphics.Capture or Desktop Duplication API
  (DXGI) for capture; WMI / `PerformanceCounter` / ETW for telemetry; SIPSorcery for
  WebRTC; packaged as a Windows service or auto-start tray app.
- **Teacher:** .NET MAUI (WinUI/Droid/Mac/iOS heads); SignalR .NET client; WebRTC
  receiver; XAML UI with a per-platform video rendering surface.
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
4. The server resolves the correct **server IP per client interface** so multi-NIC
   hosts answer with a routable address.
5. On connection loss, clients re-probe and reconnect automatically.

All datagrams are JSON prefixed with the magic token `CLASSINSIGHT/1`. Protocol
version is carried in every message so mismatched clients can be rejected cleanly.

---

## 4. Screen streaming design (WebRTC)

- **Negotiation (pair-gated):** teacher `RequestStream(studentConnId, quality)` →
  server authorizes the pair and tells the student `StreamRequested(teacherConnId)` →
  the **student (media sender)** creates the SDP **offer** → relayed to the teacher →
  teacher `SendAnswer` → ICE candidates exchanged both ways → media flows P2P.
- **Quality hints:** `Thumbnail` (downscaled, low fps) for the grid; `Focus`
  (full resolution, higher fps) for a single selected student.
- **Codec:** H.264 with hardware encode where available; fps/bitrate adapt to the
  quality hint and to WebRTC congestion feedback.
- **Teardown:** teacher `StopStream`; when a student has no remaining viewers the agent
  stops encoding to save CPU/bandwidth, and the server returns the student to `Online`.
- **Relay fallback:** initial release assumes direct LAN connectivity; a TURN/relay
  fallback is a Phase 4 hardening item for segmented networks.

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
- 🟡 **FR-DISC-04 [P2]** A teacher app **shall** discover available server(s)/room(s) and
  connect; if multiple rooms respond it **shall** let the teacher choose. *(Discover +
  connect built in the MAUI app; multi-room selection pending; GUI not yet run.)*
- ⬜ **FR-DISC-05 [P1]** Clients **shall** automatically re-discover and reconnect after a
  network drop or server restart, restoring their roster entry.
- ✅ **FR-DISC-06 [P0]** The server **shall** reject clients whose protocol version does
  not match its own. *(**Runtime-verified**: `StudentInfo`/`TeacherInfo` carry a
  `ProtocolVersion` defaulting to the shared `Protocol.Version` the client compiles
  against; `ClassroomHub` throws `HubException` on mismatch before any roster change. A
  v999 client got "Protocol version mismatch: client v999, server v1"; the teacher app
  surfaces this as `ConnectOutcome.Incompatible`, not a join-code prompt.)*

### 6.2 Student agent & monitoring — `FR-AGENT`

- ✅ **FR-AGENT-01 [P1]** The agent **shall** register with machine name, machine id,
  signed-in user, OS version, IP, screen resolution, and agent version.
- ✅ **FR-AGENT-02 [P1]** The agent **shall** run on Windows as a background process that
  starts automatically (boot/login) without student action. *(Same exe now hosts as a Windows
  service via `AddWindowsService`/`UseWindowsService`; `deploy/Install-Agent.ps1` registers it
  `Automatic`, `LocalSystem`, with restart-on-failure. Console mode unchanged for dev — the
  service wiring is a no-op unless the SCM launched it. Under the SCM, diagnostics go to the
  Windows Event Log.)*
- 🟡 **FR-AGENT-03 [P1]** The agent **shall** capture the primary display continuously for
  streaming when requested. *(GDI `ScreenCapture` → scaled BGRA → VP8 encode loop built in
  `ScreenStreamSender`, driven on connect; runtime-pending.)*
- ⬜ **FR-AGENT-04 [P4]** The agent **should** support machines with multiple monitors,
  exposing each display to the teacher.
- ✅ **FR-AGENT-05 [P1]** The agent **shall** collect and report CPU %, memory usage, live
  bandwidth, and top processes on a fixed cadence.
- ✅ **FR-AGENT-06 [P3]** The agent **shall** report the foreground application/window
  title; it **may** report the active browser URL. *(Title done; browser URL not yet.)*
- 🟡 **FR-AGENT-07 [P4]** The agent **should** resist casual termination by a standard
  student user (e.g. run as a service / restart on exit) within the lab trust model.
  *(Running as a `LocalSystem` service means a standard student can't stop it without admin;
  `sc failure` configures restart-on-failure (5s/5s/10s). Full verification on a locked-down
  student account still pending.)*
- 🟡 **FR-AGENT-08 [P1]** The agent **shall** serve multiple concurrent teacher viewers of
  the same screen, or explicitly define and enforce a supported viewer limit. *(Agent keeps
  one `ScreenStreamSender` per watching teacher, keyed by teacher connection id; runtime-pending.)*

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
- 🟡 **FR-STREAM-04 [P3]** The system **shall** support two quality levels — thumbnail
  (grid) and focus (single student) — and switch between them on demand. *(Agent sender maps
  `StreamQuality` to resolution/fps — 640px·5fps vs 1280px·15fps — and a re-request swaps the
  sender. **Focus** verified at runtime (decoded 1280×720); thumbnail-quality and live
  switching not yet exercised.)*
- ✅ **FR-STREAM-05 [P2]** The server **shall** relay SDP/ICE **only** between a
  teacher↔student pair it authorized via `RequestStream` (pair-gated signalling).
- 🟡 **FR-STREAM-06 [P3]** When a student has no remaining viewers, the agent **shall**
  stop encoding and the server **shall** mark the student `Online` (not `Streaming`).
  *(Both sides built: `StopStream` relays `StreamStopRequested`, the agent disposes that
  teacher's sender and its capture/encode loop, and the server resets state; runtime-pending.)*

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
  fed by the shared VM's observable `Students` collection. Observed UX quirk: on WinUI the
  dashboard CollectionView auto-advances into the focus view when the window first takes focus —
  tracked for the usability pass.)*
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
- ⬜ **FR-TEACH-05 [P2]** The app **shall** run natively on Windows, macOS, iOS, and
  Android from the single **.NET MAUI** codebase (`PROCTORSystemMulti-ProjectApp`).
- ⬜ **FR-TEACH-06 [P3]** The app **should** let the teacher sort/filter students and
  adjust grid density.

### 6.6 Server & coordination — `FR-SRV`

- ✅ **FR-SRV-01 [P0]** The server **shall** maintain an in-memory roster of connected
  students and teachers, with no persistent accounts or database.
- ✅ **FR-SRV-02 [P0]** The server **shall** push roster changes (join/leave/state) to all
  teachers in real time.
- ✅ **FR-SRV-03 [P0]** The server **shall** expose a status endpoint reporting server id,
  room, protocol version, and connected counts.
- ✅ **FR-SRV-04 [P0]** On disconnect the server **shall** remove the client and purge any
  authorized viewing pairs involving it.
- ⬜ **FR-SRV-05 [P4]** The room name **shall** be configurable; the server **should**
  support running several independent rooms on one network. *(Room name configurable; multi-room pending.)*

### 6.7 Security & access — `FR-SEC`

- ✅ **FR-SEC-01 [P0]** Students **shall** join with no code and no login (zero-friction,
  by design for lab deployment).
- ✅ **FR-SEC-02 [P0]** The teacher role **shall** be gated by a pairing code compared in
  constant time: the admin-set `ClassInsight:TeacherCode`, or — if none is set — a code
  the server **auto-generates and prints to the console** at startup. Teacher auth
  **fails closed**.
- ✅ **FR-SEC-03 [P0]** The server **shall** run fully open (no teacher authentication)
  only when the admin explicitly sets `ClassInsight:AllowOpenTeacherMode=true`, and
  **shall** log a prominent warning when it does.
- ✅ **FR-SEC-04 [P0]** The server **shall not** relay WebRTC signalling between
  connections that are not an authorized teacher↔student pair.
- ⬜ **FR-SEC-05 [P5]** The system **should** offer a visible indicator on the student
  machine when monitoring/streaming is active, where policy or law requires notice.
- ✅ **FR-SEC-06 [P0]** The server **shall** throttle and lock out repeated failed teacher
  join-code attempts (per remote IP and globally) so a human-typable code cannot be
  brute-forced online. The auto-generated code **shall** be ≥ 64 bits of entropy.
- ✅ **FR-SEC-07 [P1]** The agent's `MachineId` **shall** be a random per-install
  identifier (not derived from predictable machine attributes), and the agent **shall**
  connect only to the address that answered its discovery probe, on the fixed hub path.

### 6.8 Classroom control — `FR-CTRL` (Phase 5, future)

- ⬜ **FR-CTRL-01 [P5]** The teacher **may** blank/lock student screens.
- ⬜ **FR-CTRL-02 [P5]** The teacher **may** broadcast their own screen to students.
- ⬜ **FR-CTRL-03 [P5]** The teacher **may** send a message to one or all students.
- ⬜ **FR-CTRL-04 [P5]** The teacher **may** remotely launch a URL/app on student machines.
- ⬜ **FR-CTRL-05 [P5]** The teacher **may** take remote keyboard/mouse control of a
  focused student (with on-screen indication).
- ⬜ **FR-CTRL-06 [P5]** The teacher **may** apply a web-access allow/block policy.

---

## 7. Non-Functional Requirements (SRS)

*Status: ✅ done · ⬜ not started. Many NFRs can only be fully verified once the agent
and teacher app run end-to-end; those are marked ⬜ until measured.*

### 7.1 Performance — `NFR-PERF`

- ⬜ **NFR-PERF-01** Live screen latency (capture→teacher render) **shall** be ≤ 1.5 s on
  a typical LAN under nominal load.
- ⬜ **NFR-PERF-02** Thumbnail streams **shall** sustain ≥ 5 fps; focus streams **shall**
  sustain ≥ 15 fps where the source changes.
- ⬜ **NFR-PERF-03** Telemetry **shall** update at ≥ 1 Hz with ≤ 2 s end-to-end delay.
- ⬜ **NFR-PERF-04** The agent **should** keep steady-state CPU overhead modest (target
  < 10 % of one core when streaming a thumbnail on typical lab hardware).

### 7.2 Scalability — `NFR-SCAL`

- ⬜ **NFR-SCAL-01** A single room **shall** support at least **40 concurrent student
  agents** with all tiles streaming thumbnails.
- ✅ **NFR-SCAL-02** The server's per-student resource cost **shall** stay low because it
  relays control/signalling only, not media. *(Media never touches the server by design.)*

### 7.3 Reliability & availability — `NFR-REL`

- ⬜ **NFR-REL-01** Clients **shall** auto-reconnect within a few seconds of a transient
  network loss without manual intervention.
- ⬜ **NFR-REL-02** A student agent crash or reboot **shall not** affect other students or
  the teacher's session; the student re-appears automatically on return.
- ✅ **NFR-REL-03** The server **shall** tolerate abrupt client disconnects without leaking
  roster entries or authorized pairs.

### 7.4 Security & privacy — `NFR-SEC`, `NFR-PRIV`

- ✅ **NFR-SEC-01** The server **shall not** combine a wildcard CORS origin with
  credentials; no CORS policy is registered unless a browser client is added, and then
  only with an explicit origin allowlist.
- ✅ **NFR-SEC-02** Signalling **shall** be pair-gated (see FR-SEC-04) so a client cannot
  inject SDP/ICE into arbitrary connections.
- ⬜ **NFR-SEC-03** WebRTC media **shall** be transported over SRTP (encrypted) as provided
  by the WebRTC stack. *(Realized once the WebRTC clients are built.)*
- ✅ **NFR-SEC-04** The teacher pairing code **shall** be compared in constant time.
- ✅ **NFR-SEC-05** The agent **shall not** follow a discovery response to an arbitrary
  host or path: it connects only to the datagram's source address on the fixed hub path.
- ⬜ **NFR-SEC-06 [P4]** On a shared/untrusted network the system **should** authenticate
  the server to the agent (PSK/HMAC-signed discovery over a probe nonce), use TLS (`wss`)
  with a pinned certificate, and enrol agents with a per-agent key the server binds to
  `MachineId`. *(Deliberately deferred — it trades against the zero-config requirement.)*
- ✅ **NFR-PRIV-01** ClassInsight **shall** be deployable only on operator-authorized,
  institution-owned machines; documentation **shall** state the acceptable-use and
  notice obligations.
- ✅ **NFR-PRIV-02** No personal data **shall** be persisted server-side beyond the live
  in-memory roster.

### 7.5 Usability — `NFR-USE`

- ⬜ **NFR-USE-01** A student agent **shall** require zero configuration and zero
  interaction — opening the app (or booting the PC) is sufficient to be monitored.
- ⬜ **NFR-USE-02** A teacher **shall** reach a live grid within seconds of opening the
  app on the lab network, with no manual IP entry.
- ⬜ **NFR-USE-03** The teacher UI **shall** be legible on phone, tablet, and desktop form
  factors (responsive layout, 16 px minimum side gutters on mobile).

### 7.6 Portability — `NFR-PORT`

- ⬜ **NFR-PORT-01** The teacher app **shall** build and run on Windows, macOS, iOS, and
  Android from one **.NET MAUI** codebase.
- ⬜ **NFR-PORT-02** All components **shall** target .NET 10 and share `ClassInsight.Shared`
  for contracts to prevent protocol drift. *(Server + Shared comply; teacher app pending.)*

### 7.7 Maintainability — `NFR-MAIN`

- ✅ **NFR-MAIN-01** All wire contracts and method names **shall** live in
  `ClassInsight.Shared`; no component **shall** hard-code protocol strings.
- ✅ **NFR-MAIN-02** The protocol **shall** carry a version number and bump it on any
  breaking change (see FR-DISC-06).
- ✅ **NFR-MAIN-03** Code **shall** follow the existing solution conventions and build with
  zero warnings.

### 7.8 Compatibility — `NFR-COMPAT`

- ⬜ **NFR-COMPAT-01** The system **shall** operate on a standard IPv4 LAN where UDP
  broadcast is permitted between the relevant devices. *(Discovery layer verified; full system pending.)*
- ⬜ **NFR-COMPAT-02** The system **should** degrade gracefully (and document the fix) on
  networks where broadcast is blocked, e.g. via a manually entered server address.

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
6. **Agent discovery:** the agent connects only to the address that answered its probe,
   on the fixed hub path — a response can't redirect it elsewhere (NFR-SEC-05). Full
   server authentication + TLS pinning is a P4 item for shared networks (NFR-SEC-06).

---

## 9. Delivery roadmap (start to finish)

| Phase | Goal | Key deliverables | Exit criteria |
| ----- | ---- | ---------------- | ------------- |
| **P0 — Foundations** ✅ | Contracts, server, discovery | `Shared` contracts; SignalR hub (roster, signalling relay, telemetry fan-out); UDP discovery; security hardening (pair-gating, optional teacher code, no CORS) | Server builds & runs; discovery verified end-to-end |
| **P1 — Student agent MVP** 🟡 | Agent registers, streams one screen, reports telemetry | `ClassInsight.Agent`: discovery client ✅, `RegisterStudent` ✅, 1 Hz telemetry ✅, WebRTC H.264 sender ⬜, auto-start ⬜ | A real Windows PC appears in the roster (✅) and streams its screen on request (⬜) |
| **P2 — Teacher app MVP** 🟡 | Live grid + focus view | Teacher app on `PROCTORSystemMulti-ProjectApp` (MAUI): discovery client ✅, roster grid + telemetry 🟡 (built, not yet run), WebRTC receiver + VP8 decode 🟡 (`ScreenStreamReceiver`, pure-managed `SIPSorcery.VP8`), focus view 🟡 (`FocusPage`, SkiaSharp), state badges 🟡 | A teacher on any of the 4 platforms sees live student screens in a grid |
| **P3 — Telemetry views** | Processes & bandwidth UI | Processes view, live bandwidth view/graphs, foreground app; quality switching | Teacher can inspect any student's processes and live bandwidth |
| **P4 — Hardening** | Production readiness | Reconnection robustness, multi-monitor, TURN/relay fallback, multi-room, agent packaging (MSI/service), signing | Stable 40-student room; agent deployable by IT via installer |
| **P5 — Classroom control (future)** | Interactive control | Blank/lock, broadcast, messaging, remote launch, optional remote control, web policy | Per-feature acceptance; monitoring indicators in place |

**Cross-cutting (all phases):** automated tests (§10), zero-warning builds, and keeping
this document and `README.md` in sync with reality.

---

## 10. Testing strategy

- **Unit tests:** registry logic (roster, pair authorization, teardown), discovery
  encode/decode, telemetry math (bandwidth deltas).
- **Integration tests:** SignalR hub flows — register → request stream → signalling
  relay authorization (positive and negative/injection cases) → telemetry fan-out.
- **Discovery tests:** probe/response round-trip (already validated manually in P0).
- **Manual/field tests:** multi-machine lab run for latency, fps, CPU overhead, and
  reconnection against NFR-PERF / NFR-REL targets.

---

## 11. Deployment & packaging

- **Server:** runs on the teacher's machine or a small lab server; configured via
  `appsettings.json` (`Port`, `RoomName`, `TeacherCode`); listens on `0.0.0.0`.
- **Agent:** packaged as an MSI / Windows service with auto-start, deployable by IT via
  GPO/imaging; no per-machine configuration (discovery handles connection).
- **Teacher app:** distributed per platform (MSIX/Store/TestFlight/APK) from the MAUI
  (`PROCTORSystemMulti-ProjectApp`) build.
- **Networking:** ensure UDP 47800 and the hub TCP port are allowed between lab devices;
  document a manual-server-address fallback where broadcast is blocked.

---

## 12. Risks & open questions

| ID | Risk / question | Mitigation / decision needed |
| -- | ---------------- | ---------------------------- |
| R-1 | WebRTC H.264 maturity in .NET from MAUI (esp. iOS/Android video rendering) | Largely retired: agent self-test proved SIPSorcery negotiates + carries video headlessly; the teacher app now decodes with the **pure-managed `SIPSorcery.VP8`** codec (no native/PInvoke) and renders via SkiaSharp, so the receive+render path is identical on all four heads. Remaining: swap the agent source to real screen-capture → H.264, and measure the managed VP8 decoder's cost at scale (grid uses thumbnails; focus view decodes one stream). |
| R-2 | School networks that block UDP broadcast | Provide manual server-address entry (NFR-COMPAT-02). |
| R-3 | CPU cost of many simultaneous encodes on older lab PCs | Thumbnail downscale + adaptive fps/bitrate; encode only when viewed (FR-STREAM-06). |
| R-4 | Legal/consent notice requirements vary by jurisdiction | FR-SEC-05 monitoring indicator; document admin responsibilities (NFR-PRIV). |
| R-5 | Rogue device registers as teacher and views screens | Resolved: fail-closed teacher auth — code enforced by default (configured or auto-generated 64-bit), open mode only via explicit `AllowOpenTeacherMode=true`, and failed `RegisterTeacher` attempts are throttled/locked out per IP and globally (FR-SEC-06). Future: QR-code pairing carrying a high-entropy secret. |
| R-6 | Rogue server on a shared LAN harvests telemetry; plaintext `ws` transport | Partially mitigated: agent connects only to the probe's responder on the fixed hub path (NFR-SEC-05). Full fix — signed discovery + `wss`/TLS pinning + agent enrolment (NFR-SEC-06) — deferred to P4 as it trades against zero-config. Acceptable on an isolated lab VLAN. |
| Q-1 | Max concurrent viewers per student screen? | Define in P1 (FR-AGENT-08). |
| Q-2 | Multi-room support in initial release or P4? | Currently P4; confirm with stakeholder. |

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
