/*
 * Tests for the extension's pure decision logic.
 *
 * The extension is plain JavaScript with no build step and no test framework, so
 * the two functions that decide the user's visible behaviour are lifted straight
 * out of the real source and exercised here. If either is renamed or moved the
 * extraction fails loudly, which is the point: the tests must exercise the code
 * that actually ships, not a copy of it.
 */

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const background = readFileSync(new URL('../background.js', import.meta.url), 'utf8');
const content = readFileSync(new URL('../content.js', import.meta.url), 'utf8');

/** Lifts a top-level function declaration out of a source file and evaluates it. */
function loadFunction(source, name, label) {
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

  // eslint-disable-next-line no-eval
  return eval(`(${source.slice(start, close + 1)})`);
}

/** Lifts a `const NAME = { ... };` object literal out of a source file. */
function loadObject(source, name, label) {
  const match = new RegExp(`const ${name}\\s*=\\s*\\{([\\s\\S]*?)\\n\\};`).exec(source);
  assert.ok(match, `${name} was not found in ${label}; update this test`);

  // eslint-disable-next-line no-eval
  return eval(`({${match[1]}})`);
}

// The service worker runs in a browser, where detectBrowser() reads the user agent.
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

const selectMenus = loadFunction(background, 'selectMenus', 'background.js');
const defaults = loadObject(background, 'DEFAULT_MENU_SELECTION', 'background.js');

// The host's own defaults, used when the desktop app cannot be reached.
assert.deepEqual(defaults, { downloadWith: true, downloadAll: true, media: true });

const chromeUa = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36';
const edgeUa = `${chromeUa} Edg/124.0`;
const operaUa = `${chromeUa} OPR/109.0`;

withUserAgent(chromeUa, () => {
  assert.deepEqual(
    selectMenus(undefined),
    { downloadWith: true, downloadAll: true, media: true },
    'no settings from the app means every entry is offered');

  assert.deepEqual(
    selectMenus({ enabled: false }),
    { downloadWith: false, downloadAll: false, media: false },
    'switching the integration off offers nothing');

  assert.deepEqual(
    selectMenus({ contextMenus: { media: false } }),
    { downloadWith: true, downloadAll: true, media: false },
    'a single entry can be switched off on its own');

  assert.deepEqual(
    selectMenus({ contextMenus: { downloadWith: false, downloadAll: false } }),
    { downloadWith: false, downloadAll: false, media: true },
    'several entries can be switched off at once');

  assert.deepEqual(
    selectMenus({ browsers: { chrome: false } }),
    { downloadWith: false, downloadAll: false, media: false },
    'disabling this browser offers nothing here');

  assert.deepEqual(
    selectMenus({ browsers: { chrome: false, brave: true } }),
    { downloadWith: true, downloadAll: true, media: true },
    'a fork that hides its identity still honours the brave switch');
});

withUserAgent(edgeUa, () => {
  assert.deepEqual(
    selectMenus({ browsers: { chrome: true, edge: false } }),
    { downloadWith: false, downloadAll: false, media: false },
    'disabling Edge does not disable Chrome, and must not disable Edge');
});

withUserAgent(operaUa, () => {
  assert.deepEqual(
    selectMenus({ browsers: { opera: true } }),
    { downloadWith: true, downloadAll: true, media: true },
    'an enabled browser offers its menus');
});

// ----------------------------------------------------------------- modifier keys

const modifierActive = loadFunction(content, 'modifierActive', 'content.js');

function withModifiers({ altKey = false, ctrlKey = false, shiftKey = false, metaKey = false } = {}) {
  return { altKey, ctrlKey, shiftKey, metaKey };
}

const noneHeld = withModifiers();

assert.equal(modifierActive(noneHeld, 'Alt'), false, 'Alt is not held');
assert.equal(modifierActive(withModifiers({ altKey: true }), 'Alt'), true, 'Alt is held');
assert.equal(modifierActive(withModifiers({ ctrlKey: true }), 'Ctrl'), true,
  "the application's Ctrl spelling works");
assert.equal(modifierActive(withModifiers({ ctrlKey: true }), 'Control'), true,
  "the DOM's Control spelling works");
assert.equal(modifierActive(withModifiers({ ctrlKey: true }), 'ctrl'), true,
  'the spelling is case-insensitive');
assert.equal(modifierActive(withModifiers({ shiftKey: true }), 'Shift'), true, 'Shift is held');
assert.equal(modifierActive(withModifiers({ metaKey: true }), 'Meta'), true, 'Meta is held');

assert.equal(modifierActive(withModifiers({ shiftKey: true }), 'None'), false, 'None never matches');
assert.equal(modifierActive(noneHeld, 'None'), false, 'None never matches, even held');
assert.equal(modifierActive(noneHeld, ''), false, 'an empty specification never matches');
assert.equal(modifierActive(withModifiers({ altKey: true, ctrlKey: true, shiftKey: true }), 'Alt+Ctrl+Shift'),
  true, 'a group matches when any of its modifiers is held');
assert.equal(modifierActive(noneHeld, 'Alt+Ctrl+Shift'), false,
  'a group does not match when none of its modifiers is held');
assert.equal(modifierActive(withModifiers({ ctrlKey: true }), 'Alt+Ctrl+Shift'), true,
  'one held modifier is enough for a group');
assert.equal(modifierActive(withModifiers({ altKey: true }), 'Alt, Ctrl'), true,
  'commas are accepted as separators');
assert.equal(modifierActive(noneHeld, 'Nonsense'), false, 'an unknown name never matches');
assert.equal(modifierActive(noneHeld, 'Alt+Nonsense'), false,
  'an unknown name inside a group does not create a match');

console.log('extension logic: 30 assertions passed');
