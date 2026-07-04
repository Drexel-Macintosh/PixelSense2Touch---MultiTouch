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
        private const int NumberOfSimultaniousTouches = 20;

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
            bool s = TouchInjector.InitializeTouchInjection(NumberOfSimultaniousTouches, TouchFeedback.DEFAULT);
            if (s) Debug.WriteLine($"[OK]");
            else
            {
                Debug.WriteLine($"[FAILED]");
                return;
            }

            // Setup event handlers
            this.InitEventHandlers();
        }

        public void InitEventHandlers()
        {
            Debug.Write($"{DateTime.Now}: Setting up event handlers... ");
            this.ContactTarget.ContactAdded += this.HandleAdd;
            this.ContactTarget.ContactChanged += this.HandleChange;
            this.ContactTarget.ContactRemoved += this.HandleRemove;
            Debug.WriteLine($"[OK]");
        }

        public void RemoveEventHandlers()
        {
            Debug.Write($"{DateTime.Now}: Removing event handlers... ");
            this.ContactTarget.ContactAdded -= this.HandleAdd;
            this.ContactTarget.ContactChanged -= this.HandleChange;
            this.ContactTarget.ContactRemoved -= this.HandleRemove;
            Debug.WriteLine($"[OK]");
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
            if (this.liveContacts.Count == 0) return;

            var frame = new PointerTouchInfo[this.liveContacts.Count];
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
                frame[i++] = info;
            }

            TouchInjector.InjectTouchInput(frame.Length, frame);
        }

        public void CleanUp()
        {
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
