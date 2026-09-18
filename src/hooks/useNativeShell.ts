import { useEffect, useRef, type Dispatch, type MutableRefObject, type SetStateAction } from 'react';
import {
  collapsedKey, pinnedKey, sortModeKey, viewModeKey,
} from '../state/keys';
import { defaultItemLayout, loadItemLayouts, loadPreferences, loadRecord, type PanelItemLayouts } from '../state/preferences';
import { loadAdaptivePreset, loadFolderAlignment, loadPositions, loadSizes, type FolderAlignment, type LayoutPreset, type PanelSortMode, type PanelViewMode, type ZonePositions, type ZoneSizes } from '../state/layout';
import type { AppPreferences, Category } from '../types';
import { initializeNative, isSettingsRuntime, nativeWindow, parseNativeConfig } from '../services/nativeDesktop';
import type { AppToast } from './useDesktopScan';

interface UseNativeShellOptions {
  native: boolean;
  categories: Category[];
  preferences: AppPreferences;
  runScan: (quiet?: boolean) => Promise<void> | void;
  scanInFlight: MutableRefObject<boolean>;
  setPreferences: Dispatch<SetStateAction<AppPreferences>>;
  setPositions: Dispatch<SetStateAction<ZonePositions>>;
  setSizes: Dispatch<SetStateAction<ZoneSizes>>;
  setItemLayouts: Dispatch<SetStateAction<PanelItemLayouts>>;
  setPinned: Dispatch<SetStateAction<string[]>>;
  setCollapsed: Dispatch<SetStateAction<string[]>>;
  setViewModes: Dispatch<SetStateAction<Record<string, PanelViewMode>>>;
  setSortModes: Dispatch<SetStateAction<Record<string, PanelSortMode>>>;
  setAdaptivePreset: Dispatch<SetStateAction<LayoutPreset | 'manual'>>;
  setFolderAlignment: Dispatch<SetStateAction<FolderAlignment>>;
  setShowSettings: Dispatch<SetStateAction<boolean>>;
  setShowCategoryModal: Dispatch<SetStateAction<boolean>>;
  setClickThrough: Dispatch<SetStateAction<boolean>>;
  setDraggingModal: Dispatch<SetStateAction<boolean>>;
  setToast: (toast: AppToast) => void;
}

export function useNativeShell(options: UseNativeShellOptions) {
  const {
    native, categories, preferences, runScan, scanInFlight, setPreferences, setPositions, setSizes, setItemLayouts,
    setPinned, setCollapsed, setViewModes, setSortModes, setAdaptivePreset, setFolderAlignment,
    setShowSettings, setShowCategoryModal, setClickThrough,
    setDraggingModal, setToast,
  } = options;
  const desktopWatchTimer = useRef(0);

  useEffect(() => {
    // 设置窗口（settingsRuntime）也注册监听：宿主可能发出 settingsChanged 通知刷新
    if (!native && !isSettingsRuntime()) return undefined;
    initializeNative();
    const interaction = (event: Event) => setClickThrough(Boolean((event as CustomEvent<boolean>).detail));
    const tray = (event: Event) => {
      const command = String((event as CustomEvent<string>).detail);
      if (command === 'settings') setShowSettings(true);
      if (command === 'new-category') setShowCategoryModal(true);
      if (command === 'scan') void runScan();
      if (command === 'organize') window.dispatchEvent(new Event('pivkey-organize-now'));
      if (command === 'undo-organize') window.dispatchEvent(new Event('pivkey-undo-organize'));
      if (command === 'click-through') setClickThrough((value) => !value);
      if (command === 'collapse-all') setCollapsed(categories.map((category) => category.id));
      if (command === 'expand-all') setCollapsed([]);
    };
    const modalGesture = (event: Event) => setDraggingModal(Boolean((event as CustomEvent<boolean>).detail));
    const desktopChanged = () => {
      window.clearTimeout(desktopWatchTimer.current);
      desktopWatchTimer.current = window.setTimeout(() => {
        if (!scanInFlight.current) void runScan(true);
      }, 280);
    };
    // 设置窗口：从宿主 config.json 重载全部状态；管理器：从 localStorage 重载
    const settingsChanged = () => {
      if (isSettingsRuntime()) {
        void (async () => {
          try {
            const config = parseNativeConfig(await nativeWindow.loadConfig());
            if (!config) return;
            if (config.preferences) setPreferences(config.preferences);
            if (config.positions) setPositions(config.positions);
            if (config.sizes) setSizes(config.sizes);
            if (config.itemLayouts) setItemLayouts(config.itemLayouts);
            if (config.viewModes) setViewModes(config.viewModes);
            if (config.sortModes) setSortModes(config.sortModes);
            if (config.adaptivePreset) setAdaptivePreset(config.adaptivePreset);
            if (config.folderAlignment) setFolderAlignment(config.folderAlignment);
            if (config.pinned) setPinned(config.pinned);
            if (config.collapsed) setCollapsed(config.collapsed);
          } catch {
            // 加载失败：保持当前状态
          }
        })();
        return;
      }
      const nextPreferences = loadPreferences();
      setPreferences(nextPreferences);
      setPositions(loadPositions(nextPreferences.categories));
      setSizes(loadSizes(nextPreferences.categories));
      setItemLayouts(loadItemLayouts());
      setViewModes(loadRecord<PanelViewMode>(viewModeKey));
      setSortModes(loadRecord<PanelSortMode>(sortModeKey));
      setAdaptivePreset(loadAdaptivePreset());
      setFolderAlignment(loadFolderAlignment());
      try { setPinned(JSON.parse(localStorage.getItem(pinnedKey) ?? '[]') as string[]); } catch { /* keep */ }
      try { setCollapsed(JSON.parse(localStorage.getItem(collapsedKey) ?? '[]') as string[]); } catch { /* keep */ }
    };
    const operationError = (event: Event) => {
      setToast({ message: String((event as CustomEvent<string>).detail || '文件操作失败'), type: 'info' });
    };
    // 拖文件进分区：引用模式钉选（不移动），移动模式收纳到分类文件夹
    const panelDrop = async (event: Event) => {
      const detail = (event as CustomEvent<{ categoryId: string; paths: string[] }>).detail;
      if (!detail?.categoryId || !Array.isArray(detail.paths) || !detail.paths.length) return;
      if (preferences.organizeMode === 'reference') {
        setPreferences((current) => ({
          ...current,
          referencePins: {
            ...current.referencePins,
            [current.categories.find((c) => c.id === detail.categoryId)?.id ?? detail.categoryId]: [...new Set([...(current.referencePins[detail.categoryId] ?? []), ...detail.paths])],
          },
        }));
        setToast({ message: `已将 ${detail.paths.length} 个项目加入分区（不移动文件）`, type: 'success' });
      } else {
        try {
          await nativeWindow.moveIntoCategory(detail.categoryId, detail.paths);
          setToast({ message: `已将 ${detail.paths.length} 个项目收纳到分区`, type: 'success' });
        } catch (error) { setToast({ message: error instanceof Error ? error.message : '收纳失败', type: 'info' }); }
      }
    };

    window.addEventListener('pivkey-interaction', interaction);
    window.addEventListener('pivkey-tray-command', tray);
    window.addEventListener('pivkey-modal-gesture', modalGesture);
    window.addEventListener('pivkey-desktop-changed', desktopChanged);
    window.addEventListener('pivkey-settings-changed', settingsChanged);
    window.addEventListener('pivkey-operation-error', operationError);
    window.addEventListener('pivkey-panel-drop', panelDrop);
    if (native) void runScan(true);

    return () => {
      window.clearTimeout(desktopWatchTimer.current);
      window.removeEventListener('pivkey-interaction', interaction);
      window.removeEventListener('pivkey-tray-command', tray);
      window.removeEventListener('pivkey-modal-gesture', modalGesture);
      window.removeEventListener('pivkey-desktop-changed', desktopChanged);
      window.removeEventListener('pivkey-settings-changed', settingsChanged);
      window.removeEventListener('pivkey-operation-error', operationError);
      window.removeEventListener('pivkey-panel-drop', panelDrop);
    };
  }, [categories, native, preferences, runScan, scanInFlight, setAdaptivePreset, setClickThrough, setCollapsed, setDraggingModal, setFolderAlignment, setItemLayouts, setPinned, setPositions, setPreferences, setShowCategoryModal, setShowSettings, setSizes, setSortModes, setToast, setViewModes]);

  useEffect(() => {
    if (!native) return undefined;
    const geometry = (event: Event) => {
      const detail = (event as CustomEvent<{ id: string; x: number; y: number; width: number; height: number }>).detail;
      if (!detail) return;
      setPositions((current) => ({ ...current, [detail.id]: { x: detail.x, y: detail.y } }));
      setSizes((current) => ({ ...current, [detail.id]: { width: detail.width, height: detail.height } }));
      setAdaptivePreset('manual');
      setFolderAlignment('manual');
    };
    const itemSize = (event: Event) => {
      const detail = (event as CustomEvent<{ id: string; itemSize: number; iconSize: number; labelSize: number }>).detail;
      if (!detail?.id) return;
      setItemLayouts((current) => ({
        ...current,
        [detail.id]: {
          ...defaultItemLayout,
          ...current[detail.id],
          itemSize: detail.itemSize,
          iconSize: detail.iconSize,
          labelSize: detail.labelSize,
        },
      }));
    };
    window.addEventListener('pivkey-panel-geometry', geometry);
    window.addEventListener('pivkey-panel-item-size', itemSize);
    return () => {
      window.removeEventListener('pivkey-panel-geometry', geometry);
      window.removeEventListener('pivkey-panel-item-size', itemSize);
    };
  }, [categories, native, setAdaptivePreset, setCollapsed, setFolderAlignment, setItemLayouts, setPinned, setPositions, setPreferences, setSizes]);
}
