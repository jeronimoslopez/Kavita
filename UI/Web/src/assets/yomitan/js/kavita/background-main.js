/*
 * Copyright (C) 2026 Kavita contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

// Kavita's version of Yomitan's js/background/background-main.js. It runs Yomitan's own Backend
// on a hidden page. Before that it makes sure the stored options know about the dictionaries
// Kavita bundles, since they never pass through Yomitan's import screen, which is what normally
// adds a dictionary to every profile.

import {Backend} from '../background/backend.js';
import {log} from '../core/log.js';
import {OptionsUtil} from '../data/options-util.js';
import {WebExtension} from '../extension/web-extension.js';

const hub = window.top.__kavitaYomitanHub;

// Same shape as DictionaryController.createDefaultDictionarySettings in
// js/pages/settings/dictionary-controller.js, which Kavita doesn't vendor.
function createDefaultDictionarySettings(name, enabled, styles) {
    return {
        name,
        alias: name,
        enabled,
        allowSecondarySearches: false,
        definitionsCollapsible: 'not-collapsible',
        partsOfSpeechFilter: true,
        useDeinflections: true,
        styles: styles ?? '',
    };
}

// Yomitan ranks results by the order of the profile's dictionary list. These are the bundled
// dictionaries in the order a Yomitan user would usually set them up: definitions, then names,
// kanji, and the frequency lists.
const BUNDLED_ORDER = [/^Jitendex/, /^JMnedict/, /^JMdict/, /^KANJIDIC/, /^JPDB/, /^Jiten$/, /^BCCWJ/];

function bundledRank(title) {
    const index = BUNDLED_ORDER.findIndex((pattern) => pattern.test(title));
    return index >= 0 ? index : BUNDLED_ORDER.length;
}

// "Jitendex.org [2026-10-03]" and "Jitendex.org [2026-11-01]" are the same dictionary at two releases.
function releaseless(title) {
    return title.replace(/\s*\[[^\]]*\]$/, '');
}

/**
 * Mirrors DictionaryController.ensureDictionarySettings: profiles drop settings for dictionaries
 * that are gone and gain enabled defaults for new ones. Kavita updates its bundled dictionaries in
 * place, and a new release changes the title, so a profile's settings carry over to the new title.
 */
async function prepareOptions() {
    const optionsUtil = new OptionsUtil();
    await optionsUtil.prepare();
    const options = await optionsUtil.load();
    const dictionaries = await hub.kavitaRequest('dictionaries');
    const installedTitles = new Set(dictionaries.map(({title}) => title));

    for (const profile of options.profiles) {
        const dictionaryOptions = profile.options.dictionaries;
        const listed = new Set(dictionaryOptions.map(({name}) => name));
        for (const {title} of dictionaries) {
            if (listed.has(title)) { continue; }
            const previous = dictionaryOptions.find(({name}) => !installedTitles.has(name) && releaseless(name) === releaseless(title));
            if (typeof previous === 'undefined') { continue; }
            if (previous.alias === previous.name) { previous.alias = title; }
            for (const key of ['mainDictionary', 'sortFrequencyDictionary']) {
                if (profile.options.general[key] === previous.name) { profile.options.general[key] = title; }
            }
            previous.name = title;
            listed.add(title);
        }

        for (let i = dictionaryOptions.length - 1; i >= 0; --i) {
            if (!installedTitles.has(dictionaryOptions[i].name)) { dictionaryOptions.splice(i, 1); }
        }

        // The popup reads each dictionary's CSS from here. A new release can change it.
        for (const entry of dictionaryOptions) {
            entry.styles = dictionaries.find(({title}) => title === entry.name)?.styles ?? '';
        }

        const missing = dictionaries
            .filter(({title}) => !listed.has(title))
            .sort((a, b) => bundledRank(a.title) - bundledRank(b.title));
        for (const {title, styles} of missing) {
            dictionaryOptions.push(createDefaultDictionarySettings(title, true, styles));
        }
    }

    await optionsUtil.save(options);
}

async function main() {
    try {
        const webExtension = new WebExtension();
        log.configure(webExtension.extensionName);
        await prepareOptions();
        const backend = new Backend(webExtension);
        await backend.prepare();
        hub.notifyBackgroundReady();
    } catch (e) {
        log.error(e);
        hub.notifyBackgroundFailed(e);
    }
}

void main();
