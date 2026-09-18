import { useCallback, useEffect, useRef } from 'react';
import {
  adaptivePresetKey, collapsedKey, folderAlignmentKey, itemLayoutKey, layoutKey,
  pinnedKey, preferencesKey, sizeKey, sortModeKey, viewModeKey,
} from '../state/keys';
import type { AppPreferences } from '../types';
import type { PanelItemLayouts } from '../state/preferences';
import type { FolderAlignment, LayoutPreset, PanelSortMode, PanelViewMode, ZonePositions, ZoneSizes } from '../state/layout';
import { isSettingsRuntime, nativeWindow, type NativeConfig } from '../services/nativeDesktop';

interface UsePersistedStateOptions {
  preferences: AppPreferences;
  positions: ZonePositions;
  sizes: ZoneSizes;
  itemLayouts: PanelItemLayouts;
  adaptivePreset: LayoutPreset | 'manual';
  folderAlignment: FolderAlignment;
  pinned: string[];
  collapsed: string[];
  viewModes: Record<string, PanelViewMode>;
  sortModes: Record<string, PanelSortMode>;
  // 原生设置窗口从宿主加载完成前禁用保存，避免用默认值覆盖宿主 config.json
  enabled?: boolean;
  onError?: (message: string) => void;
}

export function usePersistedState(options: UsePersistedStateOptions) {
  const latest = useRef(options);
  const reportedError = useRef(false);
  latest.current = options;
  const { preferences, positions, sizes, itemLayouts, adaptivePreset, folderAlignment, pinned, collapsed, viewModes, sortModes } = options;
  const persist = useCallback(() => {
    if (latest.current.enabled === false) return;
    const {
      preferences, positions, sizes, itemLayouts, adaptivePreset, folderAlignment,
      pinned, collapsed, viewModes, sortModes,
    } = latest.current;
    if (isSettingsRuntime()) {
      // 原生设置窗口：写入宿主 config.json（organizationHistory 仍由 useDesktopScan 单独管理）
      const config: NativeConfig = { preferences, positions, sizes, itemLayouts, adaptivePreset, folderAlignment, pinned, collapsed, viewModes, sortModes };
      void nativeWindow.saveConfig(config).then(() => {
        reportedError.current = false;
      }).catch(() => {
        if (!reportedError.current) {
          reportedError.current = true;
          latest.current.onError?.('设置保存失败，请检查 WebView2 本地数据权限');
        }
      });
      return;
    }
    try {
      localStorage.setItem(preferencesKey, JSON.stringify(preferences));
      localStorage.setItem(layoutKey, JSON.stringify(positions));
      localStorage.setItem(sizeKey, JSON.stringify(sizes));
      localStorage.setItem(itemLayoutKey, JSON.stringify(itemLayouts));
      localStorage.setItem(adaptivePresetKey, adaptivePreset);
      localStorage.setItem(folderAlignmentKey, folderAlignment);
      localStorage.setItem(pinnedKey, JSON.stringify(pinned));
      localStorage.setItem(collapsedKey, JSON.stringify(collapsed));
      localStorage.setItem(viewModeKey, JSON.stringify(viewModes));
      localStorage.setItem(sortModeKey, JSON.stringify(sortModes));
      reportedError.current = false;
    } catch {
      if (!reportedError.current) {
        reportedError.current = true;
        latest.current.onError?.('设置保存失败，请检查 WebView2 本地数据权限');
      }
    }
  }, []);

  useEffect(() => {
    const timer = window.setTimeout(persist, 180);
    return () => window.clearTimeout(timer);
  }, [adaptivePreset, collapsed, folderAlignment, itemLayouts, persist, pinned, positions, preferences, sizes, sortModes, viewModes]);

  useEffect(() => {
    window.addEventListener('pagehide', persist);
    return () => window.removeEventListener('pagehide', persist);
  }, [persist]);

  return persist;
}
