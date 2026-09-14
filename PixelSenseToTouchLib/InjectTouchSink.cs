using TCD.System.TouchInjection;

namespace PixelSenseToTouchLib
{
    // The original output path: Windows touch injection.
    //
    // Behaviour here is exactly what this app has always done, kept intact as the fallback for a
    // table without the HydraTouch digitizer installed. Its one hard limit is not fixable from
    // inside this class: injected input is blocked by UIPI from reaching any higher-integrity
    // window, and never reaches the UAC secure desktop. See ITouchSink.
    public sealed class InjectTouchSink : ITouchSink
    {
        public string Name { get { return "Windows touch injection"; } }

        public bool ReachesElevated { get { return false; } }

        public bool Start(int maxContacts)
        {
            // The cast is required: the wrapper takes a uint, and only a *constant* int converts
            // implicitly. This used to be called with a const field, so it compiled without one.
            return TouchInjector.InitializeTouchInjection((uint)maxContacts, TouchFeedback.DEFAULT);
        }

        public void Send(int count, PointerTouchInfo[] contacts)
        {
            TouchInjector.InjectTouchInput(count, contacts);
        }

        public void Dispose()
        {
            // Nothing to release: touch injection is per-process state with no handle of its own.
            // The bridge has already lifted every pointer before this runs.
        }
    }
}
