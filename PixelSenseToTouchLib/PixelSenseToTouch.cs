using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;
using Microsoft.Surface.Core;
using TCD.System.TouchInjection;

namespace PixelSenseToTouchLib
{
    // Touch bridge from the Surface 1.0 (PixelSense) vision system to native Windows
    // touch input.
    //
    // Every Surface contact is injected as a real Windows touch pointer, and the full
    // set of live pointers is re-injected on every contact event. Because genuine
    // pointers reach Windows, the OS performs gesture recognition itself (tap = click,
    // press-and-hold = right-click, drag, pinch/zoom), so no mouse emulation is needed.
    //
    // This replaces the earlier model that injected touch only for multi-finger cases
    // and emulated single-finger clicks via the Surface tap/hold gesture events + a
    // mouse simulator. That model had three defects that made real multi-touch
    // unreliable:
    //   1. On finger-up, the contact was removed from the tracking dictionary *before*
    //      InjectTouchInput ran, so the UP was never delivered and Windows kept the
    //      pointer down (ghost/stuck touches).
    //   2. Surface events fire on background threads but a single shared PointerTouchInfo
    //      struct was mutated without a lock, so concurrent finger events corrupted each
    //      other's coordinates.
    //   3. Pointer ids were contact.Id % 20, so two contacts whose ids are congruent
    //      mod 20 collapsed onto one pointer.
    public class PixelSenseToTouch : IDisposable
    {
        public ContactTarget ContactTarget { get; set; }
        // Match the Surface/PixelSense hardware maximum (also the HydraTouch digitizer's declared max),
        // so Windows reports the true contact ceiling instead of an arbitrary lower cap.
        private const int NumberOfSimultaniousTouches = 52;

        // True while the contact handlers are attached and injecting. Always read/written under
        // touchLock; the handlers test it so a callback already in flight when we detach cannot
        // resurrect a contact after LiftAll has released everything.
        public bool IsRunning { get; private set; }

        // InitializeTouchInjection succeeded. Without it InjectTouchInput can never work, so
        // attaching the handlers would only pretend to be running.
        private bool injectorReady;

        private NativeWindow window;
#if DEBUG
        public StringBuilder debuginfo;
#endif

        // Surface contact.Id -> the Windows touch pointer we injected for it.
        private readonly Dictionary<int, LiveContact> liveContacts = new Dictionary<int, LiveContact>();
        // Pool of free HID pointer ids in [0, NumberOfSimultaniousTouches), so each
        // live finger gets a stable, collision-free id for its whole lifetime.
        private readonly Stack<uint> freePointerIds = new Stack<uint>();
        private readonly object touchLock = new object();

        // Reusable inject buffer + a snapshot of the last frame actually sent: InjectFrame
        // allocates nothing per contact event (no GC churn under active touch), and skips the
        // InjectTouchInput syscall when the frame is unchanged (a held/still finger).
        private readonly PointerTouchInfo[] injectBuffer = new PointerTouchInfo[NumberOfSimultaniousTouches];
        private readonly PointerTouchInfo[] lastSent = new PointerTouchInfo[NumberOfSimultaniousTouches];
        private int lastSentCount = -1;

        private sealed class LiveContact
        {
            public uint PointerId;
            public PointerTouchInfo Info;
            public bool DownInjected;
        }

        public void Init()
        {
            for (int i = NumberOfSimultaniousTouches - 1; i >= 0; i--)
                this.freePointerIds.Push((uint)i);
#if DEBUG
            this.debuginfo = new StringBuilder();
#endif

            Debug.Write($"{DateTime.Now}: Create native window with handle... ");
            this.window = new NativeWindow();
            this.window.CreateHandle(new CreateParams());
            Debug.WriteLine($"[OK]");

            // Create a target for surface input
            Debug.Write($"{DateTime.Now}: Create contact target... ");
            this.ContactTarget = new ContactTarget(IntPtr.Zero, EventThreadChoice.OnBackgroundThread);
            this.ContactTarget.EnableInput();
            Debug.WriteLine($"[OK]");

            // Initialize the TouchInjector
            Debug.Write($"{DateTime.Now}: Init touch injector... ");
            this.injectorReady = TouchInjector.InitializeTouchInjection(NumberOfSimultaniousTouches, TouchFeedback.DEFAULT);
            if (this.injectorReady) Debug.WriteLine($"[OK]");
            else
            {
                Debug.WriteLine($"[FAILED]");
                return;
            }

            // Setup event handlers
            this.InitEventHandlers();
        }

        // Attach the contact handlers and begin injecting. Idempotent: the tray reconciles state on a
        // timer, so this is called repeatedly and must no-op when already running.
        public void InitEventHandlers()
        {
            if (!this.injectorReady || this.ContactTarget == null) return;

            lock (this.touchLock)
            {
                if (this.IsRunning) return;
                this.IsRunning = true;
            }

            Debug.Write($"{DateTime.Now}: Setting up event handlers... ");
            this.ContactTarget.ContactAdded += this.HandleAdd;
            this.ContactTarget.ContactChanged += this.HandleChange;
            this.ContactTarget.ContactRemoved += this.HandleRemove;
            Debug.WriteLine($"[OK]");
        }

        // Detach the contact handlers and stop injecting. Idempotent, and safe to call with fingers
        // on the glass: clearing IsRunning first shuts the handlers up, then LiftAll releases whatever
        // was still down. Unsubscribing alone would strand those pointers DOWN in Windows forever.
        public void RemoveEventHandlers()
        {
            lock (this.touchLock)
            {
                if (!this.IsRunning) return;
                this.IsRunning = false;
            }

            Debug.Write($"{DateTime.Now}: Removing event handlers... ");
            this.ContactTarget.ContactAdded -= this.HandleAdd;
            this.ContactTarget.ContactChanged -= this.HandleChange;
            this.ContactTarget.ContactRemoved -= this.HandleRemove;
            Debug.WriteLine($"[OK]");

            this.LiftAll();
        }

        // Release every pointer we currently hold down, in one frame, and reclaim its id.
        // Only contacts whose DOWN we actually injected are included - Windows must never see an UP
        // for a pointer that was never down.
        public void LiftAll()
        {
            lock (this.touchLock)
            {
                if (this.liveContacts.Count == 0) return;

                int count = 0;
                foreach (var kv in this.liveContacts)
                {
                    var lc = kv.Value;
                    this.freePointerIds.Push(lc.PointerId);
                    if (!lc.DownInjected) continue;

                    var info = lc.Info;
                    info.PointerInfo.PointerFlags = PointerFlags.UP;
                    this.injectBuffer[count++] = info;
                }
                this.liveContacts.Clear();
                this.lastSentCount = -1;   // next frame must inject; nothing is down now

                if (count > 0)
                {
                    Debug.WriteLine($"{DateTime.Now}: LiftAll - releasing {count} pointer(s).");
                    TouchInjector.InjectTouchInput(count, this.injectBuffer);
                }
            }
        }

        private static PointerTouchInfo CreatePointer(uint pointerId, Contact contact)
        {
            var info = new PointerTouchInfo();
            info.PointerInfo.pointerType = PointerInputType.TOUCH;
            info.PointerInfo.PointerId = pointerId;
            info.TouchMasks = TouchMask.CONTACTAREA | TouchMask.ORIENTATION | TouchMask.PRESSURE;
            info.Orientation = 90;
            info.Pressure = 32000;
            FillGeometry(ref info, contact);
            return info;
        }

        private static void FillGeometry(ref PointerTouchInfo info, Contact contact)
        {
            info.PointerInfo.PtPixelLocation.X = (int)contact.X;
            info.PointerInfo.PtPixelLocation.Y = (int)contact.Y;
            info.ContactArea.left = (int)contact.Bounds.Left;
            info.ContactArea.right = (int)contact.Bounds.Right;
            info.ContactArea.top = (int)contact.Bounds.Top;
            info.ContactArea.bottom = (int)contact.Bounds.Bottom;
        }

        private void HandleAdd(object sender, ContactEventArgs e)
        {
            var contact = e.Contact;
            if (!contact.IsFingerRecognized) return;

            lock (this.touchLock)
            {
                if (!this.IsRunning) return;   // detached (or suspended) while this callback was in flight
                if (this.liveContacts.ContainsKey(contact.Id) || this.freePointerIds.Count == 0) return;

                var pointerId = this.freePointerIds.Pop();
                this.liveContacts[contact.Id] = new LiveContact
                {
                    PointerId = pointerId,
                    Info = CreatePointer(pointerId, contact),
                    DownInjected = false
                };
#if DEBUG
                this.debuginfo.Append($"New! {pointerId}, {contact.CenterX},{contact.CenterY}\n");
#endif
                this.InjectFrame();
            }
        }

        private void HandleChange(object sender, ContactEventArgs e)
        {
            var contact = e.Contact;
            if (!contact.IsFingerRecognized) return;

            lock (this.touchLock)
            {
                if (!this.IsRunning) return;   // detached (or suspended) while this callback was in flight

                LiveContact lc;
                if (!this.liveContacts.TryGetValue(contact.Id, out lc))
                {
                    // A change before we saw the add: register it now so the pointer
                    // still goes down.
                    if (this.freePointerIds.Count == 0) return;
                    var pointerId = this.freePointerIds.Pop();
                    this.liveContacts[contact.Id] = new LiveContact
                    {
                        PointerId = pointerId,
                        Info = CreatePointer(pointerId, contact),
                        DownInjected = false
                    };
                }
                else
                {
                    var info = lc.Info;
                    FillGeometry(ref info, contact);
                    lc.Info = info;
                }
                this.InjectFrame();
            }
        }

        private void HandleRemove(object sender, ContactEventArgs e)
        {
            var contact = e.Contact;

            lock (this.touchLock)
            {
                if (!this.IsRunning) return;   // detached: LiftAll has already released everything

                LiveContact lc;
                // Release whatever we actually injected for this contact, regardless of
                // its current IsFingerRecognized state (it can flip on the way up).
                if (!this.liveContacts.TryGetValue(contact.Id, out lc)) return;

                // Inject the UP while the contact is still part of the frame, then retire
                // it and reclaim its pointer id.
                this.InjectFrame(contact.Id);
                this.liveContacts.Remove(contact.Id);
                this.freePointerIds.Push(lc.PointerId);
#if DEBUG
                this.debuginfo.Append($"Weg! {lc.PointerId}, {contact.CenterX},{contact.CenterY}\n");
#endif
            }
        }

        // Injects the complete set of live contacts in a single call. Caller holds
        // touchLock. Windows requires every InjectTouchInput call to carry all contacts
        // currently down: a finger held still is re-sent as UPDATE so it survives while
        // another finger moves, and the contact identified by removingId (if any) is
        // flagged UP.
        private void InjectFrame(int removingId = -1)
        {
            int count = this.liveContacts.Count;
            if (count == 0)
            {
                this.lastSentCount = -1;   // all fingers up: force the next frame to inject
                return;
            }

            int i = 0;
            foreach (var kv in this.liveContacts)
            {
                var lc = kv.Value;
                var info = lc.Info;

                if (kv.Key == removingId)
                {
                    info.PointerInfo.PointerFlags = PointerFlags.UP;
                }
                else if (!lc.DownInjected)
                {
                    info.PointerInfo.PointerFlags = PointerFlags.DOWN | PointerFlags.INRANGE | PointerFlags.INCONTACT;
                    lc.DownInjected = true;
                }
                else
                {
                    info.PointerInfo.PointerFlags = PointerFlags.UPDATE | PointerFlags.INRANGE | PointerFlags.INCONTACT;
                }

                lc.Info = info;
                this.injectBuffer[i++] = info;   // reuse the buffer; no per-event allocation
            }

            // Skip the InjectTouchInput syscall when this frame is identical to the one we last
            // sent. A held/still finger makes SurfaceInput re-fire ContactChanged with unchanged
            // geometry, and re-injecting an identical frame is pure overhead. A real DOWN/UP
            // always changes the pointer set or a flag, so it is never skipped.
            if (this.FrameEqualsLast(count)) return;

            TouchInjector.InjectTouchInput(count, this.injectBuffer);

            Array.Copy(this.injectBuffer, this.lastSent, count);
            this.lastSentCount = count;
        }

        // True if injectBuffer[0..count) matches the last frame actually injected. Exact
        // element-wise compare of the geometry + flags: the dictionary's enumeration order is
        // stable between two consecutive frames with no add/remove, so equal content compares
        // equal; if an add/remove reordered it, we simply do not skip (a harmless extra inject).
        private bool FrameEqualsLast(int count)
        {
            if (this.lastSentCount != count) return false;
            for (int i = 0; i < count; i++)
            {
                var a = this.injectBuffer[i];
                var b = this.lastSent[i];
                if (a.PointerInfo.PointerId != b.PointerInfo.PointerId ||
                    a.PointerInfo.PointerFlags != b.PointerInfo.PointerFlags ||
                    a.PointerInfo.PtPixelLocation.X != b.PointerInfo.PtPixelLocation.X ||
                    a.PointerInfo.PtPixelLocation.Y != b.PointerInfo.PtPixelLocation.Y ||
                    a.ContactArea.left != b.ContactArea.left ||
                    a.ContactArea.right != b.ContactArea.right ||
                    a.ContactArea.top != b.ContactArea.top ||
                    a.ContactArea.bottom != b.ContactArea.bottom)
                {
                    return false;
                }
            }
            return true;
        }

        public void CleanUp()
        {
            // Detach + release before tearing anything down: exiting with a finger on the glass would
            // otherwise leave that pointer stuck DOWN in Windows after the process is gone.
            this.RemoveEventHandlers();

            Debug.Write($"{DateTime.Now}: Start disposing resources... ");
            this.ContactTarget?.Dispose();
            Debug.Write($"ContactTarget;");

            this.window?.DestroyHandle();
            Debug.Write($"Window;");
        }

        void IDisposable.Dispose()
        {
            this.CleanUp();
        }
    }
}
