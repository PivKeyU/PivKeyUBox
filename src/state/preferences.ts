import { defaultCategories, defaultPreferences } from '../data/defaults';
import { normalizeCategory } from '../services/categoryValidation';
import type { AppPreferences, OrganizationRule, PanelItemLayout, WorkspacePreset } from '../types';
import { itemLayoutKey, preferencesKey } from './keys';

export const defaultItemLayout: PanelItemLayout = {
  itemSize: 76,
  iconSize: 54,
  gap: 6,
  alignment: 'left',
  columns: 0,
  showLabels: true,
  labelSize: 12,
};

export type PanelItemLayouts = Record<string, PanelItemLayout>;

function boundedNumber(value: unknown, fallback: number, minimum: number, maximum: number): number {
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.max(minimum, Math.min(maximum, value))
    : fallback;
}

export function normalizeItemLayout(value: unknown): PanelItemLayout {
  const parsed = value && typeof value === 'object' && !Array.isArray(value)
    ? value as Partial<PanelItemLayout>
    : {};
  return {
    itemSize: boundedNumber(parsed.itemSize, defaultItemLayout.itemSize, 44, 112),
    iconSize: boundedNumber(parsed.iconSize, defaultItemLayout.iconSize, 24, 72),
    gap: boundedNumber(parsed.gap, defaultItemLayout.gap, 0, 24),
    alignment: parsed.alignment === 'center' || parsed.alignment === 'right' ? parsed.alignment : defaultItemLayout.alignment,
    columns: Math.round(boundedNumber(parsed.columns, defaultItemLayout.columns, 0, 8)),
    showLabels: typeof parsed.showLabels === 'boolean' ? parsed.showLabels : defaultItemLayout.showLabels,
    labelSize: boundedNumber(parsed.labelSize, defaultItemLayout.labelSize, 8, 18),
  };
}

export function normalizeItemLayouts(value: unknown): PanelItemLayouts {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
  const result: PanelItemLayouts = {};
  let count = 0;
  for (const [id, layout] of Object.entries(value as Record<string, unknown>)) {
    if (!id.trim() || count >= 100 || !layout || typeof layout !== 'object' || Array.isArray(layout)) continue;
    result[id] = normalizeItemLayout(layout);
    count += 1;
  }
  return result;
}

function normalizeReferencePins(value: unknown): Record<string, string[]> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
  const pins: Record<string, string[]> = {};
  for (const [categoryId, paths] of Object.entries(value as Record<string, unknown>)) {
    if (!Array.isArray(paths)) continue;
    pins[categoryId] = [...new Set(paths.filter((path): path is string => typeof path === 'string'))];
  }
  return pins;
}

function normalizeStringList(value: unknown, limit = 120): string[] {
  if (!Array.isArray(value)) return [];
  return [...new Set(value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0))].slice(0, limit);
}

function normalizeRules(value: unknown, categories: AppPreferences['categories']): OrganizationRule[] {
  if (!Array.isArray(value)) return [];
  const categoryIds = new Set(categories.map((category) => category.id));
  return value.slice(0, 100).flatMap((entry, index) => {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) return [];
    const candidate = entry as Partial<OrganizationRule>;
    const match: OrganizationRule['match'] = candidate.match === 'name' || candidate.match === 'path' ? candidate.match : 'extension';
    const pattern = typeof candidate.pattern === 'string' ? candidate.pattern.trim() : '';
    const categoryId = typeof candidate.categoryId === 'string' ? candidate.categoryId : '';
    if (!pattern || !categoryIds.has(categoryId)) return [];
    return [{
      id: typeof candidate.id === 'string' && candidate.id.trim() ? candidate.id : `rule-${index + 1}`,
      name: typeof candidate.name === 'string' && candidate.name.trim() ? candidate.name.trim() : `${match === 'extension' ? '扩展名' : match === 'name' ? '文件名' : '路径'}规则 ${index + 1}`,
      enabled: typeof candidate.enabled === 'boolean' ? candidate.enabled : true,
      match,
      pattern,
      categoryId,
      priority: Math.round(boundedNumber(candidate.priority, index + 1, 1, 999)),
    }];
  }).sort((left, right) => left.priority - right.priority);
}

function normalizeWorkspace(value: unknown, index: number): WorkspacePreset | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const candidate = value as Partial<WorkspacePreset>;
  const name = typeof candidate.name === 'string' ? candidate.name.trim() : '';
  if (!name) return null;
  const positions = candidate.positions && typeof candidate.positions === 'object' && !Array.isArray(candidate.positions)
    ? candidate.positions as WorkspacePreset['positions'] : {};
  const sizes = candidate.sizes && typeof candidate.sizes === 'object' && !Array.isArray(candidate.sizes)
    ? candidate.sizes as WorkspacePreset['sizes'] : {};
  const viewModes = candidate.viewModes && typeof candidate.viewModes === 'object' && !Array.isArray(candidate.viewModes)
    ? candidate.viewModes as WorkspacePreset['viewModes'] : {};
  const sortModes = candidate.sortModes && typeof candidate.sortModes === 'object' && !Array.isArray(candidate.sortModes)
    ? candidate.sortModes as WorkspacePreset['sortModes'] : {};
  const itemLayouts = normalizeItemLayouts(candidate.itemLayouts);
  const adaptivePreset = candidate.adaptivePreset === 'balanced' || candidate.adaptivePreset === 'grid' || candidate.adaptivePreset === 'columns' || candidate.adaptivePreset === 'corners' || candidate.adaptivePreset === 'right-dock'
    ? candidate.adaptivePreset : 'manual';
  const folderAlignment = candidate.folderAlignment === 'left' || candidate.folderAlignment === 'center' || candidate.folderAlignment === 'right'
    ? candidate.folderAlignment : 'manual';
  return {
    id: typeof candidate.id === 'string' && candidate.id.trim() ? candidate.id : `workspace-${index + 1}`,
    name,
    positions,
    sizes,
    collapsed: normalizeStringList(candidate.collapsed),
    pinned: normalizeStringList(candidate.pinned),
    viewModes,
    sortModes,
    itemLayouts,
    adaptivePreset,
    folderAlignment,
  };
}

function normalizeWorkspaces(value: unknown): WorkspacePreset[] {
  if (!Array.isArray(value)) return [];
  const result: WorkspacePreset[] = [];
  const ids = new Set<string>();
  value.slice(0, 12).forEach((entry, index) => {
    const workspace = normalizeWorkspace(entry, index);
    if (!workspace || ids.has(workspace.id)) return;
    ids.add(workspace.id);
    result.push(workspace);
  });
  return result;
}

export function normalizePreferences(value: unknown): AppPreferences {
  const parsed = value && typeof value === 'object' ? value as Partial<AppPreferences> : {};
  const seenIds = new Set<string>();
  const seenNames = new Set<string>();
  const categories = (Array.isArray(parsed.categories) ? parsed.categories : [])
    .slice(0, 100)
    .map((category, index) => normalizeCategory(category, `recovered-${index}`))
    .filter((category): category is NonNullable<typeof category> => Boolean(category))
    .filter((category) => {
      const id = category.id.toLocaleLowerCase('zh-CN');
      const name = category.name.toLocaleLowerCase('zh-CN');
      if (seenIds.has(id) || seenNames.has(name)) return false;
      seenIds.add(id);
      seenNames.add(name);
      return true;
    });
  return {
    categories: categories.length ? categories : defaultCategories.map((category) => ({ ...category, extensions: [...category.extensions] })),
    automaticScan: typeof parsed.automaticScan === 'boolean' ? parsed.automaticScan : defaultPreferences.automaticScan,
    automaticOrganize: typeof parsed.automaticOrganize === 'boolean' ? parsed.automaticOrganize : defaultPreferences.automaticOrganize,
    organizeDelaySeconds: boundedNumber(parsed.organizeDelaySeconds, defaultPreferences.organizeDelaySeconds, 5, 60),
    organizeMode: parsed.organizeMode === 'reference' ? 'reference' : 'move',
    magicColor: typeof parsed.magicColor === 'boolean' ? parsed.magicColor : defaultPreferences.magicColor,
    referencePins: normalizeReferencePins(parsed.referencePins),
    showHiddenFiles: typeof parsed.showHiddenFiles === 'boolean' ? parsed.showHiddenFiles : defaultPreferences.showHiddenFiles,
    compactView: typeof parsed.compactView === 'boolean' ? parsed.compactView : defaultPreferences.compactView,
    capsuleMode: typeof parsed.capsuleMode === 'boolean' ? parsed.capsuleMode : defaultPreferences.capsuleMode,
    noteCapsuleMode: typeof parsed.noteCapsuleMode === 'boolean' ? parsed.noteCapsuleMode : defaultPreferences.noteCapsuleMode,
    desktopContextMenu: typeof parsed.desktopContextMenu === 'boolean' ? parsed.desktopContextMenu : defaultPreferences.desktopContextMenu,
    glassOpacity: boundedNumber(parsed.glassOpacity, defaultPreferences.glassOpacity, 0, 100),
    theme: parsed.theme === 'dark' || parsed.theme === 'system' ? parsed.theme : 'light',
    colorScheme: parsed.colorScheme === 'warm' || parsed.colorScheme === 'ink' || parsed.colorScheme === 'forest' || parsed.colorScheme === 'rose' || parsed.colorScheme === 'custom' ? parsed.colorScheme : 'white',
    customColor: typeof parsed.customColor === 'string' && /^#[0-9a-f]{6}$/i.test(parsed.customColor) ? parsed.customColor : defaultPreferences.customColor,
    customSurfaceColor: typeof parsed.customSurfaceColor === 'string' && /^#[0-9a-f]{6}$/i.test(parsed.customSurfaceColor) ? parsed.customSurfaceColor : defaultPreferences.customSurfaceColor,
    uiScale: boundedNumber(parsed.uiScale, defaultPreferences.uiScale, 80, 130),
    showExtensions: typeof parsed.showExtensions === 'boolean' ? parsed.showExtensions : defaultPreferences.showExtensions,
    labelPosition: parsed.labelPosition === 'right' ? 'right' : 'bottom',
    autoHide: typeof parsed.autoHide === 'boolean' ? parsed.autoHide : defaultPreferences.autoHide,
    autoHideDelaySeconds: boundedNumber(parsed.autoHideDelaySeconds, defaultPreferences.autoHideDelaySeconds, 1, 10),
    rules: normalizeRules(parsed.rules, categories.length ? categories : defaultCategories),
    favorites: normalizeStringList(parsed.favorites),
    recentItems: normalizeStringList(parsed.recentItems, 30),
    pinnedItems: normalizeStringList(parsed.pinnedItems),
    workspaces: normalizeWorkspaces(parsed.workspaces),
    activeWorkspaceId: typeof parsed.activeWorkspaceId === 'string' ? parsed.activeWorkspaceId : '',
  };
}

export function loadPreferences(): AppPreferences {
  try {
    const value = localStorage.getItem(preferencesKey);
    return value ? normalizePreferences(JSON.parse(value)) : normalizePreferences(defaultPreferences);
  } catch {
    return normalizePreferences(defaultPreferences);
  }
}

export function loadItemLayouts(): PanelItemLayouts {
  try {
    return normalizeItemLayouts(JSON.parse(localStorage.getItem(itemLayoutKey) ?? '{}'));
  } catch {
    return {};
  }
}

export function loadStringArray(key: string): string[] {
  try {
    const parsed = JSON.parse(localStorage.getItem(key) ?? '[]') as unknown;
    return Array.isArray(parsed) ? [...new Set(parsed.filter((value): value is string => typeof value === 'string'))] : [];
  } catch {
    return [];
  }
}

export function loadRecord<T>(key: string): Record<string, T> {
  try {
    const parsed = JSON.parse(localStorage.getItem(key) ?? '{}') as unknown;
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed as Record<string, T> : {};
  } catch {
    return {};
  }
}
