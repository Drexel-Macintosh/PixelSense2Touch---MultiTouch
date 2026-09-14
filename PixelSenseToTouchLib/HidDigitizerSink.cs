using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TCD.System.TouchInjection;

namespace PixelSenseToTouchLib
{
    // The UAC-capable output path: hand contacts to the HydraTouch virtual HID digitizer.
    //
    // HydraTouch.sys (a KMDF/VHF driver shipped with the Surface1-Hydra-x64 project) presents a real
    // HID touch-screen digitizer to Windows. Reports written here enter the input stack in the
    // kernel, so they are indistinguishable from a physical touchscreen's - which is precisely why
    // they reach the places injected input cannot: elevated windows, the lock screen, and the UAC
    // consent prompt on the secure desktop.
    //
    // The frame handed to Send is the same one the injection sink receives. Only the wire format
    // differs: screen pixels are rescaled to the digitizer's 0..4095 logical range, and each
    // contact's DOWN/UPDATE/UP state collapses to a single tip-down bit, which is all HID carries.
    // The driver derives its HID contact count from those bits.
    public sealed class HidDigitizerSink : ITouchSink
    {
        public const string DevicePath = @"\\.\HydraTouch";

        // Contract from the driver's Public.h. The buffer is a fixed METHOD_BUFFERED size regardless
        // of how many contacts are live; only Count and the first Count slots are read.
        private const uint IoctlSubmit = 0xCF55A000u;          // CTL_CODE(0xCF55, 0x800, BUFFERED, WRITE)
        private const int MaxDigitizerContacts = 52;           // the descriptor's declared maximum
        private const int SubmitSize = 4 + MaxDigitizerContacts * 16;
        private const uint FlagDown = 0x00000001u;
        private const uint LogicalMax = 4095u;

        // POINTER_FLAG_UP. Read numerically rather than through the wrapper's enum so this does not
        // depend on how TCD.System.TouchInjection happens to declare its flag values.
        private const uint PointerFlagUp = 0x00040000u;

        private const uint GenericWrite = 0x40000000u;
        private const uint ShareReadWrite = 0x00000003u;
        private const uint OpenExisting = 3u;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        // The projector runs at a fixed resolution, but re-reading it occasionally costs nothing and
        // means a display change cannot silently leave every contact mapped to the wrong place.
        private const int ScreenRecheckMs = 2000;

        private readonly byte[] buffer = new byte[SubmitSize];
        private IntPtr handle = InvalidHandle;
        private int screenWidth = 1, screenHeight = 1;
        private int screenCheckedAt;

        // Win32 error from a failed Start, so the caller can say *why* rather than just "unavailable".
        public int LastError { get; private set; }

        public string Name { get { return "HydraTouch HID digitizer"; } }

        public bool ReachesElevated { get { return true; } }

        public bool Start(int maxContacts)
        {
            RefreshScreen();
            this.handle = CreateFile(DevicePath, GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (this.handle == InvalidHandle)
            {
                // 2 = not installed, 5 = access denied (not elevated). Both are actionable.
                this.LastError = Marshal.GetLastWin32Error();
                return false;
            }
            this.LastError = 0;
            return true;
        }

        public void Send(int count, PointerTouchInfo[] contacts)
        {
            if (this.handle == InvalidHandle) return;

            if (unchecked(Environment.TickCount - this.screenCheckedAt) >= ScreenRecheckMs) RefreshScreen();

            int n = 0;
            for (int i = 0; i < count && n < MaxDigitizerContacts; i++)
            {
                var p = contacts[i].PointerInfo;
                bool down = ((uint)p.PointerFlags & PointerFlagUp) == 0;

                int o = 4 + n * 16;
                WriteUInt32(this.buffer, o, p.PointerId);
                WriteUInt32(this.buffer, o + 4, Scale(p.PtPixelLocation.X, this.screenWidth));
                WriteUInt32(this.buffer, o + 8, Scale(p.PtPixelLocation.Y, this.screenHeight));
                WriteUInt32(this.buffer, o + 12, down ? FlagDown : 0u);
                n++;
            }
            WriteUInt32(this.buffer, 0, (uint)n);

            Submit();
        }

        public void Dispose()
        {
            if (this.handle == InvalidHandle) return;
            try
            {
                // Count = 0 means "all fingers up". The bridge lifts its pointers before disposing,
                // but a contact left down in the driver would outlive this process and stick, so
                // release unconditionally.
                WriteUInt32(this.buffer, 0, 0u);
                Submit();
            }
            catch { }
            finally
            {
                CloseHandle(this.handle);
                this.handle = InvalidHandle;
            }
        }

        private void Submit()
        {
            uint returned;
            DeviceIoControl(this.handle, IoctlSubmit, this.buffer, SubmitSize, IntPtr.Zero, 0u, out returned, IntPtr.Zero);
        }

        private void RefreshScreen()
        {
            try
            {
                var b = Screen.PrimaryScreen.Bounds;
                if (b.Width > 0 && b.Height > 0) { this.screenWidth = b.Width; this.screenHeight = b.Height; }
            }
            catch { }   // keep the last known bounds; a failed probe must never stop touch
            this.screenCheckedAt = Environment.TickCount;
        }

        private static uint Scale(int value, int extent)
        {
            if (extent <= 1) return 0;
            if (value < 0) value = 0;
            else if (value > extent - 1) value = extent - 1;
            return (uint)((long)value * LogicalMax / (extent - 1));
        }

        // Written by hand rather than via BitConverter.GetBytes, which allocates a temporary array
        // per field - this runs for every contact of every frame.
        private static void WriteUInt32(byte[] b, int offset, uint value)
        {
            b[offset] = (byte)value;
            b[offset + 1] = (byte)(value >> 8);
            b[offset + 2] = (byte)(value >> 16);
            b[offset + 3] = (byte)(value >> 24);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
                                                uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr handle, uint code, byte[] inBuffer, int inSize,
                                                   IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
