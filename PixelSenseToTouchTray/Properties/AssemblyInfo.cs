using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("PixelSenseToTouch")]
[assembly: AssemblyDescription("Surface 1.0 (PixelSense) to Windows touch bridge - Surface1-Hydra-x64 multi-touch fork.")]
[assembly: AssemblyConfiguration("")]
// Company/Trademark stay as the upstream author's: this is an MIT fork of PixelSense2Touch.
[assembly: AssemblyCompany("Boaz Pat-El")]
[assembly: AssemblyProduct("PixelSenseToTouch")]
[assembly: AssemblyCopyright("Not really any Copyright © 2020")]
[assembly: AssemblyTrademark("Heer Boaz")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("60ee18c8-8474-4dbf-ad3a-4cbf25e9ad80")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
// 2.2.0.0 adds the pluggable output sink (HydraTouch HID digitizer, falling back to touch
// injection). Bumped so the staged binary cannot be confused with the inject-only 2.1.1.0 build.
// 2.3.0.0 adds the IdleMask contact hint (HydraIdleHint -> IOCTL 2108 on the HydraX64Beta
// driver). Bumped past the staged sink-only 2.2.0.0 for the same reason.
// 2.3.1.0 makes injection the default sink again (2.2/2.3 preferred the digitizer when it was
// installed); the HID sink is opt-in via PIXELSENSETOUCH_SINK=hid until its flashing is understood.
[assembly: AssemblyVersion("2.3.1.0")]
[assembly: AssemblyFileVersion("2.3.1.0")]
