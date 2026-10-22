/*
 * Ports of the parts of Yomitan's HotkeyUtil and document-util that its settings page uses to show and
 * capture keyboard and mouse inputs. Modifiers are 'alt', 'ctrl', 'meta', 'shift' or 'mouseN'.
 */

const mouseInputPattern = /^mouse(\d+)$/;
const modifierPriorities = new Map<string, number>([
  ['meta', -4],
  ['ctrl', -3],
  ['alt', -2],
  ['shift', -1],
]);
const stringComparer = new Intl.Collator('en-US');

export const MODIFIER_KEYS = ['alt', 'ctrl', 'shift', 'meta'] as const;

export function getModifierKeyNames(os: string): Map<string, string> {
  switch (os) {
    case 'win':
      return new Map([['alt', 'Alt'], ['ctrl', 'Ctrl'], ['shift', 'Shift'], ['meta', 'Windows']]);
    case 'mac':
      return new Map([['alt', 'Opt'], ['ctrl', 'Ctrl'], ['shift', 'Shift'], ['meta', 'Cmd']]);
    case 'linux':
    case 'openbsd':
    case 'cros':
    case 'android':
      return new Map([['alt', 'Alt'], ['ctrl', 'Ctrl'], ['shift', 'Shift'], ['meta', 'Super']]);
    default:
      return new Map([['alt', 'Alt'], ['ctrl', 'Ctrl'], ['shift', 'Shift'], ['meta', 'Meta']]);
  }
}

export function getModifierDisplayValue(modifier: string, os: string) {
  const match = mouseInputPattern.exec(modifier);
  if (match !== null) return `Mouse ${match[1]}`;
  return getModifierKeyNames(os).get(modifier) ?? modifier;
}

export function getKeyDisplayValue(key: string) {
  if (key.length === 4 && key.startsWith('Key')) return key.substring(3);
  return key;
}

export function getInputDisplayValue(key: string | null, modifiers: string[], os: string) {
  const parts = modifiers.map(m => getModifierDisplayValue(m, os));
  if (key !== null) parts.push(getKeyDisplayValue(key));
  return parts.join(' + ');
}

export function isMouseInput(modifier: string) {
  return mouseInputPattern.test(modifier);
}

/**
 * Keys first (meta, ctrl, alt, shift), then mouse buttons by number.
 */
export function sortModifiers(modifiers: string[]) {
  return modifiers
    .map((modifier, index) => {
      const match = mouseInputPattern.exec(modifier);
      const isMouse = match !== null ? 1 : 0;
      const priority = match !== null ? Number.parseInt(match[1], 10) : (modifierPriorities.get(modifier) ?? 0);
      return {modifier, isMouse, priority, index};
    })
    .sort((a, b) => (a.isMouse - b.isMouse) || (a.priority - b.priority)
      || stringComparer.compare(a.modifier, b.modifier) || (a.index - b.index))
    .map(info => info.modifier);
}

/**
 * Scan inputs store modifiers as a comma separated string.
 */
export function splitModifiers(value: string) {
  return value.split(/[,;\s]+/)
    .map(v => v.trim().toLowerCase())
    .filter(v => v.length > 0 && (isMouseInput(v) || (MODIFIER_KEYS as readonly string[]).includes(v)));
}

export function joinModifiers(modifiers: string[]) {
  return modifiers.join(', ');
}

export function getActiveModifiers(event: KeyboardEvent | MouseEvent) {
  const modifiers: string[] = [];
  if (event.altKey) modifiers.push('alt');
  if (event.ctrlKey) modifiers.push('ctrl');
  if (event.metaKey) modifiers.push('meta');
  if (event.shiftKey) modifiers.push('shift');

  // Some browsers leave the modifier being pressed out of its own keydown event
  if (event instanceof KeyboardEvent) {
    const add = (modifier: string, codes: string[], key: string) => {
      if ((codes.includes(event.code) || event.key === key) && !modifiers.includes(modifier)) modifiers.push(modifier);
    };
    add('alt', ['AltLeft', 'AltRight'], 'Alt');
    add('ctrl', ['ControlLeft', 'ControlRight'], 'Control');
    add('meta', ['MetaLeft', 'MetaRight'], 'Meta');
    add('shift', ['ShiftLeft', 'ShiftRight'], 'Shift');
  }
  return modifiers;
}

export function getActiveButtons(event: MouseEvent) {
  const result: string[] = [];
  let buttons = event.buttons;
  if (typeof buttons === 'number' && buttons > 0) {
    for (let i = 0; i < 6; ++i) {
      const flag = 1 << i;
      if ((buttons & flag) !== 0) {
        result.push(`mouse${i}`);
        buttons &= ~flag;
        if (buttons === 0) break;
      }
    }
  }
  return result;
}

export function isModifierKeyCode(code: string | undefined) {
  switch (code) {
    case 'AltLeft':
    case 'AltRight':
    case 'ControlLeft':
    case 'ControlRight':
    case 'MetaLeft':
    case 'MetaRight':
    case 'ShiftLeft':
    case 'ShiftRight':
    case 'OSLeft':
    case 'OSRight':
      return true;
    default:
      return false;
  }
}
