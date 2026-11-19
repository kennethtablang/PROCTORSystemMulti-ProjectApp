# PROCTOR Teacher on Android and iOS

The teacher app also runs on phones and tablets: the same live screens, focus view, messages, locks
and website limits as on the teacher's PC. It is only the **teacher** side. Student computers keep
the Windows agent; Android and iOS don't let an app watch or control the rest of the device the way
the agent does on Windows.

The phone/tablet connects to the classroom service on the teacher's PC (PC01), so it must be on a
network that reaches it (the lab LAN, or Wi-Fi bridged to it). If the room doesn't appear by itself,
type the teacher PC's address once; the app remembers it and its server key.

| | Android | iOS (iPhone / iPad) |
|---|---|---|
| File | `PROCTOR-Teacher-Android.apk` | `PROCTOR-Teacher-iOS.ipa` |
| Built on | This Windows PC: `Build-AndroidApp.ps1` | A Mac: `Build-iOSApp.sh` |
| Needs | Nothing extra | Apple Developer Program membership (USD 99 / year) |
| Install | Open the .apk on the device | Through Apple: TestFlight, Ad Hoc or MDM (see below) |
| Updates | Install the newer .apk over the old one | A new build through the same channel |
| Room discovery | Automatic | Type the address once (automatic after Apple grants multicast) |

Versions follow `ProctorVersion` in `Directory.Build.props`, like the Windows setups. The app's id is
`com.proctor.teacher` on both platforms.

## Android

### Build

```powershell
.\PROCTOR\installer\Build-AndroidApp.ps1
```

This writes `PROCTOR\dist\PROCTOR-Teacher-Android.apk` (+ `.sha256`). `Publish-Release.ps1` also
builds it and attaches it to the GitHub release (`-SkipAndroid` leaves it out).

**Signing key.** The first build creates the key in `%LOCALAPPDATA%\PROCTOR\android-signing\`:
`proctor-teacher.keystore` and `proctor-teacher.keystore.password`. **Back up both files** (USB drive,
password manager). Android installs an update over the app only when it's signed with the same key.
If the key is lost, every device has to uninstall the app and install it again, which loses its saved
servers and settings. To build on another PC, copy both files to the same folder there, or pass
`-KeystorePath`.

### Install on a phone or tablet

1. Copy `PROCTOR-Teacher-Android.apk` to the device (USB, Google Drive, email, or the GitHub release
   page in the device's browser).
2. Open it. Android asks once to allow **Install unknown apps** for the browser or file manager.
   Allow it, then tap **Install**.
3. Open **PROCTOR Teacher** and choose the classroom.

Google Play Protect may say the app is from an unknown developer (it isn't on the Play Store). Tap
**More details → Install anyway**.

With a USB cable and USB debugging on: `adb install -r PROCTOR-Teacher-Android.apk`.

For many school-owned tablets, push the .apk with your MDM (Intune, Google Admin and others).

### What works differently on Android

Everything the Windows teacher app does, except running the classroom service itself (that stays on
the teacher's PC, PC01). Where a phone works differently:

| Feature | On Android |
|---|---|
| **Show my screen** | Android asks **Start now** (screen recording) each time, and shows a notification while sharing. Sent at up to 1280 px on the long side so the phone keeps up. |
| **Remote control** | Tap = click, hold = right-click, drag = drag, two fingers = scroll. The **keyboard** button types on the student's PC (US layout; letters like é aren't sent). A connected mouse or keyboard works like on a PC. |
| **Screenshots, attendance, handed-in work** | Saved inside the app; **Share** sends them to Drive, Files, email… |

### Update

The app checks GitHub for a newer release a little after it opens, and then every 4 hours. It
downloads `PROCTOR-Teacher-Android.apk` and checks its size and SHA-256. Saved classrooms and
settings are always kept.

**Fully automatic (Android 12 and newer, from the second update on).** No pop-up, no taps. The update
installs once the teacher has left the app for a minute, and never while they're sharing their
screen or controlling a PC. The next time they open PROCTOR it's the new version.

**The first update after installing the .apk by hand**, and **every update on Android 11 or older**,
need the teacher once:

1. The pop-up offers **Update now**.
2. *Once ever:* Android opens **Install unknown apps** for PROCTOR. Turn on **Allow from this
   source**, come back and tap **Install**.
3. Android asks **Do you want to update this app?** Tap **Update**.

Why the first one asks: Android lets an app update itself silently only when it installed its
current version itself. The hand-installed .apk was installed by the browser or Files app; after
PROCTOR's first in-app update, PROCTOR is its own installer and later updates are silent.

**School-managed tablets:** with an MDM (Intune, Google Workspace, Jamf…), push the .apk as a managed
app. The MDM then installs and updates it silently from the start.

Android only accepts the update if it's signed with the same key as the installed app (see
*Signing key* above). The release must include the .apk, which `Publish-Release.ps1` attaches unless
it's run with `-SkipAndroid`.

## iOS (iPhone and iPad)

Apple doesn't allow installing an app file directly, as Android does. Every iOS app is signed by
Apple's tools on a Mac and delivered through one of Apple's channels.

### One-time setup

1. **Apple Developer Program**: enrol the school or yourself at <https://developer.apple.com/programs/>.
2. **A Mac** with Xcode (App Store), the .NET 10 SDK and the iOS workload:
   `sudo dotnet workload install ios`. In Xcode → Settings → Accounts, sign in with the developer
   account.
3. In <https://developer.apple.com/account/resources>:
   - **Identifiers → +** → App ID → bundle id `com.proctor.teacher`. If Apple says it's taken, use your
     own (e.g. `com.yourschool.proctor`) and build with `-b com.yourschool.proctor`.
   - **Certificates → +** → **Apple Distribution**, and install it in the Mac's keychain.
   - **Profiles → +**: pick the channel below. **App Store Connect** for TestFlight, **Ad Hoc** for
     registered devices. Download it and double-click it on the Mac.
4. *(Optional)* Request the **multicast networking entitlement** for the bundle id at
   <https://developer.apple.com/contact/request/networking-multicast> so the app finds the classroom
   by itself. Until Apple grants it, build without `-m` and type the teacher PC's address once.

### Build (on the Mac)

```bash
git clone https://github.com/kennethtablang/PROCTORSystemMulti-ProjectApp.git
cd PROCTORSystemMulti-ProjectApp
PROCTOR/installer/Build-iOSApp.sh -p "<provisioning profile name>"
```

This writes `PROCTOR/dist/PROCTOR-Teacher-iOS.ipa`. Options: `-k` signing identity, `-b` bundle id,
`-m` multicast entitlement (only once granted).

### Get it onto teachers' devices

| Channel | Best for | How |
|---|---|---|
| **TestFlight** (recommended) | Any number of teachers, up to 10,000 | App Store Connect profile. Upload the .ipa with Apple's **Transporter** app, add teachers as testers. They install **TestFlight** from the App Store and accept the invite. Each build lasts 90 days, so upload a new one at least that often. |
| **Ad Hoc** | A few devices you can register | Register each device's UDID under Devices (up to 100 per device type per year), make the Ad Hoc profile include them, then install with **Apple Configurator** or Xcode → Devices and Simulators → +. |
| **Apple School Manager + MDM** | School-managed iPads | Publish as a private (custom) app for your organisation in App Store Connect, then push it to iPads with your MDM. |

### Update

Bump `ProctorVersion`, build again on the Mac, and deliver it through the same channel. TestFlight
and MDM update devices for you. Ad Hoc means installing the new .ipa again.
