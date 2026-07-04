# Latest version binary download
https://github.com/Heer-Boaz/PixelSense2Touch/releases/tag/v2.0

# PixelSense2Touch
App that adds touch support to Microsoft PixelSense 1.0 (Microsoft Surface) that runs on Windows 8+.

## Update - Robust multi-touch
Reworked how contacts reach Windows so that **simultaneous multi-touch is reliable**.
Every Surface contact is now injected as a real Windows touch pointer, and the full set
of live pointers is re-injected on every contact event. Because genuine touch pointers
reach the OS, **Windows performs gesture recognition itself** — tap, drag, pinch/zoom,
and press-and-hold right-click all come "for free" from the touch stream.

This fixes three defects in the previous multi-touch handling:
- **Stuck/ghost pointers on release** — a lifted finger was removed from tracking
  *before* its `UP` was injected, so Windows never saw the release and kept the pointer
  down. The `UP` is now injected while the contact is still part of the frame.
- **Cross-thread corruption** — Surface events fire on background threads but shared
  pointer state was mutated without a lock, so two fingers moving at once overwrote each
  other. All shared state is now under a single lock.
- **Pointer-id collisions** — ids were `contact.Id % 20`, so contacts with ids congruent
  mod 20 merged into one pointer. Ids now come from a collision-free allocator.

**Note on right-click:** right-click is now Windows' native *press-and-hold* gesture,
which replaces the previous mouse-emulation approach. Enable it under
**Control Panel → Pen and Touch → Press and hold → "Enable press and hold for
right-clicking"** (on by default). As a result the **InputSimulatorStandard** dependency
is no longer needed and has been removed.

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
