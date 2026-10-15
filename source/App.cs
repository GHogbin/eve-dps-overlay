using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Serialization;

[assembly: AssemblyTitle("EVE DPS")]
[assembly: AssemblyDescription("A local combat log DPS overlay for EVE Online")]
[assembly: AssemblyVersion("0.1.0.0")]

namespace EveDps
{
    public sealed class Preferences
    {
        public string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"EVE\logs\Gamelogs");
        public string Pilot = "";
        public int WindowSeconds = 10, IdleSeconds = 30, OpacityPercent = 96, ScalePercent = 100;
        public int X = 80, Y = 100;
        // Portable configuration stays beside the app; no installation or registry writes.
        public static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.xml"); } }
        public static Preferences Load()
        {
            try
            {
                using (StreamReader reader = new StreamReader(FilePath))
                {
                    Preferences p = (Preferences)new XmlSerializer(typeof(Preferences)).Deserialize(reader);
                    p.WindowSeconds = Math.Max(1, Math.Min(120, p.WindowSeconds));
                    p.IdleSeconds = Math.Max(15, Math.Min(300, p.IdleSeconds));
                    p.OpacityPercent = Math.Max(50, Math.Min(100, p.OpacityPercent));
                    p.ScalePercent = Math.Max(100, Math.Min(175, p.ScalePercent));
                    if (string.IsNullOrWhiteSpace(p.Folder)) p.Folder = new Preferences().Folder;
                    return p;
                }
            }
            catch (IOException) { return new Preferences(); }
            catch (InvalidOperationException) { return new Preferences(); }
            catch (UnauthorizedAccessException) { return new Preferences(); }
        }
        public bool Save()
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(FilePath)) new XmlSerializer(typeof(Preferences)).Serialize(writer, this);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        public static Icon MakeIcon()
        {
            using (Bitmap bitmap = new Bitmap(64, 64))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(14, 23, 34)); g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Color.FromArgb(92, 230, 204), 5))
                    g.DrawLines(pen, new Point[] { new Point(7, 35), new Point(20, 35), new Point(27, 16), new Point(36, 49), new Point(43, 28), new Point(57, 28) });
                IntPtr handle = bitmap.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(handle).Clone(); DestroyIcon(handle); return icon;
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Native.SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length >= 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
                if (args.Length >= 2 && args[0] == "--write-icon")
                {
                    using (Icon icon = Native.MakeIcon())
                    using (FileStream stream = File.Create(args[1])) icon.Save(stream);
                    return 0;
                }
                bool render = args.Length >= 2 && args[0] == "--render-preview";
                using (Overlay form = new Overlay(render ? new Preferences() : Preferences.Load(), render))
                {
                    if (render)
                    {
                        form.CreateControl(); form.SeedPreview();
                        using (Bitmap image = new Bitmap(form.Width, form.Height))
                        { form.DrawToBitmap(image, form.ClientRectangle); image.Save(args[1], ImageFormat.Png); }
                        return 0;
                    }
                    if (args.Contains("--demo")) form.SetDemo(true);
                    Application.Run(form);
                }
                return 0;
            }
            catch (Exception error)
            {
                if (args.Length >= 2 && args[0].StartsWith("--"))
                    File.WriteAllText(args[1] + ".error.txt", error.ToString());
                else MessageBox.Show(error.Message, "EVE DPS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    public sealed class Overlay : Form
    {
        const int W = 420, H = 432;
        static readonly Color Background = Color.FromArgb(12, 18, 27), Panel = Color.FromArgb(19, 28, 40),
            Accent = Color.FromArgb(97, 233, 201), Muted = Color.FromArgb(136, 154, 174), TextColor = Color.FromArgb(237, 244, 250),
            Border = Color.FromArgb(41, 57, 74), Amber = Color.FromArgb(244, 191, 99);
        readonly Preferences prefs;
        readonly CombatMeter meter = new CombatMeter();
        readonly LogMonitor monitor;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly bool preview;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly Icon appIcon;
        readonly ToolTip tooltip = new ToolTip();
        bool locked, demo, dragging, hotkeysAvailable = true;
        Point dragStart;
        DateTime nextDemo = DateTime.MinValue, previewNow;
        string notice = "";
        DateTime noticeUntil = DateTime.MinValue;
        readonly Random random = new Random(8);
        int hover = -1;
        readonly Rectangle[] buttons = { new Rectangle(310, 8, 30, 28), new Rectangle(344, 8, 28, 28), new Rectangle(378, 8, 30, 28),
            new Rectangle(20, 365, 184, 33), new Rectangle(214, 365, 186, 33) };

        public Overlay(Preferences preferences, bool isPreview)
        {
            prefs = preferences; preview = isPreview;
            Text = "EVE DPS"; BackColor = Background; ForeColor = TextColor;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true; TopMost = true; ShowInTaskbar = false;
            appIcon = Native.MakeIcon(); Icon = appIcon;
            ApplyPreferences();
            Point position = new Point(prefs.X, prefs.Y);
            Rectangle location = new Rectangle(position, Size);
            if (!Screen.AllScreens.Any(delegate(Screen screen) { return screen.WorkingArea.IntersectsWith(location); })) position = new Point(80, 100);
            Location = position;
            monitor = new LogMonitor(prefs.Folder, prefs.Pilot);
            monitor.SessionChanged += delegate { meter.Reset(); };
            monitor.Damage += delegate(DamageEvent e) { meter.Add(e); };
            menu = new ContextMenuStrip { BackColor = Panel, ForeColor = TextColor, ShowImageMargin = false };
            menu.Items.Add("Show / hide overlay   Ctrl+Alt+H", null, delegate { ToggleVisibility(); });
            menu.Items.Add("Lock / unlock   Ctrl+Alt+D", null, delegate { ToggleLock(); });
            menu.Items.Add("Settings...", null, delegate { OpenSettings(); });
            menu.Items.Add("New fight", null, delegate { meter.Reset(); Invalidate(); });
            menu.Items.Add("Demo data on / off", null, delegate { SetDemo(!demo); });
            menu.Items.Add("Export fight summary...", null, delegate { Export(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Close(); });
            if (!preview)
            {
                tray = new NotifyIcon { Icon = appIcon, Text = "EVE DPS - waiting for combat", Visible = true, ContextMenuStrip = menu };
                tray.DoubleClick += delegate { if (!Visible) Show(); else if (locked) ToggleLock(); };
                timer.Interval = 250;
                timer.Tick += delegate { TickMeter(); };
                Shown += delegate
                {
                    bool lockKey = Native.RegisterHotKey(Handle, 1, 0x4003, (uint)Keys.D);
                    bool showKey = Native.RegisterHotKey(Handle, 2, 0x4003, (uint)Keys.H);
                    hotkeysAvailable = lockKey && showKey;
                    if (!hotkeysAvailable) Notify("Hotkey unavailable - use the tray icon");
                    timer.Start(); TickMeter();
                };
            }
            MouseMove += MovePointer;
            MouseLeave += delegate { hover = -1; Invalidate(); };
            MouseDown += DownPointer;
            MouseUp += delegate { if (dragging) { dragging = false; Capture = false; SavePosition(); } };
            FormClosing += delegate
            {
                timer.Stop(); Native.UnregisterHotKey(Handle, 1); Native.UnregisterHotKey(Handle, 2);
                if (!preview) SavePosition();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
            };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                if (m.WParam.ToInt32() == 1) ToggleLock();
                if (m.WParam.ToInt32() == 2) ToggleVisibility();
            }
            base.WndProc(ref m);
        }
        void ApplyPreferences()
        {
            Size = new Size(W * prefs.ScalePercent / 100, H * prefs.ScalePercent / 100);
            Opacity = prefs.OpacityPercent / 100.0;
            meter.IdleSeconds = prefs.IdleSeconds;
        }
        void SavePosition() { prefs.X = Left; prefs.Y = Top; if (!prefs.Save()) Notify("Cannot save settings beside the app"); }
        void Notify(string message) { notice = message; noticeUntil = DateTime.UtcNow.AddSeconds(9); Invalidate(); }
        void ToggleVisibility() { if (Visible) Hide(); else Show(); }
        void ToggleLock()
        {
            locked = !locked;
            int style = Native.GetWindowLong(Handle, -20);
            Native.SetWindowLong(Handle, -20, locked ? style | 0x20 | 0x80000 : style & ~0x20);
            Invalidate();
        }
        public void SetDemo(bool enabled)
        {
            demo = enabled; meter.Reset(); nextDemo = DateTime.MinValue;
            if (!demo) monitor.Restart();
            Invalidate();
        }
        public void SeedPreview()
        {
            demo = true; previewNow = DateTime.UtcNow;
            for (int i = 0; i < 82; i++)
            {
                DateTime t = previewNow.AddSeconds(i - 81);
                meter.Add(new DamageEvent { Time = t, Outgoing = true, Amount = 620 + 280 * Math.Sin(i * 0.19) + 180 * Math.Sin(i * 0.57), Target = "Guristas battleship" });
                meter.Add(new DamageEvent { Time = t, Outgoing = false, Amount = 80 + 30 * Math.Cos(i * 0.25) });
            }
        }
        void TickMeter()
        {
            DateTime now = DateTime.UtcNow;
            if (demo && now >= nextDemo)
            {
                nextDemo = now.AddSeconds(1);
                meter.Add(new DamageEvent { Time = now, Outgoing = true, Amount = 450 + random.Next(500), Target = "Demo target" });
                meter.Add(new DamageEvent { Time = now, Outgoing = false, Amount = random.Next(60, 180) });
            }
            if (!demo) monitor.Poll(now);
            meter.Prune(now);
            if (tray != null) tray.Text = "EVE DPS | " + (demo ? "DEMO | " : "") + meter.Rolling(now, prefs.WindowSeconds, true).ToString("N0") + " DPS";
            Invalidate();
        }
        Point Canvas(Point p) { return new Point(p.X * W / ClientSize.Width, p.Y * H / ClientSize.Height); }
        void DownPointer(object sender, MouseEventArgs e)
        {
            if (locked) return;
            if (e.Button == MouseButtons.Right) { menu.Show(this, e.Location); return; }
            if (e.Button != MouseButtons.Left) return;
            Point point = Canvas(e.Location);
            int index = Array.FindIndex(buttons, delegate(Rectangle r) { return r.Contains(point); });
            if (index == 0) { menu.Show(this, e.Location); return; }
            if (index == 1) { Hide(); return; }
            if (index == 2) { Close(); return; }
            if (index == 3) { meter.Reset(); Invalidate(); return; }
            if (index == 4) { ToggleLock(); return; }
            dragging = true; dragStart = e.Location; Capture = true;
        }
        void MovePointer(object sender, MouseEventArgs e)
        {
            if (dragging) { Location = new Point(Left + e.X - dragStart.X, Top + e.Y - dragStart.Y); return; }
            Point p = Canvas(e.Location);
            int next = Array.FindIndex(buttons, delegate(Rectangle r) { return r.Contains(p); });
            if (next != hover)
            {
                hover = next; Cursor = next >= 0 ? Cursors.Hand : Cursors.SizeAll; Invalidate();
                string[] tips = { "Menu: settings, demo, export", "Hide to system tray (Ctrl+Alt+H)", "Exit EVE DPS", "Clear the current fight", "Click-through lock. Unlock with Ctrl+Alt+D or the tray icon." };
                tooltip.SetToolTip(this, next >= 0 ? tips[next] : "Drag to move the overlay");
            }
        }
        void OpenSettings()
        {
            using (SettingsDialog dialog = new SettingsDialog(prefs, monitor.Sessions))
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                bool sourceChanged = prefs.Folder != dialog.Folder || prefs.Pilot != dialog.Pilot;
                prefs.Folder = dialog.Folder; prefs.Pilot = dialog.Pilot;
                prefs.WindowSeconds = dialog.WindowSeconds; prefs.IdleSeconds = dialog.IdleSeconds;
                prefs.OpacityPercent = dialog.OpacityPercent; prefs.ScalePercent = dialog.ScalePercent;
                ApplyPreferences();
                if (sourceChanged) { monitor.Folder = prefs.Folder; monitor.PilotFilter = prefs.Pilot; monitor.Restart(); meter.Reset(); }
                if (!prefs.Save()) Notify("Cannot save settings beside the app");
                Invalidate();
            }
        }
        void Export()
        {
            if (!meter.HasFight) { Notify("Start a fight before exporting"); return; }
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = "eve-dps-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                try
                {
                    string line = string.Join(",", new string[] { demo ? "demo" : "combat-log", meter.First.Value.ToString("o"), meter.Last.Value.ToString("o"),
                        meter.Duration.ToString("F1", CultureInfo.InvariantCulture), meter.TotalOut.ToString("F1", CultureInfo.InvariantCulture),
                        meter.TotalIn.ToString("F1", CultureInfo.InvariantCulture), meter.Average.ToString("F1", CultureInfo.InvariantCulture),
                        meter.Hits.ToString(), meter.Misses.ToString() });
                    File.WriteAllText(dialog.FileName, "source,start_utc,last_activity_utc,duration_seconds,damage_dealt,damage_received,average_dps,hits,misses\r\n" + line + "\r\n");
                    Notify("Fight summary exported");
                }
                catch (IOException ex) { MessageBox.Show(ex.Message, "Cannot export"); }
                catch (UnauthorizedAccessException ex) { MessageBox.Show(ex.Message, "Cannot export"); }
            }
        }

        static void TextAt(Graphics g, string text, float size, Color color, float x, float y, FontStyle style, float width)
        {
            using (Font font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel))
            using (Brush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, font, brush, new RectangleF(x, y, width, size * 1.7f), format);
        }
        static void Fill(Graphics g, Color color, float x, float y, float w, float h)
        { using (Brush brush = new SolidBrush(color)) g.FillRectangle(brush, x, y, w, h); }
        static void Rule(Graphics g, Color color, float x1, float y1, float x2, float y2)
        { using (Pen pen = new Pen(color)) g.DrawLine(pen, x1, y1, x2, y2); }
        static string Number(double n) { return n.ToString("N0", CultureInfo.CurrentCulture); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics;
            g.ScaleTransform(ClientSize.Width / (float)W, ClientSize.Height / (float)H);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            DateTime now = preview ? previewNow : DateTime.UtcNow;
            Fill(g, Background, 0, 0, W, H);
            using (Pen p = new Pen(Border)) g.DrawRectangle(p, 0, 0, W - 1, H - 1);
            Fill(g, Accent, 0, 0, W, 2);
            using (Pen p = new Pen(Accent, 1.7f)) g.DrawLines(p, new PointF[] { new PointF(20, 25), new PointF(26, 25), new PointF(30, 16), new PointF(35, 31), new PointF(40, 21), new PointF(47, 21) });
            TextAt(g, "EVE  /  DPS", 13, TextColor, 56, 14, FontStyle.Bold, 160);
            Color stateColor = demo ? Amber : meter.Active(now) ? Accent : Muted;
            TextAt(g, demo ? "DEMO" : meter.Active(now) ? "LIVE" : "READY", 10, stateColor, 239, 17, FontStyle.Bold, 60);
            for (int i = 0; i < 3; i++) if (hover == i) Fill(g, i == 2 ? Color.FromArgb(75, 38, 46) : Panel, buttons[i].X, buttons[i].Y, buttons[i].Width, buttons[i].Height);
            TextAt(g, "\u00b7\u00b7\u00b7", 19, Muted, 313, 8, FontStyle.Bold, 30);
            Rule(g, Muted, 352, 24, 364, 24);
            Rule(g, Muted, 387, 18, 397, 28); Rule(g, Muted, 397, 18, 387, 28);
            Rule(g, Border, 20, 44, 400, 44);

            string pilot = demo ? "SIMULATED COMBAT" : monitor.Session != null ? monitor.Session.Pilot : "WAITING FOR EVE";
            TextAt(g, pilot, 12, Muted, 20, 57, FontStyle.Regular, 285);
            TextAt(g, prefs.WindowSeconds + "s window", 11, Muted, 321, 58, FontStyle.Regular, 85);
            TextAt(g, "LIVE DPS", 11, Accent, 20, 86, FontStyle.Bold, 150);
            TextAt(g, Number(meter.Rolling(now, prefs.WindowSeconds, true)), 58, TextColor, 15, 96, FontStyle.Bold, 330);
            TextAt(g, "applied damage / second", 11, Muted, 21, 163, FontStyle.Regular, 280);

            Fill(g, Panel, 20, 190, 184, 68); Fill(g, Panel, 214, 190, 186, 68);
            TextAt(g, "FIGHT AVERAGE", 10, Muted, 32, 200, FontStyle.Bold, 160);
            TextAt(g, Number(meter.Average), 27, TextColor, 30, 216, FontStyle.Bold, 160);
            TextAt(g, "DAMAGE DEALT", 10, Muted, 226, 200, FontStyle.Bold, 160);
            TextAt(g, Number(meter.TotalOut), 27, TextColor, 224, 216, FontStyle.Bold, 170);

            TextAt(g, "DPS / LAST 60 SECONDS", 9, Muted, 20, 272, FontStyle.Bold, 190);
            TextAt(g, "IN  " + Number(meter.Rolling(now, prefs.WindowSeconds, false)) + " DPS", 10, Muted, 291, 271, FontStyle.Regular, 114);
            DrawGraph(g, now);
            TimeSpan duration = TimeSpan.FromSeconds(meter.Duration);
            string time = ((int)duration.TotalMinutes).ToString("00") + ":" + duration.Seconds.ToString("00");
            TextAt(g, time + "  /  " + (meter.HasFight ? meter.Active(now) ? "fight in progress" : "fight complete" : "no combat yet"), 10, Muted, 20, 341, FontStyle.Regular, 205);
            TextAt(g, meter.Hits + " hits  /  " + meter.Misses + " misses", 10, Muted, 271, 341, FontStyle.Regular, 135);

            for (int i = 3; i < 5; i++)
            {
                Rectangle r = buttons[i]; Fill(g, hover == i ? Color.FromArgb(35, 52, 67) : Panel, r.X, r.Y, r.Width, r.Height);
                using (Pen p = new Pen(i == 4 && locked ? Accent : Border)) g.DrawRectangle(p, r);
            }
            TextAt(g, "NEW FIGHT", 10, TextColor, 76, 375, FontStyle.Bold, 125);
            TextAt(g, locked ? "LOCKED  /  Ctrl+Alt+D" : "LOCK OVERLAY", 10, locked ? Accent : TextColor, locked ? 243 : 257, 375, FontStyle.Bold, 150);
            string footer = demo ? "Demo data  /  Menu > Demo to return to live logs" :
                meter.HasFight ? (string.IsNullOrEmpty(meter.LastTarget) ? "Reading combat activity" : "Last target: " + meter.LastTarget) :
                monitor.Session != null && monitor.Status.StartsWith("Watching ") ? "Watching logs  /  waiting for new damage" : monitor.Status;
            if (!demo && !monitor.Status.StartsWith("Watching ")) footer = monitor.Status;
            if (now < noticeUntil) footer = notice;
            TextAt(g, footer, 9, demo ? Amber : Muted, 20, 412, FontStyle.Regular, 385);
        }
        void DrawGraph(Graphics g, DateTime now)
        {
            const int left = 21, right = 399, top = 293, bottom = 332;
            double[] values = Enumerable.Range(0, 61).Select(delegate(int i) { return meter.Rolling(now.AddSeconds(i - 60), prefs.WindowSeconds, true); }).ToArray();
            double max = Math.Max(1, values.Max() * 1.15);
            Rule(g, Border, left, bottom, right, bottom); Rule(g, Color.FromArgb(26, 39, 53), left, top + 19, right, top + 19);
            if (values.Max() <= 0) { TextAt(g, "Waiting for outgoing damage", 10, Muted, 124, 305, FontStyle.Regular, 230); return; }
            PointF[] points = values.Select(delegate(double value, int i) { return new PointF(left + (right - left) * i / 60f, bottom - (float)(value / max) * (bottom - top)); }).ToArray();
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddLines(points); path.AddLine(points[60], new PointF(right, bottom)); path.AddLine(new PointF(right, bottom), new PointF(left, bottom)); path.CloseFigure();
                using (LinearGradientBrush brush = new LinearGradientBrush(new Point(left, top), new Point(left, bottom), Color.FromArgb(65, Accent), Color.FromArgb(3, Accent))) g.FillPath(brush, path);
            }
            using (Pen pen = new Pen(Accent, 1.6f)) g.DrawLines(pen, points);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); tooltip.Dispose(); menu.Dispose(); appIcon.Dispose(); }
            base.Dispose(disposing);
        }
    }

    public sealed class SettingsDialog : Form
    {
        readonly TextBox folder;
        readonly ComboBox pilot;
        readonly NumericUpDown window, idle, opacity, scale;
        public string Folder { get { return folder.Text.Trim(); } }
        public string Pilot { get { return pilot.SelectedIndex == 0 ? "" : pilot.Text.Trim(); } }
        public int WindowSeconds { get { return (int)window.Value; } }
        public int IdleSeconds { get { return (int)idle.Value; } }
        public int OpacityPercent { get { return (int)opacity.Value; } }
        public int ScalePercent { get { return (int)scale.Value; } }
        public SettingsDialog(Preferences prefs, List<LogSession> sessions)
        {
            Text = "EVE DPS settings"; Size = new Size(534, 535); MinimumSize = Size; MaximumSize = Size;
            StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; TopMost = true; Font = new Font("Segoe UI", 10);
            Label title = LabelAt("COMBAT LOG SOURCE", 20, 18, 440); title.Font = new Font(Font, FontStyle.Bold);
            LabelAt("EVE Gamelogs folder", 20, 54, 440);
            folder = new TextBox { Left = 20, Top = 80, Width = 376, Text = prefs.Folder }; Controls.Add(folder);
            Button browse = new Button { Left = 405, Top = 78, Width = 89, Height = 29, Text = "Browse..." }; Controls.Add(browse);
            browse.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "Choose EVE/logs/Gamelogs", SelectedPath = folder.Text })
                    if (dialog.ShowDialog() == DialogResult.OK) folder.Text = dialog.SelectedPath;
            };
            LabelAt("Character (automatic follows the newest session)", 20, 121, 474);
            pilot = new ComboBox { Left = 20, Top = 148, Width = 474, DropDownStyle = ComboBoxStyle.DropDownList };
            pilot.Items.Add("Automatic - most recent session");
            foreach (string name in sessions.Select(delegate(LogSession s) { return s.Pilot; }).Concat(new string[] { prefs.Pilot }).Where(delegate(string s) { return !string.IsNullOrWhiteSpace(s); }).Distinct().OrderBy(delegate(string s) { return s; })) pilot.Items.Add(name);
            pilot.SelectedIndex = string.IsNullOrEmpty(prefs.Pilot) ? 0 : Math.Max(0, pilot.Items.IndexOf(prefs.Pilot)); Controls.Add(pilot);
            window = NumberAt("Live DPS window (seconds)", 201, 1, 120, prefs.WindowSeconds);
            idle = NumberAt("New fight after idle (seconds)", 243, 15, 300, prefs.IdleSeconds);
            opacity = NumberAt("Overlay opacity (%)", 285, 50, 100, prefs.OpacityPercent);
            scale = NumberAt("Overlay size (%)", 327, 100, 175, prefs.ScalePercent); scale.Increment = 25;
            Label help = LabelAt("Use EVE in borderless/windowed mode with English combat logs.\nCtrl+Alt+D locks click-through. Ctrl+Alt+H hides/shows.\nTray icon menu provides the same controls.", 20, 379, 480); help.Height = 70; help.Font = new Font("Segoe UI", 9);
            Button save = new Button { Text = "Save", Left = 385, Top = 454, Width = 109, Height = 31 };
            Button cancel = new Button { Text = "Cancel", Left = 268, Top = 454, Width = 109, Height = 31, DialogResult = DialogResult.Cancel };
            Controls.Add(save); Controls.Add(cancel); AcceptButton = save; CancelButton = cancel;
            save.Click += delegate
            {
                if (!Directory.Exists(Folder)) { MessageBox.Show(this, "Choose an existing EVE Gamelogs folder.", "Folder not found"); return; }
                DialogResult = DialogResult.OK; Close();
            };
        }
        Label LabelAt(string text, int x, int y, int width)
        { Label label = new Label { Left = x, Top = y, Width = width, Height = 25, Text = text }; Controls.Add(label); return label; }
        NumericUpDown NumberAt(string label, int y, int min, int max, int value)
        {
            LabelAt(label, 20, y + 3, 340);
            NumericUpDown input = new NumericUpDown { Left = 384, Top = y, Width = 110, Minimum = min, Maximum = max, Value = value };
            Controls.Add(input); return input;
        }
    }
}
