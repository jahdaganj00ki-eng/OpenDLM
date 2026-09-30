/*
 * OpenDLM content script.
 *
 * Three jobs:
 *   1. record whether a modifier key was held when a link was clicked, so the
 *      service worker knows whether the user wanted the browser or OpenDLM to
 *      handle the download;
 *   2. intercept clicks on download-looking links in the capture phase and
 *      hand them straight to OpenDLM;
 *   3. offer a small, dismissible in-page badge on video elements.
 *
 * Loaded as a classic content script: no imports, no build step.
 */

(() => {
  if (window.__opendlmContentLoaded) {
    return;
  }
  window.__opendlmContentLoaded = true;

  const MEDIA_PATTERN = /\.(mp4|webm|mkv|mp3|m4a|m3u8|mpd)$/i;
  const BLOCKED_SCHEMES = /^(javascript|mailto|tel|sms|callto|data|blob|about|chrome|chrome-extension|moz-extension|edge|view-source|file|ftp):/i;
  const MIN_BADGE_WIDTH = 220;
  const MIN_BADGE_HEIGHT = 120;

  const DEFAULT_SETTINGS = {
    enabled: true,
    mediaSniffing: true,
    forceTakeoverKey: 'Alt',
    keepDownloadKey: 'Shift',
    takeoverExtensions: [],
  };

  let settings = Object.assign({}, DEFAULT_SETTINGS);

  /* Media URLs seen on this page, whether or not they sit in a <video>. */
  const sniffedMedia = new Set();

  /* Media URLs the user hid the badge for during this page view. */
  const dismissedMedia = new Set();

  /* ------------------------------------------------------------------ *
   * Settings
   * ------------------------------------------------------------------ */

  function normalizeSettings(raw) {
    const next = Object.assign({}, DEFAULT_SETTINGS);
    if (!raw || typeof raw !== 'object') {
      return next;
    }
    if (typeof raw.enabled === 'boolean') {
      next.enabled = raw.enabled;
    }
    if (typeof raw.mediaSniffing === 'boolean') {
      next.mediaSniffing = raw.mediaSniffing;
    }
    if (typeof raw.forceTakeoverKey === 'string') {
      next.forceTakeoverKey = raw.forceTakeoverKey;
    }
    if (typeof raw.keepDownloadKey === 'string') {
      next.keepDownloadKey = raw.keepDownloadKey;
    }
    if (Array.isArray(raw.takeoverExtensions)) {
      next.takeoverExtensions = raw.takeoverExtensions
        .filter((entry) => typeof entry === 'string')
        .map((entry) => entry.trim().toLowerCase().replace(/^\.+/, ''))
        .filter(Boolean);
    }
    return next;
  }

  function loadSettings() {
    try {
      chrome.storage.local.get('settings', (stored) => {
        void chrome.runtime.lastError;
        settings = normalizeSettings(stored ? stored.settings : null);
        applySettings();
      });
    } catch (error) {
      settings = normalizeSettings(null);
    }
  }

  function applySettings() {
    if (!settings.mediaSniffing) {
      hideBadge();
    }
  }

  try {
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area === 'local' && changes.settings) {
        settings = normalizeSettings(changes.settings.newValue);
        applySettings();
      }
    });
  } catch (error) {
    // Storage events are a convenience; the initial load is enough.
  }

  /* ------------------------------------------------------------------ *
   * Small helpers
   * ------------------------------------------------------------------ */

  function send(message) {
    try {
      chrome.runtime.sendMessage(message, () => {
        void chrome.runtime.lastError;
      });
    } catch (error) {
      // The worker may be starting up; the next action will retry.
    }
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

  function extensionOf(value) {
    const name = baseName(value);
    const dot = name.lastIndexOf('.');
    if (dot < 0 || dot === name.length - 1) {
      return '';
    }
    const extension = name.slice(dot + 1).toLowerCase().replace(/[^a-z0-9.+-]/g, '');
    return extension.length > 12 ? '' : extension;
  }

  function withoutFragment(url) {
    const index = url.indexOf('#');
    return index < 0 ? url : url.slice(0, index);
  }

  /**
   * True when a takeover modifier is held.
   *
   * Accepts a single modifier or a '+'-separated group such as "Alt+Ctrl", so
   * several bindings can be active at once, and accepts both the application's
   * spelling ("Ctrl") and the DOM's ("Control") - only one of the two used to
   * work. "None" and an empty value never match.
   *
   * The semantics are "any of these is held", because a group of force bindings
   * means "hold Alt, or Ctrl, or Shift to force", not "hold all three".
   */
  function modifierActive(event, key) {
    if (!key || key === 'None') {
      return false;
    }

    const parts = String(key).split(/[+\s,;]+/).filter(Boolean);

    for (const part of parts) {
      switch (part.toLowerCase()) {
        case 'alt':
        case 'option':
          if (event.altKey) { return true; }
          break;
        case 'ctrl':
        case 'control':
          if (event.ctrlKey) { return true; }
          break;
        case 'shift':
          if (event.shiftKey) { return true; }
          break;
        case 'meta':
        case 'cmd':
        case 'win':
          if (event.metaKey) { return true; }
          break;
        default:
          break;
      }
    }

    return false;
  }

  function findAnchor(node) {
    if (!node || typeof node.closest !== 'function') {
      return null;
    }
    return node.closest('a[href], area[href]');
  }

  /* ------------------------------------------------------------------ *
   * 1. Modifier intent
   * ------------------------------------------------------------------ */

  function recordIntent(event) {
    send({
      type: 'modifierIntent',
      keep: modifierActive(event, settings.keepDownloadKey),
      force: modifierActive(event, settings.forceTakeoverKey),
      href: '',
    });
  }

  document.addEventListener('pointerdown', recordIntent, true);
  document.addEventListener('mousedown', recordIntent, true);

  /* ------------------------------------------------------------------ *
   * 2. Link interception
   * ------------------------------------------------------------------ */

  function isInterceptableLink(anchor) {
    const raw = anchor.getAttribute('href') || '';
    if (!raw || raw.startsWith('#')) {
      return null;
    }
    if (BLOCKED_SCHEMES.test(raw.trim())) {
      return null;
    }

    let resolved;
    try {
      resolved = new URL(anchor.href, document.baseURI);
    } catch (error) {
      return null;
    }

    if (resolved.protocol !== 'http:' && resolved.protocol !== 'https:') {
      return null;
    }

    // Same-document links (including "#" navigation) stay with the page.
    if (withoutFragment(resolved.href) === withoutFragment(location.href)) {
      return null;
    }

    return resolved.href;
  }

  function onClick(event) {
    if (event.defaultPrevented) {
      return;
    }
    if (typeof event.button === 'number' && event.button !== 0) {
      return;
    }

    // Ctrl/Cmd means "open in a new tab" - never hijack that.
    if (event.ctrlKey || event.metaKey) {
      return;
    }

    const anchor = findAnchor(event.target);
    if (!anchor) {
      return;
    }

    const href = isInterceptableLink(anchor);
    if (!href) {
      return;
    }

    // Refresh the intent right before the default action runs.
    const keep = modifierActive(event, settings.keepDownloadKey);
    const force = modifierActive(event, settings.forceTakeoverKey);
    send({ type: 'modifierIntent', keep, force, href });

    if (keep) {
      // The user explicitly wants the browser to handle this one.
      return;
    }

    if (!settings.enabled) {
      return;
    }

    const downloadAttribute = anchor.hasAttribute('download');
    const declaredName = (anchor.getAttribute('download') || '').trim();
    const extension = extensionOf(declaredName || href);
    const listed = Boolean(extension) && settings.takeoverExtensions.includes(extension);
    const targetsBlank = anchor.target && anchor.target.toLowerCase() === '_blank';

    if (!downloadAttribute && !listed && !force) {
      return;
    }

    // A plain "_blank" navigation is page navigation, not a download, unless the
    // link is explicitly a download or its type is on the takeover list.
    if (targetsBlank && !downloadAttribute && !listed) {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();

    const message = {
      type: 'interceptLink',
      url: href,
      filename: declaredName || baseName(href),
      pageUrl: location.href,
      referer: location.href,
      userAgent: navigator.userAgent,
    };

    try {
      chrome.runtime.sendMessage(message, () => {
        void chrome.runtime.lastError;
      });
    } catch (error) {
      // Nothing else to do; the click was already stopped.
    }
  }

  document.addEventListener('click', onClick, true);

  /* ------------------------------------------------------------------ *
   * 3. Media sniffing and the in-page badge
   * ------------------------------------------------------------------ */

  function isFetchable(url) {
    return typeof url === 'string' && /^https?:\/\//i.test(url);
  }

  function looksLikeMedia(url) {
    if (!isFetchable(url)) {
      return false;
    }
    return MEDIA_PATTERN.test(url.split('#')[0].split('?')[0]);
  }

  function mediaUrlOf(element) {
    if (!element) {
      return '';
    }
    const candidates = [element.currentSrc, element.src];
    for (const candidate of candidates) {
      if (looksLikeMedia(candidate)) {
        return candidate;
      }
    }
    if (isFetchable(element.currentSrc)) {
      return element.currentSrc;
    }
    if (isFetchable(element.src)) {
      return element.src;
    }
    return '';
  }

  function sniff() {
    try {
      const elements = document.querySelectorAll('video, audio, source');
      for (const element of elements) {
        const url = mediaUrlOf(element);
        if (url) {
          sniffedMedia.add(url);
        }
      }

      const entries = performance.getEntriesByType('resource');
      for (const entry of entries) {
        if (looksLikeMedia(entry.name)) {
          sniffedMedia.add(entry.name);
        }
      }
    } catch (error) {
      // Sniffing is opportunistic.
    }
  }

  /* --- badge ------------------------------------------------------- */

  let badge = null;
  let badgeLabel = null;
  let badgeVideo = null;
  let badgeVisible = false;
  let hideTimer = 0;
  let flashTimer = 0;
  let positionQueued = false;

  const BADGE_TEXT = 'Download with OpenDLM';

  function buildBadge() {
    const root = document.createElement('div');
    root.className = 'opendlm-badge';
    root.hidden = true;

    const label = document.createElement('span');
    label.className = 'opendlm-badge-label';
    label.textContent = BADGE_TEXT;

    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'opendlm-badge-close';
    close.textContent = '\u00d7';
    close.title = 'Hide for this video';
    close.setAttribute('aria-label', 'Hide for this video');

    root.appendChild(label);
    root.appendChild(close);

    root.addEventListener('mousedown', (event) => {
      event.stopPropagation();
    }, true);

    close.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();
      const video = badgeVideo;
      if (video) {
        const url = mediaUrlOf(video);
        if (url) {
          dismissedMedia.add(url);
        }
      }
      hideBadge();
    });

    root.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();
      const video = badgeVideo;
      if (!video) {
        return;
      }
      const url = mediaUrlOf(video);
      if (!url) {
        return;
      }
      send({
        type: 'interceptLink',
        url,
        filename: baseName(url),
        pageUrl: location.href,
        referer: location.href,
        userAgent: navigator.userAgent,
        description: document.title,
      });
      flashBadge('Sent to OpenDLM');
    });

    badge = root;
    badgeLabel = label;
    (document.body || document.documentElement).appendChild(root);
  }

  function ensureBadge() {
    if (!badge) {
      buildBadge();
    }
    return badge;
  }

  function positionBadge() {
    positionQueued = false;
    if (!badge || !badgeVisible || !badgeVideo) {
      return;
    }

    const rect = badgeVideo.getBoundingClientRect();
    const width = badge.offsetWidth || 170;
    const height = badge.offsetHeight || 28;

    let left = rect.right - width - 10;
    let top = rect.top + 10;

    left = Math.max(8, Math.min(left, window.innerWidth - width - 8));
    top = Math.max(8, Math.min(top, window.innerHeight - height - 8));

    badge.style.left = left + 'px';
    badge.style.top = top + 'px';
  }

  function queuePosition() {
    if (positionQueued) {
      return;
    }
    positionQueued = true;
    window.requestAnimationFrame(positionBadge);
  }

  function showBadgeFor(video) {
    if (!settings.mediaSniffing) {
      return;
    }

    const url = mediaUrlOf(video);
    if (url && dismissedMedia.has(url)) {
      return;
    }

    const rect = video.getBoundingClientRect();
    if (rect.width < MIN_BADGE_WIDTH || rect.height < MIN_BADGE_HEIGHT) {
      return;
    }

    window.clearTimeout(hideTimer);
    ensureBadge();

    if (badgeVideo !== video) {
      badgeVideo = video;
      badgeLabel.textContent = BADGE_TEXT;
    }

    if (!badgeVisible) {
      badge.hidden = false;
      badge.classList.add('opendlm-badge-visible');
      badgeVisible = true;
    }

    positionBadge();
  }

  function hideBadge() {
    if (!badge || !badgeVisible) {
      return;
    }
    badge.classList.remove('opendlm-badge-visible');
    badge.hidden = true;
    badgeVisible = false;
    badgeVideo = null;
  }

  function scheduleHide() {
    window.clearTimeout(hideTimer);
    hideTimer = window.setTimeout(hideBadge, 320);
  }

  function flashBadge(text) {
    if (!badge || !badgeLabel) {
      return;
    }
    window.clearTimeout(flashTimer);
    badgeLabel.textContent = text;
    flashTimer = window.setTimeout(() => {
      if (badgeLabel) {
        badgeLabel.textContent = BADGE_TEXT;
      }
    }, 1600);
  }

  function onMouseMove(event) {
    if (!settings.mediaSniffing) {
      return;
    }

    const target = event.target;
    const video = (target && typeof target.closest === 'function') ? target.closest('video') : null;

    if (video) {
      showBadgeFor(video);
      return;
    }

    if (!badgeVisible) {
      return;
    }

    if (badge && target && badge.contains(target)) {
      return;
    }

    if (badgeVideo) {
      const rect = badgeVideo.getBoundingClientRect();
      const near = event.clientX >= rect.left - 8
        && event.clientX <= rect.right + 8
        && event.clientY >= rect.top - 8
        && event.clientY <= rect.bottom + 8;
      if (near) {
        return;
      }
    }

    scheduleHide();
  }

  document.addEventListener('mousemove', onMouseMove, true);
  document.addEventListener('loadedmetadata', sniff, true);
  document.addEventListener('play', sniff, true);
  window.addEventListener('scroll', queuePosition, { passive: true, capture: true });
  window.addEventListener('resize', queuePosition, { passive: true });
  window.addEventListener('pagehide', hideBadge, true);

  /* ------------------------------------------------------------------ *
   * Start
   * ------------------------------------------------------------------ */

  loadSettings();
  sniff();
  window.setInterval(sniff, 3000);
  window.addEventListener('load', sniff, { once: true });

  /* Expose a tiny read-only summary for debugging from the page console. */
  try {
    Object.defineProperty(window, '__opendlm', {
      value: Object.freeze({
        sniffed: () => Array.from(sniffedMedia),
        version: chrome.runtime.getManifest().version,
      }),
      configurable: true,
    });
  } catch (error) {
    // Purely diagnostic.
  }
})();
