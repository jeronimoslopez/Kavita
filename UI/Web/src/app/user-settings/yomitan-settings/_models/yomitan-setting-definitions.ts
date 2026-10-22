import {YomitanSettingDef} from "./yomitan-options";

/*
 * Settings that map one-to-one onto Yomitan's settings.html controls, in Yomitan's order. Labels,
 * descriptions and help text live in en.json under yomitan-settings, keyed by each definition's key.
 * Settings that need their own editor (lists, profiles, dictionaries) are built in the section components.
 */

export const YOMITAN_GENERAL_SETTINGS: YomitanSettingDef[] = [
  {key: 'enable', controls: [{path: 'general.enable', type: 'toggle'}]},
  {
    key: 'max-results',
    controls: [{path: 'general.maxResults', type: 'number', min: 1}],
    advanced: true,
    description: true,
  },
];

export const YOMITAN_SCANNING_SETTINGS: YomitanSettingDef[] = [
  {key: 'scan-delay', controls: [{path: 'scanning.delay', type: 'number', min: 0}], advanced: true, description: true},
  {
    key: 'scan-without-mousemove',
    controls: [{path: 'scanning.scanWithoutMousemove', type: 'toggle'}],
    advanced: true,
    description: true,
  },
  {key: 'select-text', controls: [{path: 'scanning.selectText', type: 'toggle'}]},
  {key: 'alphanumeric', controls: [{path: 'scanning.alphanumeric', type: 'toggle'}], description: true},
  {
    key: 'layout-aware-scan',
    controls: [{path: 'scanning.layoutAwareScan', type: 'toggle'}],
    advanced: true,
    description: true,
  },
  {key: 'deep-dom-scan', controls: [{path: 'scanning.deepDomScan', type: 'toggle'}], advanced: true, description: true},
  {
    key: 'normalize-css-zoom',
    controls: [{path: 'scanning.normalizeCssZoom', type: 'toggle'}],
    advanced: true,
    description: true,
    help: true,
  },
  {
    key: 'wildcard-scanning',
    controls: [{path: 'scanning.matchTypePrefix', type: 'toggle'}],
    advanced: true,
    description: true,
    help: true,
  },
  {
    key: 'text-scan-length',
    controls: [{path: 'scanning.length', type: 'number', min: 1, step: 1}],
    advanced: true,
    description: true,
  },
];

export const YOMITAN_POPUP_SETTINGS: YomitanSettingDef[] = [
  {
    key: 'enable-on-popup-expressions',
    controls: [{path: 'scanning.enableOnPopupExpressions', type: 'toggle'}],
    advanced: true,
    child: true,
    visibleWhen: o => o.scanning.popupNestingMaxDepth > 0,
  },
  {key: 'auto-hide-results', controls: [{path: 'scanning.autoHideResults', type: 'toggle'}], description: true},
  {
    key: 'auto-hide-delay',
    controls: [{path: 'scanning.hideDelay', type: 'number', min: 0}],
    child: true,
    visibleWhen: o => o.scanning.autoHideResults,
  },
  {
    key: 'hide-popup-on-cursor-exit',
    controls: [{path: 'scanning.hidePopupOnCursorExit', type: 'toggle'}],
    description: true,
  },
  {
    key: 'hide-popup-on-cursor-exit-delay',
    controls: [{path: 'scanning.hidePopupOnCursorExitDelay', type: 'number', min: 0}],
    child: true,
    visibleWhen: o => o.scanning.hidePopupOnCursorExit,
  },
  {
    key: 'reduced-motion-scrolling',
    controls: [{path: 'scanning.reducedMotionScrolling', type: 'toggle'}],
    advanced: true,
    description: true,
  },
  {
    key: 'reduced-motion-scrolling-scale',
    controls: [
      {
        path: 'scanning.reducedMotionScrollingScale',
        type: 'select',
        options: [
          {value: '0.4', key: 'reduced-motion-scrolling-scale-0-4'},
          {value: '0.5', key: 'reduced-motion-scrolling-scale-0-5'},
          {value: '0.6', key: 'reduced-motion-scrolling-scale-0-6'},
          {value: '0.7', key: 'reduced-motion-scrolling-scale-0-7'},
          {value: '0.8', key: 'reduced-motion-scrolling-scale-0-8'},
          {value: '0.9', key: 'reduced-motion-scrolling-scale-0-9'},
          {value: '1', key: 'reduced-motion-scrolling-scale-1'},
        ],
        numeric: true,
      },
    ],
    advanced: true,
    description: true,
    child: true,
    visibleWhen: o => o.scanning.reducedMotionScrolling,
  },
  {
    key: 'reduced-motion-scrolling-swipe-threshold',
    controls: [{path: 'scanning.reducedMotionScrollingSwipeThreshold', type: 'number', min: 0}],
    advanced: true,
    description: true,
    child: true,
    visibleWhen: o => o.scanning.reducedMotionScrolling,
  },
  {key: 'enable-popup-search', controls: [{path: 'scanning.enablePopupSearch', type: 'toggle'}], advanced: true},
  {
    key: 'show-iframe-popups-in-root-frame',
    controls: [{path: 'general.showIframePopupsInRootFrame', type: 'toggle'}],
    advanced: true,
    help: true,
  },
];

export const YOMITAN_APPEARANCE_SETTINGS: YomitanSettingDef[] = [
  {
    key: 'theme',
    controls: [
      {
        path: 'general.popupTheme',
        type: 'select',
        sub: 'theme-body',
        options: [
          {value: 'site', key: 'popup-theme-site'},
          {value: 'light', key: 'popup-theme-light'},
          {value: 'dark', key: 'popup-theme-dark'},
          {value: 'browser', key: 'popup-theme-browser'},
        ],
      },
      {
        path: 'general.popupOuterTheme',
        type: 'select',
        sub: 'theme-shadow',
        options: [
          {value: 'site', key: 'popup-outer-theme-site'},
          {value: 'light', key: 'popup-outer-theme-light'},
          {value: 'dark', key: 'popup-outer-theme-dark'},
          {value: 'browser', key: 'popup-outer-theme-browser'},
          {value: 'none', key: 'popup-outer-theme-none'},
        ],
      },
    ],
    description: true,
  },
  {
    key: 'action-bar',
    controls: [
      {
        path: 'general.popupActionBarVisibility',
        type: 'select',
        sub: 'action-bar-visibility',
        options: [
          {value: 'auto', key: 'popup-action-bar-visibility-auto'},
          {value: 'always', key: 'popup-action-bar-visibility-always'},
        ],
      },
      {
        path: 'general.popupActionBarLocation',
        type: 'select',
        sub: 'action-bar-location',
        options: [
          {value: 'left', key: 'popup-action-bar-location-left'},
          {value: 'right', key: 'popup-action-bar-location-right'},
          {value: 'top', key: 'popup-action-bar-location-top'},
          {value: 'bottom', key: 'popup-action-bar-location-bottom'},
        ],
      },
    ],
    description: true,
  },
  {
    key: 'font-family',
    controls: [{path: 'general.fontFamily', type: 'text'}],
    advanced: true,
    description: true,
    help: true,
  },
  {key: 'font-size', controls: [{path: 'general.fontSize', type: 'number', min: 1}], advanced: true, description: true},
  {key: 'line-height', controls: [{path: 'general.lineHeight', type: 'text'}], advanced: true, description: true},
  {
    key: 'compact-glossaries',
    controls: [
      {
        path: 'general.glossaryLayoutMode',
        type: 'select',
        options: [
          {value: 'default', key: 'glossary-layout-mode-default'},
          {value: 'compact', key: 'glossary-layout-mode-compact'},
          {value: 'compact-popup-anki', key: 'glossary-layout-mode-compact-popup-anki'},
        ],
      },
    ],
    description: true,
  },
  {key: 'compact-tags', controls: [{path: 'general.compactTags', type: 'toggle'}], description: true},
  {
    key: 'enable-search-tags',
    controls: [{path: 'scanning.enableSearchTags', type: 'toggle'}],
    advanced: true,
    description: true,
  },
  {
    key: 'term-display-mode',
    controls: [
      {
        path: 'general.termDisplayMode',
        type: 'select',
        options: [
          {value: 'ruby', key: 'term-display-mode-ruby'},
          {value: 'ruby-and-reading', key: 'term-display-mode-ruby-and-reading'},
          {value: 'term-and-reading', key: 'term-display-mode-term-and-reading'},
          {value: 'term-only', key: 'term-display-mode-term-only'},
        ],
      },
    ],
    advanced: true,
    description: true,
  },
  {
    key: 'frequency-display-mode',
    controls: [
      {
        path: 'general.frequencyDisplayMode',
        type: 'select',
        options: [
          {value: 'tags', key: 'frequency-display-mode-tags'},
          {value: 'tags-grouped', key: 'frequency-display-mode-tags-grouped'},
          {value: 'split-tags', key: 'frequency-display-mode-split-tags'},
          {value: 'split-tags-grouped', key: 'frequency-display-mode-split-tags-grouped'},
          {value: 'inline-list', key: 'frequency-display-mode-inline-list'},
          {value: 'list', key: 'frequency-display-mode-list'},
          {value: 'list-bordered', key: 'frequency-display-mode-list-bordered'},
        ],
      },
    ],
    advanced: true,
    description: true,
  },
  {
    key: 'popup-current-indicator-mode',
    controls: [
      {
        path: 'general.popupCurrentIndicatorMode',
        type: 'select',
        options: [
          {value: 'none', key: 'popup-current-indicator-mode-none'},
          {value: 'asterisk', key: 'popup-current-indicator-mode-asterisk'},
          {value: 'triangle', key: 'popup-current-indicator-mode-triangle'},
          {value: 'bar-left', key: 'popup-current-indicator-mode-bar-left'},
          {value: 'bar-right', key: 'popup-current-indicator-mode-bar-right'},
          {value: 'dot-left', key: 'popup-current-indicator-mode-dot-left'},
          {value: 'dot-right', key: 'popup-current-indicator-mode-dot-right'},
        ],
      },
    ],
    advanced: true,
    description: true,
  },
  {
    key: 'pitch-accent',
    controls: [
      {path: 'general.showPitchAccentDownstepNotation', type: 'checkbox', sub: 'pitch-accent-downstep-notation'},
      {path: 'general.showPitchAccentPositionNotation', type: 'checkbox', sub: 'pitch-accent-downstep-position'},
      {path: 'general.showPitchAccentGraph', type: 'checkbox', sub: 'pitch-accent-graph'},
    ],
    description: true,
    help: true,
  },
];

export const YOMITAN_RESULT_DISPLAY_SETTINGS: YomitanSettingDef[] = [
  {
    key: 'result-output-mode',
    controls: [
      {
        path: 'general.resultOutputMode',
        type: 'select',
        options: [
          {value: 'split', key: 'result-output-mode-split'},
          {value: 'group', key: 'result-output-mode-group'},
          {value: 'term', key: 'result-output-mode-term'},
          {value: 'merge', key: 'result-output-mode-merge'},
        ],
      },
    ],
    description: true,
    help: true,
  },
  {
    key: 'main-dictionary',
    controls: [{path: 'general.mainDictionary', type: 'select', options: 'mainDictionary'}],
    child: true,
    visibleWhen: o => o.general.resultOutputMode === 'merge',
  },
  {key: 'average-frequency', controls: [{path: 'general.averageFrequency', type: 'toggle'}], description: true},
];

export const YOMITAN_POPUP_SIZE_SETTINGS: YomitanSettingDef[] = [
  {
    key: 'popup-display-mode',
    controls: [
      {
        path: 'general.popupDisplayMode',
        type: 'select',
        options: [
          {value: 'default', key: 'popup-display-mode-default'},
          {value: 'full-width', key: 'popup-display-mode-full-width'},
        ],
      },
    ],
    description: true,
    help: true,
  },
  {
    key: 'popup-full-width-position',
    controls: [
      {
        path: 'general.popupFullWidthPosition',
        type: 'select',
        options: [
          {value: 'top', key: 'popup-full-width-position-top'},
          {value: 'above-cursor', key: 'popup-full-width-position-above-cursor'},
          {value: 'bottom', key: 'popup-full-width-position-bottom'},
        ],
      },
    ],
    child: true,
    visibleWhen: o => o.general.popupDisplayMode === 'full-width',
  },
  {
    key: 'popup-scale',
    controls: [
      {
        path: 'general.popupScalingFactor',
        type: 'select',
        options: [
          {value: '0.25', key: 'popup-scaling-factor-0-25'},
          {value: '0.33', key: 'popup-scaling-factor-0-33'},
          {value: '0.5', key: 'popup-scaling-factor-0-5'},
          {value: '0.67', key: 'popup-scaling-factor-0-67'},
          {value: '0.75', key: 'popup-scaling-factor-0-75'},
          {value: '0.8', key: 'popup-scaling-factor-0-8'},
          {value: '0.9', key: 'popup-scaling-factor-0-9'},
          {value: '1', key: 'popup-scaling-factor-1'},
          {value: '1.1', key: 'popup-scaling-factor-1-1'},
          {value: '1.25', key: 'popup-scaling-factor-1-25'},
          {value: '1.5', key: 'popup-scaling-factor-1-5'},
          {value: '1.75', key: 'popup-scaling-factor-1-75'},
          {value: '2', key: 'popup-scaling-factor-2'},
          {value: '2.5', key: 'popup-scaling-factor-2-5'},
          {value: '3', key: 'popup-scaling-factor-3'},
          {value: '4', key: 'popup-scaling-factor-4'},
          {value: '5', key: 'popup-scaling-factor-5'},
        ],
        numeric: true,
      },
    ],
    description: true,
  },
  {
    key: 'auto-scale',
    controls: [
      {path: 'general.popupScaleRelativeToPageZoom', type: 'checkbox', sub: 'auto-scale-zoom-level'},
      {path: 'general.popupScaleRelativeToVisualViewport', type: 'checkbox', sub: 'auto-scale-viewport'},
    ],
    advanced: true,
    help: true,
    child: true,
  },
  {
    key: 'popup-size',
    controls: [
      {path: 'general.popupWidth', type: 'number', sub: 'popup-size-width', min: 1},
      {path: 'general.popupHeight', type: 'number', sub: 'popup-size-height', min: 1},
    ],
    description: true,
  },
  {
    key: 'popup-horizontal-text-position',
    controls: [
      {
        path: 'general.popupHorizontalTextPosition',
        type: 'select',
        options: [
          {value: 'below', key: 'popup-horizontal-text-position-below'},
          {value: 'above', key: 'popup-horizontal-text-position-above'},
        ],
      },
    ],
    advanced: true,
    description: true,
  },
  {
    key: 'popup-vertical-text-position',
    controls: [
      {
        path: 'general.popupVerticalTextPosition',
        type: 'select',
        options: [
          {value: 'default', key: 'popup-vertical-text-position-default'},
          {value: 'before', key: 'popup-vertical-text-position-before'},
          {value: 'after', key: 'popup-vertical-text-position-after'},
          {value: 'left', key: 'popup-vertical-text-position-left'},
          {value: 'right', key: 'popup-vertical-text-position-right'},
        ],
      },
    ],
    advanced: true,
    description: true,
  },
  {
    key: 'horizontal-text-offset',
    controls: [
      {path: 'general.popupHorizontalOffset', type: 'number', sub: 'horizontal-text-offset-x'},
      {path: 'general.popupVerticalOffset', type: 'number', sub: 'horizontal-text-offset-y'},
    ],
    advanced: true,
    description: true,
  },
  {
    key: 'vertical-text-offset',
    controls: [
      {path: 'general.popupHorizontalOffset2', type: 'number', sub: 'vertical-text-offset-x'},
      {path: 'general.popupVerticalOffset2', type: 'number', sub: 'vertical-text-offset-y'},
    ],
    advanced: true,
    description: true,
  },
];

export const YOMITAN_AUDIO_SETTINGS: YomitanSettingDef[] = [
  {key: 'audio-enabled', controls: [{path: 'audio.enabled', type: 'toggle'}], description: true},
  {key: 'audio-auto-play', controls: [{path: 'audio.autoPlay', type: 'toggle'}], description: true},
  {
    key: 'audio-fallback-sound',
    controls: [
      {
        path: 'audio.fallbackSoundType',
        type: 'select',
        options: [
          {value: 'none', key: 'fallback-sound-type-none'},
          {value: 'click', key: 'fallback-sound-type-click'},
          {value: 'bloop', key: 'fallback-sound-type-bloop'},
        ],
      },
    ],
    advanced: true,
    description: true,
  },
  {key: 'audio-volume', controls: [{path: 'audio.volume', type: 'number', min: 0, max: 100}], description: true},
  {key: 'enable-default-audio-sources', controls: [{path: 'audio.enableDefaultAudioSources', type: 'toggle'}]},
];

export const YOMITAN_TEXT_PARSING_SETTINGS: YomitanSettingDef[] = [
  {
    key: 'sentence-scan-extent',
    controls: [{path: 'sentenceParsing.scanExtent', type: 'number', min: 0, step: 1}],
    advanced: true,
    description: true,
  },
  {
    key: 'termination-characters',
    controls: [
      {
        path: 'sentenceParsing.terminationCharacterMode',
        type: 'select',
        options: [
          {value: 'custom', key: 'termination-character-mode-custom'},
          {value: 'custom-no-newlines', key: 'termination-character-mode-custom-no-newlines'},
          {value: 'newlines', key: 'termination-character-mode-newlines'},
          {value: 'none', key: 'termination-character-mode-none'},
        ],
      },
    ],
    advanced: true,
  },
];

export const YOMITAN_TRANSLATION_SETTINGS: YomitanSettingDef[] = [
  {
    key: 'search-resolution',
    controls: [
      {
        path: 'translation.searchResolution',
        type: 'select',
        options: [
          {value: 'letter', key: 'search-resolution-letter'},
          {value: 'word', key: 'search-resolution-word'},
        ],
      },
    ],
    advanced: true,
    description: true,
  },
];

export const YOMITAN_TRANSLATION_REPLACEMENTS_SETTINGS: YomitanSettingDef[] = [
  {
    key: 'search-original',
    controls: [{path: 'translation.textReplacements.searchOriginal', type: 'toggle'}],
    description: true,
  },
];

export const YOMITAN_ANKI_SETTINGS: YomitanSettingDef[] = [
  {
    key: 'anki-server',
    controls: [{path: 'anki.server', type: 'text', placeholder: 'http://127.0.0.1:8765'}],
    description: true,
    help: true,
  },
  {key: 'anki-tags', controls: [{path: 'anki.tags', type: 'text', transform: 'tags'}], description: true},
  {
    key: 'anki-api-key',
    controls: [{path: 'anki.apiKey', type: 'password', placeholder: 'Disabled'}],
    advanced: true,
    description: true,
  },
  {key: 'anki-check-for-duplicates', controls: [{path: 'anki.checkForDuplicates', type: 'toggle'}], advanced: true},
  {
    key: 'anki-duplicate-all-models',
    controls: [{path: 'anki.duplicateScopeCheckAllModels', type: 'toggle'}],
    advanced: true,
    help: true,
    child: true,
    visibleWhen: o => o.anki.checkForDuplicates,
  },
  {
    key: 'anki-duplicate-scope',
    controls: [
      {
        path: 'anki.duplicateScope',
        type: 'select',
        options: [
          {value: 'collection', key: 'duplicate-scope-collection'},
          {value: 'deck', key: 'duplicate-scope-deck'},
          {value: 'deck-root', key: 'duplicate-scope-deck-root'},
        ],
      },
    ],
    advanced: true,
    help: true,
    child: true,
    visibleWhen: o => o.anki.checkForDuplicates,
  },
  {
    key: 'anki-duplicate-behavior',
    controls: [
      {
        path: 'anki.duplicateBehavior',
        type: 'select',
        options: [
          {value: 'prevent', key: 'duplicate-behavior-prevent'},
          {value: 'overwrite', key: 'duplicate-behavior-overwrite'},
          {value: 'new', key: 'duplicate-behavior-new'},
        ],
      },
    ],
    advanced: true,
    help: true,
    child: true,
    visibleWhen: o => o.anki.checkForDuplicates,
  },
  {
    key: 'anki-note-dupe-check-first',
    controls: [{path: 'anki.noteDupeCheckFirst', type: 'toggle'}],
    advanced: true,
    help: true,
    child: true,
    visibleWhen: o => o.anki.checkForDuplicates,
  },
  {
    key: 'anki-download-timeout',
    controls: [{path: 'anki.downloadTimeout', type: 'number', min: 0}],
    advanced: true,
    description: true,
    help: true,
  },
  {
    key: 'anki-suspend-new-cards',
    controls: [{path: 'anki.suspendNewCards', type: 'toggle'}],
    advanced: true,
    description: true,
  },
  {
    key: 'anki-note-gui-mode',
    controls: [
      {
        path: 'anki.noteGuiMode',
        type: 'select',
        options: [
          {value: 'browse', key: 'note-gui-mode-browse'},
          {value: 'edit', key: 'note-gui-mode-edit'},
        ],
      },
    ],
    advanced: true,
    description: true,
    help: true,
  },
  {
    key: 'anki-display-tags',
    controls: [
      {
        path: 'anki.displayTagsAndFlags',
        type: 'select',
        options: [
          {value: 'never', key: 'display-tags-and-flags-never'},
          {value: 'always', key: 'display-tags-and-flags-always'},
          {value: 'non-standard', key: 'display-tags-and-flags-non-standard'},
          {value: 'custom', key: 'display-tags-and-flags-custom'},
        ],
      },
    ],
    advanced: true,
    help: true,
  },
  {
    key: 'anki-target-tags',
    controls: [{path: 'anki.targetTags', type: 'text', transform: 'tags'}],
    advanced: true,
    description: true,
    child: true,
    visibleWhen: o => o.anki.displayTagsAndFlags === 'custom',
  },
  {key: 'anki-force-sync', controls: [{path: 'anki.forceSync', type: 'toggle'}], advanced: true, description: true},
];

export const YOMITAN_SCANNING_PREVENTION_SETTINGS: YomitanSettingDef[] = [
  {
    key: 'prevent-middle-mouse',
    advanced: true,
    controls: [
      {path: 'scanning.preventMiddleMouse.onTextHover', type: 'checkbox', sub: 'prevention-on-text-hover'},
      {path: 'scanning.preventMiddleMouse.onWebPages', type: 'checkbox', sub: 'prevention-on-web-pages'},
      {path: 'scanning.preventMiddleMouse.onPopupPages', type: 'checkbox', sub: 'prevention-on-popup-pages'},
    ],
  },
  {
    key: 'prevent-back-forward',
    advanced: true,
    controls: [
      {path: 'scanning.preventBackForward.onTextHover', type: 'checkbox', sub: 'prevention-on-text-hover'},
      {path: 'scanning.preventBackForward.onWebPages', type: 'checkbox', sub: 'prevention-on-web-pages'},
      {path: 'scanning.preventBackForward.onPopupPages', type: 'checkbox', sub: 'prevention-on-popup-pages'},
    ],
  },
];

export const YOMITAN_APPEARANCE_CSS_SETTINGS: YomitanSettingDef[] = [
  {key: 'custom-popup-css', advanced: true, controls: [{path: 'general.customPopupCss', type: 'textarea'}]},
  {key: 'custom-popup-outer-css', advanced: true, controls: [{path: 'general.customPopupOuterCss', type: 'textarea'}]},
];
