import { useEffect, useMemo, useRef } from 'react';
import type { AppPreferences, DesktopItem, PanelItemLayout } from '../types';
import { defaultItemLayout, type PanelItemLayouts } from '../state/preferences';
import type { PanelSortMode, PanelViewMode, ZonePositions, ZoneSizes } from '../state/layout';
import { resolveAccent, resolveEffectiveTheme, resolveSurfaceRgb, toHexColor } from '../state/theme';
import { dominantKindColor } from '../state/magicColor';
import { buildPanelSyncKey } from '../utils/panelSync';
import { nativeWindow } from '../services/nativeDesktop';

interface UsePanelSyncOptions {
  native: boolean;
  preferences: AppPreferences;
  positions: ZonePositions;
  sizes: ZoneSizes;
  pinned: string[];
  collapsed: string[];
  itemLayouts: PanelItemLayouts;
  viewModes: Record<string, PanelViewMode>;
  sortModes: Record<string, PanelSortMode>;
  items: DesktopItem[];
}

export function usePanelSync(options: UsePanelSyncOptions) {
  const {
    native, preferences, positions, sizes, pinned, collapsed, itemLayouts, viewModes, sortModes, items,
  } = options;
  const panelSyncKey = useRef('');
  const panelRevisions = useRef<Record<string, { key: string; revision: number }>>({});
  // 已经发给原生窗口的图标（id -> iconUrl）。后续同步里未变化的项目不再重复传输 base64 图标。
  const sentIconsRef = useRef<Map<string, string>>(new Map());
  const syncTimer = useRef<number | null>(null);
  const itemsByCategory = useMemo(() => {
    const result: Record<string, DesktopItem[]> = {};
    items.forEach((item) => {
      (result[item.categoryId] ??= []).push(item);
    });
    return result;
  }, [items]);

  useEffect(() => {
    if (!native) return undefined;
    const effectiveTheme = resolveEffectiveTheme(preferences.theme);
    const panelThemeAccent = resolveAccent(preferences.colorScheme, preferences.customColor);
    const panelHeaderSurface = toHexColor(resolveSurfaceRgb(preferences.colorScheme, preferences.customColor, effectiveTheme, preferences.customSurfaceColor));
    const sentIcons = sentIconsRef.current;
    const liveItemIds = new Set<string>();
    const panels = preferences.categories.map((category) => {
      const position = positions[category.id] ?? { x: 28, y: 82 };
      const size = sizes[category.id] ?? { width: 292, height: 238 };
      const panelItems = (itemsByCategory[category.id] ?? [])
        .map(({ id, name, path, iconUrl, extension, modifiedAt, size: itemSize, kind, managed }) => {
          liveItemIds.add(id);
          return { id, name, path, iconUrl, extension, modifiedAt, size: itemSize, kind, readOnly: Boolean(managed || preferences.organizeMode === 'reference') };
        });
      // Magic 分区色：按内容占比最高的类型覆盖该分区颜色，胶囊/色块/标题条跟随内容
      const magic = preferences.magicColor ? dominantKindColor(panelItems) : null;
      const itemLayout: PanelItemLayout = { ...defaultItemLayout, ...itemLayouts[category.id] };
      const panel = {
        id: category.id,
        name: category.name,
        color: magic ?? category.color,
        themeAccent: magic !== null ? resolveAccent('custom', magic) : panelThemeAccent,
        headerSurface: (preferences.colorScheme === 'white' || preferences.colorScheme === 'custom')
          ? panelHeaderSurface
          : (magic !== null ? toHexColor(resolveSurfaceRgb('custom', magic, effectiveTheme, preferences.customSurfaceColor)) : panelHeaderSurface),
        glassOpacity: preferences.glassOpacity,
        theme: effectiveTheme,
        compact: preferences.compactView,
        capsuleMode: preferences.capsuleMode,
        categoryIcon: category.icon,
        readOnly: Boolean(category.portalPath),
        viewMode: viewModes[category.id] ?? 'grid',
        sortMode: sortModes[category.id] ?? 'name',
        itemSize: itemLayout.itemSize,
        iconSize: itemLayout.iconSize,
        itemGap: itemLayout.gap,
        itemAlignment: itemLayout.alignment,
        itemColumns: itemLayout.columns,
        showLabels: itemLayout.showLabels,
        labelSize: itemLayout.labelSize,
        ...position,
        ...size,
        pinned: pinned.includes(category.id),
        collapsed: collapsed.includes(category.id),
        items: panelItems,
      };
      const panelKey = buildPanelSyncKey([panel] as Array<Record<string, unknown>>);
      const previous = panelRevisions.current[category.id];
      if (!previous || previous.key !== panelKey) {
        panelRevisions.current[category.id] = { key: panelKey, revision: (previous?.revision ?? 0) + 1 };
      }
      return { ...panel, revision: panelRevisions.current[category.id].revision };
    });
    const liveIds = new Set(preferences.categories.map((category) => category.id));
    Object.keys(panelRevisions.current).forEach((id) => {
      if (!liveIds.has(id)) delete panelRevisions.current[id];
    });
    for (const id of Array.from(sentIcons.keys())) {
      if (!liveItemIds.has(id)) sentIcons.delete(id);
    }
    // 同步键始终基于完整项目数据计算（含图标），保证未变化的项目不会重复同步；
    // 真正发送给原生窗口的负载则剔除未变化项目的 base64 图标，大幅减少传输体积。
    const payloadPanels = panels.map((panel) => ({
      ...panel,
      items: panel.items.map((item) => (
        item.iconUrl !== undefined && sentIcons.get(item.id) === item.iconUrl
          ? { id: item.id, name: item.name, path: item.path, extension: item.extension, modifiedAt: item.modifiedAt, size: item.size, kind: item.kind, readOnly: item.readOnly }
          : item
      )),
    }));
    const syncKey = buildPanelSyncKey(panels as Array<Record<string, unknown>>);
    if (syncKey === panelSyncKey.current) {
      if (syncTimer.current !== null) {
        window.clearTimeout(syncTimer.current);
        syncTimer.current = null;
      }
      return undefined;
    }
    if (syncTimer.current !== null) window.clearTimeout(syncTimer.current);
    syncTimer.current = window.setTimeout(() => {
      syncTimer.current = null;
      if (syncKey === panelSyncKey.current) return;
      panelSyncKey.current = syncKey;
      panels.forEach((panel) => {
        panel.items.forEach((item) => {
          if (item.iconUrl !== undefined) sentIcons.set(item.id, item.iconUrl);
        });
      });
      void nativeWindow.syncPanels(payloadPanels);
    }, 24);
    return () => {
      if (syncTimer.current !== null) {
        window.clearTimeout(syncTimer.current);
        syncTimer.current = null;
      }
    };
  }, [
    collapsed, itemLayouts, itemsByCategory, native, pinned, positions,
    preferences.categories, preferences.colorScheme, preferences.compactView, preferences.capsuleMode,
    preferences.customColor, preferences.glassOpacity, preferences.magicColor, preferences.theme,
    preferences.organizeMode, sizes, sortModes, viewModes,
  ]);
}
