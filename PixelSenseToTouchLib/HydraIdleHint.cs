using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Surface.Core;

namespace PixelSenseToTouchLib
{
    // Occupancy hint for the HydraX64Beta IdleMask gate (IOCTL 2108 IOCTL_HYDRA_IDLE_CONTACT_HINT).
    //
    // The camera driver's IdleMask gate saves most of SurfaceInput's idle CPU by handing the
    // runtime an all-zero changed-cells mask when the table is empty. The runtime drops every
    // tracked contact the moment it receives such a mask, so the gate may only ever do that when
    // NOTHING is on the glass - and the one authoritative occupancy signal is the runtime's own
    // contact list, which this app already receives through Microsoft.Surface.Core. So we keep a
    // running count of the contacts the runtime is tracking (ALL types: fingers, blobs, tags) and
    // push it to the driver: on every change and every 500 ms as a heartbeat. The driver treats a
    // missing or stale hint (older than IdleHintTimeoutMs, default 1.5 s) as "not empty" and keeps
    // delivering full masks, which is today's behaviour - so nothing this class does can make
    // touch worse. It can only enable the idle saving.
    //
    // Never claim 0 without evidence. A wrong ">0" costs the idle saving for a while; a wrong "0"
    // costs a real contact (the runtime drops it on the first all-zero mask), so every rule here
    // errs towards ">0":
    //   1. Attach() subscribes BEFORE ContactTarget.EnableInput() and counts ContactChanged as well
    //      as ContactAdded/Removed. A contact SurfaceInput was already tracking when this process
    //      connected (a tag left on the table, a finger held through a restart) reaches a new client
    //      as Changed only, never as Added - the same case PixelSenseToTouch.HandleChange handles
    //      for injection - so a Changed for an id not in the set counts as an add.
    //   2. No hint carries a real count until the target has delivered its first frame (or first
    //      contact event): at that moment the set is reconciled with ContactTarget.GetState() and
    //      the resulting count is the first hint. Until then the only thing sent is one 2108-support
    //      probe per open with Contacts=1 and HYDRA_IDLE_HINT_FLAG_PROBE ("occupied until proven"):
    //      the driver treats any Contacts>0 as not-empty, so a probe can never enable the gate; it
    //      only makes Status right before Init returns. A probe that goes stale is NOHINT = today.
    //   3. On the way out (Dispose, i.e. Stop/Exit/logoff) one goodbye hint goes out - Contacts =
    //      0xFFFFFFFF with HYDRA_IDLE_HINT_FLAG_GOODBYE - so a driver that knows the flag marks the
    //      stream stale at once instead of after IdleHintTimeoutMs; an older driver sees "not empty"
    //      until the timeout. A kill cannot send it; the timeout covers that as before.
    //
    // The heartbeat Timer exists from Attach() on, so a count change that lands while the first
    // synchronous send is in flight is pushed immediately instead of waiting for the next beat.
    //
    // The count subscription is attached to the ContactTarget for its whole life and is
    // independent of the injection handlers: the Shell-suspend logic detaches only those, so the
    // driver keeps getting an accurate count while the Surface Shell has injection suspended.
    //
    // Inert unless the IdleMask beta driver is installed and we run elevated: opening \\.\HYDRA
    // for GENERIC_WRITE needs an elevated caller (the device grants only read to Everyone), and
    // the production driver / an older beta answers 2108 with ERROR_INVALID_FUNCTION. Either way
    // this logs once, reports "off" in Status, and does nothing further. A device that is merely
    // absent or restarting (errors 2 / 433 / 1167 - a driver swap under a running app) is retried
    // every 5 s, both for the initial open and after a loss.
    //
    // Contract: src\driver\HydraX64-beta\Public.h (HYDRA_IDLE_CONTACT_HINT, 16 bytes, pack 4).
    public sealed class HydraIdleHint : IDisposable
    {
        public const string DevicePath = @"\\.\HYDRA";

        // Heartbeat period. The driver's default timeout is 1500 ms, so a lost heartbeat or two
        // (the IOCTL queues behind the in-flight capture) still leaves the hint fresh.
        public const int HeartbeatMs = 500;

        // CTL_CODE(FILE_DEVICE_HYDRA, 2108, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS),
        // computed the way Public.h does so the number is never hand-copied: 0xCF54E0F0.
        private const uint FileDeviceHydra = 53076;
        private const uint MethodBuffered = 0;
        private const uint FileReadAccess = 1;
        private const uint FileWriteAccess = 2;
        public static readonly uint IoctlIdleContactHint =
            CtlCode(FileDeviceHydra, 2108, MethodBuffered, FileReadAccess | FileWriteAccess);

        private const uint HintMagic = 0x31544E48;     // HYDRA_IDLE_HINT_MAGIC 'HNT1'
        private const int HintSize = 16;               // { Magic, Contacts, Seq, Flags }

        // HYDRA_IDLE_CONTACT_HINT.Flags bits (Public.h HYDRA_IDLE_HINT_FLAG_*). 1.0.23.0 ignores
        // Flags altogether; both bits are chosen so that a driver that does not read them still
        // does the safe thing on the Contacts value alone.
        public const uint FlagGoodbye = 0x1;           // sender going away: drop the hint now (Contacts = ContactsUnknown)
        public const uint FlagProbe = 0x2;             // 2108-support probe before the first frame (Contacts = 1)
        public const uint ContactsUnknown = 0xFFFFFFFF; // "occupancy unknown" - any driver reads it as not-empty
        private const uint ProbeContacts = 1;          // "occupied until proven": never 0 without evidence

        private const uint GenericRead = 0x80000000u;
        private const uint GenericWrite = 0x40000000u;
        private const uint ShareReadWrite = 0x00000003u;
        private const uint OpenExisting = 3u;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        private const int ErrorInvalidFunction = 1;    // the driver has no 2108 handler
        private const int ErrorFileNotFound = 2;       // device not present / not started
        private const int ErrorAccessDenied = 5;       // not elevated (GENERIC_WRITE refused)
        private const int ErrorInvalidHandle = 6;
        private const int ErrorNotSupported = 50;
        private const int ErrorNoSuchDevice = 433;
        private const int ErrorOperationAborted = 995;
        private const int ErrorDeviceNotConnected = 1167;

        // When the device is absent (at start) or goes away under us (a driver swap restarts it),
        // try the open this many heartbeats apart; and give up on a driver that keeps rejecting the
        // IOCTL for some other reason after this many consecutive failures.
        private const int ReopenEveryBeats = 10;       // 5 s
        private const int MaxConsecutiveFailures = 10;

        // Contact ids the runtime currently tracks. A set rather than a bare counter so a duplicate
        // Added, a per-frame Changed or an unmatched Removed cannot skew the count in the dangerous
        // direction (a count of 0 with a finger down). Touched only under its own lock.
        private readonly HashSet<int> live = new HashSet<int>();
        // The published count: written with Interlocked on the Core thread, read by the sender.
        private int contacts;
        // Claimed (Interlocked) by whichever event arrives first - a contact event or a frame - which
        // then reconciles the set with the target's collection exactly once.
        private int evidenceClaimed;
        // 0 until that reconciled count has been PUBLISHED (Interlocked; set after `contacts`, so
        // the sender can never read it as 1 while `contacts` still holds the pre-frame 0). Before
        // that nothing can vouch for an empty table, so no real count is sent.
        private int frameSeen;

        private readonly object sendLock = new object();
        private readonly byte[] buffer = new byte[HintSize];
        private ContactTarget target;
        private IntPtr handle = InvalidHandle;
        private Timer heartbeat;                       // created in Attach (parked), armed by Open
        private uint seq;
        private int consecutiveFailures;
        private int reopenCountdown;                   // under sendLock
        private bool opened;                           // under sendLock: Open() has run
        private bool probePending;                     // under sendLock: one probe per successful open, until evidence
        private volatile bool stopped;                 // off for good (set under sendLock)
        private volatile bool disposed;
        private volatile string status = "idle hint: off: not started";

        // "idle hint: on (seq N, contacts C)", "idle hint: probing ..." or "idle hint: off: <reason>"
        // - for the tray.
        public string Status { get { return this.status; } }

        public bool IsOn { get { return this.heartbeat != null && this.handle != InvalidHandle && !this.stopped; } }

        // Contacts the runtime is tracking right now, as counted from its events.
        public int Contacts { get { return Interlocked.CompareExchange(ref this.contacts, 0, 0); } }

        // True once the target has delivered a frame or a contact event, i.e. once a count of 0
        // would be evidence rather than a guess.
        public bool FrameSeen { get { return Interlocked.CompareExchange(ref this.frameSeen, 0, 0) != 0; } }

        // Subscribe to the target's contact and frame events. MUST run before the target's
        // EnableInput(): the events only dispatch once input is enabled, and anything the runtime
        // was already tracking arrives in the very first frames as ContactChanged. The subscription
        // lives for the life of the target - nothing but Dispose removes it. Opens nothing yet.
        public void Attach(ContactTarget contactTarget)
        {
            if (contactTarget == null) throw new ArgumentNullException("contactTarget");
            if (this.disposed) throw new ObjectDisposedException("HydraIdleHint");
            if (this.target != null) return;   // idempotent

            // The timer exists before the first event can arrive (parked until Open arms it), so
            // Track()'s immediate push always has something to poke.
            this.heartbeat = new Timer(this.HandleHeartbeat, null, Timeout.Infinite, Timeout.Infinite);

            this.target = contactTarget;
            this.target.ContactAdded += this.HandleContactAdded;
            this.target.ContactChanged += this.HandleContactChanged;
            this.target.ContactRemoved += this.HandleContactRemoved;
            // FrameReceived turns on the runtime's per-frame client event, including empty frames -
            // the only way an EMPTY table can tell us it has been seen. It is detached again after
            // the first frame (AfterFirstEvidence) so the steady state costs nothing extra.
            this.target.FrameReceived += this.HandleFrameReceived;
        }

        // Open the driver, send the 2108-support probe (or the real count, if evidence already
        // arrived) synchronously so Status is right before we return, and start the heartbeat.
        // A permanent failure (not elevated, no 2108) is logged once and leaves this a no-op with
        // Status "off"; an absent device is retried every 5 s from the heartbeat.
        public void Open()
        {
            if (this.target == null) throw new InvalidOperationException("Attach() the ContactTarget before Open().");
            lock (this.sendLock)
            {
                if (this.opened || this.disposed || this.stopped) return;   // idempotent
                this.opened = true;

                int error;
                if (!this.TryOpen(out error))
                {
                    if (IsTransientOpenError(error))
                    {
                        // Device absent / restarting (P2T launched while the driver (re)starts):
                        // retry from the heartbeat instead of giving up for the process lifetime.
                        this.reopenCountdown = ReopenEveryBeats;
                        this.status = "idle hint: off: " + DevicePath + " is not present (error " + error + ") - retrying every " + (ReopenEveryBeats * HeartbeatMs / 1000) + " s";
                        Debug.WriteLine($"{DateTime.Now}: HydraIdleHint: {DevicePath} not present (error {error}); will retry the open every {ReopenEveryBeats * HeartbeatMs} ms.");
                        this.Arm(HeartbeatMs);
                        return;
                    }
                    string why;
                    if (error == ErrorAccessDenied) why = "not elevated - " + DevicePath + " refused GENERIC_WRITE (error 5)";
                    else why = "could not open " + DevicePath + " (error " + error + ")";
                    this.Stop(why + " - no hints will be sent");
                    return;
                }

                // One probe per open, until the first frame: "occupied until proven". It establishes
                // whether this driver has 2108 at all, so Status is right before Init returns.
                this.probePending = true;
                this.SendLocked();
                if (this.stopped) return;

                this.Arm(HeartbeatMs);
                Debug.WriteLine($"{DateTime.Now}: HydraIdleHint: sending IOCTL 2108 (0x{IoctlIdleContactHint:X8}) every {HeartbeatMs} ms and on every contact-count change; real counts start with the runtime's first frame.");
            }
        }

        private void HandleContactAdded(object sender, ContactEventArgs e) { this.Track(e.Contact.Id, true); }
        // A Changed for an id we have not seen is a contact the runtime tracked before we subscribed
        // (or whose Added we lost): count it. For a known id HashSet.Add is a no-op - one lock and
        // one lookup per Changed event, nothing else.
        private void HandleContactChanged(object sender, ContactEventArgs e) { this.Track(e.Contact.Id, true); }
        private void HandleContactRemoved(object sender, ContactEventArgs e) { this.Track(e.Contact.Id, false); }
        // A frame - possibly empty - has been delivered to this target: whatever the set holds now
        // is the runtime's view, so a 0 is evidence from here on.
        private void HandleFrameReceived(object sender, FrameReceivedEventArgs e) { this.OnEvidence(); }

        private void Track(int id, bool added)
        {
            // The first contact event is evidence too: the target's collection already holds this
            // contact (Core updates it before raising the event), so the reconciliation below sees it.
            bool first = Interlocked.Exchange(ref this.evidenceClaimed, 1) == 0;
            int n;
            lock (this.live)
            {
                bool changed = added ? this.live.Add(id) : this.live.Remove(id);
                if (first) this.SeedFromTargetLocked();
                if (!changed && !first) return;   // duplicate add / per-frame Changed / unmatched remove: nothing to report
                n = this.live.Count;
            }
            Interlocked.Exchange(ref this.contacts, n);
            if (first) this.AfterFirstEvidence();   // raises frameSeen - after the count above is visible
            this.Push();
        }

        private void OnEvidence()
        {
            if (Interlocked.Exchange(ref this.evidenceClaimed, 1) != 0) return;
            int n;
            lock (this.live)
            {
                this.SeedFromTargetLocked();
                n = this.live.Count;
            }
            Interlocked.Exchange(ref this.contacts, n);
            this.AfterFirstEvidence();
            this.Push();
        }

        // Caller holds `live`. Union the event-tracked ids with the target's own collection, which
        // Core fills from the same stream (update-first contacts included) - so a contact whose
        // event we missed between Attach and here still counts. Union only: an id we tracked but
        // the collection lacks stays counted (the safe direction) until its Removed arrives.
        private void SeedFromTargetLocked()
        {
            ContactTarget t = this.target;
            if (t == null) return;
            try
            {
                foreach (Contact c in t.GetState()) this.live.Add(c.Id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{DateTime.Now}: HydraIdleHint: GetState() failed at the first frame ({ex.GetType().Name}: {ex.Message}); using the event-tracked set only.");
            }
        }

        // Caller has just published the reconciled count. Only now may the sender treat it as evidence.
        private void AfterFirstEvidence()
        {
            Interlocked.Exchange(ref this.frameSeen, 1);
            Debug.WriteLine($"{DateTime.Now}: HydraIdleHint: first frame from the runtime - {this.Contacts} contact(s) on the glass; real counts from now on.");
            // Detach the per-frame event off the Core dispatch thread: removing the last handler
            // tells the runtime to stop sending empty-frame events, which is an IPC call.
            ThreadPool.QueueUserWorkItem(this.DetachFrameEvents);
        }

        private void DetachFrameEvents(object state)
        {
            ContactTarget t = this.target;
            if (t == null) return;
            try { t.FrameReceived -= this.HandleFrameReceived; }
            catch (Exception ex) { Debug.WriteLine($"{DateTime.Now}: HydraIdleHint: FrameReceived detach failed ({ex.GetType().Name}: {ex.Message}) - harmless."); }
        }

        // Fire the heartbeat now (on a pool thread - the IOCTL queues behind the in-flight
        // capture, and the Core callback must not hold up the injection handlers behind it) and
        // restart the period from here.
        private void Push()
        {
            Timer t = this.heartbeat;
            if (t == null || this.stopped || this.disposed) return;
            try { t.Change(0, HeartbeatMs); }
            catch (ObjectDisposedException) { }
        }

        // Caller holds sendLock.
        private void Arm(int dueMs)
        {
            Timer t = this.heartbeat;
            if (t == null) return;
            try { t.Change(dueMs, HeartbeatMs); }
            catch (ObjectDisposedException) { }
        }

        private void HandleHeartbeat(object state) { this.Send(); }

        private void Send()
        {
            lock (this.sendLock)
            {
                if (this.stopped || this.disposed || !this.opened) return;
                this.SendLocked();
            }
        }

        // Caller holds sendLock. One heartbeat / push: reopen if needed, then send the real count
        // (with evidence), the one pending probe (without), or nothing.
        private void SendLocked()
        {
            if (this.handle == InvalidHandle)
            {
                // Device absent or lost earlier: retry the open every few heartbeats, not every one.
                if (--this.reopenCountdown > 0) return;
                this.reopenCountdown = ReopenEveryBeats;
                int eOpen;
                if (!this.TryOpen(out eOpen)) { this.status = "idle hint: off: open of " + DevicePath + " failed (error " + eOpen + "), retrying"; return; }
                this.probePending = true;   // the driver has forgotten us: probe again if there is still no evidence
                Debug.WriteLine($"{DateTime.Now}: HydraIdleHint: {DevicePath} opened.");
            }

            bool evidence = this.FrameSeen;
            uint n, flags;
            if (evidence) { n = (uint)this.Contacts; flags = 0; }
            else if (this.probePending) { n = ProbeContacts; flags = FlagProbe; }
            else return;   // no frame yet and the probe is out: say nothing (stale probe = NOHINT = today)

            uint s;
            int error;
            if (this.Transmit(n, flags, out s, out error))
            {
                this.consecutiveFailures = 0;
                if (evidence) this.status = "idle hint: on (seq " + s + ", contacts " + n + ")";
                else { this.probePending = false; this.status = "idle hint: probing (2108 accepted, seq " + s + ") - waiting for the runtime's first frame"; }
                return;
            }

            if (error == ErrorInvalidFunction || error == ErrorNotSupported)
            {
                // Production driver or a pre-IdleMask beta: no such IOCTL. Nothing to retry.
                this.Stop("driver has no idle-hint support (IOCTL 2108, error " + error + ")");
                return;
            }
            if (IsHandleLevelError(error))
            {
                // The device went away (driver swap / restart). Keep the heartbeat and reopen later.
                this.Close();
                this.reopenCountdown = ReopenEveryBeats;
                this.status = "idle hint: off: device lost (error " + error + "), reopening";
                Debug.WriteLine($"{DateTime.Now}: HydraIdleHint: IOCTL 2108 failed with error {error}; will retry the open.");
                return;
            }
            if (++this.consecutiveFailures >= MaxConsecutiveFailures)
            {
                this.Stop("IOCTL 2108 keeps failing (error " + error + ")");
                return;
            }
            this.status = "idle hint: off: IOCTL 2108 failed (error " + error + "), retrying";
        }

        // Caller holds sendLock and the handle is open. One IOCTL, no policy.
        private bool Transmit(uint n, uint flags, out uint s, out int error)
        {
            error = 0;
            s = ++this.seq;
            WriteUInt32(this.buffer, 0, HintMagic);
            WriteUInt32(this.buffer, 4, n);
            WriteUInt32(this.buffer, 8, s);
            WriteUInt32(this.buffer, 12, flags);
            uint returned;
            if (DeviceIoControl(this.handle, IoctlIdleContactHint, this.buffer, HintSize, IntPtr.Zero, 0u, out returned, IntPtr.Zero)) return true;
            error = Marshal.GetLastWin32Error();
            return false;
        }

        // Caller holds sendLock.
        private void Stop(string why)
        {
            this.stopped = true;
            this.Close();
            this.status = "idle hint: off: " + why;
            Debug.WriteLine($"{DateTime.Now}: HydraIdleHint: {why} - no further hints.");
            Timer t = this.heartbeat;
            if (t != null) { try { t.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { } }
        }

        private bool TryOpen(out int error)
        {
            error = 0;
            this.handle = CreateFile(DevicePath, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (this.handle != InvalidHandle) return true;
            error = Marshal.GetLastWin32Error();
            return false;
        }

        private void Close()
        {
            if (this.handle == InvalidHandle) return;
            CloseHandle(this.handle);
            this.handle = InvalidHandle;
        }

        // Detach, stop the heartbeat, send the goodbye (best effort, synchronous - one IOCTL that
        // queues behind at most the in-flight capture) and close. The driver's hint timeout still
        // covers a sender that never gets here (a kill).
        public void Dispose()
        {
            if (this.disposed) return;
            this.disposed = true;

            ContactTarget t = this.target;
            this.target = null;
            if (t != null)
            {
                t.ContactAdded -= this.HandleContactAdded;
                t.ContactChanged -= this.HandleContactChanged;
                t.ContactRemoved -= this.HandleContactRemoved;
                try { t.FrameReceived -= this.HandleFrameReceived; } catch (Exception) { }   // a no-op if already detached
            }
            Timer timer = this.heartbeat;
            this.heartbeat = null;
            if (timer != null)
            {
                // Wait for a callback in flight so the handle is not closed under it.
                using (var done = new ManualResetEvent(false))
                {
                    if (!timer.Dispose(done)) done.Set();
                    done.WaitOne(2000);
                }
            }
            lock (this.sendLock)
            {
                if (this.handle != InvalidHandle && !this.stopped)
                {
                    uint s; int error;
                    bool ok = this.Transmit(ContactsUnknown, FlagGoodbye, out s, out error);
                    Debug.WriteLine($"{DateTime.Now}: HydraIdleHint: goodbye hint (seq {s}) {(ok ? "sent" : "failed (error " + error + ")")}.");
                }
                this.Close();
                this.status = "idle hint: off: stopped";
            }
        }

        private static uint CtlCode(uint deviceType, uint function, uint method, uint access)
        {
            return (deviceType << 16) | (access << 14) | (function << 2) | method;
        }

        // The device is absent or (re)starting: worth retrying. Anything else at the initial open
        // (5 = not elevated in particular) is permanent for this process.
        private static bool IsTransientOpenError(int error)
        {
            return error == ErrorFileNotFound || error == ErrorNoSuchDevice || error == ErrorDeviceNotConnected;
        }

        private static bool IsHandleLevelError(int error)
        {
            return error == ErrorInvalidHandle || error == ErrorDeviceNotConnected || error == ErrorNoSuchDevice ||
                   error == ErrorFileNotFound || error == ErrorOperationAborted || error == ErrorAccessDenied;
        }

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
