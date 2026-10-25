# WifiSigLevel — Design

## Goal
A small standalone Windows desktop app that lists the Wi-Fi networks currently in range and shows each network's signal level, refreshing automatically.

## Decisions
- Native WinForms GUI, C#, built with the .NET Framework 4 compiler already on Windows (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). No installs; output is a single `WifiSigLevel.exe`.
- Data source: Windows WLAN API (`wlanapi.dll`) via P/Invoke, not parsing `netsh` text (localized, fragile).
- "Signal level" = the signal-quality percentage (0-100) Windows reports. No dBm.

## Layout
- `WifiSigLevel.cs` — single source file.
- `build.ps1` — compiles to `WifiSigLevel.exe` (`/target:winexe`, references System.Windows.Forms and System.Drawing).

### Units
- `WlanApi` — P/Invoke wrappers: open handle, enumerate interfaces, `WlanScan`, `WlanGetAvailableNetworkList`. Returns `List<WifiNetwork>`. No UI knowledge.
- `WifiNetwork` — SSID, signal quality (0-100), security (open/secured + auth type if available), connected flag, interface name.
- `MainForm` — `ListView` (details view), Refresh button, auto-refresh checkbox (every 5 s, default on), status bar, clickable column sorting.

## UI
Columns: SSID, Signal (text bar + percentage, e.g. `████░ 80%`), Security, Connected. Default sort: signal descending. Hidden SSIDs shown as "(hidden)". Duplicate SSIDs collapsed to the strongest entry.

## Data flow
Refresh runs on a background thread: `WlanScan`, wait ~2 s, read network list, marshal results to the UI thread. UI never blocks; a refresh requested while one is in flight is skipped.

## Error handling
Status bar message, no crash, retry on next tick, for: no Wi-Fi adapter, WLAN service not running, Wi-Fi off, scan failure. If `WlanScan` fails or is rate-limited, show the cached list from `WlanGetAvailableNetworkList`.

## Testing
- `WifiSigLevel.exe --selftest` runs the scan + sort/dedupe and prints results to the console without opening a window.
- Manual verification: compare output against `netsh wlan show networks mode=bssid` on the dev machine.

## Out of scope
dBm values, channel graphs, connecting to networks, BSSID-level detail.
