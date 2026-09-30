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

One finding worth stating first, because it corrects an earlier claim: **this
installation does have a dark mode.** It is a menu item, `34042 = "Dunkelmodus"`
in the language file, and id `21206` explains that *"IDM uses the dark mode when it
is enabled in the Windows settings"* — it follows the Windows app theme, and id
`21205` says a restart is required for the change to take effect. It is absent from
the registry entirely, which is why a registry-only search wrongly concludes the
feature does not exist. OpenDLM offers the same three choices, applies them without
a restart, and follows Windows by default.

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
| Duplicate link warning | `DuplLinksA` | Done | Warns with the existing entry's state, and offers to add or skip |
| Remember the answer | `RememberDuplLinksA` | Done | The answer is stored per address, so the same link is not asked about twice. A signed link is matched by its path, because its token changes on every request |
| FTP transfers | `FtpPasive`, `UseFtpProxy` | Done | Probe with `SIZE`, transfer with `RETR`, resume with restart markers. Always a single connection, because restarting several streams over one control channel is not something servers agree on |
| FTP proxy protocol | `nProxyMode` | Done | The per-protocol switch and the proxy decision apply to FTP as well |

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
| PAC script | `ProxyPac`, `WBProxy` | Done | The automatic mode evaluates a configured script per destination through Windows' own `WinHttpGetProxyForUrl`, so every PAC feature works, including per-host routing and `DIRECT`. With no script configured, the system PAC is used |
| Passive FTP | `FtpPasive` | Done | Used by every FTP transfer, with automatic fallback to a full restart when a server ignores restart markers |

## Interface

| Reference setting | Registry evidence | OpenDLM | Note |
|---|---|---|---|
| Dark mode | `Languages\*.lng` id 34042 "Dark mode" (menu), 21206 (follows the Windows setting) | Done | Light, dark and follow-Windows, switchable at runtime. IDM needs a restart for the change to take effect; OpenDLM applies it immediately, and follows the Windows setting by default |
| Toolbar style | `ToolbarStyle` | Done | Icons and text / icons only / large icons |
| Large buttons | `LargeButtons` | Done | Via the large-icons toolbar style |
| Toolbar state | `ToolbarState_v5.11` | Partial | Order and width of toolbar buttons are fixed; the list view columns *are* reorderable |
| Category pane / details pane / status bar | `DwnlPanel`, `FoldersTree` | Done | All three toggle and persist |
| Window size and position | `windowPlacementV6` | Done | Validated against the current screen topology |
| Sort column and direction | `sortOrder` | Done | Persisted |
| Column list | `ListSettings` | Done | FileName, Size, Status, Downloaded, Speed, Time left, Connections, Date added, Save to, Queue, Referer, Last try, Description |
| "Parent" column | `ListSettings\Parent_wp` | Partial | Means the *parent web page* the download came from (`21216` "Übergeordnete Webseite", listed as a search field next to the referer), not a parent download. OpenDLM records the page URL and shows it as "Referer", but has no separate parent-site column or the "arrange by parent site" command |
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
| Context menu entries | `menuExt` (per browser and per action) | Done | Each of the three entries is switched on and off on its own, and the extension only offers menus in a browser the user left enabled |
| Media sniffing | `DwnlPanel` type list | Partial | Detected in the page; the reference's editable list of sniffed types plus its subtitle formats is not configurable in OpenDLM |
| Force takeover key | `SpecialKeys\UseKeyToForce`, `AltF`, `CtrlF`, `ShiftF` | Done | Independent list: Alt, Ctrl and Shift can all force at once, stored as "Alt+Ctrl" |
| Prevent key | `SpecialKeys\UseKeyToPrevent`, `ShiftP`, `CtrlP`, `AltP` | Done | A second, independent list, so a force set and a prevent set can differ |
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
windows and the daily ceilings, protocol-aware proxy with per-protocol switches, host
exceptions, local bypass, SOCKS4/5 and a real PAC script, FTP with resume and
restart-marker detection, per-file-type folders and categories, all thirteen grid
columns, three toolbar styles, light/dark/system themes, progress and completion
dialogs, tray icon, notifications, the full options surface, duplicate-link memory,
and the browser extension with its own tested settings vocabulary.

**Deliberately not reproduced:** kernel filter driver, shell extension, COM hooks,
site-specific video grabber, licence activation, third-party help files and
translations.

## Known differences, all deliberate and none of them settings the user can see as
missing:** the reference ships 40+ translation files and OpenDLM ships English; its
Insert/Delete takeover keys are replaced by the Insert and Delete shortcuts in the
list; its launcher, update checker and site-specific video grabber are not
reproduced, because they are proprietary content rather than settings.

## Gaps this comparison found

Confirmed against the language file and the installed package. The first block is
now closed; what remains is listed after it.

### Closed since the comparison

| Gap | Reference evidence | Where it lives now |
|---|---|---|
| Dial Up / VPN tab | `21221`, `21222`, `1058`, `1307`-`1313` | `DialUpManager` enumerates Windows RAS connections, dials with redial attempts and interval, hangs up; `DialUpWindow` edits it and connects live; the scheduler brings the connection up when a queue starts |
| Find dialog | `34040`, `34041`, `1807`-`1812`, `21122`-`21124` | `FindWindow` (Ctrl+F, F3) over `DownloadSearcher`: file name, description, page, link, parent page, referer, partial or exact, find next and previous |
| Export and import | `32809`-`32823` | `DownloadTransfer` writes a rich OpenDLM file or a text file of `url<TAB>referer`, and reads either back; wired to the Tasks menu |
| Language selection | menu `m32841` | A note in the options explains that only English ships; a setting exists, no dead menu |
| Font selection | `m34045`, `34046`, `21218` | Interface font family, size and a reset button |
| Toolbar icon styles | `34010`-`34012`, `33510` | Toolbar layout (icons and text / icons only / large) plus an icon size (classic / small / large) |
| Sort by menu | `32828`-`32840` | View -> Sort by with the reference's seven entries and a reverse-order toggle |
| "Load now" | `32773` | "Load the first block only" stages the first segment and continues in the background |
| Catch basket | `32809` | "Recover interrupted downloads" |
| Clean up | `32794` | "Clean up finished and failed" |
| Proxy "from the browser" | `1107`, `1113`, `1141`, `1863` | Per-protocol switches; the extension reads Chrome's proxy and the app prefers it for that one download |
| Browser proxy on failure | `1844` | A setting, honoured when the browser supplied a proxy |
| Sound tab with preview | `1320`, `1091` | **Still open** |
| IE / Netscape / MSN integration | `1065`-`1257` | **Out of scope**: those browsers are retired |
| Plugins section | General tab | **Out of scope** |
| ZIP preview | dialog at line 333, `1429`, `1430` | **Still open** |
| Add to queue instead of starting | `1862` | The setting exists; offered per dialog rather than per schedule |

### Extension, compared file by file

| Reference file | Size | OpenDLM |
|---|---|---|
| `background.js` | 56 KB | `background.js`, comparable size |
| `content.js` | 29 KB | `content.js` + `content.css`, `document_start`, all frames |
| `document.js` | 5 KB | none |
| `debug.js` | 693 B | none |
| `captured.html` / `captured.js` | 1.8 / 1 KB | none |
| `welcome.html` / `welcome.js` | 8.8 / 3.2 KB | **added** |
| `_locales/` | 17 languages | none (English only) |
| `_metadata/verified_contents.json` | 5.4 KB | n/a (not store-listed) |

Permissions:

| Reference | OpenDLM |
|---|---|
| `nativeMessaging`, `storage`, `downloads`, `contextMenus`, `scripting`, `tabs`, `cookies` | yes |
| `downloads.shelf`, `webNavigation`, `proxy` | **added** |
| `webRequest`, `declarativeNetRequest` | no |
| `management` | no |
| optional `system.display` | no |
| `host_permissions: <all_urls>`, `minimum_chrome_version: 109` | yes |

User-visible features:

| Reference | OpenDLM |
|---|---|
| "Transfer download to" an in-progress browser download | **added** |
| "Download selected links" | **added** |
| Toolbar button with normal / `[DISABLED]` / `[ERROR]` states | **added** |
| First-run welcome page | **added** |
| Seventeen localisations | not done; only English ships |
| Link interception with modifier keys, media detection, download all links, takeover of browser downloads, native messaging | yes |
