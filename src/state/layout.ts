import type { Category } from '../types';
import { adaptivePresetKey, folderAlignmentKey, layoutKey, sizeKey } from './keys';

export type ZonePosition = { x: number; y: number };
export type ZonePositions = Record<string, ZonePosition>;
export type ZoneSize = { width: number; height: number };
export type ZoneSizes = Record<string, ZoneSize>;
export type PanelViewMode = 'grid' | 'list';
export type PanelSortMode = 'name' | 'modified' | 'size';
export type LayoutPreset = 'balanced' | 'grid' | 'columns' | 'corners' | 'right-dock';
export type FolderAlignment = 'manual' | 'left' | 'center' | 'right';

export function defaultSizes(categories: Category[]): ZoneSizes {
  return Object.fromEntries(categories.map((category) => [category.id, { width: 292, height: 238 }]));
}

export function loadSizes(categories: Category[]): ZoneSizes {
  try {
    return { ...defaultSizes(categories), ...JSON.parse(localStorage.getItem(sizeKey) ?? '{}') as ZoneSizes };
  } catch {
    return defaultSizes(categories);
  }
}

export function loadPositions(categories: Category[], zoom = 1): ZonePositions {
  try {
    const saved = JSON.parse(localStorage.getItem(layoutKey) ?? '{}') as ZonePositions;
    if (Object.keys(saved).length) return saved;
  } catch {
    /* Use the first desktop layout. */
  }
  const width = (typeof window === 'undefined' ? 1440 : window.innerWidth) / zoom;
  const columns = width < 900 ? 2 : Math.min(4, Math.max(2, Math.floor((width - 80) / 320)));
  return Object.fromEntries(categories.map((category, index) => [
    category.id,
    { x: 24 + (index % columns) * 292, y: 24 + Math.floor(index / columns) * 238 },
  ]));
}

export function clampPosition(position: ZonePosition, size: ZoneSize = { width: 300, height: 200 }, zoom = 1): ZonePosition {
  const width = (typeof window === 'undefined' ? 1440 : window.innerWidth) / zoom;
  const height = (typeof window === 'undefined' ? 900 : window.innerHeight) / zoom;
  return {
    x: Math.max(10, Math.min(position.x, Math.max(10, width - size.width - 10))),
    y: Math.max(10, Math.min(position.y, Math.max(10, height - size.height - 10))),
  };
}

export function loadAdaptivePreset(): LayoutPreset | 'manual' {
  const folderMode = localStorage.getItem(folderAlignmentKey);
  if (folderMode === 'left' || folderMode === 'center' || folderMode === 'right') return 'manual';
  const value = localStorage.getItem(adaptivePresetKey);
  return value === 'balanced' || value === 'grid' || value === 'columns' || value === 'corners' || value === 'right-dock' ? value : 'manual';
}

export function loadFolderAlignment(): FolderAlignment {
  const value = localStorage.getItem(folderAlignmentKey);
  return value === 'left' || value === 'center' || value === 'right' ? value : 'manual';
}
