/*
 * Copyright (C) 2026 Kavita contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

// Stands in for Yomitan's IndexedDB-backed DictionaryDatabase (js/dictionary/dictionary-database.js)
// on the background page. Kavita keeps the dictionaries in a SQLite file on the server, and each
// query goes to api/yomitan/db/*, which returns rows already shaped the way the IndexedDB version's
// _createTerm, _createKanji and friends shape them. The import map in background.html points
// Yomitan's import of dictionary-database.js here.

import {createApiMap, invokeApiMapHandler} from '../core/api-map.js';
import {ExtensionError} from '../core/extension-error.js';
import {log} from '../core/log.js';

function getHub() {
    return window.top.__kavitaYomitanHub;
}

/**
 * @param {Map<string, unknown>|Set<string>} dictionaries
 * @returns {string[]}
 */
function dictionaryNames(dictionaries) {
    return [...dictionaries.keys()];
}

/**
 * @param {string} base64
 * @returns {ArrayBuffer}
 */
function base64ToArrayBuffer(base64) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; ++i) { bytes[i] = binary.charCodeAt(i); }
    return bytes.buffer;
}

export class DictionaryDatabase {
    constructor() {
        this._prepared = false;
        this._apiMap = createApiMap([
            ['drawMedia', this._onDrawMedia.bind(this)],
        ]);
    }

    async prepare() {
        this._prepared = true;
    }

    async close() {
        this._prepared = false;
    }

    isPrepared() {
        return this._prepared;
    }

    async purge() {
        throw new Error('Kavita manages its bundled dictionaries on the server');
    }

    async deleteDictionary() {
        throw new Error('Kavita manages its bundled dictionaries on the server');
    }

    findTermsBulk(termList, dictionaries, matchType) {
        if (termList.length === 0) { return Promise.resolve([]); }
        return this._post('db/terms-bulk', {termList, dictionaries: dictionaryNames(dictionaries), matchType});
    }

    findTermsExactBulk(termList, dictionaries) {
        if (termList.length === 0) { return Promise.resolve([]); }
        return this._post('db/terms-exact-bulk', {termList, dictionaries: dictionaryNames(dictionaries)});
    }

    findTermsBySequenceBulk(items) {
        if (items.length === 0) { return Promise.resolve([]); }
        return this._post('db/terms-by-sequence-bulk', {items});
    }

    findTermMetaBulk(termList, dictionaries) {
        if (termList.length === 0) { return Promise.resolve([]); }
        return this._post('db/term-meta-bulk', {termList, dictionaries: dictionaryNames(dictionaries)});
    }

    findKanjiBulk(kanjiList, dictionaries) {
        if (kanjiList.length === 0) { return Promise.resolve([]); }
        return this._post('db/kanji-bulk', {kanjiList, dictionaries: dictionaryNames(dictionaries)});
    }

    findKanjiMetaBulk(kanjiList, dictionaries) {
        if (kanjiList.length === 0) { return Promise.resolve([]); }
        return this._post('db/kanji-meta-bulk', {kanjiList, dictionaries: dictionaryNames(dictionaries)});
    }

    async findTagMetaBulk(items) {
        if (items.length === 0) { return []; }
        const results = await this._post('db/tag-meta-bulk', {items});
        // IndexedDB reports a miss as undefined, and the translator checks for exactly that.
        return results.map((tag) => (tag === null ? void 0 : tag));
    }

    async findTagForTitle(name, dictionary) {
        const [tag] = await this.findTagMetaBulk([{query: name, dictionary}]);
        return tag ?? null;
    }

    /**
     * @param {{path: string, dictionary: string}[]} items
     * @returns {Promise<{index: number, dictionary: string, path: string, mediaType: string, width: number, height: number, content: ArrayBuffer}[]>}
     */
    async getMedia(items) {
        if (items.length === 0) { return []; }
        const results = await this._post('db/media', {items});
        return results.map((media) => ({...media, content: base64ToArrayBuffer(media.content)}));
    }

    getDictionaryInfo() {
        return getHub().kavitaRequest('dictionaries');
    }

    async getDictionaryCounts(dictionaryNamesList, getTotal) {
        const summaries = await this.getDictionaryInfo();
        const counts = [];
        const total = getTotal ? {kanji: 0, kanjiMeta: 0, terms: 0, termMeta: 0, tagMeta: 0, media: 0} : null;
        for (const name of dictionaryNamesList) {
            const summary = summaries.find((item) => item.title === name);
            const item = {
                kanji: summary?.counts?.kanji?.total ?? 0,
                kanjiMeta: summary?.counts?.kanjiMeta?.total ?? 0,
                terms: summary?.counts?.terms?.total ?? 0,
                termMeta: summary?.counts?.termMeta?.total ?? 0,
                tagMeta: summary?.counts?.tagMeta?.total ?? 0,
                media: summary?.counts?.media?.total ?? 0,
            };
            counts.push(item);
            if (total !== null) {
                for (const key of Object.keys(total)) { total[key] += item[key]; }
            }
        }
        return {total, counts};
    }

    async dictionaryExists(title) {
        const summaries = await this.getDictionaryInfo();
        return summaries.some((summary) => summary.title === title);
    }

    // Media drawing. Yomitan's popup hands each structured-content image an OffscreenCanvas and
    // asks the database to draw into it through the media drawing worker. Upstream decodes SVG with
    // resvg. The background page has a DOM, so the browser decodes everything here instead.

    /**
     * @param {MessagePort} port
     */
    async connectToDatabaseWorker(port) {
        port.onmessage = (event) => {
            const {action, params} = event.data;
            return invokeApiMapHandler(this._apiMap, action, params, [port], () => {});
        };
        port.onmessageerror = (event) => {
            const error = new ExtensionError('DictionaryDatabase: Error receiving message from main thread');
            error.data = event;
            log.error(error);
        };
    }

    _onDrawMedia(params, port) {
        void this.drawMedia(params.requests, port);
    }

    /**
     * @param {{path: string, dictionary: string, canvasIndex: number, canvasWidth: number, canvasHeight: number, generation: number}[]} items
     * @param {MessagePort} source
     */
    async drawMedia(items, source) {
        const groups = new Map();
        for (const {path, dictionary, canvasIndex, canvasWidth, canvasHeight, generation} of items) {
            const key = `${path}:::${dictionary}`;
            if (!groups.has(key)) {
                groups.set(key, {path, dictionary, canvasIndexes: [], canvasWidth, canvasHeight, generation});
            }
            groups.get(key).canvasIndexes.push(canvasIndex);
        }
        const groupList = [...groups.values()];
        let mediaList;
        try {
            mediaList = await this.getMedia(groupList.map(({path, dictionary}) => ({path, dictionary})));
        } catch (e) {
            log.error(e);
            return;
        }
        await Promise.all(mediaList.map(async (media) => {
            const group = groupList[media.index];
            if (typeof group === 'undefined') { return; }
            try {
                const decodedImage = await this._decodeImage(media, group.canvasWidth, group.canvasHeight);
                source.postMessage({
                    action: 'drawDecodedImageToCanvases',
                    params: {decodedImage, canvasIndexes: group.canvasIndexes, generation: group.generation},
                }, [decodedImage]);
            } catch (e) {
                log.error(e);
            }
        }));
    }

    async _decodeImage(media, width, height) {
        const blob = new Blob([media.content], {type: media.mediaType});
        const resize = {resizeWidth: Math.max(1, width), resizeHeight: Math.max(1, height), resizeQuality: 'high'};
        if (media.mediaType !== 'image/svg+xml') {
            return await createImageBitmap(blob, resize);
        }
        // createImageBitmap can't take an SVG blob directly, but it can take a loaded <img>.
        const url = URL.createObjectURL(blob);
        try {
            const image = new Image();
            image.decoding = 'async';
            image.src = url;
            await image.decode();
            return await createImageBitmap(image, resize);
        } finally {
            URL.revokeObjectURL(url);
        }
    }

    _post(path, body) {
        return getHub().kavitaRequest(path, body);
    }
}
