/*
 * Tests for the extension's pure decision logic.
 *
 * The extension is plain JavaScript with no build step and no test framework, so
 * the few functions that decide what the user actually sees are lifted straight out
 * of the real source and exercised here. If one is renamed or moved, the extraction
 * fails loudly, which is the point: these tests must exercise the code that
 * actually ships, never a copy of it.
 *
 * A lifted function cannot see the rest of its file, so the names it depends on are
 * handed to it explicitly when it is evaluated.
 */

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const background = readFileSync(new URL('../background.js', import.meta.url), 'utf8');
const content = readFileSync(new URL('../content.js', import.meta.url), 'utf8');

let assertions = 0;

function check(actual, expected, message) {
  assertions += 1;
  assert.deepEqual(actual, expected, message);
}

/** Returns the text of a top-level function declaration, including its body. */
function functionSource(source, name, label) {
  const start = source.search(new RegExp(`^(?:async\\s+)?function ${name}\\s*\\(`, 'm'));
  assert.ok(start >= 0, `${name} was not found in ${label}; update this test`);

  const open = source.indexOf('{', start);
  let depth = 0;
  let close = open;

  for (let index = open; index < source.length; index += 1) {
    if (source[index] === '{') {
      depth += 1;
    } else if (source[index] === '}') {
      depth -= 1;
      if (depth === 0) {
        close = index;
        break;
      }
    }
  }

  return source.slice(start, close + 1);
}

/** Returns the text of a `const NAME = { ... };` object literal. */
function objectSource(source, name, label) {
  const match = new RegExp(`const ${name}\\s*=\\s*\\{([\\s\\S]*?)\\n\\};`).exec(source);
  assert.ok(match, `${name} was not found in ${label}; update this test`);
  return `{${match[1]}}`;
}

/**
 * Evaluates a self-contained definition. A function declaration is a valid
 * expression once parenthesised, and so is an object literal.
 */
function build(text) {
  // eslint-disable-next-line no-new-func
  return new Function(`"use strict"; return (${text});`)();
}

/** The service worker runs in a browser, where detectBrowser() reads the user agent. */
function withUserAgent(userAgent, body) {
  const previous = Object.getOwnPropertyDescriptor(globalThis, 'navigator');

  Object.defineProperty(globalThis, 'navigator', {
    value: { userAgent },
    configurable: true,
  });

  try {
    return body();
  } finally {
    if (previous) {
      Object.defineProperty(globalThis, 'navigator', previous);
    } else {
      delete globalThis.navigator;
    }
  }
}

// ------------------------------------------------------------------ context menus

const defaults = build(objectSource(background, 'DEFAULT_MENU_SELECTION', 'background.js'));
const detectBrowser = build(functionSource(background, 'detectBrowser', 'background.js'));

/*
 * A function lifted out of a file cannot see that file's other declarations, and
 * new Function() only takes parameter names - passing a name would leave the value
 * undefined. The dependencies are therefore inlined as source ahead of the
 * definition, so the lifted function runs against exactly what it ships with.
 */
const selectMenus = new Function(`"use strict";
const DEFAULT_MENU_SELECTION = ${objectSource(background, 'DEFAULT_MENU_SELECTION', 'background.js')};
${functionSource(background, 'detectBrowser', 'background.js')}
return (${functionSource(background, 'selectMenus', 'background.js')});
`)();

// The fallback used when the desktop app cannot be reached.
check(defaults, { downloadWith: true, downloadAll: true, media: true }, 'the host default offers all three');

const chromeUa = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36';
const edgeUa = `${chromeUa} Edg/124.0`;
const operaUa = `${chromeUa} OPR/109.0`;
const vivaldiUa = `${chromeUa} Vivaldi/6.5`;

withUserAgent(chromeUa, () => {
  check(detectBrowser(), 'chrome', 'plain Chromium is chrome');
  check(
    selectMenus(undefined),
    { downloadWith: true, downloadAll: true, media: true },
    'no settings from the app means every entry is offered');
  check(
    selectMenus({ enabled: false }),
    { downloadWith: false, downloadAll: false, media: false },
    'switching the integration off offers nothing');
  check(
    selectMenus({ contextMenus: { media: false } }),
    { downloadWith: true, downloadAll: true, media: false },
    'a single entry can be switched off on its own');
  check(
    selectMenus({ contextMenus: { downloadWith: false, downloadAll: false } }),
    { downloadWith: false, downloadAll: false, media: true },
    'several entries can be switched off at once');
  check(
    selectMenus({ browsers: { chrome: false } }),
    { downloadWith: false, downloadAll: false, media: false },
    'disabling this browser offers nothing here');
  check(
    selectMenus({ browsers: { chrome: false, brave: true } }),
    { downloadWith: true, downloadAll: true, media: true },
    'a fork that hides its identity still honours the brave switch');
});

withUserAgent(edgeUa, () => {
  check(detectBrowser(), 'edge', 'Edge identifies itself');
  check(
    selectMenus({ browsers: { chrome: true, edge: false } }),
    { downloadWith: false, downloadAll: false, media: false },
    'disabling Edge does not disable Chrome, and must not disable Edge');
});

withUserAgent(operaUa, () => {
  check(detectBrowser(), 'opera', 'Opera identifies itself');
  check(
    selectMenus({ browsers: { opera: true } }),
    { downloadWith: true, downloadAll: true, media: true },
    'an enabled browser offers its menus');
});

withUserAgent(vivaldiUa, () => {
  check(detectBrowser(), 'vivaldi', 'Vivaldi identifies itself');
  check(
    selectMenus({ browsers: { vivaldi: false, chrome: true } }),
    { downloadWith: false, downloadAll: false, media: false },
    'disabling Vivaldi stops its own menus even when Chrome stays on');
});

// ----------------------------------------------------------------- modifier keys

const modifierActive = build(functionSource(content, 'modifierActive', 'content.js'));

function held({ altKey = false, ctrlKey = false, shiftKey = false, metaKey = false } = {}) {
  return { altKey, ctrlKey, shiftKey, metaKey };
}

const noneHeld = held();

check(modifierActive(noneHeld, 'Alt'), false, 'Alt is not held');
check(modifierActive(held({ altKey: true }), 'Alt'), true, 'Alt is held');
check(modifierActive(held({ ctrlKey: true }), 'Ctrl'), true, "the app's Ctrl spelling works");
check(modifierActive(held({ ctrlKey: true }), 'Control'), true, "the DOM's Control spelling works");
check(modifierActive(held({ ctrlKey: true }), 'ctrl'), true, 'the spelling is case-insensitive');
check(modifierActive(held({ shiftKey: true }), 'Shift'), true, 'Shift is held');
check(modifierActive(held({ metaKey: true }), 'Meta'), true, 'Meta is held');
check(modifierActive(noneHeld, 'None'), false, 'None never matches');
check(modifierActive(noneHeld, ''), false, 'an empty specification never matches');
check(
  modifierActive(held({ altKey: true, ctrlKey: true, shiftKey: true }), 'Alt+Ctrl+Shift'),
  true,
  'a group matches when any of its modifiers is held');
check(
  modifierActive(noneHeld, 'Alt+Ctrl+Shift'),
  false,
  'a group does not match when none of its modifiers is held');
check(
  modifierActive(held({ ctrlKey: true }), 'Alt+Ctrl+Shift'),
  true,
  'one held modifier is enough for a group');
check(modifierActive(held({ altKey: true }), 'Alt, Ctrl'), true, 'commas are accepted as separators');
check(modifierActive(noneHeld, 'Nonsense'), false, 'an unknown name never matches');
check(modifierActive(noneHeld, 'Alt+Nonsense'), false, 'an unknown name inside a group creates no match');

console.log(`extension logic: ${assertions} assertions passed`);
