# OpenDLM browser extension

A Manifest V3 extension that hands downloads to the **OpenDLM** desktop app — a
free, MIT-licensed download manager for Windows.

It is plain JavaScript with **no build step, no bundler and no dependencies**.
The folder you are reading is exactly what gets loaded into the browser.

## What it does

| Feature | How |
| --- | --- |
| Download takeover | Watches `chrome.downloads.onCreated`, cancels downloads whose file type is on your list, and sends them to OpenDLM. |
| Link interception | A content script catches clicks on download-looking links in the capture phase and routes them to OpenDLM. |
| Media detection | A small dismissible badge over video elements offers "Download with OpenDLM". |
| Video list | The toolbar popup can send every video on the current page in one batch. |
| Context menus | Right-click a link, media element or page for batch actions. |

## Install (unpacked)

1. Open `chrome://extensions` (or `edge://extensions`, `brave://extensions`).
2. Turn on **Developer mode** (top right).
3. Choose **Load unpacked** and select this `extension` folder.
4. Copy the **ID** shown on the extension card — you need it in the next step.
5. Register the native messaging host (see `docs/EXTENSION.md`):

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\register-native-host.ps1 -ExtensionId <YOUR-ID>
   ```

6. Reload the extension once after registering, then open the popup. The status
   row should read **Connected**.

## Files

| File | Purpose |
| --- | --- |
| `manifest.json` | Manifest V3 declaration, permissions and content-script registration. |
| `background.js` | Service worker: native messaging, download takeover, context menus, message routing. |
| `content.js` | Link interception, modifier-key intent, media sniffing and the in-page badge. |
| `content.css` | Namespaced styles for the badge (the root resets inherited page CSS). |
| `popup.html` / `popup.js` | Toolbar popup: connection state, master toggle, "download this page's videos". |
| `options.html` / `options.js` | Options: takeover list, modifier keys, media badge, connection test. |
| `icons/` | Generated PNG icons. Regenerate with `node scripts/make-icons.mjs`. |

## Using the modifier keys

Defaults:

* **Alt** + click a link — force OpenDLM to take this download even if its file
  type is not on the takeover list.
* **Shift** + click a link — force the **browser** to keep this download.

Both keys are configurable in the options page. Ctrl/Cmd + click always means
"open in a new tab" and is never intercepted.

## Licence

OpenDLM is free and open source software, released under the MIT licence.
