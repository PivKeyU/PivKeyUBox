import idle from '../assets/characters/chiikawa-idle.png';
import wave from '../assets/characters/chiikawa-wave.png';
import search from '../assets/characters/chiikawa-search.png';
import read from '../assets/characters/chiikawa-read.png';
import carryFolder from '../assets/characters/chiikawa-carry-folder.png';
import organize from '../assets/characters/chiikawa-organize.png';

export const CHARACTER_ICONS = [
  { name: 'chiikawa-idle', label: '站立', source: idle },
  { name: 'chiikawa-wave', label: '挥手', source: wave },
  { name: 'chiikawa-search', label: '搜索', source: search },
  { name: 'chiikawa-read', label: '阅读', source: read },
  { name: 'chiikawa-carry-folder', label: '搬文件夹', source: carryFolder },
  { name: 'chiikawa-organize', label: '整理文件', source: organize },
] as const;

export type CharacterIconName = (typeof CHARACTER_ICONS)[number]['name'];

const characterIconByName = Object.fromEntries(CHARACTER_ICONS.map((icon) => [icon.name, icon])) as Record<CharacterIconName, (typeof CHARACTER_ICONS)[number]>;

export function characterIconSource(name: string): string | undefined {
  return characterIconByName[name as CharacterIconName]?.source;
}

export function isCharacterIcon(name: string): name is CharacterIconName {
  return Boolean(characterIconByName[name as CharacterIconName]);
}

const defaultIconByCategory: Record<string, CharacterIconName> = {
  shortcuts: 'chiikawa-wave',
  documents: 'chiikawa-read',
  sheets: 'chiikawa-organize',
  slides: 'chiikawa-search',
  images: 'chiikawa-idle',
  media: 'chiikawa-wave',
  archives: 'chiikawa-carry-folder',
  folders: 'chiikawa-organize',
};

export function defaultCategoryIcon(categoryId: string): CharacterIconName {
  return defaultIconByCategory[categoryId] ?? 'chiikawa-idle';
}
