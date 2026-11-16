// Time Zone Changer - standalone Windows 11 tray app.
// Compiles with the .NET Framework 4.x C# compiler (C# 5 syntax) - see build.ps1.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Time Zone Changer")]
[assembly: System.Reflection.AssemblyProduct("Time Zone Changer")]
[assembly: System.Reflection.AssemblyDescription("View and change the Windows system time zone")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]

namespace TimeZoneChanger
{
    static class Program
    {
        const string MutexName = "Local\\TimeZoneChanger_SingleInstance";
        const string ShowEventName = "Local\\TimeZoneChanger_ShowWindow";

        [STAThread]
        static void Main(string[] args)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Already running: ask the existing instance to show its window.
                    try
                    {
                        using (EventWaitHandle ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set();
                    }
                    catch { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool startHidden = args.Any(a => a.Equals("/tray", StringComparison.OrdinalIgnoreCase));

                using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    MainForm form = new MainForm(startHidden);
                    ThreadPool.RegisterWaitForSingleObject(showEvent, delegate
                    {
                        try { form.BeginInvoke(new Action(form.ShowFromTray)); } catch { }
                    }, null, -1, false);
                    Application.Run(form);
                }
            }
        }
    }

    static class Native
    {
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }

    // ---------------- Simple key=value settings file ----------------
    class Settings
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TimeZoneChanger");
        static readonly string FilePath = Path.Combine(DataDir, "settings.ini");

        public string HomeZone = "";
        public bool HomeInTray = true;
        public bool FloatingClock = false;
        public int FloatX = int.MinValue, FloatY = int.MinValue;
        public int FloatW = 0, FloatH = 0;

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    int n;
                    if (k == "HomeZone") s.HomeZone = v;
                    else if (k == "HomeInTray") s.HomeInTray = v == "1";
                    else if (k == "FloatingClock") s.FloatingClock = v == "1";
                    else if (k == "FloatX" && int.TryParse(v, out n)) s.FloatX = n;
                    else if (k == "FloatY" && int.TryParse(v, out n)) s.FloatY = n;
                    else if (k == "FloatW" && int.TryParse(v, out n)) s.FloatW = n;
                    else if (k == "FloatH" && int.TryParse(v, out n)) s.FloatH = n;
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllLines(FilePath, new string[]
                {
                    "HomeZone=" + HomeZone,
                    "HomeInTray=" + (HomeInTray ? "1" : "0"),
                    "FloatingClock=" + (FloatingClock ? "1" : "0"),
                    "FloatX=" + FloatX,
                    "FloatY=" + FloatY,
                    "FloatW=" + FloatW,
                    "FloatH=" + FloatH
                });
            }
            catch { }
        }
    }

    // ---------------- Floating always-on-top home clock (drag to move, drag edges to resize) ----------------
    class HomeClockWindow : Form
    {
        const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDBLCLK = 0xA3, WM_NCRBUTTONUP = 0xA5, WM_EXITSIZEMOVE = 0x232;
        const int Grip = 7;   // resize border thickness in pixels

        static readonly Color Bg = Color.FromArgb(32, 32, 32);
        static readonly Color Fg = Color.White;
        static readonly Color Dim = Color.FromArgb(228, 228, 228);
        static readonly Color Faint = Color.FromArgb(150, 150, 150);

        TimeZoneInfo home;
        public event EventHandler OpenRequested, HideRequested, PositionSaved;

        public HomeClockWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            Opacity = 0.93;
            ClientSize = new Size(200, 64);
            MinimumSize = new Size(64, 26);
            MaximumSize = new Size(1400, 600);
            Text = "Home Clock";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open Time Zone Changer", null, delegate { if (OpenRequested != null) OpenRequested(this, EventArgs.Empty); });
            menu.Items.Add("Reset size", null, delegate
            {
                ClientSize = new Size(200, 64);
                if (PositionSaved != null) PositionSaved(this, EventArgs.Empty);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Hide floating clock", null, delegate { if (HideRequested != null) HideRequested(this, EventArgs.Empty); });
            ContextMenuStrip = menu;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: hide from Alt+Tab
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE: don't steal focus
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // Win11 rounded corners
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                int lp = unchecked((int)(long)m.LParam);
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                bool l = p.X < Grip, r = p.X >= ClientSize.Width - Grip;
                bool t = p.Y < Grip, b = p.Y >= ClientSize.Height - Grip;
                int hit = 2; // HTCAPTION -> drag to move
                if (t && l) hit = 13; else if (t && r) hit = 14;
                else if (b && l) hit = 16; else if (b && r) hit = 17;
                else if (l) hit = 10; else if (r) hit = 11;
                else if (t) hit = 12; else if (b) hit = 15;
                m.Result = new IntPtr(hit);
                return;
            }
            if (m.Msg == WM_NCLBUTTONDBLCLK)
            {
                if (OpenRequested != null) OpenRequested(this, EventArgs.Empty);
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_NCRBUTTONUP)
            {
                ContextMenuStrip.Show(Cursor.Position);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == WM_EXITSIZEMOVE && PositionSaved != null) PositionSaved(this, EventArgs.Empty);
        }

        public void UpdateTime(TimeZoneInfo homeZone)
        {
            home = homeZone;
            Invalidate();
        }

        public void KeepOnTop()
        {
            if (Visible)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // ---------- Adaptive layout ----------
        // The content and formats change with the window size so the text stays readable:
        //   tiny          ->  09:41
        //   short & wide  ->  09:41:27  ·  Tue 17 Nov            (one line)
        //   small         ->  09:41 / Tue 17
        //   medium        ->  09:41:27 / Home · Beijing · Tue 17 Nov
        //   large         ->  09:41:27 / Tuesday, 17 November 2026 / Home · Beijing · UTC+08:00

        const float MinInfoPx = 10f;   // below this a secondary line is dropped instead of shrunk

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (home == null) return;

            DateTime t = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, home);
            string city = MainForm.ShortName(home);
            TimeSpan off = home.GetUtcOffset(DateTime.UtcNow);
            string offset = "UTC" + (off < TimeSpan.Zero ? "-" : "+") + off.Duration().ToString(@"hh\:mm");

            int pad = Math.Max(4, Math.Min(ClientSize.Width, ClientSize.Height) / 12);
            RectangleF area = new RectangleF(pad, pad * 0.6f, ClientSize.Width - 2 * pad, ClientSize.Height - 1.2f * pad);
            float w = area.Width, h = area.Height, aspect = w / Math.Max(1f, h);

            bool done = false;

            // Large: three lines
            if (!done && h >= 96 && w >= 210)
            {
                RectangleF r1 = new RectangleF(area.X, area.Y, w, h * 0.44f);
                RectangleF r2 = new RectangleF(area.X, r1.Bottom, w, h * 0.34f);
                RectangleF r3 = new RectangleF(area.X, r2.Bottom, w, h * 0.22f);
                string dateLong = t.ToString("dddd, d MMMM yyyy");
                string footer = "Home · " + city + " · " + offset;
                float f2 = FitSize(g, dateLong, r2, false), f3 = FitSize(g, footer, r3, false);
                if (f2 >= MinInfoPx && f3 >= MinInfoPx)
                {
                    DrawFitted(g, t.ToString("HH:mm:ss"), "00:00:00", r1, true, Fg);
                    DrawText(g, dateLong, r2, Math.Min(f2, f3 * 1.25f), false, Dim);
                    DrawText(g, footer, r3, Math.Min(f3, f2), false, Faint);
                    done = true;
                }
            }

            // Short & wide: single line with date
            if (!done && h < 44 && aspect >= 5.5f)
            {
                string line = t.ToString("HH:mm:ss") + "  ·  " + t.ToString("ddd d MMM");
                DrawFitted(g, line, "00:00:00  ·  Wed 30 Sep", area, true, Fg);
                done = true;
            }

            // Two lines: time + info (choose the most detailed info that stays readable)
            if (!done && h >= 40)
            {
                string timeFmt = w >= 140 ? "HH:mm:ss" : "HH:mm";
                RectangleF r1 = new RectangleF(area.X, area.Y, w, h * 0.54f);
                RectangleF r2 = new RectangleF(area.X, r1.Bottom, w, h * 0.46f);
                string[] infos =
                {
                    "Home · " + city + " · " + t.ToString("ddd d MMM"),
                    city + " · " + t.ToString("ddd d MMM"),
                    t.ToString("ddd d MMM"),
                    t.ToString("ddd d")
                };
                // Like the Windows tray clock: the date is nearly as large as the time (capped at ~85%).
                float timePx = FitSize(g, timeFmt == "HH:mm" ? "00:00" : "00:00:00", r1, true);
                foreach (string info in infos)
                {
                    float f = Math.Min(FitSize(g, info, r2, false), timePx * 0.85f);
                    if (f >= MinInfoPx)
                    {
                        DrawFitted(g, t.ToString(timeFmt), timeFmt == "HH:mm" ? "00:00" : "00:00:00", r1, true, Fg);
                        DrawText(g, info, r2, f, false, Dim);
                        done = true;
                        break;
                    }
                }
            }

            // Fallback: time only
            if (!done)
            {
                bool secs = aspect >= 3.2f;
                DrawFitted(g, t.ToString(secs ? "HH:mm:ss" : "HH:mm"), secs ? "00:00:00" : "00:00", area, true, Fg);
            }

            // Subtle resize grip (bottom-right)
            using (Brush gb = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
            {
                int cw = ClientSize.Width, ch = ClientSize.Height;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j <= i; j++)
                        g.FillRectangle(gb, cw - 4 - (i - j) * 3 - 2, ch - 4 - j * 3 - 2, 2, 2);
            }
        }

        static Font MakeFont(float px, bool bold)
        {
            // Semibold time / regular date, similar to the Windows 11 taskbar clock.
            return new Font(bold ? "Segoe UI Semibold" : "Segoe UI", Math.Max(1f, px), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        // Largest pixel size at which 'text' fits inside 'r'.
        static float FitSize(Graphics g, string text, RectangleF r, bool bold)
        {
            const float refPx = 100f;
            using (Font f = MakeFont(refPx, bold))
            {
                SizeF sz = g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic);
                if (sz.Width <= 0 || sz.Height <= 0) return refPx;
                // Line height of Segoe UI is ~1.33em; cap height is what matters visually, so allow a bit of overlap.
                float byW = refPx * r.Width * 0.9f / sz.Width;   // keep ~5% side margin
                float byH = refPx * r.Height / (f.GetHeight(g) * 0.92f);
                return Math.Max(1f, Math.Min(byW, byH));
            }
        }

        // Size computed from a fixed template so the text doesn't "breathe" as digits change.
        static void DrawFitted(Graphics g, string text, string template, RectangleF r, bool bold, Color c)
        {
            DrawText(g, text, r, FitSize(g, template, r, bold), bold, c);
        }

        static void DrawText(Graphics g, string text, RectangleF r, float px, bool bold, Color c)
        {
            using (Font f = MakeFont(px, bold))
            using (Brush b = new SolidBrush(c))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                // GDI+ skips a line entirely if the layout box is shorter than the line height,
                // so draw in a box sized to the font and centred on the target slot.
                float lh = f.GetHeight(g);
                float cy = r.Y + r.Height / 2f;
                RectangleF box = new RectangleF(r.X - r.Width, cy - lh, r.Width * 3f, lh * 2f);
                g.DrawString(text, f, b, box, sf);
            }
        }
    }

    // ---------------- Main window ----------------
    class MainForm : Form
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "TimeZoneChanger";
        const int MaxRecent = 8;
        static readonly string RecentFile = Path.Combine(Settings.DataDir, "recent.txt");

        bool allowVisible;
        bool reallyExit;
        bool trayHintShown;
        bool loading = true;

        Label lblName, lblDetails, lblClock, lblPreview, lblHomeTime;
        TextBox txtSearch;
        ListBox lstZones;
        Button btnApply, btnRefresh, btnSettings;
        CheckBox chkStartup, chkHomeTray, chkFloating;
        ComboBox cmbHome;
        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        System.Windows.Forms.Timer timer;
        HomeClockWindow floatWin;

        Icon appSmallIcon;
        IntPtr dynIconHandle = IntPtr.Zero;
        string trayIconKey = "";

        Settings settings;
        TimeZoneInfo currentTz, homeTz;
        List<TimeZoneInfo> allZones;
        List<string> recent = new List<string>();

        public MainForm(bool startHidden)
        {
            allowVisible = !startHidden;
            trayHintShown = startHidden;

            Text = "Time Zone Changer";
            ClientSize = new Size(604, 685);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Icon = LoadAppIcon(new Size(32, 32));
            appSmallIcon = LoadAppIcon(SystemInformation.SmallIconSize);

            allZones = TimeZoneInfo.GetSystemTimeZones()
                .OrderBy(z => z.BaseUtcOffset).ThenBy(z => z.DisplayName).ToList();

            settings = Settings.Load();
            TimeZoneInfo.ClearCachedData();
            homeTz = FindZone(settings.HomeZone);
            if (homeTz == null)
            {
                // First run: your current zone becomes "home".
                homeTz = FindZone(TimeZoneInfo.Local.Id) ?? TimeZoneInfo.Local;
                settings.HomeZone = homeTz.Id;
                settings.Save();
            }

            InitStartup();
            BuildUi();
            BuildTray();
            BuildFloatingClock();
            LoadRecent();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { UpdateClock(); };

            SystemEvents.TimeChanged += OnSystemTimeChanged;

            RefreshCurrent();
            UpdateZoneList();
            loading = false;
            UpdateClock();
            timer.Start();

            if (settings.FloatingClock) ShowFloatingClock();

            // Make sure a window handle exists so BeginInvoke works even when started hidden.
            IntPtr h = Handle;
        }

        // ---------------- UI construction ----------------

        void BuildUi()
        {
            GroupBox grpCurrent = new GroupBox();
            grpCurrent.Text = "Current System Time Zone";
            grpCurrent.Location = new Point(12, 10);
            grpCurrent.Size = new Size(580, 150);
            Controls.Add(grpCurrent);

            lblName = new Label();
            lblName.Location = new Point(12, 25);
            lblName.Size = new Size(555, 25);
            lblName.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            grpCurrent.Controls.Add(lblName);

            lblDetails = new Label();
            lblDetails.Location = new Point(12, 52);
            lblDetails.Size = new Size(555, 50);
            grpCurrent.Controls.Add(lblDetails);

            lblClock = new Label();
            lblClock.Location = new Point(12, 105);
            lblClock.Size = new Size(555, 32);
            lblClock.Font = new Font("Segoe UI", 14f);
            lblClock.ForeColor = Color.DarkBlue;
            grpCurrent.Controls.Add(lblClock);

            // --- Change time zone ---
            GroupBox grpChange = new GroupBox();
            grpChange.Text = "Change Time Zone";
            grpChange.Location = new Point(12, 168);
            grpChange.Size = new Size(580, 340);
            Controls.Add(grpChange);

            Label lblSearch = new Label();
            lblSearch.Text = "Search:";
            lblSearch.Location = new Point(12, 30);
            lblSearch.AutoSize = true;
            grpChange.Controls.Add(lblSearch);

            txtSearch = new TextBox();
            txtSearch.Location = new Point(75, 27);
            txtSearch.Size = new Size(490, 25);
            txtSearch.TextChanged += delegate { UpdateZoneList(); };
            grpChange.Controls.Add(txtSearch);

            lstZones = new ListBox();
            lstZones.Location = new Point(12, 60);
            lstZones.Size = new Size(553, 196);
            lstZones.DisplayMember = "DisplayName";
            lstZones.HorizontalScrollbar = true;
            lstZones.SelectedIndexChanged += delegate { OnSelectionChanged(); };
            lstZones.DoubleClick += delegate { if (btnApply.Enabled) ApplySelected(); };
            grpChange.Controls.Add(lstZones);

            lblPreview = new Label();
            lblPreview.Location = new Point(12, 260);
            lblPreview.Size = new Size(553, 25);
            lblPreview.ForeColor = Color.DimGray;
            grpChange.Controls.Add(lblPreview);

            btnApply = new Button();
            btnApply.Text = "Apply Selected Time Zone";
            btnApply.Location = new Point(12, 290);
            btnApply.Size = new Size(220, 35);
            btnApply.Enabled = false;
            btnApply.Click += delegate { ApplySelected(); };
            grpChange.Controls.Add(btnApply);

            btnRefresh = new Button();
            btnRefresh.Text = "Refresh";
            btnRefresh.Location = new Point(240, 290);
            btnRefresh.Size = new Size(100, 35);
            btnRefresh.Click += delegate
            {
                txtSearch.Text = "";
                RefreshCurrent();
                UpdateZoneList();
                UpdateClock();
            };
            grpChange.Controls.Add(btnRefresh);

            btnSettings = new Button();
            btnSettings.Text = "Open Windows Settings";
            btnSettings.Location = new Point(375, 290);
            btnSettings.Size = new Size(190, 35);
            btnSettings.Click += delegate { OpenWindowsSettings(); };
            grpChange.Controls.Add(btnSettings);

            // --- Home clock ---
            GroupBox grpHome = new GroupBox();
            grpHome.Text = "Home Clock  (stays on your home time when the system zone changes)";
            grpHome.Location = new Point(12, 516);
            grpHome.Size = new Size(580, 128);
            Controls.Add(grpHome);

            Label lblHome = new Label();
            lblHome.Text = "Home zone:";
            lblHome.Location = new Point(12, 30);
            lblHome.AutoSize = true;
            grpHome.Controls.Add(lblHome);

            cmbHome = new ComboBox();
            cmbHome.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbHome.Location = new Point(100, 27);
            cmbHome.Size = new Size(465, 25);
            cmbHome.DropDownWidth = 520;
            cmbHome.MaxDropDownItems = 20;
            cmbHome.DisplayMember = "DisplayName";
            foreach (TimeZoneInfo z in allZones) cmbHome.Items.Add(z);
            cmbHome.SelectedItem = allZones.FirstOrDefault(z => z.Id == homeTz.Id);
            cmbHome.SelectedIndexChanged += delegate
            {
                if (loading) return;
                TimeZoneInfo sel = cmbHome.SelectedItem as TimeZoneInfo;
                if (sel == null) return;
                homeTz = sel;
                settings.HomeZone = sel.Id;
                settings.Save();
                trayIconKey = "";
                UpdateClock();
            };
            grpHome.Controls.Add(cmbHome);

            chkHomeTray = new CheckBox();
            chkHomeTray.Text = "Show home time in the tray icon";
            chkHomeTray.Location = new Point(14, 62);
            chkHomeTray.AutoSize = true;
            chkHomeTray.Checked = settings.HomeInTray;
            chkHomeTray.CheckedChanged += delegate
            {
                settings.HomeInTray = chkHomeTray.Checked;
                settings.Save();
                trayIconKey = "";
                UpdateClock();
            };
            grpHome.Controls.Add(chkHomeTray);

            chkFloating = new CheckBox();
            chkFloating.Text = "Show floating home clock (always on top, drag to move)";
            chkFloating.Location = new Point(14, 92);
            chkFloating.AutoSize = true;
            chkFloating.Checked = settings.FloatingClock;
            chkFloating.CheckedChanged += delegate
            {
                settings.FloatingClock = chkFloating.Checked;
                settings.Save();
                if (chkFloating.Checked) ShowFloatingClock(); else floatWin.Hide();
            };
            grpHome.Controls.Add(chkFloating);

            lblHomeTime = new Label();
            lblHomeTime.Location = new Point(300, 62);
            lblHomeTime.Size = new Size(265, 25);
            lblHomeTime.TextAlign = ContentAlignment.MiddleRight;
            lblHomeTime.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            lblHomeTime.ForeColor = Color.FromArgb(0, 90, 190);
            grpHome.Controls.Add(lblHomeTime);

            // --- Footer ---
            chkStartup = new CheckBox();
            chkStartup.Text = "Start with Windows (in system tray)";
            chkStartup.Location = new Point(14, 655);
            chkStartup.AutoSize = true;
            chkStartup.Checked = IsStartupEnabled();
            chkStartup.CheckedChanged += delegate { SetStartup(chkStartup.Checked); };
            Controls.Add(chkStartup);

            Label lblHint = new Label();
            lblHint.Text = "Closing the window keeps it in the tray.";
            lblHint.ForeColor = Color.Gray;
            lblHint.AutoSize = true;
            lblHint.Location = new Point(330, 657);
            Controls.Add(lblHint);
        }

        void BuildTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Opening += delegate { RebuildTrayMenu(); };

            tray = new NotifyIcon();
            tray.Icon = appSmallIcon;
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowFromTray();
            };
            RebuildTrayMenu();
        }

        void BuildFloatingClock()
        {
            floatWin = new HomeClockWindow();
            floatWin.OpenRequested += delegate { ShowFromTray(); };
            floatWin.HideRequested += delegate { chkFloating.Checked = false; };
            floatWin.PositionSaved += delegate
            {
                settings.FloatX = floatWin.Left;
                settings.FloatY = floatWin.Top;
                settings.FloatW = floatWin.Width;
                settings.FloatH = floatWin.Height;
                settings.Save();
            };
        }

        void ShowFloatingClock()
        {
            if (settings.FloatW >= floatWin.MinimumSize.Width && settings.FloatH >= floatWin.MinimumSize.Height)
                floatWin.Size = new Size(settings.FloatW, settings.FloatH);
            Point p = new Point(settings.FloatX, settings.FloatY);
            Rectangle r = new Rectangle(p, floatWin.Size);
            bool onScreen = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r) &&
                                                       s.WorkingArea.Contains(r.Left + 20, r.Top + 10));
            if (!onScreen)
            {
                // Default: bottom-right corner, just above the taskbar.
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                p = new Point(wa.Right - floatWin.Width - 12, wa.Bottom - floatWin.Height - 12);
            }
            floatWin.Location = p;
            floatWin.UpdateTime(homeTz);
            floatWin.Show();
            floatWin.KeepOnTop();
        }

        void RebuildTrayMenu()
        {
            trayMenu.Items.Clear();

            ToolStripMenuItem header = new ToolStripMenuItem(currentTz != null ? currentTz.DisplayName : "Time zone");
            header.Enabled = false;
            trayMenu.Items.Add(header);

            if (homeTz != null)
            {
                DateTime ht = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, homeTz);
                ToolStripMenuItem home = new ToolStripMenuItem("Home (" + ShortName(homeTz) + "): " + ht.ToString("HH:mm  ddd d MMM"));
                home.Enabled = false;
                trayMenu.Items.Add(home);
            }
            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripLabel quickLabel = new ToolStripLabel("Quick switch (recent):");
            quickLabel.ForeColor = Color.Gray;
            trayMenu.Items.Add(quickLabel);

            bool any = false;
            foreach (string id in recent)
            {
                TimeZoneInfo tz = FindZone(id);
                if (tz == null) continue;
                any = true;
                ToolStripMenuItem item = new ToolStripMenuItem(tz.DisplayName);
                item.Checked = currentTz != null && tz.Id == currentTz.Id;
                TimeZoneInfo captured = tz;
                item.Click += delegate
                {
                    if (currentTz == null || captured.Id != currentTz.Id) ApplyZone(captured, false);
                };
                trayMenu.Items.Add(item);
            }
            if (!any)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("(none yet - change a zone from the window)");
                none.Enabled = false;
                trayMenu.Items.Add(none);
            }

            if (homeTz != null && currentTz != null && homeTz.Id != currentTz.Id)
            {
                ToolStripMenuItem back = new ToolStripMenuItem("Switch back to home zone");
                TimeZoneInfo h = homeTz;
                back.Click += delegate { ApplyZone(h, false); };
                trayMenu.Items.Add(back);
            }

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem homeTray = new ToolStripMenuItem("Show home time in tray icon");
            homeTray.Checked = settings.HomeInTray;
            homeTray.Click += delegate { chkHomeTray.Checked = !chkHomeTray.Checked; };
            trayMenu.Items.Add(homeTray);

            ToolStripMenuItem floating = new ToolStripMenuItem("Show floating home clock");
            floating.Checked = settings.FloatingClock;
            floating.Click += delegate { chkFloating.Checked = !chkFloating.Checked; };
            trayMenu.Items.Add(floating);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem open = new ToolStripMenuItem("Open Time Zone Changer");
            open.Font = new Font(open.Font, FontStyle.Bold);
            open.Click += delegate { ShowFromTray(); };
            trayMenu.Items.Add(open);

            ToolStripMenuItem startup = new ToolStripMenuItem("Start with Windows");
            startup.Checked = IsStartupEnabled();
            startup.Click += delegate { chkStartup.Checked = !IsStartupEnabled(); };
            trayMenu.Items.Add(startup);

            ToolStripMenuItem winSettings = new ToolStripMenuItem("Windows Date && Time settings");
            winSettings.Click += delegate { OpenWindowsSettings(); };
            trayMenu.Items.Add(winSettings);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitApp(); };
            trayMenu.Items.Add(exit);
        }

        // ---------------- Helpers ----------------

        static string FormatOffset(TimeSpan o)
        {
            return "UTC" + (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm");
        }

        // "(UTC+08:00) Beijing, Chongqing, Hong Kong" -> "Beijing"
        public static string ShortName(TimeZoneInfo tz)
        {
            string n = tz.DisplayName;
            int p = n.IndexOf(") ");
            if (p >= 0) n = n.Substring(p + 2);
            int c = n.IndexOf(',');
            if (c > 0) n = n.Substring(0, c);
            if (n.Length > 22) n = n.Substring(0, 21) + "…";
            return n.Trim();
        }

        TimeZoneInfo FindZone(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return allZones.FirstOrDefault(z => z.Id == id);
        }

        // ---------------- Logic ----------------

        void RefreshCurrent()
        {
            TimeZoneInfo.ClearCachedData();
            currentTz = TimeZoneInfo.Local;
            DateTime now = DateTime.Now;

            string dst;
            if (!currentTz.SupportsDaylightSavingTime) dst = "Not observed";
            else if (currentTz.IsDaylightSavingTime(now)) dst = "Yes (active)";
            else dst = "Supported (not active)";

            lblName.Text = currentTz.DisplayName;
            lblDetails.Text = "ID: " + currentTz.Id + "\r\n" +
                "Current offset: " + FormatOffset(currentTz.GetUtcOffset(now)) +
                "   |   Daylight saving: " + dst;

            OnSelectionChanged();
            UpdateTrayText();
        }

        void UpdateTrayText()
        {
            if (tray == null || currentTz == null) return;
            DateTime utc = DateTime.UtcNow;
            string text = "System: " + TimeZoneInfo.ConvertTimeFromUtc(utc, currentTz).ToString("HH:mm") +
                          " " + ShortName(currentTz);
            if (homeTz != null)
                text += "\nHome: " + TimeZoneInfo.ConvertTimeFromUtc(utc, homeTz).ToString("HH:mm") +
                        " " + ShortName(homeTz);
            if (text.Length > 63) text = text.Substring(0, 63); // NotifyIcon limit
            tray.Text = text;
        }

        void UpdateClock()
        {
            if (loading) return;
            DateTime now = DateTime.Now;
            lblClock.Text = "Local time:  " + now.ToString("yyyy-MM-dd  HH:mm:ss  (dddd)");

            TimeZoneInfo sel = lstZones.SelectedItem as TimeZoneInfo;
            if (sel != null)
            {
                DateTime t = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, sel);
                lblPreview.Text = "Time in selected zone: " + t.ToString("yyyy-MM-dd HH:mm:ss") +
                                  "  (" + FormatOffset(sel.GetUtcOffset(DateTime.UtcNow)) + ")";
            }
            else lblPreview.Text = "";

            if (homeTz != null)
            {
                DateTime ht = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, homeTz);
                lblHomeTime.Text = "Home: " + ht.ToString("HH:mm:ss  ddd d MMM");
            }

            if (floatWin != null && floatWin.Visible)
            {
                floatWin.UpdateTime(homeTz);
                floatWin.KeepOnTop();
            }

            UpdateTrayIcon();
            if (now.Second == 0 || trayIconKey.Length == 0) UpdateTrayText();
        }

        // Draws the home time ("HH" over "mm") as the tray icon.
        void UpdateTrayIcon()
        {
            if (tray == null) return;
            if (!settings.HomeInTray || homeTz == null)
            {
                if (trayIconKey != "app") { SetTrayIcon(appSmallIcon, IntPtr.Zero); trayIconKey = "app"; }
                return;
            }
            DateTime t = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, homeTz);
            string key = t.ToString("HHmm");
            if (key == trayIconKey) return;
            trayIconKey = key;
            using (Bitmap bmp = RenderTimeIcon(t.ToString("HH"), t.ToString("mm")))
            {
                IntPtr h = bmp.GetHicon();
                SetTrayIcon(Icon.FromHandle(h), h);
            }
            UpdateTrayText();
        }

        void SetTrayIcon(Icon icon, IntPtr handle)
        {
            tray.Icon = icon;
            if (dynIconHandle != IntPtr.Zero) Native.DestroyIcon(dynIconHandle);
            dynIconHandle = handle;
        }

        public static Bitmap RenderTimeIcon(string top, string bottom)
        {
            const int s = 32;
            Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using (GraphicsPath path = new GraphicsPath())
                {
                    int r = 7, d = r * 2;
                    path.AddArc(0, 0, d, d, 180, 90);
                    path.AddArc(s - 1 - d, 0, d, d, 270, 90);
                    path.AddArc(s - 1 - d, s - 1 - d, d, d, 0, 90);
                    path.AddArc(0, s - 1 - d, d, d, 90, 90);
                    path.CloseFigure();
                    using (Brush b = new SolidBrush(Color.FromArgb(255, 0, 90, 190))) g.FillPath(b, path);
                }

                using (Font f = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(top, f, Brushes.White, new RectangleF(0, 0, s, s / 2f + 1), sf);
                    g.DrawString(bottom, f, Brushes.White, new RectangleF(0, s / 2f - 1, s, s / 2f + 1), sf);
                }
            }
            return bmp;
        }

        void UpdateZoneList()
        {
            string filter = txtSearch.Text.Trim();
            lstZones.BeginUpdate();
            lstZones.Items.Clear();
            foreach (TimeZoneInfo z in allZones)
            {
                if (filter.Length == 0 ||
                    z.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    z.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    z.StandardName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lstZones.Items.Add(z);
                    if (filter.Length == 0 && currentTz != null && z.Id == currentTz.Id)
                        lstZones.SelectedItem = z;
                }
            }
            lstZones.EndUpdate();
            OnSelectionChanged();
        }

        void OnSelectionChanged()
        {
            if (btnApply == null) return;
            TimeZoneInfo sel = lstZones.SelectedItem as TimeZoneInfo;
            btnApply.Enabled = sel != null && currentTz != null && sel.Id != currentTz.Id;
            UpdateClock();
        }

        void ApplySelected()
        {
            TimeZoneInfo sel = lstZones.SelectedItem as TimeZoneInfo;
            if (sel == null) return;
            DialogResult r = MessageBox.Show(this,
                "Change system time zone to:\r\n\r\n" + sel.DisplayName + "?",
                "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) ApplyZone(sel, true);
        }

        void ApplyZone(TimeZoneInfo tz, bool fromWindow)
        {
            string error = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "tzutil.exe"), "/s \"" + tz.Id + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                        error = output.Trim().Length > 0 ? output.Trim() : "tzutil exit code " + p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error != null)
            {
                MessageBox.Show(fromWindow ? this : null,
                    "Failed to change time zone.\r\n\r\n" + error +
                    "\r\n\r\nTry running this program as Administrator.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            AddRecent(tz.Id);
            RefreshCurrent();
            UpdateClock();

            if (fromWindow)
                MessageBox.Show(this, "Time zone changed to:\r\n" + currentTz.DisplayName,
                    "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                tray.ShowBalloonTip(3000, "Time zone changed", currentTz.DisplayName, ToolTipIcon.Info);
        }

        void OnSystemTimeChanged(object sender, EventArgs e)
        {
            // Time zone changed externally (e.g., in Windows Settings).
            try
            {
                BeginInvoke(new Action(delegate
                {
                    RefreshCurrent();
                    UpdateClock();
                }));
            }
            catch { }
        }

        // ---------------- Recent zones ----------------

        void LoadRecent()
        {
            try
            {
                if (File.Exists(RecentFile))
                    recent = File.ReadAllLines(RecentFile).Where(l => l.Trim().Length > 0)
                                 .Distinct().Take(MaxRecent).ToList();
            }
            catch { recent = new List<string>(); }

            if (recent.Count == 0) recent.Add(TimeZoneInfo.Local.Id);
        }

        void AddRecent(string id)
        {
            recent.Remove(id);
            recent.Insert(0, id);
            if (recent.Count > MaxRecent) recent = recent.Take(MaxRecent).ToList();
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                File.WriteAllLines(RecentFile, recent.ToArray());
            }
            catch { }
        }

        // ---------------- Startup with Windows ----------------
        // The app requires admin rights. Windows won't auto-start elevated apps from the
        // registry "Run" key, so startup uses a Task Scheduler logon task with
        // "Run with highest privileges" instead (no UAC prompt at logon).

        const string TaskName = "TimeZoneChanger";
        static bool? startupEnabled;

        static int RunSchtasks(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        static bool IsStartupEnabled()
        {
            if (!startupEnabled.HasValue)
                startupEnabled = RunSchtasks("/query /tn \"" + TaskName + "\"") == 0;
            return startupEnabled.Value;
        }

        // Called once at launch: migrate the old registry Run entry and keep the task's exe path current.
        static void InitStartup()
        {
            bool hadRunKey = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null && key.GetValue(AppName) != null)
                    {
                        key.DeleteValue(AppName);
                        hadRunKey = true;
                    }
                }
            }
            catch { }

            if (hadRunKey || IsStartupEnabled()) CreateStartupTask(false);
        }

        static bool CreateStartupTask(bool showErrors)
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Start Time Zone Changer in the system tray at logon (as administrator).</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Esc(user) + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + Esc(user) + "</UserId>" +
                "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +   // never kill the tray app
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + Esc(Application.ExecutablePath) + "</Command>" +
                "<Arguments>/tray</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";

            string tmp = Path.Combine(Path.GetTempPath(), "TimeZoneChanger_task.xml");
            int code;
            try
            {
                File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
                code = RunSchtasks("/create /tn \"" + TaskName + "\" /xml \"" + tmp + "\" /f");
            }
            catch { code = -1; }
            finally { try { File.Delete(tmp); } catch { } }

            startupEnabled = code == 0;
            if (code != 0 && showErrors)
                MessageBox.Show("Could not create the startup task (schtasks exit code " + code + ").\r\n" +
                    "Make sure the app is running as administrator.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            return code == 0;
        }

        static string Esc(string s)
        {
            return System.Security.SecurityElement.Escape(s);
        }

        static void SetStartup(bool enable)
        {
            if (enable == IsStartupEnabled()) return;
            if (enable)
            {
                CreateStartupTask(true);
            }
            else
            {
                int code = RunSchtasks("/delete /tn \"" + TaskName + "\" /f");
                startupEnabled = code != 0 && RunSchtasks("/query /tn \"" + TaskName + "\"") == 0;
                if (startupEnabled.Value)
                    MessageBox.Show("Could not remove the startup task (schtasks exit code " + code + ").",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------- Window / tray behaviour ----------------

        static void OpenWindowsSettings()
        {
            try { Process.Start("ms-settings:dateandtime"); } catch { }
        }

        static Icon LoadAppIcon(Size size)
        {
            try
            {
                using (Stream s = typeof(MainForm).Assembly.GetManifestResourceStream("TimeZoneChanger.app.ico"))
                    if (s != null) return new Icon(s, size);
            }
            catch { }
            return SystemIcons.Application;
        }

        public void ShowFromTray()
        {
            allowVisible = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!allowVisible)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            txtSearch.Focus();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(3000, "Still running",
                        "Time Zone Changer is in the system tray. Right-click the icon to quick-switch or exit.",
                        ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        void ExitApp()
        {
            reallyExit = true;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.TimeChanged -= OnSystemTimeChanged;
                if (timer != null) timer.Dispose();
                if (floatWin != null) floatWin.Dispose();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (trayMenu != null) trayMenu.Dispose();
                if (dynIconHandle != IntPtr.Zero) { Native.DestroyIcon(dynIconHandle); dynIconHandle = IntPtr.Zero; }
            }
            base.Dispose(disposing);
        }
    }
}