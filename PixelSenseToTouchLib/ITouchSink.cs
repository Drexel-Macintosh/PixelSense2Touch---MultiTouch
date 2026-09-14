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
        // Prefer the HID digitizer when it is installed, fall back to injection. This is the
        // default because the digitizer is strictly better where they overlap and the only one
        // that works where they don't (elevated windows, lock screen, UAC).
        Auto,
        Hid,
        Inject
    }

    public static class TouchSink
    {
        // Optional override, for falling back without reinstalling or for comparing the two paths:
        //   set PIXELSENSETOUCH_SINK=inject
        public const string ModeVariable = "PIXELSENSETOUCH_SINK";

        public static TouchSinkMode ModeFromEnvironment()
        {
            string v;
            try { v = Environment.GetEnvironmentVariable(ModeVariable); }
            catch { return TouchSinkMode.Auto; }

            if (string.IsNullOrEmpty(v)) return TouchSinkMode.Auto;
            v = v.Trim();
            if (v.Equals("hid", StringComparison.OrdinalIgnoreCase)) return TouchSinkMode.Hid;
            if (v.Equals("inject", StringComparison.OrdinalIgnoreCase)) return TouchSinkMode.Inject;
            return TouchSinkMode.Auto;
        }

        // Returns a started sink, or null if none could be started. detail always explains the
        // outcome - including why a preferred sink was passed over - so a silent downgrade from
        // "works on UAC" to "does not" can never happen unnoticed.
        public static ITouchSink Select(TouchSinkMode mode, int maxContacts, out string detail)
        {
            if (mode != TouchSinkMode.Inject)
            {
                var hid = new HidDigitizerSink();
                if (hid.Start(maxContacts))
                {
                    detail = "using the " + hid.Name + " - touch reaches elevated windows and UAC.";
                    return hid;
                }

                string why = hid.LastError == 2
                    ? "the HydraTouch digitizer is not installed"
                    : "could not open " + HidDigitizerSink.DevicePath + " (error " + hid.LastError +
                      (hid.LastError == 5 ? " - access denied; run elevated" : "") + ")";
                hid.Dispose();

                if (mode == TouchSinkMode.Hid) { detail = "HID sink requested but " + why + "."; return null; }

                var fallback = new InjectTouchSink();
                if (fallback.Start(maxContacts))
                {
                    detail = "using " + fallback.Name + " (" + why +
                             "). Touch will NOT work on elevated windows or UAC prompts.";
                    return fallback;
                }
                fallback.Dispose();
                detail = "no touch sink available: " + why + ", and touch injection failed to start.";
                return null;
            }

            var inject = new InjectTouchSink();
            if (inject.Start(maxContacts))
            {
                detail = "using " + inject.Name +
                         " (forced). Touch will NOT work on elevated windows or UAC prompts.";
                return inject;
            }
            inject.Dispose();
            detail = "touch injection failed to start.";
            return null;
        }
    }
}
