# WifiSigLevel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a standalone Windows WinForms app that lists in-range Wi-Fi networks with their signal level and auto-refreshes.

**Architecture:** One C# source file (`WifiSigLevel.cs`) compiled by the Windows-bundled .NET Framework 4 `csc.exe` into a single exe. Pure display/sort/dedupe logic (`NetworkLogic`) is separated from the `wlanapi.dll` P/Invoke layer (`WlanApi`) and from the UI (`MainForm`). A `--selftest` switch runs assertions and a live scan from the console.

**Tech Stack:** C# 5 (the bundled `csc.exe` does NOT support C# 6+: no `$"..."`, no `?.`, no `nameof`, no expression-bodied members), WinForms, `wlanapi.dll` via P/Invoke, PowerShell build script.

**Spec:** `docs/superpowers/specs/2026-10-25-wifi-sig-level-design.md`

**Note:** `C:\dl\proj\wifiSigLevel` is not a git repository, so there are no commit steps.

## Global Constraints

- Compiler: `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`; output is a single `WifiSigLevel.exe`; no installs.
- Data source: Windows WLAN API (`wlanapi.dll`), not `netsh` parsing.
- Signal level = signal-quality percentage 0-100; no dBm.
- Columns: SSID, Signal (text bar + percentage, e.g. `████░ 80%`), Security, Connected. Default sort: signal descending.
- Hidden SSIDs shown as `(hidden)`; duplicate SSIDs collapsed to the strongest entry.
- Auto-refresh every 5 s, default on; Refresh button; status bar; clickable column sorting.
- Refresh runs on a background thread, UI never blocks, overlapping refreshes are skipped.
- Errors go to the status bar, never crash, retry on next tick.
- All non-ASCII characters in the source must be written as `\uXXXX` escapes (old `csc` misreads BOM-less UTF-8).
- Out of scope: dBm, channel graphs, connecting to networks, BSSID detail.

## Review Focus

1. Signal quality outside 0-100 from the API: clamp, never negative/overlong bar. (Task 1 test)
2. Same SSID visible on multiple adapters/APs, or several hidden networks: one row each, strongest signal, connected flag preserved. (Task 1 test)
3. SSID length field > 32 or non-UTF-8 bytes: no exception or overrun. (Task 2 clamps length; Task 4 manual check of code path by review)
4. Refresh clicked repeatedly / timer firing during a scan: only one scan at a time. (Task 3 busy guard; Task 4 manual)
5. Wi-Fi turned off, no adapter, or window closed mid-scan: status message or silent ignore, no crash. (Task 4 manual)

---

## File Structure

- Create: `C:\dl\proj\wifiSigLevel\build.ps1` — compiles the exe.
- Create: `C:\dl\proj\wifiSigLevel\WifiSigLevel.cs` — everything else, in this order: `WifiNetwork`, `NetworkLogic`, `ScanResult`, `WlanApi`, `MainForm`, `SelfTest`, `Program`.

---

### Task 1: Build script, pure logic, self-test harness

**Files:**
- Create: `build.ps1`
- Create: `WifiSigLevel.cs`

**Interfaces:**
- Produces:
  - `class WifiNetwork { string Ssid; int Signal; string Security; bool Connected; string Interface; }` (public fields)
  - `static class NetworkLogic`: `const string HiddenName = "(hidden)"`; `int ClampSignal(int)`; `string SignalBar(int)`; `string AuthName(uint algo, bool secured)`; `string DisplaySsid(string)`; `List<WifiNetwork> Dedupe(IEnumerable<WifiNetwork>)`
  - `static class SelfTest { static int Run(); }` returns process exit code (0 = all pass)

- [ ] **Step 1: Create `build.ps1`**

```powershell
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /out:"$PSScriptRoot\WifiSigLevel.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "$PSScriptRoot\WifiSigLevel.cs"
exit $LASTEXITCODE
```

- [ ] **Step 2: Create `WifiSigLevel.cs` with the tests first (logic classes are stubs that fail)**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

class WifiNetwork
{
    public string Ssid;
    public int Signal;
    public string Security;
    public bool Connected;
    public string Interface;
}

static class NetworkLogic
{
    public const string HiddenName = "(hidden)";
    public static int ClampSignal(int q) { throw new NotImplementedException(); }
    public static string SignalBar(int q) { throw new NotImplementedException(); }
    public static string AuthName(uint algo, bool secured) { throw new NotImplementedException(); }
    public static string DisplaySsid(string ssid) { throw new NotImplementedException(); }
    public static List<WifiNetwork> Dedupe(IEnumerable<WifiNetwork> input) { throw new NotImplementedException(); }
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
        string full = "\u2588\u2588\u2588\u2588\u2588";
        string empty = "\u2591\u2591\u2591\u2591\u2591";

        Check("ClampSignal below 0", NetworkLogic.ClampSignal(-5) == 0);
        Check("ClampSignal above 100", NetworkLogic.ClampSignal(150) == 100);
        Check("ClampSignal in range", NetworkLogic.ClampSignal(42) == 42);

        Check("SignalBar 0", NetworkLogic.SignalBar(0) == empty + " 0%");
        Check("SignalBar 80", NetworkLogic.SignalBar(80) == "\u2588\u2588\u2588\u2588\u2591 80%");
        Check("SignalBar 100", NetworkLogic.SignalBar(100) == full + " 100%");
        Check("SignalBar out of range is clamped", NetworkLogic.SignalBar(999) == full + " 100%");
        Check("SignalBar negative is clamped", NetworkLogic.SignalBar(-20) == empty + " 0%");

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
        return 1;
    }
}
```

- [ ] **Step 3: Build and run to verify the tests fail**

Run (PowerShell, in `C:\dl\proj\wifiSigLevel`):
```powershell
.\build.ps1
$p = Start-Process .\WifiSigLevel.exe -ArgumentList '--selftest' -RedirectStandardOutput out.txt -RedirectStandardError err.txt -NoNewWindow -Wait -PassThru; Get-Content out.txt, err.txt; $p.ExitCode
```
Expected: build succeeds; run fails with `NotImplementedException` text in `err.txt`/crash, exit code non-zero.

- [ ] **Step 4: Implement `NetworkLogic`** — replace the stub class with:

```csharp
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
        return new string('\u2588', filled) + new string('\u2591', 5 - filled) + " " + q + "%";
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
```

- [ ] **Step 5: Build and run to verify all tests pass**

Run the same commands as Step 3.
Expected: `out.txt` shows every line `PASS ...`, final line `ALL PASSED`, exit code `0`.

---

### Task 2: WLAN API layer

**Files:**
- Modify: `WifiSigLevel.cs` (add `ScanResult` and `WlanApi` after `NetworkLogic`; add live scan to `SelfTest.Run`)

**Interfaces:**
- Consumes: `WifiNetwork`, `NetworkLogic.DisplaySsid/ClampSignal/AuthName` (Task 1)
- Produces:
  - `class ScanResult { List<WifiNetwork> Networks; string Error; }`
  - `static class WlanApi { static ScanResult Scan(int settleMs); }` — never throws for expected failures; sets `Error` instead. Networks are raw (not deduped).

Struct offsets used (from `WLAN_INTERFACE_INFO_LIST` / `WLAN_AVAILABLE_NETWORK_LIST`): list header is 8 bytes (count, index). `WLAN_INTERFACE_INFO` is 532 bytes (GUID at 0, UTF-16 description at 16). `WLAN_AVAILABLE_NETWORK` is 628 bytes: SSID length at 512, SSID bytes at 516, signal quality at 604, security-enabled at 608, default auth algorithm at 612, flags at 620 (bit 0 = connected).

- [ ] **Step 1: Add the failing self-test check** — in `SelfTest.Run`, immediately before the final `Console.WriteLine(failures == 0 ...`:

```csharp
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
```

- [ ] **Step 2: Run build to verify it fails**

Run: `.\build.ps1`
Expected: compile error `The name 'ScanResult'` / `'WlanApi' does not exist`.

- [ ] **Step 3: Add `ScanResult` and `WlanApi`** (add `using System.Text; using System.Threading;` to the top of the file):

```csharp
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
```

- [ ] **Step 4: Build and run self-test**

Run the Task 1 Step 3 commands.
Expected: build succeeds; `out.txt` includes `SCAN error=none`, a `NET x5 | 80% | WPA2-Personal | connected=...` style line (signal near what `netsh wlan show networks mode=bssid` reports), `PASS Scan returns without throwing`, `ALL PASSED`, exit code 0.

---

### Task 3: Main window

**Files:**
- Modify: `WifiSigLevel.cs` (add `using System.Drawing; using System.Windows.Forms;` at top; add `MainForm` before `SelfTest`; replace `Program.Main`)

**Interfaces:**
- Consumes: `WlanApi.Scan(int)`, `ScanResult`, `NetworkLogic.Dedupe/SignalBar`, `WifiNetwork`
- Produces: `class MainForm : Form` (no public API)

- [ ] **Step 1: Add `MainForm`**

```csharp
class MainForm : Form
{
    ListView list;
    Button refreshBtn;
    CheckBox autoChk;
    StatusStrip status;
    ToolStripStatusLabel statusLabel;
    System.Windows.Forms.Timer timer;
    Font boldFont;
    List<WifiNetwork> current = new List<WifiNetwork>();
    int sortCol = 1;
    bool sortDesc = true;
    int busy;

    public MainForm()
    {
        Text = "Wi-Fi Signal Levels";
        ClientSize = new Size(600, 380);
        StartPosition = FormStartPosition.CenterScreen;

        list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        list.Columns.Add("SSID", 220);
        list.Columns.Add("Signal", 150);
        list.Columns.Add("Security", 130);
        list.Columns.Add("Connected", 80);
        list.ColumnClick += OnColumnClick;
        boldFont = new Font(list.Font, FontStyle.Bold);

        var top = new Panel { Dock = DockStyle.Top, Height = 36 };
        refreshBtn = new Button { Text = "Refresh", Left = 8, Top = 6, Width = 80 };
        refreshBtn.Click += delegate { RefreshNetworks(); };
        autoChk = new CheckBox { Text = "Auto-refresh (5 s)", Left = 100, Top = 9, Width = 160, Checked = true };
        autoChk.CheckedChanged += delegate { timer.Enabled = autoChk.Checked; };
        top.Controls.Add(refreshBtn);
        top.Controls.Add(autoChk);

        status = new StatusStrip();
        statusLabel = new ToolStripStatusLabel("Ready");
        status.Items.Add(statusLabel);

        Controls.Add(list);
        Controls.Add(top);
        Controls.Add(status);

        timer = new System.Windows.Forms.Timer { Interval = 5000, Enabled = true };
        timer.Tick += delegate { RefreshNetworks(); };

        Shown += delegate { RefreshNetworks(); };
        FormClosing += delegate { timer.Stop(); };
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
            default: rows = sortDesc ? rows.OrderByDescending(n => n.Connected) : rows.OrderBy(n => n.Connected); break;
        }
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var n in rows)
        {
            var item = new ListViewItem(new[] { n.Ssid, NetworkLogic.SignalBar(n.Signal), n.Security, n.Connected ? "Yes" : "" });
            if (n.Connected) item.Font = boldFont;
            list.Items.Add(item);
        }
        list.EndUpdate();
    }

    void OnColumnClick(object sender, ColumnClickEventArgs e)
    {
        if (e.Column == sortCol) sortDesc = !sortDesc;
        else { sortCol = e.Column; sortDesc = (e.Column == 1 || e.Column == 3); }
        Render();
    }
}
```

- [ ] **Step 2: Replace `Program.Main`**

```csharp
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }
```

- [ ] **Step 3: Build and re-run self-test (no regression)**

Run: `.\build.ps1` then the Task 1 Step 3 run commands.
Expected: build succeeds with no warnings about C# features; `ALL PASSED`, exit code 0.

- [ ] **Step 4: Launch the GUI**

Run: `Start-Process .\WifiSigLevel.exe`
Expected: a "Wi-Fi Signal Levels" window opens, status shows "Scanning..." then "N networks - updated HH:mm:ss"; list shows `x5` with a bar and percentage (connected row in bold, "Yes").

---

### Task 4: Manual verification against netsh and failure modes

**Files:** none changed unless a defect is found (fix in `WifiSigLevel.cs` and re-run Tasks 1-3 verification).

- [ ] **Step 1: Compare with netsh**

Run: `netsh wlan show networks mode=bssid`
Expected: the SSIDs and signal percentages in the app match (allow a few % drift between scans).

- [ ] **Step 2: Column sorting**

Click each header: SSID sorts A-Z/Z-A on repeat clicks, Signal strongest/weakest, Security, Connected. Expected: list reorders each click, no flicker crash.

- [ ] **Step 3: Overlapping refresh (Review Focus 4)**

Click Refresh rapidly 5 times. Expected: button disables during the scan, one "Scanning..." cycle at a time, no errors.

- [ ] **Step 4: Wi-Fi off (Review Focus 5)**

Turn Wi-Fi off in Windows quick settings while the app is open. Expected within ~7 s: status bar shows an error or "No networks found (is Wi-Fi turned on?)" or keeps the last list with a message; no crash. Turn Wi-Fi back on; expected: list recovers on the next tick.

- [ ] **Step 5: Close mid-scan (Review Focus 5)**

Click Refresh, then immediately close the window. Expected: the process exits cleanly (check `Get-Process WifiSigLevel` returns nothing after ~3 s) with no error dialog.

- [ ] **Step 6: Auto-refresh toggle**

Untick "Auto-refresh". Expected: status timestamp stops changing; Refresh button still works. Re-tick; expected: updates resume every ~5 s.
