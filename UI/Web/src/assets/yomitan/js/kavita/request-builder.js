/*
 * Copyright (C) 2026 Kavita contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

// Yomitan's RequestBuilder relies on host permissions and declarativeNetRequest to fetch audio
// pages like jisho.org from any origin. A web page can't, so Kavita's server fetches them instead
// (api/yomitan/fetch) and this rebuilds a Response from what it returns.

import {RequestBuilder as VendoredRequestBuilder} from '../background/request-builder.js?vendored';

/**
 * @param {HeadersInit|undefined} headers
 * @returns {Record<string, string>}
 */
function headersToObject(headers) {
    const result = {};
    if (typeof headers === 'undefined' || headers === null) { return result; }
    new Headers(headers).forEach((value, key) => { result[key] = value; });
    return result;
}

/**
 * @param {BodyInit|null|undefined} body
 * @returns {string|undefined}
 */
function bodyToString(body) {
    if (body === null || typeof body === 'undefined') { return void 0; }
    if (typeof body === 'string') { return body; }
    if (body instanceof URLSearchParams) { return body.toString(); }
    throw new Error('Unsupported request body for the Kavita fetch proxy');
}

export class RequestBuilder extends VendoredRequestBuilder {
    async prepare() {
        // No declarativeNetRequest rules to clear.
    }

    /**
     * @param {string} url
     * @param {RequestInit} init
     * @returns {Promise<Response>}
     */
    async fetchAnonymous(url, init) {
        const result = await window.top.__kavitaYomitanHub.kavitaRequest('fetch', {
            url: encodeURI(decodeURIComponent(url)),
            method: init?.method ?? 'GET',
            headers: headersToObject(init?.headers),
            body: bodyToString(init?.body),
        });
        const binary = atob(result.body ?? '');
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; ++i) { bytes[i] = binary.charCodeAt(i); }
        const nullBody = [101, 204, 205, 304].includes(result.status);
        const response = new Response(nullBody ? null : bytes, {
            status: result.status,
            headers: result.contentType ? {'Content-Type': result.contentType} : {},
        });
        Object.defineProperty(response, 'url', {value: result.url ?? url});
        return response;
    }
}
