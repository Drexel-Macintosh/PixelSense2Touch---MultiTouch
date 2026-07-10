# ⬇️ Download the ready-to-run build (Surface1-Hydra-x64)

**[HydraTouch-PixelSenseToTouch-x64.zip](https://github.com/Drexel-Macintosh/PixelSense2Touch---MultiTouch/releases/latest/download/HydraTouch-PixelSenseToTouch-x64.zip)** — the working, validated build from this fork. Unzip, read the included `README.txt`, run it over Surface Input. (Everything it needs is in the zip.)

> This fork's improvements are also proposed **upstream** as a pull request to [Heer-Boaz/PixelSense2Touch](https://github.com/Heer-Boaz/PixelSense2Touch). This download exists so the working build is available even if upstream hasn't merged or released it yet.

---

# PixelSense2Touch — Surface1-Hydra-x64 fork

## What this fork changes (vs. upstream v2.0)

- **Robust simultaneous multi-touch.** Every Surface contact is injected as a real Windows touch pointer and the full live set is re-injected each event, so Windows does its own gestures (tap → click, press-and-hold → right-click, drag, pinch/zoom). This fixes three defects that made the previous build effectively single-touch: a stuck/ghost pointer on finger-up, cross-thread corruption of the shared pointer, and pointer-id collisions (`id % 20`). See the upstream PR for the full write-up.
- **Leaner runtime.** The touch frame is built into a reusable buffer (no per-event allocation / GC churn), and an unchanged frame is not re-sent (a held-still finger no longer re-fires an identical inject). Same behavior, lower CPU — measurable on the original Core2Duo table.
- **Built x86** to match the 32-bit Surface runtime it talks to.
- **Dropped the unused `InputSimulatorStandard` dependency** — real touch pointers make Windows generate gestures natively, so the old mouse-emulation path is gone.
- **"Hydra Touch" branding** (tray/app icon + name) for the Surface1-Hydra-x64 distribution. Icon/name only — touch behavior is identical.

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
