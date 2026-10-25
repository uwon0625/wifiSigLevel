using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class WifiNetwork
{
    public string Ssid;
    public int Signal;
    public string Security;
    public bool Connected;
    public string Interface;
    public int Min;
    public int Max;
}

static class NetworkLogic
{
    public const string HiddenName = "(hidden)";

    public static int ClampSignal(int q)
    {
        return q < 0 ? 0 : (q > 100 ? 100 : q);
    }

    public static string SignalBar(int q)
    {
        q = ClampSignal(q);
        int filled = (q + 10) / 20;
        return new string('█', filled) + new string('░', 5 - filled) + " " + q + "%";
    }

    // "min% [──●───] max%": the marker shows where the current level sits in the observed range.
    public static string RangeBar(int min, int max, int cur)
    {
        const int width = 9;
        min = ClampSignal(min);
        max = ClampSignal(max);
        cur = ClampSignal(cur);
        int pos = max <= min ? width / 2 : (int)Math.Round((double)(cur - min) * (width - 1) / (max - min));
        if (pos < 0) pos = 0;
        if (pos > width - 1) pos = width - 1;
        return min + "% [" + new string('─', pos) + "●" + new string('─', width - 1 - pos) + "] " + max + "%";
    }

    // 0..1 position of cur within [min, max]; a single sample sits in the middle.
    public static double RangeFraction(int min, int max, int cur)
    {
        if (max <= min) return 0.5;
        double f = (double)(ClampSignal(cur) - min) / (max - min);
        return f < 0 ? 0 : (f > 1 ? 1 : f);
    }

    public static Color SignalColor(int q)
    {
        if (q >= 70) return Color.FromArgb(46, 160, 67);
        if (q >= 40) return Color.FromArgb(230, 160, 20);
        return Color.FromArgb(214, 69, 65);
    }

    public static string AuthName(uint algo, bool secured)
    {
        switch (algo)
        {
            case 1: return "Open";
            case 2: return "WEP";
            case 3: return "WPA-Enterprise";
            case 4: return "WPA-Personal";
            case 6: return "WPA2-Enterprise";
            case 7: return "WPA2-Personal";
            case 8: return "WPA3-Enterprise 192";
            case 9: return "WPA3-Personal";
            case 10: return "OWE";
            case 11: return "WPA3-Enterprise";
            default: return secured ? "Secured" : "Open";
        }
    }

    public static string DisplaySsid(string ssid)
    {
        return string.IsNullOrEmpty(ssid) ? HiddenName : ssid;
    }

    static WifiNetwork Copy(WifiNetwork n)
    {
        return new WifiNetwork
        {
            Ssid = DisplaySsid(n.Ssid),
            Signal = ClampSignal(n.Signal),
            Security = n.Security,
            Connected = n.Connected,
            Interface = n.Interface
        };
    }

    public static List<WifiNetwork> Dedupe(IEnumerable<WifiNetwork> input)
    {
        var best = new Dictionary<string, WifiNetwork>();
        foreach (var n in input)
        {
            var c = Copy(n);
            WifiNetwork cur;
            if (!best.TryGetValue(c.Ssid, out cur))
            {
                best[c.Ssid] = c;
                continue;
            }
            bool connected = cur.Connected || c.Connected;
            if (c.Signal > cur.Signal) best[c.Ssid] = c;
            best[c.Ssid].Connected = connected;
        }
        return best.Values
            .OrderByDescending(x => x.Signal)
            .ThenBy(x => x.Ssid, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

class ScanResult
{
    public List<WifiNetwork> Networks = new List<WifiNetwork>();
    public string Error;
}

static class WlanApi
{
    const int InterfaceInfoSize = 532;
    const int NetworkSize = 628;
    const uint ErrorServiceNotActive = 1062;

    [DllImport("wlanapi.dll")]
    static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr client);
    [DllImport("wlanapi.dll")]
    static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    static extern uint WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")]
    static extern uint WlanScan(IntPtr client, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    static extern uint WlanGetAvailableNetworkList(IntPtr client, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")]
    static extern void WlanFreeMemory(IntPtr p);

    static IntPtr At(IntPtr p, int offset) { return new IntPtr(p.ToInt64() + offset); }

    public static ScanResult Scan(int settleMs)
    {
        var result = new ScanResult();
        IntPtr client;
        uint negotiated;
        uint rc = WlanOpenHandle(2, IntPtr.Zero, out negotiated, out client);
        if (rc == ErrorServiceNotActive) { result.Error = "WLAN service is not running"; return result; }
        if (rc != 0) { result.Error = "Cannot open WLAN API (error " + rc + ")"; return result; }
        try
        {
            var ifaces = new List<KeyValuePair<Guid, string>>();
            IntPtr ifList;
            rc = WlanEnumInterfaces(client, IntPtr.Zero, out ifList);
            if (rc != 0) { result.Error = "Cannot list Wi-Fi adapters (error " + rc + ")"; return result; }
            try
            {
                int count = Marshal.ReadInt32(ifList);
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = At(ifList, 8 + i * InterfaceInfoSize);
                    Guid g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
                    string desc = Marshal.PtrToStringUni(At(p, 16)) ?? "";
                    ifaces.Add(new KeyValuePair<Guid, string>(g, desc));
                }
            }
            finally { WlanFreeMemory(ifList); }

            if (ifaces.Count == 0) { result.Error = "No Wi-Fi adapter found"; return result; }

            foreach (var f in ifaces)
            {
                Guid g = f.Key;
                WlanScan(client, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // failure/rate-limit: fall back to cached list
            }
            Thread.Sleep(settleMs);

            string lastError = null;
            foreach (var f in ifaces)
            {
                Guid g = f.Key;
                IntPtr netList;
                rc = WlanGetAvailableNetworkList(client, ref g, 0, IntPtr.Zero, out netList);
                if (rc != 0) { lastError = "Cannot read networks (error " + rc + ")"; continue; }
                try
                {
                    int count = Marshal.ReadInt32(netList);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr p = At(netList, 8 + i * NetworkSize);
                        int len = Marshal.ReadInt32(At(p, 512));
                        if (len < 0) len = 0;
                        if (len > 32) len = 32;
                        var bytes = new byte[len];
                        if (len > 0) Marshal.Copy(At(p, 516), bytes, 0, len);
                        bool secured = Marshal.ReadInt32(At(p, 608)) != 0;
                        uint auth = (uint)Marshal.ReadInt32(At(p, 612));
                        int flags = Marshal.ReadInt32(At(p, 620));
                        result.Networks.Add(new WifiNetwork
                        {
                            Ssid = NetworkLogic.DisplaySsid(Encoding.UTF8.GetString(bytes)),
                            Signal = NetworkLogic.ClampSignal(Marshal.ReadInt32(At(p, 604))),
                            Security = NetworkLogic.AuthName(auth, secured),
                            Connected = (flags & 1) != 0,
                            Interface = f.Value
                        });
                    }
                }
                finally { WlanFreeMemory(netList); }
            }
            if (result.Networks.Count == 0 && lastError != null) result.Error = lastError;
        }
        finally { WlanCloseHandle(client, IntPtr.Zero); }
        return result;
    }
}

class MainForm : Form
{
    ListView list;
    Button refreshBtn;
    CheckBox autoChk;
    TextBox intervalBox;
    const int DefaultIntervalSec = 5;
    const int MinIntervalSec = 1;
    const int MaxIntervalSec = 3600;
    StatusStrip status;
    ToolStripStatusLabel statusLabel;
    System.Windows.Forms.Timer timer;
    Font boldFont;
    List<WifiNetwork> current = new List<WifiNetwork>();
    Dictionary<string, int[]> ranges = new Dictionary<string, int[]>(); // SSID -> {min, max} seen this session
    int sortCol = 1;
    bool sortDesc = true;
    int busy;

    public MainForm()
    {
        Text = "Wi-Fi Signal Levels";
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(880, 460);
        MinimumSize = new Size(700, 300);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;

        list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false,
            OwnerDraw = true, BorderStyle = BorderStyle.None, HideSelection = false, BackColor = Color.White
        };
        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(list, true, null);
        list.SmallImageList = new ImageList { ImageSize = new Size(1, 34) }; // sets the row height
        list.Columns.Add("SSID", 210);
        list.Columns.Add("Signal", 150);
        list.Columns.Add("Security", 150);
        list.Columns.Add("Connected", 90);
        list.Columns.Add("Session range (min - max)", 260);
        list.ColumnClick += OnColumnClick;
        list.DrawColumnHeader += OnDrawHeader;
        list.DrawItem += delegate { }; // cells are drawn in DrawSubItem
        list.DrawSubItem += OnDrawSubItem;
        boldFont = new Font(list.Font, FontStyle.Bold);

        var top = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.FromArgb(247, 248, 250) };
        top.Paint += delegate(object s, PaintEventArgs e)
        {
            using (var p = new Pen(Color.FromArgb(225, 228, 232)))
                e.Graphics.DrawLine(p, 0, top.Height - 1, top.Width, top.Height - 1);
        };
        refreshBtn = new Button
        {
            Text = "Refresh", Left = 12, Top = 11, Width = 92, Height = 30,
            FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, UseVisualStyleBackColor = false
        };
        refreshBtn.FlatAppearance.BorderSize = 0;
        refreshBtn.Click += delegate { RefreshNetworks(); };
        autoChk = new CheckBox { Text = "Auto-refresh every", Left = 124, Top = 16, Width = 140, Checked = true };
        autoChk.CheckedChanged += delegate { timer.Enabled = autoChk.Checked; };
        intervalBox = new TextBox { Text = DefaultIntervalSec.ToString(), Left = 268, Top = 14, Width = 44, TextAlign = HorizontalAlignment.Center };
        intervalBox.Leave += delegate { ApplyInterval(); };
        intervalBox.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            ApplyInterval();
        };
        var secLabel = new Label { Text = "seconds", Left = 318, Top = 17, AutoSize = true };
        top.Controls.Add(refreshBtn);
        top.Controls.Add(autoChk);
        top.Controls.Add(intervalBox);
        top.Controls.Add(secLabel);

        status = new StatusStrip();
        statusLabel = new ToolStripStatusLabel("Ready");
        status.Items.Add(statusLabel);

        Controls.Add(list);
        Controls.Add(top);
        Controls.Add(status);

        timer = new System.Windows.Forms.Timer { Interval = DefaultIntervalSec * 1000, Enabled = true };
        timer.Tick += delegate { RefreshNetworks(); };

        Shown += delegate { RefreshNetworks(); };
        FormClosing += delegate { timer.Stop(); };
    }

    // Invalid input reverts the box to the interval currently in effect.
    void ApplyInterval()
    {
        int sec;
        if (int.TryParse(intervalBox.Text.Trim(), out sec) && sec >= MinIntervalSec && sec <= MaxIntervalSec)
            timer.Interval = sec * 1000;
        intervalBox.Text = (timer.Interval / 1000).ToString();
    }

    void RefreshNetworks()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        refreshBtn.Enabled = false;
        statusLabel.Text = "Scanning...";
        ThreadPool.QueueUserWorkItem(delegate
        {
            ScanResult r;
            try { r = WlanApi.Scan(2000); }
            catch (Exception ex) { r = new ScanResult(); r.Error = ex.Message; }
            try { BeginInvoke((MethodInvoker)delegate { ShowResult(r); }); }
            catch (InvalidOperationException) { } // form already closed
        });
    }

    void ShowResult(ScanResult r)
    {
        busy = 0;
        refreshBtn.Enabled = true;
        if (r.Error != null && r.Networks.Count == 0)
        {
            statusLabel.Text = r.Error + " - showing last results";
            return;
        }
        current = NetworkLogic.Dedupe(r.Networks);
        foreach (var n in current)
        {
            int[] mm;
            if (!ranges.TryGetValue(n.Ssid, out mm)) { mm = new[] { n.Signal, n.Signal }; ranges[n.Ssid] = mm; }
            if (n.Signal < mm[0]) mm[0] = n.Signal;
            if (n.Signal > mm[1]) mm[1] = n.Signal;
            n.Min = mm[0];
            n.Max = mm[1];
        }
        Render();
        statusLabel.Text = current.Count == 0
            ? "No networks found (is Wi-Fi turned on?)"
            : string.Format("{0} networks - updated {1:HH:mm:ss}", current.Count, DateTime.Now);
    }

    void Render()
    {
        IEnumerable<WifiNetwork> rows = current;
        switch (sortCol)
        {
            case 0: rows = sortDesc ? rows.OrderByDescending(n => n.Ssid, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(n => n.Ssid, StringComparer.OrdinalIgnoreCase); break;
            case 1: rows = sortDesc ? rows.OrderByDescending(n => n.Signal) : rows.OrderBy(n => n.Signal); break;
            case 2: rows = sortDesc ? rows.OrderByDescending(n => n.Security) : rows.OrderBy(n => n.Security); break;
            case 4: rows = sortDesc ? rows.OrderByDescending(n => n.Max - n.Min) : rows.OrderBy(n => n.Max - n.Min); break;
            default: rows = sortDesc ? rows.OrderByDescending(n => n.Connected) : rows.OrderBy(n => n.Connected); break;
        }
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var n in rows)
        {
            var item = new ListViewItem(new[] { n.Ssid, NetworkLogic.SignalBar(n.Signal), n.Security, n.Connected ? "Yes" : "", NetworkLogic.RangeBar(n.Min, n.Max, n.Signal) });
            item.Tag = n;
            list.Items.Add(item);
        }
        list.EndUpdate();
    }

    static readonly Color Accent = Color.FromArgb(0, 120, 212);
    static readonly Color TextColor = Color.FromArgb(32, 32, 32);
    static readonly Color MutedColor = Color.FromArgb(110, 110, 110);
    static readonly Color TrackColor = Color.FromArgb(222, 226, 230);

    void OnDrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(Color.FromArgb(247, 248, 250))) g.FillRectangle(b, e.Bounds);
        using (var p = new Pen(Color.FromArgb(225, 228, 232)))
            g.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        string text = e.Header.Text + (e.ColumnIndex == sortCol ? (sortDesc ? "  ▼" : "  ▲") : "");
        var r = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, e.Bounds.Width - 14, e.Bounds.Height);
        TextRenderer.DrawText(g, text, boldFont, r, MutedColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    void OnDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
    {
        var n = (WifiNetwork)e.Item.Tag;
        var g = e.Graphics;
        Color bg = e.Item.Selected ? Color.FromArgb(204, 228, 247)
            : n.Connected ? Color.FromArgb(237, 246, 255)
            : (e.ItemIndex % 2 == 1 ? Color.FromArgb(250, 251, 252) : Color.White);
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var cell = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, e.Bounds.Width - 16, e.Bounds.Height);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        Font font = n.Connected ? boldFont : list.Font;
        int midY = cell.Top + cell.Height / 2;

        switch (e.ColumnIndex)
        {
            case 1:
                {
                    var track = new Rectangle(cell.Left, midY - 4, 80, 8);
                    FillRound(g, TrackColor, track);
                    int w = Math.Max(8, track.Width * n.Signal / 100);
                    FillRound(g, NetworkLogic.SignalColor(n.Signal), new Rectangle(track.Left, track.Top, w, track.Height));
                    var tr = new Rectangle(track.Right + 8, cell.Top, cell.Right - track.Right - 8, cell.Height);
                    TextRenderer.DrawText(g, n.Signal + "%", font, tr, TextColor, flags);
                    break;
                }
            case 3:
                TextRenderer.DrawText(g, n.Connected ? "✓ Yes" : "", boldFont, cell, Color.FromArgb(46, 160, 67), flags);
                break;
            case 4:
                {
                    const int labelW = 44;
                    var lr = new Rectangle(cell.Left, cell.Top, labelW, cell.Height);
                    var rr = new Rectangle(cell.Right - labelW, cell.Top, labelW, cell.Height);
                    TextRenderer.DrawText(g, n.Min + "%", list.Font, lr, MutedColor, flags);
                    TextRenderer.DrawText(g, n.Max + "%", list.Font, rr, MutedColor, flags | TextFormatFlags.Right);
                    int x0 = lr.Right + 4, x1 = rr.Left - 4;
                    if (x1 - x0 < 20) break;
                    FillRound(g, TrackColor, new Rectangle(x0, midY - 2, x1 - x0, 4));
                    int mx = x0 + (int)Math.Round(NetworkLogic.RangeFraction(n.Min, n.Max, n.Signal) * (x1 - x0));
                    var dot = new Rectangle(mx - 7, midY - 7, 14, 14);
                    using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, dot);
                    dot.Inflate(-2, -2);
                    using (var b = new SolidBrush(NetworkLogic.SignalColor(n.Signal))) g.FillEllipse(b, dot);
                    break;
                }
            default:
                TextRenderer.DrawText(g, e.SubItem.Text, font, cell, e.ColumnIndex == 0 ? TextColor : MutedColor, flags);
                break;
        }
    }

    static void FillRound(Graphics g, Color c, Rectangle r)
    {
        using (var path = new GraphicsPath())
        using (var b = new SolidBrush(c))
        {
            int d = r.Height;
            path.AddArc(r.Left, r.Top, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
            path.CloseFigure();
            g.FillPath(b, path);
        }
    }

    void OnColumnClick(object sender, ColumnClickEventArgs e)
    {
        if (e.Column == sortCol) sortDesc = !sortDesc;
        else { sortCol = e.Column; sortDesc = (e.Column == 1 || e.Column == 3); }
        Render();
    }
}

static class SelfTest
{
    static int failures;

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int pid);

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    static WifiNetwork Net(string ssid, int sig, bool conn)
    {
        return new WifiNetwork { Ssid = ssid, Signal = sig, Security = "WPA2-Personal", Connected = conn, Interface = "wlan0" };
    }

    public static int Run()
    {
        AttachConsole(-1);
        string full = "█████";
        string empty = "░░░░░";

        Check("ClampSignal below 0", NetworkLogic.ClampSignal(-5) == 0);
        Check("ClampSignal above 100", NetworkLogic.ClampSignal(150) == 100);
        Check("ClampSignal in range", NetworkLogic.ClampSignal(42) == 42);

        Check("SignalBar 0", NetworkLogic.SignalBar(0) == empty + " 0%");
        Check("SignalBar 80", NetworkLogic.SignalBar(80) == "████░ 80%");
        Check("SignalBar 100", NetworkLogic.SignalBar(100) == full + " 100%");
        Check("SignalBar out of range is clamped", NetworkLogic.SignalBar(999) == full + " 100%");
        Check("SignalBar negative is clamped", NetworkLogic.SignalBar(-20) == empty + " 0%");

        Check("RangeBar at min", NetworkLogic.RangeBar(20, 80, 20) == "20% [●────────] 80%");
        Check("RangeBar at max", NetworkLogic.RangeBar(20, 80, 80) == "20% [────────●] 80%");
        Check("RangeBar middle", NetworkLogic.RangeBar(20, 80, 50) == "20% [────●────] 80%");
        Check("RangeBar single sample", NetworkLogic.RangeBar(60, 60, 60) == "60% [────●────] 60%");

        Check("DisplaySsid empty", NetworkLogic.DisplaySsid("") == "(hidden)");
        Check("DisplaySsid null", NetworkLogic.DisplaySsid(null) == "(hidden)");
        Check("DisplaySsid normal", NetworkLogic.DisplaySsid("x5") == "x5");

        Check("AuthName WPA2-Personal", NetworkLogic.AuthName(7, true) == "WPA2-Personal");
        Check("AuthName open", NetworkLogic.AuthName(1, false) == "Open");
        Check("AuthName unknown secured", NetworkLogic.AuthName(99, true) == "Secured");
        Check("AuthName unknown open", NetworkLogic.AuthName(99, false) == "Open");

        var d = NetworkLogic.Dedupe(new[] {
            Net("home", 40, false), Net("home", 70, false), Net("cafe", 90, false),
            Net("", 10, false), Net("", 30, false)
        });
        Check("Dedupe collapses duplicates (3 rows)", d.Count == 3);
        Check("Dedupe keeps strongest", d.First(n => n.Ssid == "home").Signal == 70);
        Check("Dedupe collapses hidden", d.Count(n => n.Ssid == "(hidden)") == 1);
        Check("Dedupe sorts signal desc", d[0].Ssid == "cafe" && d[1].Ssid == "home" && d[2].Ssid == "(hidden)");

        var c = NetworkLogic.Dedupe(new[] { Net("home", 40, true), Net("home", 70, false) });
        Check("Dedupe preserves connected flag on strongest", c.Count == 1 && c[0].Signal == 70 && c[0].Connected);

        Check("Dedupe empty input", NetworkLogic.Dedupe(new WifiNetwork[0]).Count == 0);

        ScanResult scan = null;
        try { scan = WlanApi.Scan(2000); } catch (Exception ex) { Console.WriteLine("scan threw: " + ex); }
        Check("Scan returns without throwing", scan != null);
        if (scan != null)
        {
            Console.WriteLine("SCAN error=" + (scan.Error ?? "none"));
            foreach (var n in NetworkLogic.Dedupe(scan.Networks))
                Console.WriteLine(string.Format("NET {0} | {1}% | {2} | connected={3}", n.Ssid, n.Signal, n.Security, n.Connected));
            Check("Scan has networks or an error", scan.Networks.Count > 0 || scan.Error != null);
        }

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }
}
