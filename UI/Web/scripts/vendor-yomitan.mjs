#!/usr/bin/env node
// Copies the parts of Yomitan's extension (https://github.com/yomidevs/yomitan) that Kavita
// runs in the browser into src/assets/yomitan. Files are copied verbatim. The only edits are
// root-relative asset paths, which become relative so Kavita can serve them under any base URL.
//
// Usage: node scripts/vendor-yomitan.mjs <path to a Yomitan checkout with built libs (npm run build:libs)>
//
// The hand-written integration lives in src/assets/yomitan/js/kavita and is left untouched.

import {execFileSync} from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const repoDir = process.argv[2];
if (!repoDir) {
    console.error('Usage: node scripts/vendor-yomitan.mjs <yomitan checkout>');
    process.exit(1);
}
const extDir = path.resolve(repoDir, 'ext');
const outDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../src/assets/yomitan');
const keepDir = path.join(outDir, 'js', 'kavita');

if (!fs.existsSync(path.join(extDir, 'lib', 'parse5.js'))) {
    console.error(`${extDir}/lib is missing. Run "npm ci && npm run build:libs" in the Yomitan checkout first.`);
    process.exit(1);
}

// Modules Kavita loads directly. Everything they import comes along.
const entryPoints = [
    'js/app/frontend.js',
    'js/app/popup-factory.js',
    'js/application.js',
    'js/input/hotkey-handler.js',
    'js/background/backend.js',
    'js/display/popup-main.js',
    'js/display/media-drawing-worker.js',
    'js/templates/template-renderer-frame-main.js',
    'js/data/options-util.js',
    'js/language/languages.js',
];

// Static files the modules fetch at runtime or reference from CSS and HTML.
const staticPaths = [
    'css/material.css',
    'css/display.css',
    'css/display-pronunciation.css',
    'css/structured-content.css',
    'css/popup-outer.css',
    'images',
    'data',
    'popup.html',
    'template-renderer.html',
    'templates-display.html',
];

const importPattern = /(?:import|export)\s[^'"]*?from\s*['"]([^'"]+)['"]|import\(\s*['"]([^'"]+)['"]\s*\)|new URL\(\s*['"]([^'"]+)['"]\s*,\s*import\.meta\.url/g;

function stripComments(source) {
    return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');
}

function collectModules() {
    const seen = new Set();
    const stack = entryPoints.map((entry) => path.join(extDir, entry));
    while (stack.length > 0) {
        const file = stack.pop();
        if (seen.has(file)) { continue; }
        seen.add(file);
        const source = stripComments(fs.readFileSync(file, 'utf8'));
        for (const match of source.matchAll(importPattern)) {
            const specifier = match[1] ?? match[2] ?? match[3];
            // new URL('comm/x.js', import.meta.url) is relative even without a leading "./".
            const isUrlReference = typeof match[3] === 'string' && !/^[a-z]+:|^\//i.test(specifier);
            if (!specifier.startsWith('.') && !isUrlReference) { continue; }
            const target = path.resolve(path.dirname(file), specifier);
            if (fs.existsSync(target)) { stack.push(target); }
        }
    }
    return [...seen];
}

// "/images/x.svg" in a file at css/display.css becomes "../images/x.svg".
function relativeFrom(file, rootPath) {
    const fromDir = path.dirname(path.relative(extDir, file));
    const relative = path.relative(fromDir, rootPath.replace(/^\//, ''));
    return relative.startsWith('.') ? relative : `./${relative}`;
}

function rewriteCss(file, source) {
    return source.replace(/url\((['"]?)(\/[^'")]+)\1\)/g, (_m, quote, url) => `url(${quote}${relativeFrom(file, url)}${quote})`);
}

// Extension pages that Kavita loads in iframes. Each one gets the chrome.* shim before any module
// runs, plus an import map that swaps the few modules Kavita replaces.
const pageWiring = new Map([
    ['popup.html', {
        context: 'extension',
        imports: {'./js/comm/api.js': './js/kavita/api.js'},
    }],
    ['template-renderer.html', {
        context: 'extension',
        imports: {},
    }],
]);

// The popup links to the extension's settings page. Kavita's equivalent is its own settings route.
const htmlRewrites = new Map([
    ['popup.html', [['href="./settings.html#dictionaries" target="_top"', 'href="../../settings#yomitan" target="_top"']]],
]);

function rewriteHtml(file, source) {
    const relative = path.relative(extDir, file);
    source = source.replace(/\b(href|src)="(\/[^"/][^"]*)"/g, (_m, attribute, url) => `${attribute}="${relativeFrom(file, url)}"`);
    for (const [search, replacement] of htmlRewrites.get(relative) ?? []) {
        source = source.replace(search, replacement);
    }
    const wiring = pageWiring.get(relative);
    if (typeof wiring !== 'undefined') {
        const importMap = JSON.stringify({imports: wiring.imports});
        const head = [
            `<script src="js/kavita/chrome-shim.js" data-context="${wiring.context}"></script>`,
            `<script type="importmap">${importMap}</script>`,
        ].join('\n    ');
        source = source.replace(/(<meta charset="UTF-8">)/, `$1\n    ${head}`);
    }
    return source;
}

// Paths that are resolved against the page URL rather than through chrome.runtime.getURL.
const scriptRewrites = new Map([
    ['js/media/audio-system.js', [[/new Audio\('\/data\//g, 'new Audio(\'data/']]],
]);

function copyFile(file) {
    const relative = path.relative(extDir, file);
    const target = path.join(outDir, relative);
    fs.mkdirSync(path.dirname(target), {recursive: true});
    if (file.endsWith('.css')) {
        fs.writeFileSync(target, rewriteCss(file, fs.readFileSync(file, 'utf8')));
    } else if (file.endsWith('.html')) {
        fs.writeFileSync(target, rewriteHtml(file, fs.readFileSync(file, 'utf8')));
    } else if (scriptRewrites.has(relative)) {
        let source = fs.readFileSync(file, 'utf8');
        for (const [pattern, replacement] of scriptRewrites.get(relative)) {
            source = source.replace(pattern, replacement);
        }
        fs.writeFileSync(target, source);
    } else {
        fs.copyFileSync(file, target);
    }
}

function walk(file, callback) {
    if (fs.statSync(file).isDirectory()) {
        for (const child of fs.readdirSync(file)) { walk(path.join(file, child), callback); }
    } else {
        callback(file);
    }
}

// Start clean, keeping the hand-written integration.
for (const child of fs.existsSync(outDir) ? fs.readdirSync(outDir) : []) {
    const target = path.join(outDir, child);
    if (child === 'js') {
        for (const jsChild of fs.readdirSync(target)) {
            if (path.join(target, jsChild) !== keepDir) { fs.rmSync(path.join(target, jsChild), {recursive: true}); }
        }
    } else if (child !== 'kavita') {
        fs.rmSync(target, {recursive: true});
    }
}

const modules = collectModules();
for (const file of modules) { copyFile(file); }
for (const staticPath of staticPaths) { walk(path.join(extDir, staticPath), copyFile); }
fs.copyFileSync(path.resolve(repoDir, 'LICENSE'), path.join(outDir, 'LICENSE'));

let revision = 'unknown';
try {
    const describe = execFileSync('git', ['-C', repoDir, 'describe', '--tags', '--always'], {encoding: 'utf8'}).trim();
    const commit = execFileSync('git', ['-C', repoDir, 'rev-parse', 'HEAD'], {encoding: 'utf8'}).trim();
    revision = `${describe} (${commit})`;
} catch {
    // A copy without .git still vendors fine, it just can't record where it came from.
}
fs.writeFileSync(path.join(outDir, 'VENDORED'), `Yomitan ${revision}\nModules: ${modules.length}\n`);
console.log(`Vendored ${modules.length} modules from Yomitan ${revision} into ${outDir}`);
