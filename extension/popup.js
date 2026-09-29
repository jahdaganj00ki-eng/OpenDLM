/*
 * OpenDLM toolbar popup.
 *
 * The popup is short lived: it re-reads settings and re-pings the app every
 * time it opens, and never assumes anything survived from a previous opening.
 */

const els = {
  extVersion: document.getElementById('ext-version'),
  statusDot: document.getElementById('status-dot'),
  statusTitle: document.getElementById('status-title'),
  statusDetail: document.getElementById('status-detail'),
  toggle: document.getElementById('toggle-enabled'),
  videos: document.getElementById('btn-videos'),
  options: document.getElementById('btn-options'),
  actionStatus: document.getElementById('action-status'),
};

const MEDIA_PATTERN = /\.(mp4|webm|mkv|mp3|m4a|m3u8|mpd)$/i;

/** How long to wait for the app before reporting "not running". */
const PING_TIMEOUT_MS = 6000;

function setStatus(state, title, detail) {
  els.statusDot.dataset.state = state;
  els.statusTitle.textContent = title;
  els.statusDetail.textContent = detail;
}

function setActionStatus(text) {
  els.actionStatus.textContent = text || '';
}

/** Wraps a promise so a hung app cannot leave the popup spinning forever. */
function withTimeout(promise, ms) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Timed out after ' + ms + ' ms.')), ms);
    promise.then(
      (value) => { clearTimeout(timer); resolve(value); },
      (error) => { clearTimeout(timer); reject(error); },
    );
  });
}

function baseName(value) {
  if (typeof value !== 'string' || !value) {
    return '';
  }
  const withoutFragment = value.split('#')[0].split('?')[0];
  const parts = withoutFragment.split('/');
  const last = parts[parts.length - 1] || '';
  try {
    return decodeURIComponent(last);
  } catch (error) {
    return last;
  }
}

function extensionOf(url) {
  const name = baseName(url);
  const dot = name.lastIndexOf('.');
  return dot < 0 ? '' : name.slice(dot + 1).toLowerCase();
}

async function sendRuntime(message) {
  try {
    const reply = await chrome.runtime.sendMessage(message);
    return reply || { ok: false, error: 'The extension background worker did not respond.' };
  } catch (error) {
    return { ok: false, error: error && error.message ? error.message : String(error) };
  }
}

async function loadVersion() {
  try {
    els.extVersion.textContent = 'version ' + chrome.runtime.getManifest().version;
  } catch (error) {
    els.extVersion.textContent = 'version unknown';
  }
}

async function loadSettings() {
  try {
    const stored = await chrome.storage.local.get('settings');
    const settings = stored && stored.settings ? stored.settings : {};
    els.toggle.checked = settings.enabled !== false;
  } catch (error) {
    els.toggle.checked = true;
  }
}

async function saveEnabled(enabled) {
  try {
    const stored = await chrome.storage.local.get('settings');
    const settings = Object.assign({}, stored && stored.settings ? stored.settings : {});
    settings.enabled = enabled;
    await chrome.storage.local.set({ settings });
    await sendRuntime({ type: 'setSettings', settings });
    setActionStatus(enabled ? 'Takeover enabled.' : 'Takeover disabled.');
  } catch (error) {
    setActionStatus('Could not save the setting.');
  }
}

async function checkConnection() {
  setStatus('unknown', 'Checking the app...', 'Contacting the OpenDLM desktop app.');

  const result = await sendRuntime({ type: 'ping' });
  const reply = result && result.reply ? result.reply : null;

  if (!reply) {
    setStatus('warn', 'No answer', (result && result.error) || 'The extension could not reach the native host.');
    return false;
  }

  if (reply.type === 'pong' && reply.running) {
    const version = reply.appVersion ? 'OpenDLM ' + reply.appVersion : 'OpenDLM';
    setStatus('ok', 'Connected', version + ' is running and ready.');
    return true;
  }

  if (reply.type === 'pong') {
    setStatus('warn', 'App not running', 'The OpenDLM desktop app is not running. It will be started automatically when you send a download.');
    return false;
  }

  setStatus('warn', 'Unexpected reply', reply.message || 'The app replied with an unexpected message.');
  return false;
}

/** Runs inside the page through chrome.scripting; must be fully self-contained. */
function collectPageMedia() {
  const pattern = /\.(mp4|webm|mkv|mp3|m4a|m3u8|mpd)$/i;
  const results = [];
  const seen = new Set();

  function usable(url) {
    return typeof url === 'string' && /^https?:\/\//i.test(url);
  }

  function add(url) {
    if (!usable(url) || seen.has(url)) {
      return;
    }
    seen.add(url);
    results.push({ url: url, filename: '' });
  }

  function strip(url) {
    return url.split('#')[0].split('?')[0];
  }

  for (const element of document.querySelectorAll('video, audio, source')) {
    const candidates = [element.currentSrc, element.src];
    let hit = '';
    for (const candidate of candidates) {
      if (usable(candidate) && pattern.test(strip(candidate))) {
        hit = candidate;
        break;
      }
    }
    if (!hit) {
      for (const candidate of candidates) {
        if (usable(candidate)) {
          hit = candidate;
          break;
        }
      }
    }
    add(hit);
  }

  for (const entry of performance.getEntriesByType('resource')) {
    if (pattern.test(strip(entry.name))) {
      add(entry.name);
    }
  }

  return results.slice(0, 50);
}

async function downloadPageVideos() {
  els.videos.disabled = true;
  setActionStatus('Looking for media on this page...');

  try {
    const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
    const tab = tabs && tabs[0];
    if (!tab || typeof tab.id !== 'number') {
      setActionStatus('No active tab to inspect.');
      return;
    }

    let found = [];
    try {
      const injected = await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        func: collectPageMedia,
      });
      if (Array.isArray(injected) && injected[0] && Array.isArray(injected[0].result)) {
        found = injected[0].result;
      }
    } catch (error) {
      setActionStatus('This page cannot be inspected (browser pages and the Web Store are off limits).');
      return;
    }

    if (!found.length) {
      setActionStatus('No downloadable media was found on this page.');
      return;
    }

    const items = found.map((entry) => {
      const item = {
        type: 'addDownload',
        url: entry.url,
        startNow: true,
        pageUrl: tab.url || '',
        referer: tab.url || '',
      };
      const name = baseName(entry.url);
      if (name) {
        item.filename = name;
      }
      const extension = extensionOf(entry.url);
      if (extension === 'mp4' || extension === 'webm' || extension === 'mkv' || extension === 'm3u8' || extension === 'mpd') {
        item.category = 'video';
      } else if (extension === 'mp3' || extension === 'm4a') {
        item.category = 'audio';
      }
      return item;
    });

    const result = await sendRuntime({ type: 'addBatch', items: items });
    if (result && result.ok) {
      setActionStatus('Sent ' + items.length + (items.length === 1 ? ' item' : ' items') + ' to OpenDLM.');
    } else {
      setActionStatus((result && result.error) || 'OpenDLM could not accept the request.');
    }
  } finally {
    els.videos.disabled = false;
  }
}

els.toggle.addEventListener('change', () => {
  saveEnabled(els.toggle.checked).catch(() => setActionStatus('Could not save the setting.'));
});

els.videos.addEventListener('click', () => {
  downloadPageVideos().catch((error) => setActionStatus(String(error && error.message ? error.message : error)));
});

els.options.addEventListener('click', () => {
  chrome.runtime.openOptionsPage();
});

(async () => {
  await loadVersion();
  await loadSettings();
  try {
    await withTimeout(checkConnection(), PING_TIMEOUT_MS);
  } catch (error) {
    setStatus('warn', 'No answer', 'The app did not reply in time. It may still be starting up.');
  }
})();
