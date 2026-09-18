import type { Category } from '../types';

const invalidWindowsNameCharacters = /[<>:"/\\|?*\u0000-\u001f]/;
const reservedWindowsNames = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?$/i;

const legacyDefaultIcons: Record<string, { legacy: string[]; next: string }> = {
  shortcuts: { legacy: ['rocket', 'shortcut'], next: 'chiikawa-wave' },
  documents: { legacy: ['file-text'], next: 'chiikawa-read' },
  sheets: { legacy: ['sheet'], next: 'chiikawa-organize' },
  slides: { legacy: ['presentation'], next: 'chiikawa-search' },
  images: { legacy: ['image'], next: 'chiikawa-idle' },
  media: { legacy: ['play'], next: 'chiikawa-wave' },
  archives: { legacy: ['archive'], next: 'chiikawa-carry-folder' },
  folders: { legacy: ['folder'], next: 'chiikawa-organize' },
};

const legacyDefaultColors: Record<string, { legacy: string[]; next: string }> = {
  shortcuts: { legacy: ['#ff6b5f', '#e98687'], next: '#3478f6' },
  documents: { legacy: ['#3478f6'], next: '#a9cbe4' },
  sheets: { legacy: ['#34a853'], next: '#9ecfc0' },
  slides: { legacy: ['#ff9f0a'], next: '#f4c968' },
  images: { legacy: ['#af52de'], next: '#c5b7df' },
  media: { legacy: ['#ff375f', '#e4a5b5'], next: '#5856d6' },
  archives: { legacy: ['#8e8e93'], next: '#dfb07b' },
  folders: { legacy: ['#18a999'], next: '#9ecfc0' },
};

export function normalizeCategoryName(value: string, existingNames: string[] = []): string {
  const name = value.trim();
  if (!name) throw new Error('请输入分类名称');
  if (name.length > 20) throw new Error('分类名称不能超过 20 个字符');
  if (name === '.' || name === '..') throw new Error('分类名称不能是“.”或“..”');
  if (invalidWindowsNameCharacters.test(name)) throw new Error('分类名称不能包含 \\ / : * ? " < > | 等字符');
  if (name.endsWith('.')) throw new Error('分类名称不能以句点结尾');
  if (reservedWindowsNames.test(name)) throw new Error(`“${name}”是 Windows 保留名称`);
  if (existingNames.some((candidate) => candidate.trim().toLocaleLowerCase('zh-CN') === name.toLocaleLowerCase('zh-CN'))) {
    throw new Error(`已经存在“${name}”分区`);
  }
  return name;
}

export function normalizeCategory(value: unknown, fallbackId: string): Category | null {
  if (!value || typeof value !== 'object') return null;
  const candidate = value as Partial<Category>;
  try {
    const name = normalizeCategoryName(typeof candidate.name === 'string' ? candidate.name : '');
    const extensions = Array.isArray(candidate.extensions)
      ? [...new Set(candidate.extensions
        .filter((extension): extension is string => typeof extension === 'string')
        .map((extension) => extension.replace(/^\./, '').trim().toLowerCase())
        .filter(Boolean))]
      : [];
    // portalPath 非 string 时删除该字段（不保留非法值）
    const portalPath = typeof candidate.portalPath === 'string' && candidate.portalPath.trim()
      ? { portalPath: candidate.portalPath }
      : {};
    const id = typeof candidate.id === 'string' && candidate.id.trim() ? candidate.id : fallbackId;
    const rawIcon = typeof candidate.icon === 'string' && candidate.icon.trim() ? candidate.icon : 'chiikawa-idle';
    const legacyIcon = legacyDefaultIcons[id];
    const icon = legacyIcon && legacyIcon.legacy.includes(rawIcon) ? legacyIcon.next : rawIcon;
    const rawColor = typeof candidate.color === 'string' && /^#[0-9a-f]{6}$/i.test(candidate.color) ? candidate.color : '#9ecfc0';
    const legacyColor = legacyDefaultColors[id];
    const color = legacyColor && legacyColor.legacy.some((candidateLegacy) => candidateLegacy.toLowerCase() === rawColor.toLowerCase()) ? legacyColor.next : rawColor;
    return {
      id,
      name,
      icon,
      color,
      extensions,
      acceptsFolders: Boolean(candidate.acceptsFolders),
      ...portalPath,
    };
  } catch {
    return null;
  }
}
