# OpenDLM browser extension and native messaging host

This document explains how to load the OpenDLM browser extension, how to
register the native messaging host that bridges the extension to the desktop
app, and exactly what the two of them say to each other.

Everything here is written from scratch for OpenDLM and is MIT licensed.

## Contents

1. [How the pieces fit together](#1-how-the-pieces-fit-together)
2. [Loading the unpacked extension](#2-loading-the-unpacked-extension)
3. [Finding the extension ID](#3-finding-the-extension-id)
4. [Registering the native messaging host](#4-registering-the-native-messaging-host)
5. [Verifying the installation](#5-verifying-the-installation)
6. [Message protocol](#6-message-protocol)
7. [Extension settings keys](#7-extension-settings-keys)
8. [Troubleshooting](#8-troubleshooting)
9. [Log and configuration locations](#9-log-and-configuration-locations)
10. [Uninstalling](#10-uninstalling)

---

## 1. How the pieces fit together

A browser extension cannot open a Windows named pipe on its own, so a small
console program sits in the middle:

```
Chrome (stdio, native messaging framing)
        |
        v
OpenDLMNativeHost.exe          <- started by the browser, one process per message
        |
        v
\\.\pipe\OpenDLM.ipc           <- one request in, one response out, per connection
        |
        v
OpenDLM.exe                    <- the desktop app, owns the pipe server
```

* The **extension** never talks to the pipe. It calls
  `chrome.runtime.sendNativeMessage("com.opendlm.host", message)`.
* The **native host** reconnects to the pipe for every message, because
  Manifest V3 service workers are recycled aggressively and a long-lived
  connection would not survive.
* If the host cannot reach the app it answers with
  `{"type":"error","requestType":"<type>","message":"OpenDLM is not running"}`
  so the browser is never left waiting.

Project layout:

| Path | Contents |
| --- | --- |
| `extension/` | The Manifest V3 extension. Load this folder unpacked. |
| `src/OpenDLM.NativeHost/` | The native messaging host (C#, .NET 8, no NuGet packages). |
| `scripts/register-native-host.ps1` | Registers the host with Chrome, Edge and Brave. |
| `scripts/make-icons.mjs` | Regenerates `extension/icons/*.png` (pure Node.js). |

---

## 2. Loading the unpacked extension

The extension has no build step: the `extension` folder is loaded as-is.

1. Start the browser and open its extensions page.
   * Chrome: `chrome://extensions`
   * Edge: `edge://extensions`
   * Brave: `brave://extensions`
2. Turn on **Developer mode**. In Chrome and Edge the switch is in the top right
   corner; in Brave it is in the sidebar.
3. Choose **Load unpacked**.
4. Select the `extension` folder (the one containing `manifest.json`), then
   confirm.
5. The OpenDLM card appears with a **Service worker** link and an **Errors**
   button. Pin the toolbar button so the popup is one click away.

The extension requests these permissions:

| Permission | Why |
| --- | --- |
| `downloads` | Observe, cancel and erase browser downloads that OpenDLM should take over. |
| `contextMenus` | The three right-click entries. |
| `storage` | Persist settings and short-lived session state. |
| `nativeMessaging` | Talk to `com.opendlm.host`. |
| `notifications` | One actionable warning per session when the host is missing. |
| `tabs` | Resolve the originating tab of a download and open the options page. |
| `scripting` | Collect page links and media URLs on request. |
| `host_permissions: <all_urls>` | Work on every site you visit. |

---

## 3. Finding the extension ID

Chromium assigns an ID to every unpacked extension. It is derived from the
folder path unless you pin it with a `key` field, so it stays the same on your
machine but differs between machines.

1. Open `chrome://extensions` (or the Edge/Brave equivalent).
2. Turn on **Developer mode**.
3. Find the **OpenDLM Download Manager** card.
4. Copy the **ID** shown under the extension name. It is 32 characters long and
   uses only the letters `a` to `p`, for example
   `abcdefghijklmnopabcdefghijklmnop`.

You need this ID in the next step. Chromium rejects wildcards in
`allowed_origins`, so the ID must be written into the manifest literally.

---

## 4. Registering the native messaging host

Run the script from the repository root in Windows PowerShell 5.1 or PowerShell 7:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\register-native-host.ps1 -ExtensionId <YOUR-EXTENSION-ID>
```

To allow more than one ID (for example a second browser profile), repeat the
parameter or pass a comma-separated list:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\register-native-host.ps1 `
    -ExtensionId abcdefghijklmnopabcdefghijklmnop,ponmlkjihgfedcbaponmlkjihgfedcba
```

### What the script writes

1. `%LOCALAPPDATA%\OpenDLM\com.opendlm.host.json`:

   ```json
   {
     "name": "com.opendlm.host",
     "description": "OpenDLM native messaging host bridging the browser extension to the OpenDLM desktop app.",
     "path": "C:\\path\\to\\OpenDLMNativeHost.exe",
     "type": "stdio",
     "allowed_origins": ["chrome-extension://<your-id>/"]
   }
   ```

2. The default value of each of these registry keys, pointing at the manifest
   above:

   | Browser | Registry key |
   | --- | --- |
   | Chrome | `HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.opendlm.host` |
   | Edge | `HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.opendlm.host` |
   | Brave | `HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.opendlm.host` |

   Only `HKEY_CURRENT_USER` is touched, so the script never needs administrator
   rights.

3. Optionally, if you pass `-AppPath`, the value
   `HKCU:\Software\OpenDLM\InstallPath`. The native host reads it when it has to
   start the desktop app on demand. Pass either the folder or the full path to
   `OpenDLM.exe`:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\register-native-host.ps1 `
       -ExtensionId <YOUR-EXTENSION-ID> -AppPath "C:\Program Files\OpenDLM\OpenDLM.exe"
   ```

### Parameters

| Parameter | Meaning |
| --- | --- |
| `-ExtensionId <id>` | One or more Chromium extension IDs to allow. Repeatable and comma-separated. |
| `-AppPath <path>` | Optional path to `OpenDLM.exe` or its folder. Stored as `InstallPath`. |
| `-HostPath <path>` | Optional explicit path to `OpenDLMNativeHost.exe` when it is not beside the app and not in a local build output folder. |
| `-Unregister` | Removes the registry keys and the manifest instead of writing them. |

### Where the host executable is found

`-HostPath` wins if given. Otherwise the script looks for
`OpenDLMNativeHost.exe` in this order:

1. Next to the app named by `-AppPath`.
2. `src\OpenDLM.NativeHost\bin\Release\net8.0-windows\`
   (then `bin\Debug\net8.0-windows\`) inside this repository.
3. Beside the script itself.

If none of those exist the script stops with exit code `1` and prints the build
command to run. If you did not pass `-ExtensionId`, the script still writes the
manifest but `allowed_origins` is empty and it prints a prominent warning with
instructions for finding the ID. Chromium will refuse to connect until the ID is
added.

The script is idempotent: running it twice produces the same result and prints
exactly what it created, updated or removed.

---

## 5. Verifying the installation

1. Register the host (step 4).
2. Reload the extension card at `chrome://extensions` so the browser picks up the
   new registration.
3. Click the OpenDLM toolbar button. The status row should read **Connected**
   followed by the app version.
4. If the row reads **App not running**, that is still a healthy install: the
   host is registered, the app simply is not started yet. OpenDLM starts
   automatically the next time you send it a download.
5. Open `chrome://extensions`, click **Errors** on the OpenDLM card: it should
   list nothing.
6. Check the host log for a fresh line (see
   [section 9](#9-log-and-configuration-locations)).

The options page (`chrome://extensions` -> OpenDLM -> **Extension options**, or
the popup's **OpenDLM options** button) has a **Test connection** button that
performs the same check.

---

## 6. Message protocol

Protocol version: **1**.

### Transport

Both hops use the same framing:

| Layer | Framing |
| --- | --- |
| Browser to native host | 4-byte little-endian `Int32` byte length, then that many bytes of UTF-8 JSON. |
| Native host to browser | 4-byte little-endian `Int32` byte length, then that many bytes of UTF-8 JSON. |
| Native host to app pipe | 4-byte little-endian `Int32` byte length, then that many bytes of UTF-8 JSON. |
| App pipe to native host | 4-byte little-endian `Int32` byte length, then that many bytes of UTF-8 JSON. |

Every message is a JSON object with a `"type"` string.

* The IPC pipe is strictly **one request in, one response out, per connection**.
  The client connects, writes one framed request, reads one framed response, and
  closes.
* Chromium rejects any single native messaging payload of 1 MB or more. The host
  refuses to read a frame larger than 1 MB, and replaces an oversized reply from
  the app with an error message rather than truncating JSON.
* The host never writes anything to stdout except protocol frames. All
  diagnostics go to the log file.

### Requests from the extension

| `type` | Fields | Meaning |
| --- | --- | --- |
| `hello` | `extensionVersion` (string) | Handshake and capability probe. |
| `ping` | none | Liveness check. |
| `addDownload` | see below | Queue one download. |
| `addBatch` | `items`: array of `addDownload` objects | Queue many downloads. |
| `getSettings` | none | Read integration settings from the app. |
| `setSetting` | `key` (string), `value` (any JSON) | Write one integration setting. |

The host relays the request bytes to the app unchanged and relays the app's
reply bytes back unchanged.

#### The `addDownload` object

Only `url` is mandatory. Unknown fields are omitted rather than sent as `null`.

```json
{
  "type": "addDownload",
  "url":         "https://example.com/file.zip",
  "pageUrl":     "https://example.com/page",
  "referer":     "https://example.com/page",
  "filename":    "file.zip",
  "mimeType":    "application/zip",
  "totalBytes":  104857600,
  "description": "some text",
  "cookies":     "a=1; b=2",
  "userAgent":   "Mozilla/5.0 ...",
  "startNow":    true,
  "category":    "compressed"
}
```

The extension fills these fields as follows:

| Field | Source |
| --- | --- |
| `url` | `item.finalUrl`, falling back to `item.url`. Only `http:` and `https:` URLs are sent. |
| `filename` | Base name of `item.filename`, or of the URL. |
| `mimeType` | `item.mime`. |
| `totalBytes` | `item.fileSize`, falling back to `item.totalBytes`. |
| `pageUrl`, `referer` | The URL of the originating tab, falling back to `item.referrer`. |
| `userAgent` | `navigator.userAgent`. |
| `description` | Link text, when a batch was collected from the page. |
| `category` | Derived from the file extension: `compressed`, `video`, `audio`, `document`, `program` or `image`. |
| `startNow` | Always `true`. |
| `cookies` | Never sent by this extension; reserved for the app. |

### Responses written back to the extension

| `type` | Fields |
| --- | --- |
| `hello` | `appVersion` (string), `protocol` (int), `running` (bool) |
| `pong` | `running` (bool), `appVersion` (string) |
| `ack` | `requestType` (string), `itemId` (string, optional), `count` (int, optional) |
| `settings` | `settings` (object) |
| `error` | `requestType` (string), `message` (string) |

The app produces the first five. The host produces `error` in these cases:

| Situation | Reply |
| --- | --- |
| App not listening, and cannot be started | `{"type":"error","requestType":"<type>","message":"OpenDLM is not running"}` |
| App listening but the exchange broke | `{"type":"error","requestType":"<type>","message":"OpenDLM did not answer the request."}` |
| App reply was empty, malformed or larger than 1 MB | `{"type":"error","requestType":"<type>","message":"..."}` |
| Request JSON was malformed | `{"type":"error","requestType":"(unknown)","message":"The request was not valid JSON."}` |
| Unknown `type` | `{"type":"error","requestType":"<type>","message":"Unsupported request type '<type>'."}` |

### Handshake fallbacks

`ping` and `hello` are the two messages that must always produce a useful
answer, because the popup renders its connection state from them. If the app
cannot be reached, the host starts it, retries the connection for a few seconds,
and then answers locally instead of erroring:

```json
{"type":"hello","appVersion":"","protocol":1,"running":false}
{"type":"pong","running":false,"appVersion":""}
```

Every other request type fails with the `error` message above.

### Starting the app on demand

For `addDownload`, `addBatch`, `getSettings` and `setSetting` the host tries to
start `OpenDLM.exe` when the pipe is not up, then retries the connection every
250 ms for up to 8 seconds. The executable is located by trying, in order:

1. The `InstallPath` value under `HKEY_CURRENT_USER\Software\OpenDLM`.
2. `OpenDLM.exe` beside `OpenDLMNativeHost.exe`.
3. A developer build output found by walking up from the host directory:
   `<root>\src\OpenDLM.App\bin\{Debug,Release}\net8.0-windows\OpenDLM.exe`.

For `ping` and `hello` the retry budget is shorter (about 3 seconds) so the
popup stays responsive. A request is only ever re-sent when the connect call
itself was refused, which means the app cannot have received it; a request is
never re-sent after the pipe connected, so downloads cannot be queued twice.

---

## 7. Extension settings keys

Settings are stored as a single `settings` object in `chrome.storage.local`, and
every save is also pushed to the app as one `setSetting` message per key.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `enabled` | boolean | `true` | Master switch for automatic download takeover. |
| `takeoverExtensions` | string[] | ~90 archive, video, audio, document and program extensions | File types handed to OpenDLM. |
| `forceTakeoverKey` | string | `"Alt"` | Hold this key and click a link to force takeover. |
| `keepDownloadKey` | string | `"Shift"` | Hold this key and click a link to let the browser keep the download. |
| `mediaSniffing` | boolean | `true` | Show the in-page media badge. |

Modifier key values are `Alt`, `Control`, `Shift`, `Meta` and `None`.

Explicit actions (the right-click menu entries and the popup's
**Download this page's videos** button) deliberately ignore the `enabled` switch,
because they are direct user requests rather than automatic behaviour.

---

## 8. Troubleshooting

### "Specified native messaging host not found"

The browser does not know the host name `com.opendlm.host` for this browser.

* Re-run `scripts\register-native-host.ps1` and read its summary output.
* Confirm the registry key for the browser you are using exists and its
  `(default)` value points at
  `%LOCALAPPDATA%\OpenDLM\com.opendlm.host.json`:

  ```powershell
  Get-ItemProperty 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.opendlm.host'
  ```

* Confirm the `path` inside that JSON file points at an existing
  `OpenDLMNativeHost.exe`. A stale path is the most common cause after moving or
  rebuilding the repository.
* If you loaded the extension in Brave or Edge, register that browser too:
  the script writes all three keys, but a different browser path may have been
  deleted by cleaning software.

### "Access to the specified native messaging host is forbidden"

This message means the host **was** found, but the calling extension's origin is
not listed in `allowed_origins`. The extension ID in the manifest is wrong,
missing or stale.

* Re-read the ID from `chrome://extensions` (see
  [section 3](#3-finding-the-extension-id)) and re-run the script with
  `-ExtensionId <that ID>`.
* Each allowed origin must end with a trailing slash and look exactly like
  `chrome-extension://abcdefghijklmnopabcdefghijklmnop/`. Chromium rejects
  wildcards such as `chrome-extension://*/*`.
* If the ID changed, it is usually because the extension was reloaded from a
  different folder path. Keep the folder in one place.
* Editing `com.opendlm.host.json` by hand works, but re-running the script is
  safer because it also refreshes all three registry keys.

### "OpenDLM is not running"

The host was launched and reached the app's pipe, but the app was not there and
could not be started.

* Start `OpenDLM.exe` manually and try again.
* Register the install location so the host can start it for you:

  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts\register-native-host.ps1 `
      -ExtensionId <YOUR-EXTENSION-ID> -AppPath "C:\Program Files\OpenDLM\OpenDLM.exe"
  ```

* Confirm the desktop app really owns the pipe name `OpenDLM.ipc`. If the app is
  running but the popup still says "App not running", the app side is not
  hosting the pipe server yet.

### The extension shows "No answer" and nothing else

The native host process started but exited before replying, or the browser
blocked it.

* Open `chrome://extensions` and press **Errors** on the OpenDLM card.
* Read the tail of `%LOCALAPPDATA%\OpenDLM\logs\nativehost.log`. A fatal entry
  there explains the exit.
* Run the host by hand to see whether it stays alive. It waits on stdin, so it
  should sit idle until you press Ctrl+C:

  ```powershell
  & "src\OpenDLM.NativeHost\bin\Debug\net8.0-windows\OpenDLMNativeHost.exe"
  ```

### Downloads are still handled by the browser

* Check the master switch in the popup.
* Check the file type is listed on the options page. Types are matched on the
  file name's final extension, without the dot.
* Links on a page you had already open before installing the extension only work
  after a reload, because the content script is injected at load time.
* Pages the browser forbids extensions from touching (`chrome://`, the Web
  Store, other extensions' pages) are never intercepted.

### A download was taken over and the browser kept a second copy

This should not happen: the extension marks a download as claimed before it
cancels it, and the marker survives a service worker restart. If you do see it,
please include the tail of the native host log and the download's URL in a bug
report.

### Ctrl+click no longer opens a new tab

It still does. The extension never intercepts clicks with Ctrl or Cmd held. If a
site behaves oddly, check that `forceTakeoverKey` and `keepDownloadKey` are not
both set to `Control` on the options page.

---

## 9. Log and configuration locations

| What | Path |
| --- | --- |
| Native host log | `%LOCALAPPDATA%\OpenDLM\logs\nativehost.log` |
| Desktop app log | `%LOCALAPPDATA%\OpenDLM\logs\opendlm.log` |
| Native messaging manifest | `%LOCALAPPDATA%\OpenDLM\com.opendlm.host.json` |
| App settings (roaming) | `%APPDATA%\OpenDLM\settings.json` |

Read the tail of the host log in PowerShell:

```powershell
Get-Content "$env:LOCALAPPDATA\OpenDLM\logs\nativehost.log" -Tail 50
```

The host log is self-trimming: once it passes about 1 MB it keeps only the most
recent 256 KB, so it cannot grow without bound. The app log rotates at 2 MB.

Every log line looks like this:

```
2026-01-31 14:22:07.412 [INFO ] Extension handshake from version 1.0.0.
2026-01-31 14:22:07.418 [INFO ] stdin closed; exiting normally.
```

Nothing the host logs is ever written to stdout, because stdout carries the
native messaging protocol frames.

---

## 10. Uninstalling

```powershell
powershell -ExecutionPolicy Bypass -File scripts\register-native-host.ps1 -Unregister
```

This removes the three registry keys and
`%LOCALAPPDATA%\OpenDLM\com.opendlm.host.json`. It does not touch
`HKCU:\Software\OpenDLM\InstallPath`, because that value belongs to the desktop
app; delete that key by hand if you are removing OpenDLM entirely.

Then remove the extension from `chrome://extensions`.
