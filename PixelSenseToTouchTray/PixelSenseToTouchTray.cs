using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;
using PixelSenseToTouchLib;
using Mutex = System.Threading.Mutex;

namespace PixelSense2Touch
{
    class PixelSense2TouchTray : ApplicationContext {
		// The Surface Shell handles Surface contacts natively. SurfaceInput broadcasts contacts to
		// every client, so while the Shell is up our injected Windows pointers do not replace its
		// input - they arrive on top of it, and the Shell (WPF) sees every finger twice. So we
		// suspend injection for as long as SurfaceShell.exe is running.
		private const string ShellProcessName = "SurfaceShell";
		private const int ShellPollMs = 1000;
		// Stop fast, resume slow. The table's loader restarts a dead Shell in place with -recover
		// (StartupProcesses.xml), which briefly makes the process vanish. Requiring several
		// consecutive clear polls keeps a restart from flickering injection back on. For the same
		// reason the Shell is tracked BY NAME, never by a cached pid - a recover reappears under
		// the same name with a new pid.
		private const int ResumeDebouncePolls = 3;

		private readonly System.ComponentModel.IContainer components;
		private NotifyIcon notifyIcon;
		private ContextMenuStrip contextMenu;
		private ToolStripMenuItem statusMenuItem;
		private ToolStripMenuItem hintMenuItem;     // "idle hint: ..." read-out under the Touch line
		private ToolStripMenuItem stopMenuItem;
		private ToolStripMenuItem startMenuItem;
		private ToolStripMenuItem watchShellMenuItem;
		private ToolStripMenuItem aboutMenuItem;
#if DEBUG
		private ToolStripMenuItem debugMenuItem;
#endif
		private ToolStripMenuItem exitMenuItem;
		private PixelSenseToTouch pixelSenseToTouchProvider;

		private Timer shellWatchTimer;
		private readonly int sessionId = Process.GetCurrentProcess().SessionId;
		// What the user last asked for, tracked separately from what is actually running: a Shell
		// exit must never override an explicit Stop.
		private bool userWantsRunning = true;
		private bool shellPresent;
		private int shellAbsentPolls;
		// The short touch status ApplyState last chose, kept so the tooltip can be re-composed with
		// the idle-hint state without re-running the state machine.
		private string shortStatus = "starting";
		private readonly object shutdownLock = new object();

		// One instance per session. A second copy would inject every finger twice and - worse -
		// send its own IdleMask contact hints to the driver from a ContactTarget that has not seen
		// the runtime's contacts yet, so it must never get as far as Init(). Held for the life of
		// the process; the OS releases it when the process dies, so a crash cannot wedge the next start.
		private static Mutex singleInstance;

		[STAThread]
		static void Main() {
			if (!ClaimSingleInstance()) return;
			try {
				Application.EnableVisualStyles();
				Application.SetCompatibleTextRenderingDefault(false);
				var oContext = new PixelSense2TouchTray();
				Application.Run(oContext);
			}
			finally {
				try { singleInstance.ReleaseMutex(); } catch { }
				singleInstance.Dispose();
			}
		}

		private static bool ClaimSingleInstance() {
			int session = 0;
			try { using (Process me = Process.GetCurrentProcess()) session = me.SessionId; } catch { }
			string name = @"Global\PixelSenseToTouch-" + session;
			bool createdNew;
			try {
				singleInstance = new Mutex(true, name, out createdNew);
			}
			catch (UnauthorizedAccessException) {
				// The mutex exists but this copy may not open it (the first copy runs elevated and
				// this one does not, or vice versa): another instance is running.
				createdNew = false;
			}
			catch (Exception ex) {
				// Cannot create the mutex at all (kernel-object namespace policy). Do not let that
				// stop the app: run without the guard, as every version before 2.3 did.
				Trace.WriteLine(DateTime.Now + ": PixelSenseToTouch: single-instance mutex " + name + " unavailable (" + ex.GetType().Name + ": " + ex.Message + ") - running unguarded.");
				singleInstance = new Mutex();
				return true;
			}
			if (createdNew) return true;
			Trace.WriteLine(DateTime.Now + ": PixelSenseToTouch: another instance already runs in session " + session + " (" + name + " is held) - exiting without starting.");
			if (singleInstance != null) singleInstance.Dispose();
			return false;
		}

		public PixelSense2TouchTray() {
			this.components = new System.ComponentModel.Container();

			this.SetupTrayIcon();
			this.SetupTrayMenu();
			this.InitPixelSenseToTouch();
			this.StartShellWatch();

			// Every way out must reach CleanUp: it releases any pointer still down and sends the
			// IdleMask goodbye hint. Exit (menu) calls it directly; ApplicationExit covers a message
			// loop that ends for any other reason; SessionEnding covers logoff/shutdown, where Windows
			// would otherwise just terminate the process (the driver's hint timeout then covers us,
			// but 1.5 s later and with the pointers released only by the HID sink's close).
			Application.ApplicationExit += new EventHandler(HandleApplicationExit);
			try { SystemEvents.SessionEnding += new SessionEndingEventHandler(HandleSessionEnding); }
			catch (Exception ex) { Debug.WriteLine($"{DateTime.Now}: SessionEnding hook unavailable: {ex.Message}"); }
		}

		// A bare filename resolves against Environment.CurrentDirectory, not the exe's folder, so
		// launching from anywhere else (an elevated PowerShell starts in System32; a scheduled task
		// may set no working directory at all) threw before any UI existed - the app just never
		// appeared. Resolve against the assembly's own folder instead. The loose file wins so the
		// icon stays swappable in an install (README.txt lists it), the copy embedded in this
		// assembly covers its absence, and a stock icon covers everything else: a missing icon must
		// never be what stops the tray from starting.
		private static System.Drawing.Icon LoadIcon(string name) {
			try {
				string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
				if (File.Exists(path)) return new System.Drawing.Icon(path);
			}
			catch { }
			try {
				Assembly asm = Assembly.GetExecutingAssembly();
				foreach (string res in asm.GetManifestResourceNames())
					if (res.EndsWith(name, StringComparison.OrdinalIgnoreCase))
						using (Stream st = asm.GetManifestResourceStream(res))
							return new System.Drawing.Icon(st);
			}
			catch { }
			return System.Drawing.SystemIcons.Application;
		}

		private void SetupTrayIcon() {
            this.notifyIcon = new NotifyIcon(this.components)
            {
                Icon = LoadIcon("trayicon.ico"),
                Text = "PixelSenseToTouch",
                Visible = true
            };

            this.contextMenu = new ContextMenuStrip();
			this.statusMenuItem = new ToolStripMenuItem();
			this.hintMenuItem = new ToolStripMenuItem();
			this.stopMenuItem = new ToolStripMenuItem();
			this.startMenuItem = new ToolStripMenuItem();
			this.watchShellMenuItem = new ToolStripMenuItem();
			this.aboutMenuItem = new ToolStripMenuItem();
#if DEBUG
			this.debugMenuItem = new ToolStripMenuItem();
#endif
			this.exitMenuItem = new ToolStripMenuItem();

			this.notifyIcon.ContextMenuStrip = this.contextMenu;
		}

		private void InitPixelSenseToTouch() {
			// Init PixelSenseToTouch provider
			this.pixelSenseToTouchProvider = new PixelSenseToTouch();
			this.pixelSenseToTouchProvider.Init();
		}

		private void SetupTrayMenu() {
			this.statusMenuItem.Enabled = false;   // a read-out, not a command
			this.contextMenu.Items.Add(this.statusMenuItem);
			// The IdleMask contact hint (HydraIdleHint): on/off + live seq/count. Refreshed as the
			// menu opens so the numbers are current; a read-out like the Touch line above it.
			this.hintMenuItem.Enabled = false;
			this.contextMenu.Items.Add(this.hintMenuItem);
			this.contextMenu.Opening += new System.ComponentModel.CancelEventHandler(HandleMenuOpening);
			this.contextMenu.Items.Add(new ToolStripSeparator());

			this.startMenuItem.Text = "Start";
			this.startMenuItem.Click += new EventHandler(HandleStartRequest);
			this.contextMenu.Items.Add(this.startMenuItem);

			this.stopMenuItem.Text = "Stop";
			this.stopMenuItem.Click += new EventHandler(HandleStopRequest);
			this.contextMenu.Items.Add(this.stopMenuItem);

			this.watchShellMenuItem.Text = "Suspend while Surface Shell runs";
			this.watchShellMenuItem.CheckOnClick = true;
			this.watchShellMenuItem.Checked = true;
			this.watchShellMenuItem.CheckedChanged += new EventHandler(HandleWatchShellToggle);
			this.contextMenu.Items.Add(this.watchShellMenuItem);

			this.contextMenu.Items.Add(new ToolStripSeparator());

			this.aboutMenuItem.Text = "About";
			this.aboutMenuItem.Click += new EventHandler(HandleAboutRequest);
			this.contextMenu.Items.Add(this.aboutMenuItem);

#if DEBUG
			this.debugMenuItem.Text = "Debug";
			this.debugMenuItem.Click += new EventHandler(HandleDebugRequest);
			this.contextMenu.Items.Add(this.debugMenuItem);
#endif

			this.exitMenuItem.Text = "Exit";
			this.exitMenuItem.Click += new EventHandler(HandleExitRequest);
			this.contextMenu.Items.Add(this.exitMenuItem);
		}

		private void StartShellWatch() {
			// Check once synchronously first: launching while the Shell is already up must suspend
			// immediately, not inject for the first second.
			this.shellPresent = IsShellRunning(this.sessionId);
			this.ApplyState();

			this.shellWatchTimer = new Timer(this.components) { Interval = ShellPollMs };
			this.shellWatchTimer.Tick += new EventHandler(HandleShellWatchTick);
			this.shellWatchTimer.Start();
		}

		private static bool IsShellRunning(int sessionId) {
			try {
				foreach (var p in Process.GetProcessesByName(ShellProcessName)) {
					// GetProcessesByName is session-blind; a Shell in another session is not ours.
					try { if (p.SessionId == sessionId) return true; }
					catch { }
					finally { p.Dispose(); }
				}
			}
			catch { }   // never let a transient process-enumeration failure kill the tray
			return false;
		}

		private void HandleShellWatchTick(object sender, EventArgs e) {
			if (this.pixelSenseToTouchProvider == null) { this.shellWatchTimer.Stop(); return; }   // shut down from another path
			// Piggy-back on the 1 s poll to keep the hint read-out and tooltip current (the hint can
			// turn off on its own if the driver goes away). Independent of the Shell logic below.
			this.RefreshHintStatus();

			if (!this.watchShellMenuItem.Checked) return;

			if (IsShellRunning(this.sessionId)) {
				this.shellAbsentPolls = 0;
				if (this.shellPresent) return;
				this.shellPresent = true;
				this.ApplyState();
				return;
			}

			if (!this.shellPresent) return;
			if (++this.shellAbsentPolls < ResumeDebouncePolls) return;   // maybe a -recover restart
			this.shellPresent = false;
			this.ApplyState();
		}

		private void HandleWatchShellToggle(object sender, EventArgs e) {
			this.shellAbsentPolls = 0;
			if (this.watchShellMenuItem.Checked) this.shellPresent = IsShellRunning(this.sessionId);
			this.ApplyState();
		}

		// The one place that reconciles the engine with (what the user wants, whether the Shell is up).
		private void ApplyState() {
			var provider = this.pixelSenseToTouchProvider;
			if (provider == null) return;   // shut down
			bool suspended = this.watchShellMenuItem.Checked && this.shellPresent;
			bool shouldRun = this.userWantsRunning && !suspended;

			if (shouldRun) provider.InitEventHandlers();
			else provider.RemoveEventHandlers();

			this.startMenuItem.Enabled = !this.userWantsRunning;
			this.stopMenuItem.Enabled = this.userWantsRunning;

			string status, shortStatus;
			if (!this.userWantsRunning) {
				status = "Touch: stopped";                                    shortStatus = "stopped";
			} else if (suspended) {
				status = "Touch: suspended - Surface Shell is running";       shortStatus = "suspended (Surface Shell)";
			} else if (provider.IsRunning) {
				// Deliberately does NOT claim UAC. The HID digitizer clears UIPI, so elevated
				// windows work - but the UAC prompt lives on the secure desktop, where SurfaceInput
				// freezes (measured: 0% CPU for the whole prompt) and therefore produces no contacts
				// to forward. UAC and the lock screen need the session-0 service, not this app.
				status = provider.SinkReachesElevated
					? "Touch: running - includes elevated admin windows"
					: "Touch: running - NOT on elevated windows";
				shortStatus = "running";
			} else {
				status = "Touch: unavailable - no touch sink could be started"; shortStatus = "unavailable";
			}

			this.statusMenuItem.Text = status;
			this.shortStatus = shortStatus;
			this.RefreshHintStatus();
		}

		// The idle-hint read-out next to the Touch line, and the tooltip. Only text; it never
		// touches the engine, so it is safe to call from the poll tick and the menu's Opening.
		private void RefreshHintStatus() {
			var provider = this.pixelSenseToTouchProvider;
			if (provider == null) return;
			string hint = provider.IdleHintStatus;
			this.hintMenuItem.Text = hint;
			// The tooltip keeps the app's NAME - it is the only thing identifying this tray icon -
			// and appends the short status. NotifyIcon.Text throws above 63 characters.
			bool hintOn = provider.IdleHint != null && provider.IdleHint.IsOn;
			string tip = "PixelSenseToTouch - " + this.shortStatus + (hintOn ? ", hint on" : ", hint off");
			this.notifyIcon.Text = tip.Length <= 63 ? tip : tip.Substring(0, 63);
		}

		private void HandleMenuOpening(object sender, System.ComponentModel.CancelEventArgs e) {
			this.RefreshHintStatus();
		}

		private void HandleStopRequest(object sender, EventArgs e) {
			this.userWantsRunning = false;
			this.ApplyState();
		}

		private void HandleStartRequest(object sender, EventArgs e) {
			this.userWantsRunning = true;
			this.ApplyState();
		}

		private void HandleAboutRequest(object sender, EventArgs e) {
			// Version is read from the assembly so this box cannot go stale on a rebuild.
			string ver = Assembly.GetExecutingAssembly().GetName().Version.ToString();
			var provider = this.pixelSenseToTouchProvider;
			MessageBox.Show(
				"PixelSenseToTouch " + ver + "\n" +
				"Surface 1.0 (PixelSense) to Windows touch bridge.\n\n" +

				"Turns the touches seen by a Microsoft Surface 1.0 table into real Windows " +
				"multi-touch. It rides on top of Surface Input, which owns the camera and " +
				"produces the contacts.\n\n" +

				"Version 2 was created as a part of the Hydra x64 Project by Joe LiTrenta and is " +
				"a fork of PixelSense2Touch by Boaz Pat-El. What the fork adds:\n\n" +

				"  •  Multi-touch.  The original injected one pointer and emulated clicks " +
				"with a mouse simulator. Every contact is now injected as its own Windows touch " +
				"pointer, so Windows performs the gestures itself - tap, press-and-hold, drag, " +
				"pinch and zoom - and many fingers track at once.\n\n" +

				"  •  Automatic stop and start around the Surface Shell.  The Shell handles " +
				"Surface contacts natively, so while it is running this app suspends itself, and " +
				"it resumes when you exit the Shell. Without that the Shell would see every " +
				"finger twice. Your own Stop always wins, and the behaviour can be switched off " +
				"from the tray menu.\n\n" +

				"  •  A pluggable output path (2.2).  Touch injection is the default, exactly as " +
				"before. With PIXELSENSETOUCH_SINK=hid the same contacts go out through the " +
				"HydraTouch HID digitizer as kernel HID reports instead, which reach elevated " +
				"(admin) windows that injection cannot - opt-in for now, because a held contact " +
				"can flash on that path. Neither path covers the UAC prompt or the lock screen - " +
				"they run on the secure desktop, where Surface Input produces no contacts; those " +
				"need the Hydra Touch session-0 service.\n" +
				"     Input path now: " + (provider?.SinkDetail ?? "not started") + "\n\n" +

				"  •  An idle hint for the camera driver (2.3).  The running contact count is " +
				"sent to the HydraX64Beta driver so its IdleMask gate knows when the table is " +
				"empty; inert on the production driver or when not elevated.\n" +
				"     Idle hint now: " + (provider?.IdleHintStatus ?? "stopped") + "\n\n" +

				"  •  A new tray icon.\n\n" +

				"Built on PixelSense2Touch by Boaz Pat-El - MIT license\n" +
				"http://www.boazpatel.com\n" +
				"https://github.com/Heer-Boaz/PixelSense2Touch",
				"About PixelSenseToTouch", MessageBoxButtons.OK, MessageBoxIcon.Information);
		}

#if DEBUG
		private void HandleDebugRequest(object sender, EventArgs e) {
			MessageBox.Show($"{this.pixelSenseToTouchProvider?.debuginfo}");
		}
#endif

		private void HandleExitRequest(object sender, EventArgs e) {
			this.shellWatchTimer.Stop();
			this.Shutdown();
			base.ExitThreadCore();
		}

		private void HandleApplicationExit(object sender, EventArgs e) {
			this.Shutdown();
		}

		private void HandleSessionEnding(object sender, SessionEndingEventArgs e) {
			this.Shutdown();
		}

		// Tear the engine down exactly once, whichever exit path gets here first: detach + release
		// any pointer still down, send the idle-hint goodbye, close the sink. Safe to call from any
		// thread (SessionEnding need not arrive on the UI thread) and repeatedly; the UI-thread
		// timer is left to its own tick, which stops it once the provider is gone.
		private void Shutdown() {
			PixelSenseToTouch provider;
			lock (this.shutdownLock) {
				provider = this.pixelSenseToTouchProvider;
				this.pixelSenseToTouchProvider = null;
			}
			if (provider == null) return;
			try { SystemEvents.SessionEnding -= new SessionEndingEventHandler(HandleSessionEnding); } catch { }
			try { provider.CleanUp(); }
			catch (Exception ex) { Debug.WriteLine($"{DateTime.Now}: CleanUp failed: {ex}"); }
		}
	}
}
