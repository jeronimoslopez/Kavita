# Yomitan in Kavita

Everything in `assets/yomitan/` outside this folder is copied from [Yomitan](https://github.com/yomidevs/yomitan) (GPL-3.0-or-later) by `UI/Web/scripts/vendor-yomitan.mjs`. `assets/yomitan/VENDORED` records the tag and commit. Don't edit those files by hand. Change the script or this folder and re-vendor.

## Updating Yomitan

```bash
cd ~/Workshop/yomitan && git pull && npm ci && npm run build:libs
cd <kavita>/UI/Web && node scripts/vendor-yomitan.mjs ~/Workshop/yomitan
```

The script rebuilds `assets/yomitan/` and leaves this folder alone.

## What the script changes in vendored files

- Root-relative paths in CSS and HTML (`/images/…`, `/css/…`) become relative, so the files work under Kavita's base URL.
- `js/media/audio-system.js` gets the same change for its two fallback sounds.
- `popup.html` and `template-renderer.html` get `chrome-shim.js` and an import map added at the top of `<head>`. The popup's "Go to Dictionaries settings" link points at Kavita's settings route.

No other vendored file is modified.

## How it runs

Yomitan is a browser extension, so its code expects `chrome.runtime` and `chrome.tabs`. `hub.js` lives in Kavita's window and acts as the browser. Every Yomitan frame registers with it and gets a `chrome` object whose messages, ports and storage go through the hub. That covers the reader page (Yomitan's content script), the hidden background page and each popup iframe.

- `kavita-yomitan.js` is what Angular imports (`YomitanService`). It starts the hub and background page and turns the reader's text scanner on and off.
- `background.html` and `background-main.js` run Yomitan's real `Backend`. The import map there swaps in two modules:
  - `remote-dictionary-database.js` answers the translator's database queries from Kavita's server (`api/yomitan/db/*`) instead of IndexedDB.
  - `request-builder.js` sends audio-source requests through Kavita's server (`api/yomitan/fetch`), because a web page can't make cross-origin requests the way an extension can.
- `api.js` replaces one method in the popup. Calls that transfer objects (the media drawing channel) go to the background page's window instead of a service worker.
- Options live in `chrome.storage.local` under `options`, as in Yomitan. The hub loads them from `api/yomitan/options` at startup and saves them back when the backend writes.

Clicking a word uses TextScanner's search-on-click mode, the one Yomitan's search page uses. It works alongside whatever scanning inputs are configured.
