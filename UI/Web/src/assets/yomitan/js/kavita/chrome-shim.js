/*
 * Copyright (C) 2026 Kavita contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

// Loaded as a classic script at the top of every Yomitan page Kavita frames, so `chrome` exists
// before any Yomitan module runs. The hub in Kavita's window hands out the object (see hub.js).
(() => {
    const kind = document.currentScript?.dataset.context ?? 'extension';
    let hub = null;
    try {
        hub = window.top.__kavitaYomitanHub ?? null;
    } catch (e) {
        // A cross-origin parent means this page wasn't opened by Kavita's reader.
    }
    if (hub === null) {
        throw new Error('Yomitan pages only run inside Kavita');
    }
    hub.install(window, kind);
})();
