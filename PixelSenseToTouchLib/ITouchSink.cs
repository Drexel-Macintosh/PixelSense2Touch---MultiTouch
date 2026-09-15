using System;
using TCD.System.TouchInjection;

namespace PixelSenseToTouchLib
{
    // Where a finished touch frame is handed to Windows.
    //
    // The bridge builds the same frame either way - the full set of live contacts, each with its
    // pointer id, screen position and DOWN/UPDATE/UP state - and a sink is only the last step that
    // delivers it. The split exists for exactly one reason: InjectTouchInput cannot reach UAC.
    //
    // InjectTouchInput produces *user-mode injected* input. UIPI refuses to deliver it to any window
    // running at a higher integrity level, and the UAC consent prompt additionally lives on the
    // Winlogon "secure desktop", which injected input does not reach at all. That is an operating
    // system security boundary, not something this app can tune away - which is why touch on a UAC
    // prompt was impossible for as long as injection was the only output path.
    //
    // The HID sink sidesteps it by not injecting: it hands the same contacts to the HydraTouch
    // virtual HID digitizer, whose reports enter the input stack in the kernel, below UIPI and below
    // desktop isolation, exactly like a physical touchscreen. Same frames, same pixels - but they
    // land on elevated windows, the lock screen and the UAC prompt.
    //
    // STATUS (2026-09-14): the HID sink is OPT-IN. Measured on the table, a finger held still
    // through it FLASHES - the runtime's own contact list flickers Added/Removed - while the same
    // build through injection holds solid (the camera driver was ruled out with identical masks).
    // Until that is understood, injection is the default and the digitizer is used only when
    // PIXELSENSETOUCH_SINK=hid asks for it. The Surface1-Hydra-x64 "Everywhere" mode does not need
    // this sink for UAC / the lock screen: its session-0 service feeds the digitizer directly.
    public interface ITouchSink : IDisposable
    {
        // Shown in the tray status, so whichever path is live is never a guess.
        string Name { get; }

        // True if input from this sink crosses UIPI and the secure desktop.
        bool ReachesElevated { get; }

        // Acquire the sink. False means unavailable (not installed, no permission); the caller
        // falls back or reports "unavailable" rather than pretending to run.
        bool Start(int maxContacts);

        // Deliver the complete current contact set. Windows requires every frame to carry all
        // contacts currently down, so this is always the whole set, never a delta.
        void Send(int count, PointerTouchInfo[] contacts);
    }

    public enum TouchSinkMode
    {
        // Defer to PIXELSENSETOUCH_SINK; without it, injection - the path this app has always
        // used and the one validated on the table. (2.2.0.0 preferred the digitizer here; see the
        // STATUS note above for why that was reversed.)
        Auto,
        Hid,
        Inject
    }

    public static class TouchSink
    {
        // Opt into the HID digitizer, or state injection explicitly:
        //   set PIXELSENSETOUCH_SINK=hid
        //   set PIXELSENSETOUCH_SINK=inject
        public const string ModeVariable = "PIXELSENSETOUCH_SINK";

        public static TouchSinkMode ModeFromEnvironment()
        {
            string v;
            try { v = Environment.GetEnvironmentVariable(ModeVariable); }
            catch { return TouchSinkMode.Inject; }

            if (string.IsNullOrEmpty(v)) return TouchSinkMode.Inject;
            v = v.Trim();
            if (v.Equals("hid", StringComparison.OrdinalIgnoreCase)) return TouchSinkMode.Hid;
            if (v.Equals("inject", StringComparison.OrdinalIgnoreCase)) return TouchSinkMode.Inject;
            return TouchSinkMode.Inject;
        }

        // Returns a started sink, or null if none could be started. detail always explains the
        // outcome - including why a requested sink was passed over - so the difference between
        // "works on elevated windows" and "does not" can never go unnoticed.
        public static ITouchSink Select(TouchSinkMode mode, int maxContacts, out string detail)
        {
            if (mode == TouchSinkMode.Auto) mode = ModeFromEnvironment();

            if (mode == TouchSinkMode.Hid)
            {
                var hid = new HidDigitizerSink();
                if (hid.Start(maxContacts))
                {
                    detail = "using the " + hid.Name + " (" + ModeVariable + "=hid) - touch reaches elevated windows and UAC. " +
                             "NOTE: this path is still under investigation (held contacts can flash).";
                    return hid;
                }

                string why = hid.LastError == 2
                    ? "the HydraTouch digitizer is not installed"
                    : "could not open " + HidDigitizerSink.DevicePath + " (error " + hid.LastError +
                      (hid.LastError == 5 ? " - access denied; run elevated" : "") + ")";
                hid.Dispose();

                var fallback = new InjectTouchSink();
                if (fallback.Start(maxContacts))
                {
                    detail = "using " + fallback.Name + " - " + ModeVariable + "=hid was requested but " + why +
                             ". Touch will NOT work on elevated windows or UAC prompts.";
                    return fallback;
                }
                fallback.Dispose();
                detail = "no touch sink available: " + why + ", and touch injection failed to start.";
                return null;
            }

            var inject = new InjectTouchSink();
            if (inject.Start(maxContacts))
            {
                detail = "using " + inject.Name + " (the default; set " + ModeVariable + "=hid to try the HydraTouch " +
                         "digitizer). Touch will NOT work on elevated windows or UAC prompts.";
                return inject;
            }
            inject.Dispose();
            detail = "touch injection failed to start.";
            return null;
        }
    }
}
