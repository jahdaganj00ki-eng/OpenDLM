# Feature parity with the commercial download manager

This document compares OpenDLM against the commercial download manager installed on
this machine at `C:\Program Files (x86)\Internet Download Manager`.

## How the reference was analysed

The comparison is based on the **installed settings surface**, not on the vendor's
marketing material. The settings live in `HKCU\Software\DownloadManager`, which holds
80 top-level values and 206 subkeys — the numbered entries `1`..`248` are the
per-file-type table, and the named subkeys are the configuration groups.

Registry keys were read only. No file of that installation was executed, decompiled
or copied, and no asset, icon, string table or help file from it appears in this
repository. Every feature below is implemented from public behaviour and written from
scratch.

One finding worth stating first: **that installation has no dark mode.** A recursive
search of its entire settings tree for `dark|theme|colou?r|skin` returns nothing. Its
interface is a classic light Win32 application. OpenDLM ships light, dark and
follow-Windows themes, so this is an addition rather than a parity item.

## Legend

| Mark | Meaning |
|---|---|
| Done | Implemented, and covered by a test where it is engine behaviour |
| Partial | Present, but a documented subset of the reference behaviour |
| N/A | Deliberately not reproduced — reason in the note |

## Download engine

| Reference setting / feature | Reference registry evidence | OpenDLM | Note |
|---|---|---|---|
| Multi-connection segments | `MaxConnectionsNumber`, `maxMC` | Done | 1–32 connections per file |
| Downloads at the same time | `Queue\FilesAtTheSameTime` | Done | 1–50 |
| Resume after interruption | engine | Done | Segment sidecar with `ETag` / `Size` / `Last-Modified` validation |
| Fallback to one connection | `SingleConnectionFallback` | Done | Server that answers 200 to a ranged request |
| Retry with backoff | `RetryCount`, `RetryDelaySeconds` | Done | Per segment and per download |
| Speed limit | `Scheduler\isLimitEnabled` | Done | Global token bucket across all connections |
| Verify received size | engine | Done | Optional |
| MD5 / SHA-1 checksums | engine | Done | Optional |
| Preallocated target file | engine | Done | Completion is a rename, not a copy |
| Clipboard monitoring | `MonitorUrlClipboard` | Done | Polled, single-line http(s) URLs with a file extension |
| Auto-start new downloads | `startImmediately` | Done | |
| Add to queue by default | engine | Done | `Downloads.AddToQueueByDefault` |
| Remember last save folder | `RememberLastSave` | Done | `General.RememberLastSave` |
| Duplicate link warning | `DuplLinksA`, `RememberDuplLinksA` | Partial | Warns each time; the "never ask again for this link" memory is not implemented |
| FTP transfers | `FtpPasive`, `UseFtpProxy` | **Missing** | OpenDLM is HTTP/HTTPS only today. Passive-mode and FTP proxy settings exist in the model but there is no FTP transfer path |
| FTP proxy protocol | `nProxyMode` | Partial | The switch exists; without FTP support it has no effect |

## Per-site rules

| Reference setting | Registry evidence | OpenDLM | Note |
|---|---|---|---|
| Do not use multipart for this site | `sites_sf` (`phncdn.com`) | Done | Host list, sub-domain aware, persisted, forced to one connection and tested |
| Per-site logins | `Passwords`, `sites_sf` | Done | DPAPI-protected, realm aware |

## Scheduling

| Reference setting | Registry evidence | OpenDLM | Note |
|---|---|---|---|
| Enable the scheduler | `Scheduler\isStartEnabled`, `isStopEnabled` | Done | |
| Start / stop time | `Scheduler\startTime`, `stopTime` | Done | Overnight windows supported |
| Days of week | `Scheduler\startDaysOfWeek`, `startDay` | Done | Bit mask |
| Exit when finished | `Scheduler\isExitIDMWhenDone` | Done | |
| Shut down when finished | `Scheduler\isTurnOffComputer`, `isForceTurnOff` | Done | 10–3600 s cancellable delay; `shutdown /a` cancels |
| Hang up the modem | `Scheduler\isHangUpModem` | Partial | Emits `rasdial /disconnect`; meaningful only for RAS links |
| Daily time ceiling | `Scheduler\isLimitEnabled`, `m_hours` | Done | Counted only while downloading, resets at midnight |
| Daily volume ceiling | `Scheduler\m_MBytes` | Done | |
| Warn on limit exceeded | `Scheduler\showLimitExceededWarning` | Done | Tray notification |
| Start delay | `Scheduler\isStartDilay` | N/A | Only the finish-action delay is implemented |

## Proxy

| Reference setting | Registry evidence | OpenDLM | Note |
|---|---|---|---|
| Proxy mode | `nProxyMode` | Done | None / system / manual / auto |
| Separate http / https / ftp switches | `UseHttpProxy`, `UseHttpsProxy`, `UseFtpProxy` | Done | Decided per request by a custom `IWebProxy` |
| Manual address, port, credentials | `ProxyPac`, `ProxyServers`, `nHttpPrChbSt` | Done | Password encrypted at rest |
| Host exception list | `ExceptionServers`, `PanelExceptionServers` | Done | Host or domain, sub-domain aware |
| Local bypass | `BypassProxyOnLocal` | Done | Loopback, private ranges and single-label hosts |
| SOCKS 4 / 5 | `UseSocks`, `SocksType` | Done | `socks4://` / `socks5://` URI, chosen by the setting |
| SOCKS 5 resolves names | `Socks5ProxyDNS` | Partial | .NET always sends the host name to a `socks5` proxy, so the flag is recorded but not independently switchable |
| SOCKS over DNS also for HTTP proxy | `Socks5ProxyDNS` | N/A | Not applicable outside SOCKS |
| PAC script | `ProxyPac`, `WBProxy` | Partial | The **system** PAC is honoured through the system proxy mode. A **custom** PAC URL is not evaluated — PAC evaluation would require embedding a JavaScript engine |
| Passive FTP | `FtpPasive` | N/A | No FTP path yet |

## Interface

| Reference setting | Registry evidence | OpenDLM | Note |
|---|---|---|---|
| Dark mode | (no key found) | Done | Not a parity item — the reference has none |
| Light / dark / follow Windows | — | Done | Switchable at runtime, no restart |
| Toolbar style | `ToolbarStyle` | Done | Icons and text / icons only / large icons |
| Large buttons | `LargeButtons` | Done | Via the large-icons toolbar style |
| Toolbar state | `ToolbarState_v5.11` | Partial | Order and width of toolbar buttons are fixed; the list view columns *are* reorderable |
| Category pane / details pane / status bar | `DwnlPanel`, `FoldersTree` | Done | All three toggle and persist |
| Window size and position | `windowPlacementV6` | Done | Validated against the current screen topology |
| Sort column and direction | `sortOrder` | Done | Persisted |
| Column list | `ListSettings` | Done | FileName, Size, Status, Downloaded, Speed, Time left, Connections, Date added, Save to, Queue, Referer, Last try, Description |
| "Parent" column | `ListSettings\Parent_wp` | N/A | Refers to a parent download when one item is split into several child downloads. OpenDLM has no such concept: every download is one file |
| Language / 40 translation files | `LanguageID`, `Languages\*.lng` | Partial | English only. Translations would have to be written from scratch — the reference's `.lng` files are not copied |
| Start dialog | `StartDlgShowing` | Done | |
| Complete dialog | `ComplDlgShowing`, `evDownloadComplete` | Done | Size, time taken, average speed, MD5, open file / folder |
| Progress dialog | engine | Done | Compact and full view, per-download window, hidden / compact / full |
| Tray icon | `TrayIcon` | Done | Icon drawn in code, not a shipped bitmap |
| Notifications on completion / failure | `evDownloadComplete`, `evDownloadFailed` | Done | Individually switchable |
| Queue started / finished notifications | `evQueueStarted`, `evQueueFinished` | Done | |
| Update check | `CheckUpdtVM`, `LstCheck` | N/A | OpenDLM has no updater; releases come from the GitHub Releases page |

## Browser integration

| Reference setting | Registry evidence | OpenDLM | Note |
|---|---|---|---|
| Take over browser downloads | `Extensions` | Done | Manifest V3 extension plus a native messaging host |
| Takeover file types | `Extensions` | Done | Editable, kept in step with the app over IPC |
| Context menu entries | `menuExt` (per browser and per action) | Partial | One switch for the OpenDLM entries rather than a toggle per browser and per action |
| Media sniffing | `DwnlPanel` type list | Partial | Detected in the page; the reference's editable list of sniffed types plus its subtitle formats is not configurable in OpenDLM |
| Force takeover key | `SpecialKeys\UseKeyToForce`, `AltF`, `CtrlF`, `ShiftF` | Partial | One modifier per action (`Alt` / `Ctrl` / `Shift` / `None`) instead of independent force and prevent modifiers per key |
| Prevent key | `SpecialKeys\UseKeyToPrevent`, `ShiftP`, `CtrlP`, `AltP` | Partial | Same |
| Insert to force, Delete to remove | `SpecialKeys\InsF`, `DelP` | Done | `Insert` opens the add dialog, `Delete` removes |
| React to mouse gestures | `SpecialKeys\CheckMouse` | Done | Setting exposed over IPC |
| Skip plain web pages | `SpecialKeys\SkipHtml` | Done | Setting exposed over IPC |
| Install detection for browsers | `FindApps` | N/A | The extension is loaded unpacked or from the Releases page; there is no browser discovery loop |
| Kernel network filter driver | `EnableDriver`, `idmtdi.sys`, `idmwfp.sys` | N/A | OpenDLM uses a browser extension instead. Shipping a kernel driver would require signing and materially increase the attack surface |
| Shell / Explorer integration | `rshext`, `IDMShellExt.dll` | N/A | No shell extension DLL |
| COM type library hooks | `idmantypeinfo.tlb`, `downlWithIDM*.dll` | N/A | |

## Per-file-type configuration

| Reference setting | Registry evidence | OpenDLM | Note |
|---|---|---|---|
| One entry per extension | numbered keys `1`..`248` | Done | Stored as `file-types.json` rather than 248 registry subkeys |
| Takeover decision per type | `FileTypeAction` | Done | Take over / never / ask |
| Category per type | `DwnlPanel` | Done | |
| Destination folder per type | `FoldersTree` | Done | Beats the category folder |
| Description per type | — | Done | |
| Type-specific connection settings | — | N/A | The reference's per-type overrides beyond folder and category are not reproduced |

## Licensing and internals

| Reference area | Registry evidence | OpenDLM |
|---|---|---|
| Licence / activation state | `SpecialData` (`lgasa.*`, `lgfgf.*`) | **N/A** — OpenDLM is MIT licensed and has no licensing machinery whatsoever |
| Built-in help | `idman.chm`, `grabber.chm` | N/A — this repository documents itself in `docs/` |
| Video grabber with site-specific parsing | `IDMGrHlp.exe`, `grabber.chm` | N/A — a large proprietary site database that cannot be recreated |

## Summary

**Implemented and tested:** segments, resume, fallback, retry, speed limit, checksums,
clipboard monitor, queues, site logins, per-site single-connection rules, all scheduler
windows and the daily ceilings, protocol-aware proxy with exceptions and SOCKS,
per-file-type folders and categories, all thirteen grid columns, three toolbar styles,
light/dark/system themes, progress and completion dialogs, tray icon, notifications,
the full options surface, and the browser extension with its own settings vocabulary.

**Deliberately not reproduced:** kernel filter driver, shell extension, COM hooks,
site-specific video grabber, licence activation, third-party help files and
translations.

**Known gaps:** FTP transfer path, custom PAC URL evaluation, independent force/prevent
modifier pairs, per-browser context-menu toggles, and the "never ask again" memory for
duplicate links.
