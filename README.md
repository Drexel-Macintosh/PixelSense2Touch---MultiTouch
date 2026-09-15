# ⬇️ Download the ready-to-run build (Surface1-Hydra-x64)

**[HydraTouch-PixelSenseToTouch-x64.zip](https://github.com/Drexel-Macintosh/PixelSense2Touch---MultiTouch/releases/latest/download/HydraTouch-PixelSenseToTouch-x64.zip)** — the working, validated build from this fork. Unzip, read the included `README.txt`, run it over Surface Input. (Everything it needs is in the zip.)

> This fork's improvements are also proposed **upstream** as a pull request to [Heer-Boaz/PixelSense2Touch](https://github.com/Heer-Boaz/PixelSense2Touch). This download exists so the working build is available even if upstream hasn't merged or released it yet.

---

# PixelSense2Touch — Surface1-Hydra-x64 fork

Adds Windows 10/11 multi-touch to the Microsoft Surface 1.0 (PixelSense) table. This fork is maintained as part of the **[Surface1-Hydra-x64](https://github.com/Drexel-Macintosh)** project — a from-scratch x64 driver that revives the 2008 Surface 1.0 vision hardware on Windows 11 — and is validated on real hardware.

## What this fork changes (vs. upstream v2.0)

- **Robust simultaneous multi-touch.** Every Surface contact is injected as a real Windows touch pointer and the full live set is re-injected each event, so Windows does its own gestures (tap → click, press-and-hold → right-click, drag, pinch/zoom). This fixes three defects that made the previous build effectively single-touch: a stuck/ghost pointer on finger-up, cross-thread corruption of the shared pointer, and pointer-id collisions (`id % 20`). See the upstream PR for the full write-up.
- **Leaner runtime.** The touch frame is built into a reusable buffer (no per-event allocation / GC churn), and an unchanged frame is not re-sent (a held-still finger no longer re-fires an identical inject). Same behavior, lower CPU — measurable on the original Core2Duo table.
- **Built x86** to match the 32-bit Surface runtime it talks to.
- **Dropped the unused `InputSimulatorStandard` dependency** — real touch pointers make Windows generate gestures natively, so the old mouse-emulation path is gone.
- **"Hydra Touch" branding** (tray/app icon + name) for the Surface1-Hydra-x64 distribution. Icon/name only — touch behavior is identical.

## Output sink: touch injection or the HydraTouch HID digitizer (2.2, default changed in 2.3.1)

The last step — how a finished frame reaches Windows — is behind an `ITouchSink` interface with two implementations. `InjectTouchSink` is the path this app has always used (`InjectTouchInput`) **and is the default**. `HidDigitizerSink` hands the identical contact set to the Surface1-Hydra-x64 project's **HydraTouch** virtual HID digitizer (`\.\HydraTouch`) instead, so the reports enter the input stack in the kernel, below UIPI — the only way the contacts land on elevated (admin) windows, which user-mode injected input can never reach. It is **opt-in**: set the environment variable `PIXELSENSETOUCH_SINK=hid` before starting the app (`inject` states the default explicitly). The tray's About box always says which path is live and why. Neither sink covers the UAC prompt or the lock screen: those run on the secure desktop, where Surface Input stops producing contacts at all, so they need the Hydra Touch session-0 service, not this app — and that service feeds the digitizer directly, so it does not depend on this sink.

**Status of the HID sink (2026-09-14): built, not yet validated — hence opt-in.** Measured on the table, a finger held still through the HID sink *flashes* — the runtime's own contact list flickers Added/Removed — while the same build through injection holds solid; the camera driver was ruled out (production-identical masks in both runs). Likely the digitizer's touch feeding back into the runtime, or the sink's report cadence. 2.2.0.0 and 2.3.0.0 preferred the digitizer automatically whenever it was installed, which put a flashing build on any table with HydraTouch; 2.3.1.0 reverses that default. Only `PIXELSENSETOUCH_SINK=hid` selects it now.

## Idle hint (IdleMask)

Since 2.3.0.0 the app also tells the camera driver whether anything is on the table. The Surface1-Hydra-x64 **HydraX64Beta** driver has an *IdleMask* gate that cuts most of Surface Input's idle CPU by handing the runtime an all-zero "changed cells" mask while the table is empty — but the runtime drops every contact it is tracking the moment it gets such a mask, so the gate must never fire with a finger, blob or tag on the glass. The only authoritative occupancy signal is the runtime's own contact list, which this app already receives. So `HydraIdleHint` (in `PixelSenseToTouchLib`) keeps a running count of **all** contact types from `ContactAdded`/`ContactChanged`/`ContactRemoved` and pushes it to the driver as IOCTL 2108 (`IOCTL_HYDRA_IDLE_CONTACT_HINT`, 16 bytes: magic `HNT1`, contacts, sequence, flags) on every change and every 500 ms as a heartbeat. The count subscription is permanent for the life of the app — the "suspend while Surface Shell runs" logic detaches only the injection handlers — so the gate keeps working under the Shell too.

**It never claims "empty" without evidence.** A wrong "occupied" only delays the idle saving; a wrong "empty" costs a real contact. So: the hint is attached to the `ContactTarget` *before* input is enabled and counts `ContactChanged` too, because a contact Surface Input was already tracking when this app connected (a tag left on the table, a finger held through a restart) arrives as *Changed* only, never *Added*. No real count is sent until the runtime has delivered its first frame (or first contact event) to this app; at that moment the count is reconciled with `ContactTarget.GetState()` and becomes the first hint. Until then the only thing sent is one probe per open with `Contacts=1` and flag `HYDRA_IDLE_HINT_FLAG_PROBE` (0x2) — "occupied until proven" — which establishes 2108 support for the status line. On Stop/Exit/logoff one goodbye hint goes out (`Contacts=0xFFFFFFFF`, flag `HYDRA_IDLE_HINT_FLAG_GOODBYE` 0x1) so a driver that knows the flag drops the hint at once; an older driver just sees "not empty" until its timeout. A killed process sends nothing and the timeout covers it. The tray is single-instance per session (`Global\PixelSenseToTouch-<sessionId>` mutex): a second copy exits at once, so it can never inject twice or send a startup hint from a `ContactTarget` that has not seen the runtime's contacts.

**It is inert unless the HydraX64Beta IdleMask gate is on.** Opening `\\.\HYDRA` for write needs an elevated process (the app already runs elevated for the HID digitizer), and the production HydraX64 driver or a pre-IdleMask beta answers 2108 with `ERROR_INVALID_FUNCTION`; in either case the helper logs once, reports "idle hint: off: …" in the tray menu / tooltip and goes quiet. A device that is merely absent or restarting (error 2 / 433 / 1167 — the app launched while a driver swap is in progress) is retried every 5 s, at start and after a loss. With the gate off (`IdleMask=0`, the default) the driver stores the hint and never reads it. Nothing here can make touch worse: a missing or stale hint (no heartbeat for `IdleHintTimeoutMs`, default 1.5 s — e.g. this app killed or not running) makes the driver assume "not empty" and deliver full masks, which is exactly today's behaviour. The tray's context menu shows `idle hint: probing … waiting for the runtime's first frame` and then `idle hint: on (seq N, contacts C)` while it is live; if a driver swap went from a driver *without* 2108 to one with it, restart this app once.

## Install / Update / Uninstall

Full step-by-step instructions are in **`README.txt` inside the download**. In short: this app is a *bridge* on top of "Surface Input", so the table must already have the Hydra x64 camera driver, the Surface 1.0 runtime, and calibration installed (see the Surface1-Hydra-x64 project's install guide for that one-time setup). Then run **Surface Input** (`/r Surface`, as administrator) first, and **PixelSenseToTouch** (as administrator) second.

---
---

*The original upstream README (by Boaz Pat-El) follows.*

# Latest version binary download
https://github.com/Heer-Boaz/PixelSense2Touch/releases/tag/v2.0

# PixelSense2Touch
App that adds touch support to Microsoft PixelSense 1.0 (Microsoft Surface) that runs on Windows 8+.

## Update - V2.0
The app is now able to handle multiple touches and translate those touches into
- Mouse clicks
- Right mouse clicks; place finger on screen for long time - longer than you would expect :-)
- Dragging
- Multi-touch events, such as pinching/zooming

## Prerequisites for running the app
- Microsoft PixelSense (Surface Table) 1.0
- Windows 8/10\*
- .NET Framework 4.7.2 run-time
- Microsoft Surface SDK 1.0 SP1
  - Be sure to download the SDK and place the DLLs in the outputfolder
  - You need the following assemblies:
    - Microsoft.Surface.Common.dll
    - Microsoft.Surface.Core.dll
    - Microsoft.Surface.Core.xml(?)
    - Microsoft.Surface.dll
    - Microsoft.Surface.xml(?)
    - Microsoft.Surface.Tools.dll

## Prerequisites for compiling the source
- VS2019
- .NET Framework 4.7.2 SDK
- Other libraries such as WPF
- Be sure to download the SDK and place the DLLs in folder *./PixelSense2Touch/lib*, as listed above. These files are copied to the output folder

\* See this post by Zac Bowden: https://www.windowscentral.com/windows-10-on-microsoft-surface-coffee-table and this post by Rajen: http://blog.rajenki.com/2014/02/modernizing-original-microsoft-surface/

---
## Steps to make this all work
1. Download binaries from GitHub.
2. Perform the steps as described in the prereqs-section!
3. Make sure that your PixelSense is running in *User Mode*, otherwise touch input will not be recognised.
4. Run the _Touch Input_ program that is installed as part of the Surface SDK and make sure it is running.
5. Run _PixelSenseToTouch.exe_.

When all is working, the app is currently able to translate finger (only) touches into mouse clicks, mouse holds (hold really long), drag and even pinching/zooming!

----
## Copyright / Licensing
PixelSense2Touch uses the following packages/libraries:
- TCD.System.TouchInjection by Michael (https://www.nuget.org/packages/TCD.System.TouchInjection/)
- InputSimulatorStandard (https://github.com/GregsStack/InputSimulatorStandard)
- Microsoft Surface SDK 1.0 SP1 (https://msdn.microsoft.com/en-us/library/ee804767(v=surface.10).aspx)
