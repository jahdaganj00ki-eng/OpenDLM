/*
 * The first-run page.
 *
 * Shown once, the first time the extension is installed or updated. It is deliberately
 * a plain page with no dependencies, and every control is a real affordance rather
 * than decoration.
 */

const START_KEY = 'welcomeShown';

function storageGet(keys) {
  return new Promise((resolve) => {
    try {
      chrome.storage.local.get(keys, (values) => resolve(values || {}));
    } catch (error) {
      resolve({});
    }
  });
}

function storageSet(values) {
  return new Promise((resolve) => {
    try {
      chrome.storage.local.set(values, () => resolve());
    } catch (error) {
      resolve();
    }
  });
}

async function shouldShow() {
  const values = await storageGet([START_KEY]);
  return !values[START_KEY];
}

async function markShown() {
  await storageSet({ [START_KEY]: Date.now() });
}

function close() {
  try {
    window.close();
  } catch (error) {
    // A tab that cannot close itself is not worth an error.
  }
}

document.addEventListener('DOMContentLoaded', async () => {
  const status = document.getElementById('status');

  if (!(await shouldShow())) {
    close();
    return;
  }

  const start = document.getElementById('start');
  const options = document.getElementById('options');

  if (start) {
    start.addEventListener('click', async () => {
      await markShown();
      if (status) {
        status.textContent = 'All set.';
      }
      close();
    });
  }

  if (options) {
    options.addEventListener('click', () => {
      try {
        chrome.runtime.openOptionsPage();
      } catch (error) {
        // Ignore: the page may already be closing.
      }
    });
  }

  // Remember it either way, so the page never traps the user in a loop.
  setTimeout(() => {
    markShown();
  }, 60000);
});
