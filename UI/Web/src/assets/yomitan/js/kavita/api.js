/*
 * Copyright (C) 2026 Kavita contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

// In the extension, frames reach the background service worker with postMessage for calls that
// transfer objects (the popup's media drawing channel). Kavita's background page is an iframe, so
// those messages go to its window instead. Everything else is Yomitan's API unchanged.

import {API as VendoredAPI} from '../comm/api.js?vendored';

export class API extends VendoredAPI {
    /**
     * @param {string} action
     * @param {unknown} params
     * @param {Transferable[]} transferables
     */
    _pmInvoke(action, params, transferables) {
        const backgroundWindow = window.top.__kavitaYomitanHub.backgroundWindow;
        if (backgroundWindow === null) { return; }
        backgroundWindow.postMessage({action, params}, window.location.origin, transferables);
    }
}
