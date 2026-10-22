/*
 * Copyright (C) 2026 Kavita contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

// The module Kavita's Angular app imports at runtime. It starts the hub and Yomitan's background
// page once per app session. The reader then turns Yomitan's Frontend (the content script that
// scans text and opens the popup) on and off. Kavita's settings page talks to the backend directly.

import {Frontend} from '../app/frontend.js';
import {PopupFactory} from '../app/popup-factory.js';
import {Application} from '../application.js';
import {OptionsUtil} from '../data/options-util.js';
import {HotkeyHandler} from '../input/hotkey-handler.js';
import {createHub} from './hub.js';

/** @type {Promise<KavitaYomitan>|null} */
let startPromise = null;

/**
 * @param {object} config
 * @param {string} config.assetBaseUrl
 * @param {string} config.apiUrl
 * @param {() => (string|null)} config.getToken
 * @param {string} config.settingsUrl
 * @returns {Promise<KavitaYomitan>}
 */
export function start(config) {
    if (startPromise === null) {
        startPromise = startInternal(config);
        startPromise.catch(() => { startPromise = null; });
    }
    return startPromise;
}

async function startInternal(config) {
    const hub = createHub({...config, optionsJson: null});
    const stored = await hub.kavitaRequest('options');
    if (typeof stored?.optionsJson === 'string') {
        hub.setInitialOptionsJson(stored.optionsJson);
    }
    hub.install(window, 'content');
    await hub.startBackground();
    return new KavitaYomitan(hub);
}

class KavitaYomitan {
    constructor(hub) {
        this._hub = hub;
        /** @type {Promise<Frontend>|null} */
        this._frontendPromise = null;
        /** @type {Frontend|null} */
        this._frontend = null;
        /** Text of the last successful scan, which Yomitan selects when scanning.selectText is on. */
        this._lastScannedText = null;
        this._includeSelector = null;
        this._excludeSelector = null;
    }

    /**
     * Starts scanning text inside elements matching `includeSelector`. Clicking a word looks it up,
     * on top of whatever scanning inputs the user configured in the Scanning settings.
     * @param {{includeSelector: string, excludeSelector: string}} details
     */
    async enableScanning({includeSelector, excludeSelector}) {
        this._includeSelector = includeSelector;
        this._excludeSelector = excludeSelector;
        const frontend = await this._getFrontend();
        const scanner = frontend._textScanner;
        scanner.includeSelector = includeSelector;
        scanner.excludeSelector = excludeSelector;
        frontend.setDisabledOverride(false);
    }

    async disableScanning() {
        if (this._frontendPromise === null) { return; }
        const frontend = await this._frontendPromise;
        frontend.setDisabledOverride(true);
        frontend.popup?.hide(false);
    }

    /** @returns {boolean} */
    isPopupVisible() {
        return this._frontend?.popup?.isVisibleSync() ?? false;
    }

    hidePopup() {
        this._frontend?.popup?.hide(false);
    }

    /**
     * True when the page selection is the word Yomitan selected for its last lookup, as opposed to
     * text the reader selected by hand.
     * @param {Selection|null} selection
     * @returns {boolean}
     */
    isScanSelection(selection) {
        if (selection === null || this._lastScannedText === null) { return false; }
        return selection.toString() === this._lastScannedText;
    }

    /**
     * True when a click at this point lands on text Yomitan would scan, so the reader can leave
     * the click to Yomitan instead of toggling its menus.
     * @param {number} x
     * @param {number} y
     * @returns {boolean}
     */
    isScannableTextAt(x, y) {
        if (this._includeSelector === null) { return false; }
        let node = null;
        let offset = 0;
        if (typeof document.caretPositionFromPoint === 'function') {
            const position = document.caretPositionFromPoint(x, y);
            node = position?.offsetNode ?? null;
            offset = position?.offset ?? 0;
        } else if (typeof document.caretRangeFromPoint === 'function') {
            const range = document.caretRangeFromPoint(x, y);
            node = range?.startContainer ?? null;
            offset = range?.startOffset ?? 0;
        }
        if (node === null || node.nodeType !== Node.TEXT_NODE) { return false; }
        const element = node.parentElement;
        if (element === null || !element.matches(`${this._includeSelector}, ${this._includeSelector} *`)) { return false; }
        if (this._excludeSelector && element.closest(this._excludeSelector) !== null) { return false; }
        const text = node.textContent ?? '';
        const character = text.charAt(offset) || text.charAt(offset - 1);
        return character.trim().length > 0;
    }

    /**
     * Calls one of Yomitan's backend API actions, the same way its settings page does.
     * @param {string} action
     * @param {unknown} [params]
     * @returns {Promise<unknown>}
     */
    invoke(action, params) {
        return this._hub.invokeBackend(action, params);
    }

    /**
     * Yomitan's default options document, for resetting settings.
     * @returns {Promise<object>}
     */
    async getDefaultOptions() {
        const optionsUtil = new OptionsUtil();
        await optionsUtil.prepare();
        return optionsUtil.getDefault();
    }

    /**
     * Calls Kavita's api/yomitan endpoints with the current user's token.
     * @param {string} path
     * @param {unknown} [body]
     * @returns {Promise<unknown>}
     */
    request(path, body) {
        return this._hub.kavitaRequest(path, body);
    }

    _getFrontend() {
        if (this._frontendPromise === null) {
            this._frontendPromise = new Promise((resolve, reject) => {
                void Application.main(false, async (application) => {
                    try {
                        resolve(await this._createFrontend(application));
                    } catch (e) {
                        reject(e);
                    }
                });
            });
        }
        return this._frontendPromise;
    }

    /**
     * Same setup as Yomitan's js/app/content-script-main.js, plus click-to-look-up.
     * @param {Application} application
     * @returns {Promise<Frontend>}
     */
    async _createFrontend(application) {
        const hotkeyHandler = new HotkeyHandler();
        hotkeyHandler.prepare(application.crossFrame);

        const popupFactory = new PopupFactory(application);
        popupFactory.prepare();

        const {browser} = await application.api.getEnvironmentInfo();

        const frontend = new Frontend({
            application,
            popupFactory,
            depth: 0,
            parentPopupId: null,
            parentFrameId: null,
            useProxyPopup: false,
            pageType: 'web',
            canUseWindowPopup: false,
            allowRootFramePopupProxy: true,
            childrenSupported: true,
            hotkeyHandler,
            browser,
        });
        const scanner = frontend._textScanner;
        // TextScanner's search-page mode: a click on a word looks it up. It has to be set before
        // the scanner hooks its events.
        scanner._searchOnClick = true;
        scanner.includeSelector = this._includeSelector;
        scanner.excludeSelector = this._excludeSelector;
        await frontend.prepare();
        scanner.on('searchSuccess', ({textSource}) => { this._lastScannedText = textSource.text(); });
        this._frontend = frontend;
        return frontend;
    }
}
