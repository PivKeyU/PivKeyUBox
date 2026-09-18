import type { Category, DesktopItem, ItemKind, OrganizationMove, OrganizationRule } from '../types';

const shortcutExtensions = new Set(['lnk', 'url', 'appref-ms']);
const imageExtensions = new Set(['jpg', 'jpeg', 'png', 'gif', 'webp', 'svg', 'heic', 'bmp']);
const mediaExtensions = new Set(['mp3', 'wav', 'm4a', 'flac', 'mp4', 'mov', 'mkv', 'avi']);
const archiveExtensions = new Set(['zip', 'rar', '7z', 'tar', 'gz']);

export function getExtension(name: string): string {
  const lastDot = name.lastIndexOf('.');
  return lastDot > 0 ? name.slice(lastDot + 1).toLowerCase() : '';
}

export function classifyKind(extension: string, isDirectory: boolean): ItemKind {
  if (isDirectory) return 'folder';
  if (shortcutExtensions.has(extension)) return 'shortcut';
  if (imageExtensions.has(extension)) return 'image';
  if (mediaExtensions.has(extension)) return 'media';
  if (archiveExtensions.has(extension)) return 'archive';
  if (extension) return 'document';
  return 'other';
}

export function matchCategory(extension: string, isDirectory: boolean, categories: Category[]): string {
  const exact = categories.find((category) =>
    isDirectory ? category.acceptsFolders : category.extensions.includes(extension),
  );
  return exact?.id ?? 'uncategorized';
}

function wildcardMatch(value: string, pattern: string): boolean {
  const escaped = pattern.trim().replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.');
  if (!escaped) return false;
  try { return new RegExp(`^${escaped}$`, 'i').test(value); } catch { return value.toLocaleLowerCase().includes(pattern.toLocaleLowerCase()); }
}

export function matchOrganizationRule(item: DesktopItem, rules: OrganizationRule[]): string | undefined {
  const ordered = [...rules].filter((rule) => rule.enabled && rule.pattern.trim()).sort((left, right) => left.priority - right.priority);
  for (const rule of ordered) {
    const pattern = rule.pattern.trim();
    const matched = rule.match === 'extension'
      ? pattern.split(',').map((value) => value.trim().replace(/^\./, '').toLowerCase()).includes(item.extension.toLowerCase())
      : rule.match === 'name'
        ? wildcardMatch(item.name, pattern)
        : wildcardMatch(item.path, pattern) || item.path.toLocaleLowerCase().includes(pattern.toLocaleLowerCase());
    if (matched) return rule.categoryId;
  }
  return undefined;
}

export function buildOrganizationPlan(
  items: DesktopItem[],
  categories: Category[],
  desktopPath: string,
  rules: OrganizationRule[] = [],
): OrganizationMove[] {
  return items
    .filter((item) => !item.managed)
    .map((item) => {
      const categoryId = matchOrganizationRule(item, rules) ?? item.categoryId;
      const category = categories.find((candidate) => candidate.id === categoryId);
      const separator = desktopPath.includes('\\') ? '\\' : '/';
      const folder = `${desktopPath}${separator}片刻收纳${separator}${category?.name ?? '其他'}`;
      return {
        item,
        source: item.path,
        destination: `${folder}${separator}${item.name}`,
        categoryId,
        status: 'pending' as const,
      };
    })
    .filter((move) => move.categoryId !== 'uncategorized');
}

export function makeItemId(path: string): string {
  let hash = 2166136261;
  for (let index = 0; index < path.length; index += 1) {
    hash ^= path.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return `item-${(hash >>> 0).toString(36)}`;
}

export type ItemSortMode = 'name' | 'modified' | 'size';

export function sortDesktopItems(items: DesktopItem[], mode: ItemSortMode): DesktopItem[] {
  return [...items].sort((left, right) => {
    if (mode === 'modified') {
      const result = new Date(right.modifiedAt).getTime() - new Date(left.modifiedAt).getTime();
      if (Number.isFinite(result) && result) return result;
    }
    if (mode === 'size') {
      const result = right.size - left.size;
      if (result) return result;
    }
    return left.name.localeCompare(right.name, 'zh-CN', { numeric: true, sensitivity: 'base' });
  });
}
