/*
 * OpenDLM extension service worker.
 *
 * Responsibilities:
 *   - own the native messaging connection to the OpenDLM desktop app,
 *   - take over browser downloads whose file type the user opted into,
 *   - relay link/media requests coming from the content script and popup,
 *   - own the right-click menu entries.
 *
 * Manifest V3 kills service workers aggressively, so nothing important lives
 * in a module-level variable: settings come from chrome.storage.local, and the
 * short-lived bookkeeping (processed downloads, modifier intent, "already
 * warned about the host" flag) lives in chrome.storage.session.
 */

const HOST_NAME = 'com.opendlm.host';
const PROTOCOL_VERSION = 1;

/* ------------------------------------------------------------------ *
 * Defaults
 * ------------------------------------------------------------------ */

const EXTENSION_GROUPS = {
  compressed: [
    '7z', 'ace', 'alz', 'arj', 'bz2', 'cab', 'gz', 'iso', 'jar', 'lz', 'lzma',
    'lzh', 'rar', 'rpm', 'tar', 'tbz', 'tbz2', 'tgz', 'txz', 'xz', 'zip', 'zipx', 'zst',
  ],
  video: [
    '3gp', 'avi', 'flv', 'm2ts', 'm4v', 'mkv', 'mov', 'mp4', 'mpeg',
    'mpg', 'mts', 'ogv', 'rm', 'rmvb', 'vob', 'webm', 'wmv',
  ],
  audio: [
    'aac', 'aiff', 'alac', 'ape', 'flac', 'm4a', 'm4b', 'mid', 'midi',
    'mp3', 'oga', 'ogg', 'opus', 'wav', 'wma',
  ],
  document: [
    'chm', 'csv', 'djvu', 'doc', 'docx', 'epub', 'fb2', 'mobi', 'odp',
    'ods', 'odt', 'pages', 'pdf', 'ppt', 'pptx', 'ps', 'rtf', 'xls', 'xlsx',
  ],
  program: [
    'apk', 'appx', 'deb', 'dmg', 'exe', 'msi', 'msix', 'msu', 'pkg', 'snap', 'xap',
  ],
  image: [
    'bmp', 'gif', 'heic', 'ico', 'jpeg', 'jpg', 'png', 'tif', 'tiff', 'webp',
  ],
};

/*
 * Taken over by default. Images are deliberately excluded: "save image as" is
 * a frequent, casual action, so images are only handed over from the context
 * menu or from the media badge.
 */
const DEFAULT_TAKEOVER_EXTENSIONS = []
  .concat(EXTENSION_GROUPS.compressed)
  .concat(EXTENSION_GROUPS.video)
  .concat(EXTENSION_GROUPS.audio)
  .concat(EXTENSION_GROUPS.document)
  .concat(EXTENSION_GROUPS.program)
  .sort();

const DEFAULT_SETTINGS = {
  enabled: true,
  mediaSniffing: true,
  forceTakeoverKey: 'Alt',
  keepDownloadKey: 'Shift',
  takeoverExtensions: DEFAULT_TAKEOVER_EXTENSIONS.slice(),
};

const MODIFIER_KEYS = ['Alt', 'Control', 'Shift', 'Meta', 'None'];

/** How long a "handled" marker is trusted. Long enough to survive a restart of the worker. */
const HANDLED_TTL_MS = 15000;

/** How long a modifier intent recorded by the content script stays valid. */
const INTENT_TTL_MS = 2500;

const MAX_BATCH_ITEMS = 200;

/* ------------------------------------------------------------------ *
 * Storage helpers
 * ------------------------------------------------------------------ */

/* Fast in-process guard; chrome.storage.session is the durable copy. */
const handledInMemory = new Set();

const memorySession = new Map();

function hasSessionStorage() {
  return Boolean(chrome.storage && chrome.storage.session);
}

async function sessionGet(key, fallback) {
  if (!hasSessionStorage()) {
    return memorySession.has(key) ? memorySession.get(key) : fallback;
  }
  try {
    const stored = await chrome.storage.session.get(key);
    if (stored && Object.prototype.hasOwnProperty.call(stored, key)) {
      return stored[key];
    }
    return fallback;
  } catch (error) {
    return fallback;
  }
}

async function sessionSet(key, value) {
  if (!hasSessionStorage()) {
    memorySession.set(key, value);
    return;
  }
  try {
    await chrome.storage.session.set({ [key]: value });
  } catch (error) {
    memorySession.set(key, value);
  }
}

function normalizeExtension(value) {
  if (typeof value !== 'string') {
    return '';
  }
  return value.trim().toLowerCase().replace(/^\.+/, '').replace(/[^a-z0-9.+-]/g, '');
}

function normalizeSettings(raw) {
  const settings = {
    enabled: DEFAULT_SETTINGS.enabled,
    mediaSniffing: DEFAULT_SETTINGS.mediaSniffing,
    forceTakeoverKey: DEFAULT_SETTINGS.forceTakeoverKey,
    keepDownloadKey: DEFAULT_SETTINGS.keepDownloadKey,
    takeoverExtensions: DEFAULT_SETTINGS.takeoverExtensions.slice(),
  };

  if (!raw || typeof raw !== 'object') {
    return settings;
  }

  if (typeof raw.enabled === 'boolean') {
    settings.enabled = raw.enabled;
  }
  if (typeof raw.mediaSniffing === 'boolean') {
    settings.mediaSniffing = raw.mediaSniffing;
  }
  if (typeof raw.forceTakeoverKey === 'string' && MODIFIER_KEYS.includes(raw.forceTakeoverKey)) {
    settings.forceTakeoverKey = raw.forceTakeoverKey;
  }
  if (typeof raw.keepDownloadKey === 'string' && MODIFIER_KEYS.includes(raw.keepDownloadKey)) {
    settings.keepDownloadKey = raw.keepDownloadKey;
  }
  if (Array.isArray(raw.takeoverExtensions)) {
    const seen = new Set();
    for (const entry of raw.takeoverExtensions) {
      const cleaned = normalizeExtension(entry);
      if (cleaned) {
        seen.add(cleaned);
      }
    }
    settings.takeoverExtensions = Array.from(seen).sort();
  }

  return settings;
}

async function getSettings() {
  try {
    const stored = await chrome.storage.local.get('settings');
    return normalizeSettings(stored ? stored.settings : null);
  } catch (error) {
    return normalizeSettings(null);
  }
}

/* ------------------------------------------------------------------ *
 * Small pure helpers
 * ------------------------------------------------------------------ */

function isWebUrl(url) {
  return typeof url === 'string' && /^https?:\/\//i.test(url);
}

function baseName(value) {
  if (typeof value !== 'string' || !value) {
    return '';
  }
  const withoutQuery = value.split('#')[0].split('?')[0];
  const parts = withoutQuery.split(/[\\/]/);
  const last = parts[parts.length - 1] || '';
  try {
    return decodeURIComponent(last);
  } catch (error) {
    return last;
  }
}

function extensionOf(value) {
  if (typeof value !== 'string' || !value) {
    return '';
  }
  const name = baseName(value);
  const dot = name.lastIndexOf('.');
  if (dot < 0 || dot === name.length - 1) {
    return '';
  }
  const extension = normalizeExtension(name.slice(dot + 1));
  return extension.length > 12 ? '' : extension;
}

function categorize(value) {
  const extension = extensionOf(value);
  if (!extension) {
    return '';
  }
  for (const group of Object.keys(EXTENSION_GROUPS)) {
    if (EXTENSION_GROUPS[group].includes(extension)) {
      return group;
    }
  }
  return '';
}

function truncate(value, max) {
  const text = String(value == null ? '' : value);
  return text.length <= max ? text : text.slice(0, max - 1) + '\u2026';
}

function describeError(error) {
  const text = error && error.message ? String(error.message) : String(error || '');
  if (/native messaging host.*not found|specified native messaging host not found/i.test(text)) {
    return 'The OpenDLM native messaging host is not registered for this browser. Run scripts/register-native-host.ps1 with this extension ID.';
  }
  if (/forbidden|access to the specified native messaging host/i.test(text)) {
    return 'Access to the OpenDLM native messaging host is forbidden: this extension ID is missing from allowed_origins in com.opendlm.host.json.';
  }
  if (/has not been enabled|connection.*closed|host.*exited/i.test(text)) {
    return 'The OpenDLM native messaging host started but exited immediately. Check the native host log.';
  }
  return text || 'The OpenDLM native messaging host is unavailable.';
}

/* ------------------------------------------------------------------ *
 * Native messaging
 * ------------------------------------------------------------------ */

/**
 * Reconnects to the native host for every send. A long-lived port would be
 * dropped whenever MV3 recycles the worker, so a one-shot send is both simpler
 * and more reliable.
 */
async function sendToApp(message) {
  try {
    const reply = await chrome.runtime.sendNativeMessage(HOST_NAME, message);
    if (reply && typeof reply === 'object') {
      return reply;
    }
    return {
      type: 'error',
      requestType: message.type,
      message: 'The OpenDLM native host returned an empty reply.',
    };
  } catch (error) {
    const detail = describeError(error);
    await notifyHostProblem(detail);
    return { type: 'error', requestType: message.type, message: detail };
  }
}

/** Shows one actionable notification per browser session, never a stream of them. */
async function notifyHostProblem(detail) {
  try {
    const already = await sessionGet('hostNotice', null);
    if (already) {
      return;
    }
    await sessionSet('hostNotice', { at: Date.now(), detail: truncate(detail, 240) });
  } catch (error) {
    // Fall through and still try to notify.
  }

  try {
    await chrome.notifications.create('opendlm-host-problem', {
      type: 'basic',
      iconUrl: 'icons/icon128.png',
      title: 'OpenDLM is not reachable',
      message: truncate(detail, 220),
      priority: 1,
    });
  } catch (error) {
    // Notifications are optional; the popup still shows the state.
  }
}

/* ------------------------------------------------------------------ *
 * Download request builders
 * ------------------------------------------------------------------ */

async function resolvePageUrl(item) {
  if (item && typeof item.tabId === 'number' && item.tabId >= 0) {
    try {
      const tab = await chrome.tabs.get(item.tabId);
      if (tab && typeof tab.url === 'string' && tab.url) {
        return tab.url;
      }
    } catch (error) {
      // The tab may already be gone; fall back to the referrer.
    }
  }
  return (item && typeof item.referrer === 'string') ? item.referrer : '';
}

async function buildAddDownload(options) {
  const request = { type: 'addDownload', url: options.url };

  const filename = options.filename || baseName(options.url);
  if (filename) {
    request.filename = filename;
  }
  if (options.mimeType) {
    request.mimeType = options.mimeType;
  }
  if (Number.isFinite(options.totalBytes) && options.totalBytes > 0) {
    request.totalBytes = options.totalBytes;
  }
  if (options.description) {
    request.description = truncate(options.description, 300);
  }
  if (options.cookies) {
    request.cookies = options.cookies;
  }

  const pageUrl = options.pageUrl || '';
  if (pageUrl) {
    request.pageUrl = pageUrl;
    request.referer = options.referer || pageUrl;
  }

  if (options.userAgent) {
    request.userAgent = options.userAgent;
  }

  const category = categorize(filename || options.url);
  if (category) {
    request.category = category;
  }

  request.startNow = true;
  return request;
}

function currentUserAgent() {
  try {
    return navigator.userAgent || '';
  } catch (error) {
    return '';
  }
}

/* ------------------------------------------------------------------ *
 * Browser download takeover
 * ------------------------------------------------------------------ */

async function isHandled(...keys) {
  const now = Date.now();
  for (const key of keys) {
    if (key && handledInMemory.has(key)) {
      return true;
    }
  }

  const map = await sessionGet('handled', {});
  for (const key of keys) {
    if (!key) {
      continue;
    }
    const at = map[key];
    if (typeof at === 'number' && now - at <= HANDLED_TTL_MS) {
      return true;
    }
  }
  return false;
}

async function markHandled(...keys) {
  const now = Date.now();
  for (const key of keys) {
    if (key) {
      handledInMemory.add(key);
    }
  }

  const map = await sessionGet('handled', {});
  for (const existing of Object.keys(map)) {
    if (typeof map[existing] !== 'number' || now - map[existing] > HANDLED_TTL_MS) {
      delete map[existing];
    }
  }
  for (const key of keys) {
    if (key) {
      map[key] = now;
    }
  }
  await sessionSet('handled', map);
}

async function readIntent() {
  const intent = await sessionGet('intent', null);
  if (!intent || typeof intent !== 'object' || typeof intent.at !== 'number') {
    return null;
  }
  if (Date.now() - intent.at > INTENT_TTL_MS) {
    return null;
  }
  return intent;
}

async function cancelAndErase(downloadId) {
  try {
    await chrome.downloads.cancel(downloadId);
  } catch (error) {
    // Already finished, already cancelled, or gone: nothing left to do.
  }
  try {
    await chrome.downloads.erase({ id: downloadId });
  } catch (error) {
    // Erasing is cosmetic; the cancel above is what matters.
  }
}

async function handleDownloadCreated(item) {
  if (!item || typeof item.id !== 'number') {
    return;
  }

  const settings = await getSettings();
  if (!settings.enabled) {
    return;
  }

  // Never fight our own downloads.
  if (item.byExtensionId && item.byExtensionId === chrome.runtime.id) {
    return;
  }

  const url = (item.finalUrl && item.finalUrl.length) ? item.finalUrl : item.url;
  if (!isWebUrl(url)) {
    return;
  }

  const idKey = 'id:' + item.id;
  const urlKey = 'url:' + url;
  if (await isHandled(idKey, urlKey)) {
    return;
  }

  const intent = await readIntent();

  // The user asked the browser to keep this one (modifier held at click time).
  if (intent && intent.keep) {
    await markHandled(idKey, urlKey);
    return;
  }

  const suggested = typeof item.filename === 'string' ? item.filename : '';
  const filename = baseName(suggested) || baseName(url);
  const extension = extensionOf(filename) || extensionOf(url);
  const forced = Boolean(intent && intent.force);

  if (!forced && (!extension || !settings.takeoverExtensions.includes(extension))) {
    return;
  }

  // Claim it before touching the download so no second event can double-handle it.
  await markHandled(idKey, urlKey);
  await cancelAndErase(item.id);

  const totalBytes = Number.isFinite(item.fileSize) && item.fileSize > 0
    ? item.fileSize
    : (Number.isFinite(item.totalBytes) && item.totalBytes > 0 ? item.totalBytes : undefined);

  const request = await buildAddDownload({
    url,
    filename,
    mimeType: item.mime || '',
    totalBytes,
    pageUrl: await resolvePageUrl(item),
    referer: item.referrer || '',
    userAgent: currentUserAgent(),
  });

  await sendToApp(request);
}

chrome.downloads.onCreated.addListener((item) => {
  handleDownloadCreated(item).catch((error) => {
    console.warn('OpenDLM: download takeover failed.', error);
  });
});

/* ------------------------------------------------------------------ *
 * Context menus
 * ------------------------------------------------------------------ */

function createContextMenus() {
  chrome.contextMenus.removeAll(() => {
    void chrome.runtime.lastError;

    const menus = [
      { id: 'opendlm-link', title: 'Download with OpenDLM', contexts: ['link'] },
      { id: 'opendlm-media', title: 'Download this media with OpenDLM', contexts: ['video', 'audio', 'image'] },
      { id: 'opendlm-page', title: 'Download all links with OpenDLM', contexts: ['page'] },
    ];

    for (const menu of menus) {
      chrome.contextMenus.create(menu, () => {
        void chrome.runtime.lastError;
      });
    }
  });
}

/** Runs inside the page via chrome.scripting; must be self-contained. */
function collectPageLinks() {
  const results = [];
  const seen = new Set();

  for (const anchor of document.querySelectorAll('a[href]')) {
    const href = anchor.href;
    if (!href || !/^https?:\/\//i.test(href) || seen.has(href)) {
      continue;
    }
    seen.add(href);

    const entry = { url: href };
    const downloadName = (anchor.getAttribute('download') || '').trim();
    if (downloadName) {
      entry.filename = downloadName;
    }
    const label = (anchor.textContent || '').trim().replace(/\s+/g, ' ');
    if (label) {
      entry.description = label.slice(0, 200);
    }
    results.push(entry);
  }

  return results.slice(0, 500);
}

async function downloadAllLinksOnPage(tab, pageUrl) {
  if (!tab || typeof tab.id !== 'number') {
    return null;
  }

  let links = [];
  try {
    const injected = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: collectPageLinks,
    });
    if (Array.isArray(injected) && injected[0] && Array.isArray(injected[0].result)) {
      links = injected[0].result;
    }
  } catch (error) {
    return { type: 'error', requestType: 'addBatch', message: 'OpenDLM could not read the links on this page: ' + describeError(error) };
  }

  if (!links.length) {
    return { type: 'error', requestType: 'addBatch', message: 'No http(s) links were found on this page.' };
  }

  const userAgent = currentUserAgent();
  const items = links.slice(0, MAX_BATCH_ITEMS).map((link) => {
    const item = { type: 'addDownload', url: link.url, startNow: true };
    if (link.filename) {
      item.filename = link.filename;
    } else {
      const name = baseName(link.url);
      if (name) {
        item.filename = name;
      }
    }
    if (link.description) {
      item.description = link.description;
    }
    if (pageUrl) {
      item.pageUrl = pageUrl;
      item.referer = pageUrl;
    }
    if (userAgent) {
      item.userAgent = userAgent;
    }
    const category = categorize(item.filename || link.url);
    if (category) {
      item.category = category;
    }
    return item;
  });

  return sendToApp({ type: 'addBatch', items });
}

async function handleContextMenu(info, tab) {
  const pageUrl = (tab && tab.url) ? tab.url : (info.pageUrl || '');

  if (info.menuItemId === 'opendlm-link') {
    if (!info.linkUrl) {
      return;
    }
    const request = await buildAddDownload({
      url: info.linkUrl,
      pageUrl,
      userAgent: currentUserAgent(),
    });
    await sendToApp(request);
    return;
  }

  if (info.menuItemId === 'opendlm-media') {
    const url = info.srcUrl || info.linkUrl;
    if (!url) {
      return;
    }
    const request = await buildAddDownload({
      url,
      pageUrl,
      userAgent: currentUserAgent(),
    });
    await sendToApp(request);
    return;
  }

  if (info.menuItemId === 'opendlm-page') {
    await downloadAllLinksOnPage(tab, pageUrl);
  }
}

chrome.contextMenus.onClicked.addListener((info, tab) => {
  handleContextMenu(info, tab).catch((error) => {
    console.warn('OpenDLM: context menu action failed.', error);
  });
});

/* ------------------------------------------------------------------ *
 * Message routing (content script, popup, options)
 * ------------------------------------------------------------------ */

async function handleRuntimeMessage(message, sender) {
  switch (message.type) {
    case 'modifierIntent': {
      await sessionSet('intent', {
        at: Date.now(),
        keep: Boolean(message.keep),
        force: Boolean(message.force),
        href: typeof message.href === 'string' ? message.href : '',
        frameUrl: sender && sender.url ? sender.url : '',
      });
      return { ok: true };
    }

    case 'interceptLink': {
      if (!isWebUrl(message.url)) {
        return { ok: false, error: 'Only http(s) URLs can be handed to OpenDLM.' };
      }

      const settings = await getSettings();
      if (!settings.enabled) {
        return { ok: false, error: 'OpenDLM integration is switched off.' };
      }

      const key = 'url:' + message.url;
      if (await isHandled(key)) {
        return { ok: true, skipped: true };
      }
      await markHandled(key);

      const request = await buildAddDownload({
        url: message.url,
        filename: message.filename || '',
        pageUrl: message.pageUrl || (sender && sender.url) || '',
        referer: message.referer || (sender && sender.url) || '',
        description: message.description || '',
        userAgent: message.userAgent || currentUserAgent(),
      });

      const reply = await sendToApp(request);
      return { ok: reply.type !== 'error', reply };
    }

    case 'ping': {
      const reply = await sendToApp({ type: 'ping' });
      return { ok: true, reply };
    }

    case 'hello': {
      const reply = await sendToApp({
        type: 'hello',
        extensionVersion: chrome.runtime.getManifest().version,
      });
      return { ok: true, reply };
    }

    case 'getSettings': {
      // The extension's own copy, used by the popup and the content script.
      return { ok: true, settings: await getSettings() };
    }

    case 'appSettings': {
      // The same information as the desktop app sees it, through the
      // getSettings request of the native messaging protocol.
      const reply = await sendToApp({ type: 'getSettings' });
      return { ok: reply.type !== 'error', reply };
    }

    case 'setSettings': {
      const settings = normalizeSettings(message.settings);
      await chrome.storage.local.set({ settings });

      const pushed = [];
      for (const key of Object.keys(settings)) {
        const reply = await sendToApp({ type: 'setSetting', key, value: settings[key] });
        pushed.push({ key, ok: reply.type !== 'error' });
      }
      return { ok: true, settings, pushed };
    }

    case 'downloadUrl': {
      if (!isWebUrl(message.url)) {
        return { ok: false, error: 'Only http(s) URLs can be handed to OpenDLM.' };
      }
      const request = await buildAddDownload({
        url: message.url,
        filename: message.filename || '',
        pageUrl: message.pageUrl || '',
        description: message.description || '',
        userAgent: currentUserAgent(),
      });
      const reply = await sendToApp(request);
      return { ok: reply.type !== 'error', reply };
    }

    case 'addBatch': {
      const items = Array.isArray(message.items) ? message.items.slice(0, MAX_BATCH_ITEMS) : [];
      if (!items.length) {
        return { ok: false, error: 'Nothing to send.' };
      }
      const reply = await sendToApp({ type: 'addBatch', items });
      return { ok: reply.type !== 'error', reply };
    }

    case 'notify': {
      try {
        await chrome.notifications.create('opendlm-' + Date.now(), {
          type: 'basic',
          iconUrl: 'icons/icon128.png',
          title: message.title || 'OpenDLM',
          message: truncate(message.body || '', 220),
        });
      } catch (error) {
        // Ignore: notifications are a nicety.
      }
      return { ok: true };
    }

    default:
      return { ok: false, error: 'Unknown message type: ' + message.type };
  }
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || typeof message.type !== 'string') {
    return false;
  }

  handleRuntimeMessage(message, sender)
    .then((result) => sendResponse(result))
    .catch((error) => sendResponse({ ok: false, error: describeError(error) }));

  return true;
});

/* ------------------------------------------------------------------ *
 * Lifecycle
 * ------------------------------------------------------------------ */

async function seedDefaults() {
  try {
    const stored = await chrome.storage.local.get('settings');
    if (!stored || !stored.settings) {
      await chrome.storage.local.set({ settings: normalizeSettings(null) });
    }
  } catch (error) {
    // Non-fatal: every read path normalizes missing values anyway.
  }
}

chrome.runtime.onInstalled.addListener((details) => {
  createContextMenus();
  seedDefaults().catch(() => {});
  if (details && details.reason === 'update') {
    // A stale "host missing" flag would hide a genuine problem after an update.
    sessionSet('hostNotice', null).catch(() => {});
  }
});

chrome.runtime.onStartup.addListener(() => {
  createContextMenus();
});

// Exposed for answers to the popup and options pages without duplicating logic.
void PROTOCOL_VERSION;
