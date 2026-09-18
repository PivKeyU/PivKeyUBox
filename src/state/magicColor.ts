import type { ItemKind } from '../types';

// Magic 分区色：每种文件类型对应的主题色
export const kindToColor: Record<ItemKind, string> = {
  shortcut: '#f4c968',
  folder: '#9ecfc0',
  document: '#a9cbe4',
  image: '#c5b7df',
  media: '#5856d6',
  archive: '#dfb07b',
  other: '#3478f6',
};

// 统计 items 中占比最高的 kind，映射为分区颜色；空数组返回 null
export function dominantKindColor(items: Array<{ kind: string }>): string | null {
  if (items.length === 0) return null;
  const counts = new Map<string, number>();
  for (const item of items) {
    counts.set(item.kind, (counts.get(item.kind) ?? 0) + 1);
  }
  let dominant = '';
  let max = 0;
  for (const [kind, count] of counts) {
    if (count > max) {
      dominant = kind;
      max = count;
    }
  }
  return kindToColor[dominant as ItemKind] ?? null;
}
