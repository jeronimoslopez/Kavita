/*
 * Copyright (C) 2026 Kavita contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

// Yomitan is written as a browser extension. Its frames talk to each other and to its background
// page through chrome.runtime and chrome.tabs. This hub lives in Kavita's window and plays the part
// of the browser: every Yomitan frame (the reader page, the hidden background page, each popup)
// registers here and gets a `chrome` object whose messaging, ports and storage are routed through
// the hub. That lets the vendored Yomitan code run unmodified.

const EXTENSION_ID = 'kavita-yomitan';
const TAB_ID = 1;
const WINDOW_ID = 1;

const MANIFEST = {
    manifest_version: 3,
    name: 'Yomitan',
    version: '26.10.22.0',
    action: {default_title: 'Yomitan'},
    options_ui: {page: 'settings.html', open_in_tab: true},
    permissions: ['storage', 'clipboardWrite', 'unlimitedStorage', 'declarativeNetRequest', 'scripting', 'offscreen', 'contextMenus'],
    optional_permissions: ['clipboardRead', 'nativeMessaging'],
    host_permissions: ['<all_urls>'],
};

class RuntimeEvent {
    constructor() {
        this._listeners = [];
    }

    addListener(listener) {
        if (!this._listeners.includes(listener)) { this._listeners.push(listener); }
    }

    removeListener(listener) {
        const index = this._listeners.indexOf(listener);
        if (index >= 0) { this._listeners.splice(index, 1); }
    }

    hasListener(listener) {
        return this._listeners.includes(listener);
    }

    hasListeners() {
        return this._listeners.length > 0;
    }

    get listeners() {
        return [...this._listeners];
    }

    dispatch(...args) {
        for (const listener of this.listeners) {
            try {
                listener(...args);
            } catch (e) {
                console.error('[Yomitan] listener failed', e);
            }
        }
    }
}

class Port {
    constructor(hub, context, name, sender) {
        this._hub = hub;
        this._context = context;
        this.name = name;
        this.sender = sender;
        this.onMessage = new RuntimeEvent();
        this.onDisconnect = new RuntimeEvent();
        this._other = null;
        this._disconnected = false;
    }

    postMessage(message) {
        if (this._disconnected) { throw new Error('Attempting to use a disconnected port object'); }
        const other = this._other;
        if (other === null) { return; }
        const copy = this._hub.cloneFor(other._context, message);
        setTimeout(() => {
            if (!other._disconnected) { other.onMessage.dispatch(copy, other); }
        }, 0);
    }

    disconnect() {
        if (this._disconnected) { return; }
        this._disconnected = true;
        const other = this._other;
        if (other !== null && !other._disconnected) {
            setTimeout(() => {
                if (other._disconnected) { return; }
                other._disconnected = true;
                other.onDisconnect.dispatch(other);
            }, 0);
        }
    }
}

export class YomitanHub {
    /**
     * @param {object} config
     * @param {string} config.assetBaseUrl Absolute URL of the vendored Yomitan folder, without a trailing slash.
     * @param {string} config.apiUrl Absolute URL of Kavita's API, with a trailing slash.
     * @param {() => (string|null)} config.getToken Returns the current user's JWT.
     * @param {string} config.settingsUrl Kavita's Yomitan settings route.
     * @param {string|null} config.optionsJson The user's stored Yomitan options, or null for defaults.
     */
    constructor(config) {
        this.config = config;
        this._contexts = [];
        this._nextFrameId = 1;
        // Yomitan opens its welcome guide once per browser session. Kavita doesn't ship that page.
        this._storage = {local: {}, session: {openedWelcomePage: true}};
        if (typeof config.optionsJson === 'string') {
            this._storage.local.options = config.optionsJson;
        }
        this._storageChanged = new RuntimeEvent();
        this._saveTimer = null;
        this._backgroundFrame = null;
    }

    // Context registration

    /**
     * Gives a window its `chrome` object. The reader page is the content script (frame 0), the
     * hidden background page has no tab, and every other frame is an extension page in the tab.
     * @param {Window} win
     * @param {'content'|'background'|'extension'} kind
     */
    install(win, kind) {
        this._pruneContexts();
        const context = {
            window: win,
            kind,
            tabId: kind === 'background' ? undefined : TAB_ID,
            frameId: kind === 'content' ? 0 : (kind === 'background' ? undefined : this._nextFrameId++),
            onMessage: new RuntimeEvent(),
            onConnect: new RuntimeEvent(),
            chrome: null,
        };
        context.chrome = this._createChrome(context);
        this._contexts.push(context);
        win.chrome = context.chrome;
        return context.chrome;
    }

    /**
     * Loads Yomitan's background page in a hidden iframe and waits until the backend is ready.
     * @returns {Promise<void>}
     */
    startBackground() {
        return new Promise((resolve, reject) => {
            const frame = document.createElement('iframe');
            frame.src = `${this.config.assetBaseUrl}/js/kavita/background.html`;
            frame.setAttribute('aria-hidden', 'true');
            frame.tabIndex = -1;
            frame.style.cssText = 'position:fixed;width:0;height:0;border:0;visibility:hidden;pointer-events:none;';
            const timer = setTimeout(() => reject(new Error('Yomitan background page did not start')), 60000);
            this._backgroundReady = () => {
                clearTimeout(timer);
                resolve();
            };
            this._backgroundFailed = (error) => {
                clearTimeout(timer);
                reject(error);
            };
            document.documentElement.appendChild(frame);
            this._backgroundFrame = frame;
        });
    }

    /**
     * Seeds chrome.storage.local with the options Kavita stored for this user.
     * @param {string} optionsJson
     */
    setInitialOptionsJson(optionsJson) {
        this._storage.local.options = optionsJson;
    }

    /** @type {Window|null} */
    get backgroundWindow() {
        return this._backgroundFrame?.contentWindow ?? null;
    }

    /** Called by the background page once Backend.prepare() resolves. */
    notifyBackgroundReady() {
        this._backgroundReady?.();
    }

    /** @param {unknown} error */
    notifyBackgroundFailed(error) {
        this._backgroundFailed?.(error);
    }

    /**
     * Sends an API message to the backend as if from an extension page, the way Yomitan's
     * settings page does. Kavita's settings section uses this.
     * @param {string} action
     * @param {unknown} params
     * @returns {Promise<unknown>}
     */
    invokeBackend(action, params) {
        const caller = this._getOrCreateHostContext();
        return new Promise((resolve, reject) => {
            caller.chrome.runtime.sendMessage({action, params}, (response) => {
                const lastError = caller.chrome.runtime.lastError;
                if (lastError) {
                    reject(new Error(lastError.message));
                } else if (response && typeof response === 'object' && 'error' in response) {
                    reject(new Error(response.error?.message ?? 'Yomitan backend error'));
                } else {
                    resolve(response?.result);
                }
            });
        });
    }

    // Kavita API access for the background page

    /**
     * @param {string} path Relative to api/yomitan/.
     * @param {unknown} [body] Sent as JSON with POST. Omitted means GET.
     * @returns {Promise<unknown>}
     */
    async kavitaRequest(path, body) {
        const headers = {Accept: 'application/json'};
        const token = this.config.getToken();
        if (token) { headers.Authorization = `Bearer ${token}`; }
        const init = {method: 'GET', headers, credentials: 'same-origin'};
        if (typeof body !== 'undefined') {
            init.method = 'POST';
            headers['Content-Type'] = 'application/json';
            init.body = JSON.stringify(body);
        }
        const response = await fetch(`${this.config.apiUrl}yomitan/${path}`, init);
        if (!response.ok) {
            throw new Error(`Kavita Yomitan API ${path} failed with ${response.status}`);
        }
        const text = await response.text();
        return text.length > 0 ? JSON.parse(text) : null;
    }

    // Internals

    cloneFor(context, value) {
        if (typeof value === 'undefined') { return value; }
        try {
            return context.window.structuredClone(value);
        } catch (e) {
            return structuredClone(value);
        }
    }

    _pruneContexts() {
        this._contexts = this._contexts.filter((context) => this._isAlive(context));
    }

    _isAlive(context) {
        try {
            return !context.window.closed && context.window.document !== null;
        } catch (e) {
            return false;
        }
    }

    _getOrCreateHostContext() {
        let context = this._contexts.find((item) => item.kind === 'host');
        if (typeof context === 'undefined') {
            context = {
                window,
                kind: 'host',
                tabId: undefined,
                frameId: undefined,
                onMessage: new RuntimeEvent(),
                onConnect: new RuntimeEvent(),
                chrome: null,
            };
            context.chrome = this._createChrome(context);
            this._contexts.push(context);
        }
        return context;
    }

    _getTab() {
        return {
            id: TAB_ID,
            index: 0,
            windowId: WINDOW_ID,
            active: true,
            highlighted: true,
            pinned: false,
            incognito: false,
            discarded: false,
            autoDiscardable: true,
            groupId: -1,
            status: 'complete',
            url: window.location.href,
            title: document.title,
        };
    }

    _getSender(context) {
        const sender = {
            id: EXTENSION_ID,
            url: context.window.location.href,
            origin: context.window.location.origin,
        };
        if (typeof context.tabId === 'number') {
            sender.tab = this._getTab();
            sender.frameId = context.frameId;
        }
        return sender;
    }

    /**
     * Calls a callback in the caller's frame with chrome.runtime.lastError set, as browsers do.
     */
    _invokeCallback(context, callback, args, errorMessage) {
        if (typeof callback !== 'function') { return; }
        const runtime = context.chrome.runtime;
        runtime.lastError = errorMessage ? {message: errorMessage} : undefined;
        try {
            callback(...args);
        } catch (e) {
            console.error('[Yomitan] callback failed', e);
        } finally {
            runtime.lastError = undefined;
        }
    }

    _deliverMessage(targets, message, senderContext, callback) {
        const sender = this._getSender(senderContext);
        setTimeout(() => {
            let responded = false;
            let pending = 0;
            const respond = (response, errorMessage) => {
                if (responded) { return; }
                responded = true;
                const copy = this.cloneFor(senderContext, response);
                // Queued, so ports a handler opened (openCrossFramePort) connect before the caller
                // sees the response, as in browsers.
                setTimeout(() => this._invokeCallback(senderContext, callback, [copy], errorMessage), 0);
            };
            const liveTargets = targets.filter((context) => this._isAlive(context));
            let listenerCount = 0;
            for (const target of liveTargets) {
                for (const listener of target.onMessage.listeners) {
                    ++listenerCount;
                    let called = false;
                    const sendResponse = (response) => {
                        if (called) { return; }
                        called = true;
                        respond(response, null);
                    };
                    let result;
                    try {
                        result = listener(this.cloneFor(target, message), this.cloneFor(target, sender), sendResponse);
                    } catch (e) {
                        console.error('[Yomitan] message listener failed', e);
                    }
                    if (result === true) {
                        ++pending;
                    } else if (result !== null && typeof result === 'object' && typeof result.then === 'function') {
                        ++pending;
                        result.then(sendResponse, () => sendResponse(void 0));
                    }
                }
            }
            if (!responded && pending === 0) {
                respond(void 0, listenerCount === 0 ?
                    'Could not establish connection. Receiving end does not exist.' :
                    'The message port closed before a response was received.');
            }
        }, 0);
    }

    _runtimeSendMessage(context, args) {
        // sendMessage([extensionId], message, [options], [callback])
        let index = 0;
        if (typeof args[0] === 'string' && args.length > 1 && typeof args[1] !== 'function') { index = 1; }
        const message = args[index];
        const callback = args.slice(index + 1).find((arg) => typeof arg === 'function');
        // Extension pages and the background page receive runtime messages. Content scripts don't,
        // and nobody receives their own.
        const targets = this._contexts.filter((target) => target !== context && (target.kind === 'background' || target.kind === 'extension' || target.kind === 'host'));
        if (typeof callback === 'function') {
            this._deliverMessage(targets, message, context, callback);
            return void 0;
        }
        return new Promise((resolve, reject) => {
            this._deliverMessage(targets, message, context, (response) => {
                const lastError = context.chrome.runtime.lastError;
                if (lastError) { reject(new Error(lastError.message)); } else { resolve(response); }
            });
        });
    }

    _tabsSendMessage(context, tabId, message, options, callback) {
        if (typeof options === 'function') {
            callback = options;
            options = {};
        }
        const frameId = options?.frameId;
        const targets = this._contexts.filter((target) => (
            target.tabId === tabId &&
            (target.kind === 'content' || target.kind === 'extension') &&
            (typeof frameId !== 'number' || target.frameId === frameId)
        ));
        if (typeof callback === 'function') {
            this._deliverMessage(targets, message, context, callback);
            return void 0;
        }
        return new Promise((resolve, reject) => {
            this._deliverMessage(targets, message, context, (response) => {
                const lastError = context.chrome.runtime.lastError;
                if (lastError) { reject(new Error(lastError.message)); } else { resolve(response); }
            });
        });
    }

    _connect(context, targets, name) {
        const local = new Port(this, context, name, void 0);
        const target = targets.find((item) => this._isAlive(item));
        if (typeof target === 'undefined') {
            setTimeout(() => {
                if (local._disconnected) { return; }
                local._disconnected = true;
                context.chrome.runtime.lastError = {message: 'Could not establish connection. Receiving end does not exist.'};
                try {
                    local.onDisconnect.dispatch(local);
                } finally {
                    context.chrome.runtime.lastError = undefined;
                }
            }, 0);
            return local;
        }
        const remote = new Port(this, target, name, this._getSender(context));
        local._other = remote;
        remote._other = local;
        setTimeout(() => target.onConnect.dispatch(remote), 0);
        return local;
    }

    _injectCss(details) {
        const {target: {tabId, frameIds, allFrames}, css, files} = details;
        const targets = this._contexts.filter((context) => (
            context.tabId === tabId &&
            (allFrames === true || (Array.isArray(frameIds) ? frameIds.includes(context.frameId) : context.frameId === 0))
        ));
        for (const context of targets) {
            const doc = context.window.document;
            if (typeof css === 'string') {
                const style = doc.createElement('style');
                style.textContent = css;
                (doc.head ?? doc.documentElement).appendChild(style);
            }
            for (const file of files ?? []) {
                const link = doc.createElement('link');
                link.rel = 'stylesheet';
                link.href = this._getUrl(file);
                (doc.head ?? doc.documentElement).appendChild(link);
            }
        }
    }

    _getUrl(path) {
        const value = String(path);
        if (/^[a-z][a-z0-9+.-]*:/i.test(value)) { return value; }
        // Yomitan reads getURL('/') as the extension's origin, for postMessage target checks.
        if (value === '/' || value === '') { return `${new URL(this.config.assetBaseUrl).origin}/`; }
        return `${this.config.assetBaseUrl}${value.startsWith('/') ? '' : '/'}${value}`;
    }

    _scheduleOptionsSave() {
        if (this._saveTimer !== null) { clearTimeout(this._saveTimer); }
        this._saveTimer = setTimeout(() => {
            this._saveTimer = null;
            const optionsJson = this._storage.local.options;
            if (typeof optionsJson !== 'string') { return; }
            this.kavitaRequest('options', {optionsJson}).catch((e) => console.error('[Yomitan] saving options failed', e));
        }, 300);
    }

    _createStorageArea(context, areaName) {
        const area = this._storage[areaName];
        const hub = this;
        const respond = (callback, value) => {
            if (typeof callback === 'function') {
                setTimeout(() => hub._invokeCallback(context, callback, typeof value === 'undefined' ? [] : [hub.cloneFor(context, value)], null), 0);
                return void 0;
            }
            return Promise.resolve(hub.cloneFor(context, value));
        };
        return {
            get(keys, callback) {
                if (typeof keys === 'function') {
                    callback = keys;
                    keys = null;
                }
                const result = {};
                if (keys === null || typeof keys === 'undefined') {
                    Object.assign(result, area);
                } else if (typeof keys === 'string') {
                    if (keys in area) { result[keys] = area[keys]; }
                } else if (Array.isArray(keys)) {
                    for (const key of keys) {
                        if (key in area) { result[key] = area[key]; }
                    }
                } else {
                    for (const [key, fallback] of Object.entries(keys)) {
                        result[key] = key in area ? area[key] : fallback;
                    }
                }
                return respond(callback, result);
            },
            set(items, callback) {
                const changes = {};
                for (const [key, value] of Object.entries(items)) {
                    changes[key] = {oldValue: area[key], newValue: value};
                    area[key] = structuredClone(value);
                }
                if (areaName === 'local' && 'options' in items) { hub._scheduleOptionsSave(); }
                setTimeout(() => hub._storageChanged.dispatch(changes, areaName), 0);
                return respond(callback, void 0);
            },
            remove(keys, callback) {
                for (const key of Array.isArray(keys) ? keys : [keys]) { delete area[key]; }
                return respond(callback, void 0);
            },
            clear(callback) {
                for (const key of Object.keys(area)) { delete area[key]; }
                return respond(callback, void 0);
            },
        };
    }

    _createChrome(context) {
        const hub = this;
        // Most browser APIs take an optional trailing callback and return a promise without one.
        const reply = (callback, value, errorMessage = null) => {
            if (typeof callback === 'function') {
                setTimeout(() => hub._invokeCallback(context, callback, typeof value === 'undefined' ? [] : [value], errorMessage), 0);
                return void 0;
            }
            return errorMessage ? Promise.reject(new Error(errorMessage)) : Promise.resolve(value);
        };
        const lastCallback = (args) => [...args].reverse().find((arg) => typeof arg === 'function');
        const allPermissions = () => ({
            permissions: [...MANIFEST.permissions, ...MANIFEST.optional_permissions],
            origins: ['<all_urls>'],
        });
        const backgroundTargets = () => hub._contexts.filter((target) => target.kind === 'background');

        return {
            runtime: {
                id: EXTENSION_ID,
                lastError: undefined,
                onMessage: context.onMessage,
                onConnect: context.onConnect,
                onInstalled: new RuntimeEvent(),
                onStartup: new RuntimeEvent(),
                getURL: (path) => hub._getUrl(path),
                getManifest: () => structuredClone(MANIFEST),
                sendMessage: (...args) => hub._runtimeSendMessage(context, args),
                connect: (...args) => {
                    const info = args.find((arg) => arg !== null && typeof arg === 'object') ?? {};
                    return hub._connect(context, backgroundTargets(), info.name ?? '');
                },
                getPlatformInfo: (callback) => {
                    const platform = (navigator.userAgentData?.platform ?? navigator.platform ?? '').toLowerCase();
                    let os = 'linux';
                    if (platform.includes('mac')) { os = 'mac'; } else if (platform.includes('win')) { os = 'win'; } else if (/android/i.test(navigator.userAgent)) { os = 'android'; } else if (platform.includes('cros')) { os = 'cros'; }
                    return reply(callback, {os, arch: 'x86-64', nacl_arch: 'x86-64'});
                },
                getContexts: (_filter, callback) => reply(callback, []),
                openOptionsPage: (callback) => {
                    window.open(hub.config.settingsUrl, '_blank');
                    return reply(callback, void 0);
                },
                reload: () => window.location.reload(),
            },
            tabs: {
                TAB_ID_NONE: -1,
                onZoomChange: new RuntimeEvent(),
                onUpdated: new RuntimeEvent(),
                onRemoved: new RuntimeEvent(),
                query: (_queryInfo, callback) => reply(callback, [hub._getTab()]),
                get: (tabId, callback) => (tabId === TAB_ID ? reply(callback, hub._getTab()) : reply(callback, void 0, `No tab with id: ${tabId}.`)),
                getCurrent: (callback) => reply(callback, void 0),
                getZoom: (_tabId, callback) => reply(callback, 1),
                sendMessage: (tabId, message, options, callback) => hub._tabsSendMessage(context, tabId, message, options, callback),
                connect: (tabId, connectInfo = {}) => {
                    const {frameId, name = ''} = connectInfo;
                    const targets = hub._contexts.filter((target) => (
                        target.tabId === tabId &&
                        (target.kind === 'content' || target.kind === 'extension') &&
                        (typeof frameId !== 'number' || target.frameId === frameId)
                    ));
                    return hub._connect(context, targets, name);
                },
                create: (createProperties, callback) => {
                    window.open(createProperties?.url ?? 'about:blank', '_blank');
                    return reply(callback, {...hub._getTab(), id: -1, url: createProperties?.url});
                },
                update: (...args) => reply(lastCallback(args), hub._getTab()),
                remove: (...args) => reply(lastCallback(args), void 0),
                captureVisibleTab: (...args) => reply(lastCallback(args), void 0, 'Screenshots are not available in Kavita'),
            },
            windows: {
                WINDOW_ID_CURRENT: -2,
                WINDOW_ID_NONE: -1,
                get: (_windowId, ...args) => reply(lastCallback(args), {id: WINDOW_ID, focused: true, type: 'normal', state: 'normal', alwaysOnTop: false, incognito: false}),
                getCurrent: (...args) => reply(lastCallback(args), {id: WINDOW_ID, focused: true, type: 'normal', state: 'normal', alwaysOnTop: false, incognito: false}),
                create: (createData, callback) => {
                    window.open(createData?.url ?? 'about:blank', '_blank');
                    return reply(callback, {id: -1, tabs: []});
                },
                update: (...args) => reply(lastCallback(args), {id: WINDOW_ID}),
            },
            storage: {
                local: hub._createStorageArea(context, 'local'),
                session: hub._createStorageArea(context, 'session'),
                onChanged: hub._storageChanged,
            },
            permissions: {
                onAdded: new RuntimeEvent(),
                onRemoved: new RuntimeEvent(),
                getAll: (callback) => reply(callback, allPermissions()),
                contains: (_permissions, callback) => reply(callback, true),
                request: (_permissions, callback) => reply(callback, true),
                remove: (_permissions, callback) => reply(callback, false),
            },
            scripting: {
                insertCSS: (details, callback) => {
                    hub._injectCss(details);
                    return reply(callback, void 0);
                },
                removeCSS: (_details, callback) => reply(callback, void 0),
                registerContentScripts: (_scripts, callback) => reply(callback, void 0),
                unregisterContentScripts: (_filter, callback) => reply(callback, void 0),
                getRegisteredContentScripts: (_filter, callback) => reply(callback, []),
            },
            declarativeNetRequest: {
                RuleActionType: {MODIFY_HEADERS: 'modifyHeaders', BLOCK: 'block', ALLOW: 'allow', REDIRECT: 'redirect'},
                HeaderOperation: {SET: 'set', REMOVE: 'remove', APPEND: 'append'},
                ResourceType: {XMLHTTPREQUEST: 'xmlhttprequest', MEDIA: 'media'},
                updateSessionRules: (_options, callback) => reply(callback, void 0),
                getSessionRules: (callback) => reply(callback, []),
                updateDynamicRules: (_options, callback) => reply(callback, void 0),
                getDynamicRules: (callback) => reply(callback, []),
            },
            contextMenus: {
                onClicked: new RuntimeEvent(),
                create: (_properties, callback) => {
                    reply(callback, void 0);
                    return 0;
                },
                update: (...args) => reply(lastCallback(args), void 0),
                remove: (...args) => reply(lastCallback(args), void 0),
                removeAll: (callback) => reply(callback, void 0),
            },
            omnibox: {
                onInputEntered: new RuntimeEvent(),
                setDefaultSuggestion: () => {},
            },
            i18n: {
                getUILanguage: () => navigator.language,
                getMessage: () => '',
            },
        };
    }
}

/**
 * Creates the hub and registers it where child frames can find it.
 * @param {ConstructorParameters<typeof YomitanHub>[0]} config
 * @returns {YomitanHub}
 */
export function createHub(config) {
    const hub = new YomitanHub(config);
    window.__kavitaYomitanHub = hub;
    return hub;
}
