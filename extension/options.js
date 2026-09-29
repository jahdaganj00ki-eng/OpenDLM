/*
 * OpenDLM options page.
 *
 * Settings live in chrome.storage.local so the service worker and the content
 * script can read them without a round trip, and every save is also pushed to
 * the desktop app as individual setSetting messages.
 */

/* Kept in sync with background.js. There is no bundler, so the defaults are
 * duplicated here on purpose rather than shared through a module. */
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

const MAX_EXTENSIONS = 500;

const els = {
  extVersion: document.getElementById('ext-version'),
  enabled: document.getElementById('enabled'),
  mediaSniffing: document.getElementById('media-sniffing'),
  forceKey: document.getElementById('force-key'),
  keepKey: document.getElementById('keep-key'),
  extensions: document.getElementById('extensions'),
  save: document.getElementById('save'),
  test: document.getElementById('test'),
  reset: document.getElementById('reset'),
  status: document.getElementById('status'),
};

function setStatus(text, tone) {
  els.status.textContent = text || '';
  if (tone) {
    els.status.dataset.tone = tone;
  } else {
    delete els.status.dataset.tone;
  }
}

function normalizeExtension(value) {
  if (typeof value !== 'string') {
    return '';
  }
  return value.trim().toLowerCase().replace(/^\.+/, '').replace(/[^a-z0-9.+-]/g, '');
}

function parseExtensionList(text) {
  const seen = new Set();
  for (const rawLine of String(text || '').split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith('#') || line.startsWith('//') || line.startsWith(';')) {
      continue;
    }
    const cleaned = normalizeExtension(line);
    if (cleaned) {
      seen.add(cleaned);
    }
    if (seen.size >= MAX_EXTENSIONS) {
      break;
    }
  }
  return Array.from(seen).sort();
}

function formatExtensionList(list) {
  const values = Array.isArray(list) && list.length ? list : DEFAULT_TAKEOVER_EXTENSIONS;
  const lines = [];
  for (let index = 0; index < values.length; index += 12) {
    lines.push(values.slice(index, index + 12).join(' '));
  }
  return lines.join('\n');
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
  if (typeof raw.forceTakeoverKey === 'string') {
    settings.forceTakeoverKey = raw.forceTakeoverKey;
  }
  if (typeof raw.keepDownloadKey === 'string') {
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

async function readStoredSettings() {
  try {
    const stored = await chrome.storage.local.get('settings');
    return normalizeSettings(stored ? stored.settings : null);
  } catch (error) {
    return normalizeSettings(null);
  }
}

async function sendRuntime(message) {
  try {
    const reply = await chrome.runtime.sendMessage(message);
    return reply || { ok: false, error: 'The extension background worker did not respond.' };
  } catch (error) {
    return { ok: false, error: error && error.message ? error.message : String(error) };
  }
}

function render(settings) {
  els.enabled.checked = settings.enabled;
  els.mediaSniffing.checked = settings.mediaSniffing;
  els.forceKey.value = settings.forceTakeoverKey;
  els.keepKey.value = settings.keepDownloadKey;
  els.extensions.value = formatExtensionList(settings.takeoverExtensions);
}

function collectFormSettings() {
  const extensions = parseExtensionList(els.extensions.value);
  return {
    enabled: els.enabled.checked,
    mediaSniffing: els.mediaSniffing.checked,
    forceTakeoverKey: els.forceKey.value,
    keepDownloadKey: els.keepKey.value,
    takeoverExtensions: extensions.length ? extensions : DEFAULT_SETTINGS.takeoverExtensions.slice(),
  };
}

async function save() {
  els.save.disabled = true;
  setStatus('Saving...');

  try {
    const settings = collectFormSettings();

    if (settings.forceTakeoverKey !== 'None' && settings.forceTakeoverKey === settings.keepDownloadKey) {
      setStatus('The force-takeover key and the keep-in-browser key are the same, so takeover cannot be forced. Pick different keys.', 'warn');
      return;
    }

    try {
      await chrome.storage.local.set({ settings });
    } catch (error) {
      setStatus('The settings could not be written to extension storage.', 'bad');
      return;
    }

    // Mirror the values into the desktop app, one key at a time.
    const result = await sendRuntime({ type: 'setSettings', settings: settings });
    const pushed = result && Array.isArray(result.pushed) ? result.pushed : [];
    const accepted = pushed.filter((entry) => entry && entry.ok).length;

    if (accepted === pushed.length && pushed.length > 0) {
      setStatus('Saved. OpenDLM accepted all ' + accepted + ' settings.', 'ok');
    } else if (accepted > 0) {
      setStatus('Saved and sent to OpenDLM. OpenDLM accepted ' + accepted + ' of ' + pushed.length + ' settings.', 'warn');
    } else {
      setStatus('Saved in the browser. OpenDLM is not running, so the settings were not mirrored to the app yet.', 'warn');
    }

    render(settings);
  } finally {
    els.save.disabled = false;
  }
}

async function testConnection() {
  els.test.disabled = true;
  setStatus('Contacting OpenDLM...');

  try {
    const result = await sendRuntime({
      type: 'hello',
    });
    const reply = result && result.reply ? result.reply : null;

    if (!reply) {
      setStatus('The extension background worker did not answer: ' + ((result && result.error) || 'unknown error'), 'bad');
      return;
    }

    if (reply.type === 'error') {
      setStatus('OpenDLM could not be reached: ' + (reply.message || 'unknown error'), 'bad');
      return;
    }

    if (reply.running) {
      setStatus('Connected to OpenDLM ' + (reply.appVersion || '(version unknown)') + ', protocol ' + reply.protocol + '.', 'ok');
      return;
    }

    setStatus('The OpenDLM desktop app is not running. It starts automatically the next time you send it a download.', 'warn');
  } finally {
    els.test.disabled = false;
  }
}

async function restoreDefaults() {
  render(normalizeSettings(null));
  setStatus('Default values are shown. Choose "Save settings" to apply them.');
}

function wire() {
  els.save.addEventListener('click', () => {
    save().catch((error) => setStatus(String(error && error.message ? error.message : error), 'bad'));
  });

  els.test.addEventListener('click', () => {
    testConnection().catch((error) => setStatus(String(error && error.message ? error.message : error), 'bad'));
  });

  els.reset.addEventListener('click', () => {
    restoreDefaults().catch((error) => setStatus(String(error && error.message ? error.message : error), 'bad'));
  });

  window.addEventListener('keydown', (event) => {
    if ((event.ctrlKey || event.metaKey) && event.key === 's') {
      event.preventDefault();
      save().catch(() => {});
    }
  });
}

(async () => {
  try {
    els.extVersion.textContent = chrome.runtime.getManifest().version;
  } catch (error) {
    els.extVersion.textContent = 'unknown';
  }

  wire();
  render(await readStoredSettings());
})();
