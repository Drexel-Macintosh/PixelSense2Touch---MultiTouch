using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using PixelSenseToTouchLib;

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

		[STAThread]
		static void Main() {
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			var oContext = new PixelSense2TouchTray();
			Application.Run(oContext);
		}

		public PixelSense2TouchTray() {
			this.components = new System.ComponentModel.Container();

			this.SetupTrayIcon();
			this.SetupTrayMenu();
			this.InitPixelSenseToTouch();
			this.StartShellWatch();
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
			bool suspended = this.watchShellMenuItem.Checked && this.shellPresent;
			bool shouldRun = this.userWantsRunning && !suspended;

			if (shouldRun) this.pixelSenseToTouchProvider.InitEventHandlers();
			else this.pixelSenseToTouchProvider.RemoveEventHandlers();

			this.startMenuItem.Enabled = !this.userWantsRunning;
			this.stopMenuItem.Enabled = this.userWantsRunning;

			string status, shortStatus;
			if (!this.userWantsRunning) {
				status = "Touch: stopped";                                    shortStatus = "stopped";
			} else if (suspended) {
				status = "Touch: suspended - Surface Shell is running";       shortStatus = "suspended (Surface Shell)";
			} else if (this.pixelSenseToTouchProvider.IsRunning) {
				status = "Touch: running";                                    shortStatus = "running";
			} else {
				status = "Touch: unavailable - touch injection failed to start"; shortStatus = "unavailable";
			}

			this.statusMenuItem.Text = status;
			// The tooltip keeps the app's NAME - it is the only thing identifying this tray icon -
			// and appends the short status. NotifyIcon.Text throws above 63 characters.
			string tip = "PixelSenseToTouch - " + shortStatus;
			this.notifyIcon.Text = tip.Length <= 63 ? tip : tip.Substring(0, 63);
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

				"  •  A new tray icon.\n\n" +

				"Built on PixelSense2Touch by Boaz Pat-El - MIT license\n" +
				"http://www.boazpatel.com\n" +
				"https://github.com/Heer-Boaz/PixelSense2Touch",
				"About PixelSenseToTouch", MessageBoxButtons.OK, MessageBoxIcon.Information);
		}

#if DEBUG
		private void HandleDebugRequest(object sender, EventArgs e) {
			MessageBox.Show($"{this.pixelSenseToTouchProvider.debuginfo}");
		}
#endif

		private void HandleExitRequest(object sender, EventArgs e) {
			this.shellWatchTimer.Stop();
			this.pixelSenseToTouchProvider.CleanUp();   // detaches + releases any pointer still down
			this.pixelSenseToTouchProvider = null; // Dispose the touch provider
			base.ExitThreadCore();
		}
	}
}
