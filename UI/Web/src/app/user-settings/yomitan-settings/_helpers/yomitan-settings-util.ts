import {YomitanModification, YomitanPath, YomitanPathPart} from "../_models/yomitan-options";

const identifierPattern = /^[a-zA-Z_][a-zA-Z0-9_]*$/;

/**
 * Splits 'scanning.inputs.0.include' style paths. Numeric segments become array indexes.
 */
export function toPathArray(path: YomitanPath): YomitanPathPart[] {
  if (Array.isArray(path)) return path;
  return path.split('.').map(part => /^\d+$/.test(part) ? Number(part) : part);
}

/**
 * Same output as Yomitan's ObjectPropertyAccessor.getPathString, which the backend parses.
 */
export function toPathString(path: YomitanPath): string {
  let result = '';
  for (const part of toPathArray(path)) {
    if (typeof part === 'number') {
      result += `[${part}]`;
    } else if (!identifierPattern.test(part)) {
      result += `["${part.replace(/["\\]/g, '\\$&')}"]`;
    } else {
      result += result.length === 0 ? part : `.${part}`;
    }
  }
  return result;
}

export function getAtPath(root: unknown, path: YomitanPath): any {
  let value: any = root;
  for (const part of toPathArray(path)) {
    if (value === null || typeof value !== 'object') return undefined;
    value = value[part];
  }
  return value;
}

function getParent(root: any, path: YomitanPathPart[]): [any, YomitanPathPart] {
  const parent = getAtPath(root, path.slice(0, -1));
  if (parent === null || typeof parent !== 'object') throw new Error(`Invalid path ${toPathString(path)}`);
  return [parent, path[path.length - 1]];
}

/**
 * Applies a modification in place, mirroring the backend's _modifySetting.
 */
export function applyModification(root: any, target: YomitanModification) {
  switch (target.action) {
    case 'set': {
      const [parent, key] = getParent(root, toPathArray(target.path));
      parent[key] = structuredClone(target.value);
      break;
    }
    case 'delete': {
      const [parent, key] = getParent(root, toPathArray(target.path));
      delete parent[key];
      break;
    }
    case 'swap': {
      const [parent1, key1] = getParent(root, toPathArray(target.path1));
      const [parent2, key2] = getParent(root, toPathArray(target.path2));
      const value = parent1[key1];
      parent1[key1] = parent2[key2];
      parent2[key2] = value;
      break;
    }
    case 'splice': {
      const array = getAtPath(root, target.path);
      if (!Array.isArray(array)) throw new Error(`Not an array: ${toPathString(target.path)}`);
      array.splice(target.start, target.deleteCount, ...structuredClone(target.items));
      break;
    }
    case 'push': {
      const array = getAtPath(root, target.path);
      if (!Array.isArray(array)) throw new Error(`Not an array: ${toPathString(target.path)}`);
      array.push(...structuredClone(target.items));
      break;
    }
  }
}

/**
 * The modification as the backend expects it, with string paths and global scope.
 */
export function serializeModification(target: YomitanModification) {
  switch (target.action) {
    case 'swap':
      return {...target, scope: 'global', path1: toPathString(target.path1), path2: toPathString(target.path2)};
    default:
      return {...target, scope: 'global', path: toPathString(target.path)};
  }
}

export function sameValue(a: unknown, b: unknown) {
  if (a === b) return true;
  return JSON.stringify(a) === JSON.stringify(b);
}

/**
 * Yomitan's ProfileController._createCopyName, so duplicates are named "Name (Copy)", "Name (Copy 2)"...
 */
export function createCopyName(name: string, existingNames: string[], maxUniqueAttempts = 100) {
  let space: string, index: string | number, prefix: string, suffix: string;
  const match = /^([\w\W]*\(Copy)((\s+)(\d+))?(\)\s*)$/.exec(name);
  if (match === null) {
    prefix = `${name} (Copy`;
    space = '';
    index = '';
    suffix = ')';
  } else {
    prefix = match[1];
    suffix = match[5];
    if (typeof match[2] === 'string') {
      space = match[3];
      index = Number.parseInt(match[4], 10) + 1;
    } else {
      space = ' ';
      index = 2;
    }
  }

  let i = 0;
  while (true) {
    const newName = `${prefix}${space}${index}${suffix}`;
    if (i++ >= maxUniqueAttempts || !existingNames.includes(newName)) {
      return newName;
    }
    if (typeof index !== 'number') {
      index = 2;
      space = ' ';
    } else {
      ++index;
    }
  }
}

/**
 * Same as Yomitan's generateId, used for new profile ids.
 */
export function generateYomitanId(length: number) {
  const array = new Uint8Array(length);
  crypto.getRandomValues(array);
  let id = '';
  for (const value of array) {
    id += value.toString(16).padStart(2, '0');
  }
  return id;
}

export function downloadJson(data: unknown, fileName: string) {
  const blob = new Blob([JSON.stringify(data, null, 4)], {type: 'application/json'});
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}
