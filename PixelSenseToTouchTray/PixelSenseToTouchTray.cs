using System;
using System.Windows.Forms;
using PixelSenseToTouchLib;

namespace PixelSense2Touch
{
    class PixelSense2TouchTray : ApplicationContext {
		private readonly System.ComponentModel.IContainer components;
		private NotifyIcon notifyIcon;
		private ContextMenuStrip contextMenu;
		private ToolStripMenuItem stopMenuItem;
		private ToolStripMenuItem startMenuItem;
		private ToolStripMenuItem aboutMenuItem;
#if DEBUG
		private ToolStripMenuItem debugMenuItem;
#endif
		private ToolStripMenuItem exitMenuItem;
		private PixelSenseToTouch pixelSenseToTouchProvider;

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
		}

		private void SetupTrayIcon() {
            this.notifyIcon = new NotifyIcon(this.components)
            {
                Icon = new System.Drawing.Icon("trayicon.ico"),
                Text = "Hydra Touch",
                Visible = true
            };

            this.contextMenu = new ContextMenuStrip();
			this.stopMenuItem = new ToolStripMenuItem();
			this.startMenuItem = new ToolStripMenuItem();
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
			this.startMenuItem.Text = "Start";
			this.startMenuItem.Click += new EventHandler(HandleStartRequest);
			this.startMenuItem.Enabled = false;
			this.contextMenu.Items.Add(this.startMenuItem);

			this.stopMenuItem.Text = "Stop";
			this.stopMenuItem.Click += new EventHandler(HandleStopRequest);
			this.stopMenuItem.Enabled = true;
			this.contextMenu.Items.Add(this.stopMenuItem);

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

		private void HandleStopRequest(object sender, EventArgs e) {
			this.pixelSenseToTouchProvider.RemoveEventHandlers();
			this.stopMenuItem.Enabled = false;
			this.startMenuItem.Enabled = true;
		}

		private void HandleStartRequest(object sender, EventArgs e) {
			this.pixelSenseToTouchProvider.InitEventHandlers();
			this.startMenuItem.Enabled = false;
			this.stopMenuItem.Enabled = true;
		}

		private void HandleAboutRequest(object sender, EventArgs e) {
			MessageBox.Show(
				"Hydra Touch - Surface 1.0 (PixelSense) to Windows touch bridge.\n\n" +
				"Multi-touch fork for the Surface1-Hydra-x64 project, built on PixelSense2Touch " +
				"by Boaz Pat-El (MIT license, http://www.boazpatel.com, " +
				"https://github.com/Heer-Boaz/PixelSense2Touch).",
				"About Hydra Touch", MessageBoxButtons.OK, MessageBoxIcon.Information);
		}

#if DEBUG
		private void HandleDebugRequest(object sender, EventArgs e) {
			MessageBox.Show($"{this.pixelSenseToTouchProvider.debuginfo}");
		}
#endif

		private void HandleExitRequest(object sender, EventArgs e) {
			this.pixelSenseToTouchProvider.CleanUp();
			this.pixelSenseToTouchProvider = null; // Dispose the touch provider
			base.ExitThreadCore();
		}
	}
}
