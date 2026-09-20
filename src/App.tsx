import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { CategoryModal } from './components/CategoryModal';
import { SettingsModal } from './components/SettingsModal';
import { Toast } from './components/Toast';
import { defaultPreferences } from './data/defaults';
import { normalizeCategoryName } from './services/categoryValidation';
import { isNativeRuntime, isSettingsRuntime, nativeWindow, parseNativeConfig, renameManagedCategory, type NativeConfig, type NativeSettingsConfig, type OrganizationHistoryEntry } from './services/nativeDesktop';
import type { AppPreferences, Category, NoteData, PanelItemLayout } from './types';
import { adaptivePresetKey, collapsedKey, folderAlignmentKey, itemLayoutKey, layoutKey, notesKey, organizationHistoryKey, pinnedKey, preferencesKey, sizeKey, sortModeKey, viewModeKey } from './state/keys';
import { colorToRgb, resolveAccent, resolveEffectiveTheme, resolveSurfaceRgb, schemeColors } from './state/theme';
import { defaultItemLayout, loadItemLayouts, loadPreferences, loadRecord, loadStringArray, type PanelItemLayouts } from './state/preferences';
import {
  clampPosition,
  defaultSizes,
  loadAdaptivePreset,
  loadFolderAlignment,
  loadPositions,
  loadSizes,
  type FolderAlignment,
  type LayoutPreset,
  type PanelSortMode,
  type PanelViewMode,
  type ZonePositions,
  type ZoneSizes,
} from './state/layout';
import { alignPanelLayout, createPresetLayout, panelLayoutGap, panelMinHeight, panelMinWidth } from './utils/panelLayout';
import { useDesktopScan } from './hooks/useDesktopScan';
import { usePanelSync } from './hooks/usePanelSync';
import { useNativeShell } from './hooks/useNativeShell';
import { useInteractiveRegions } from './hooks/useInteractiveRegions';
import { usePersistedState } from './hooks/usePersistedState';

function loadNotes(): NoteData[] {
  try {
    const raw = localStorage.getItem(notesKey);
    if (raw) {
      const parsed = JSON.parse(raw);
      if (Array.isArray(parsed)) return parsed as NoteData[];
    }
  } catch {
    return [];
  }
  return [];
}

// ===== 一次性迁移（?view=migrate）：把 localStorage 旧配置导入宿主 config.json =====
function loadOrganizationHistory(): OrganizationHistoryEntry[] {
  try {
    const parsed = JSON.parse(localStorage.getItem(organizationHistoryKey) ?? '[]') as unknown;
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((entry): entry is OrganizationHistoryEntry => Boolean(
      entry && typeof entry === 'object'
      && typeof (entry as OrganizationHistoryEntry).source === 'string'
      && typeof (entry as OrganizationHistoryEntry).destination === 'string'
      && typeof (entry as OrganizationHistoryEntry).name === 'string',
    ));
  } catch {
    return [];
  }
}

function collectLocalConfig(): NativeConfig {
  const preferences = loadPreferences();
  return {
    preferences,
    positions: loadPositions(preferences.categories),
    sizes: loadSizes(preferences.categories),
    collapsed: loadStringArray(collapsedKey),
    pinned: loadStringArray(pinnedKey),
    viewModes: loadRecord<PanelViewMode>(viewModeKey),
    sortModes: loadRecord<PanelSortMode>(sortModeKey),
    itemLayouts: loadItemLayouts(),
    adaptivePreset: loadAdaptivePreset(),
    folderAlignment: loadFolderAlignment(),
    organizationHistory: loadOrganizationHistory(),
    notes: loadNotes(),
  };
}

function MigrationBridge() {
  const [status, setStatus] = useState<'pending' | 'done' | 'failed'>('pending');
  useEffect(() => {
    if (!isNativeRuntime()) return;
    let cancelled = false;
    void (async () => {
      try {
        await nativeWindow.importConfig(collectLocalConfig());
        await nativeWindow.migrationDone();
        if (!cancelled) setStatus('done');
      } catch {
        if (!cancelled) {
          void nativeWindow.migrationDone();
          setStatus('failed');
        }
      }
    })();
    return () => { cancelled = true; };
  }, []);
  // 浏览器预览：渲染空壳
  if (!isNativeRuntime()) return <div className="app app--overlay" />;
  const message = status === 'pending' ? '正在迁移配置…' : status === 'done' ? '配置迁移完成' : '配置迁移失败';
  return (
    <div className="app app--overlay" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100vh', fontFamily: 'system-ui, "Microsoft YaHei", sans-serif', fontSize: 14, color: 'rgba(0, 0, 0, .55)' }}>
      {message}
    </div>
  );
}

// 设置窗口加载到宿主配置后写回 localStorage，保持浏览器预览数据一致
function persistConfigToLocalStorage(config: NativeSettingsConfig) {
  try {
    if (config.preferences) localStorage.setItem(preferencesKey, JSON.stringify(config.preferences));
    if (config.positions) localStorage.setItem(layoutKey, JSON.stringify(config.positions));
    if (config.sizes) localStorage.setItem(sizeKey, JSON.stringify(config.sizes));
    if (config.itemLayouts) localStorage.setItem(itemLayoutKey, JSON.stringify(config.itemLayouts));
    if (config.adaptivePreset) localStorage.setItem(adaptivePresetKey, config.adaptivePreset);
    if (config.folderAlignment) localStorage.setItem(folderAlignmentKey, config.folderAlignment);
    if (config.pinned) localStorage.setItem(pinnedKey, JSON.stringify(config.pinned));
    if (config.collapsed) localStorage.setItem(collapsedKey, JSON.stringify(config.collapsed));
    if (config.viewModes) localStorage.setItem(viewModeKey, JSON.stringify(config.viewModes));
    if (config.sortModes) localStorage.setItem(sortModeKey, JSON.stringify(config.sortModes));
    if (config.notes) localStorage.setItem(notesKey, JSON.stringify(config.notes));
  } catch {
    // 写回失败仅影响浏览器预览，忽略
  }
}

export default function App() {
  // 迁移页：不渲染设置页、不跑应用 hooks，仅做一次 localStorage → 宿主导入
  const migrateMode = new URLSearchParams(window.location.search).get('view') === 'migrate';
  if (migrateMode) return <MigrationBridge />;

  const native = isNativeRuntime();
  const settingsRuntime = isSettingsRuntime();
  const desktopNative = native && !settingsRuntime;
  const [configLoaded, setConfigLoaded] = useState(!(settingsRuntime && native));
  const [preferences, setPreferences] = useState<AppPreferences>(loadPreferences);
  const [systemDark, setSystemDark] = useState(() => window.matchMedia('(prefers-color-scheme: dark)').matches);
  const effectiveTheme = preferences.theme === 'system' ? (systemDark ? 'dark' : 'light') : preferences.theme;
  const panelPreferences = useMemo<AppPreferences>(
    () => preferences.theme === 'system' ? { ...preferences, theme: effectiveTheme } : preferences,
    [effectiveTheme, preferences],
  );
  const uiZoom = Math.max(.8, Math.min(1.3, (preferences.uiScale || 100) / 100));
  const layoutZoom = desktopNative ? 1 : uiZoom;
  const [positions, setPositions] = useState<ZonePositions>(() => loadPositions(preferences.categories, layoutZoom));
  const [sizes, setSizes] = useState<ZoneSizes>(() => loadSizes(preferences.categories));
  const sizesRef = useRef(sizes);
  sizesRef.current = sizes;
  const [pinned, setPinned] = useState<string[]>(() => loadStringArray(pinnedKey));
  const [collapsed, setCollapsed] = useState<string[]>(() => loadStringArray(collapsedKey));
  const [viewModes, setViewModes] = useState<Record<string, PanelViewMode>>(() => loadRecord<PanelViewMode>(viewModeKey));
  const [sortModes, setSortModes] = useState<Record<string, PanelSortMode>>(() => loadRecord<PanelSortMode>(sortModeKey));
  const [itemLayouts, setItemLayouts] = useState<PanelItemLayouts>(loadItemLayouts);
  const [adaptivePreset, setAdaptivePreset] = useState<LayoutPreset | 'manual'>(loadAdaptivePreset);
  const [folderAlignment, setFolderAlignment] = useState<FolderAlignment>(loadFolderAlignment);
  const [notes, setNotes] = useState<NoteData[]>(loadNotes);
  const [showCategoryModal, setShowCategoryModal] = useState(false);
  const [showSettings, setShowSettings] = useState(settingsRuntime || !native);
  const [resumeSettingsAfterCategory, setResumeSettingsAfterCategory] = useState(false);
  const [clickThrough, setClickThrough] = useState(false);
  const [draggingModal, setDraggingModal] = useState(false);
  const layoutModeVersion = useRef(0);
  const [toast, setToast] = useState<{ message: string; type?: 'success' | 'info' } | null>(null);
  // 原生模式下只有模态框拖拽是 JS 侧手势；面板拖拽在 WPF 侧完成
  const interactionActive = draggingModal;

  useEffect(() => {
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const update = () => setSystemDark(media.matches);
    update();
    media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  }, []);

  // 设置窗口：从宿主 config.json 异步加载权威配置（加载完成前渲染空壳，避免闪默认值）
  useEffect(() => {
    if (!settingsRuntime || !native) return;
    let cancelled = false;
    void (async () => {
      try {
        const raw = await nativeWindow.loadConfig();
        const rawRecord: Record<string, unknown> = raw && typeof raw === 'object' ? raw as unknown as Record<string, unknown> : {};
        const recovery = typeof rawRecord.configRecovery === 'string' ? rawRecord.configRecovery : '';
        const config = parseNativeConfig(raw);
        if (cancelled) return;
        if (!config) {
          // 加载失败显式报告而不是静默回落默认值（DeskBox 设置约定）
          setToast({ message: '设置加载失败，正在使用本地缓存配置', type: 'info' });
          return;
        }
        if (config.preferences) setPreferences(config.preferences);
        if (config.positions) setPositions(config.positions);
        if (config.sizes) setSizes(config.sizes);
        if (config.collapsed) setCollapsed(config.collapsed);
        if (config.pinned) setPinned(config.pinned);
        if (config.viewModes) setViewModes(config.viewModes);
        if (config.sortModes) setSortModes(config.sortModes);
        if (config.itemLayouts) setItemLayouts(config.itemLayouts);
        if (config.adaptivePreset) setAdaptivePreset(config.adaptivePreset);
        if (config.folderAlignment) setFolderAlignment(config.folderAlignment);
        if (config.notes) setNotes(config.notes);
        persistConfigToLocalStorage(config); // 同步写回 localStorage，保持浏览器预览一致
        if (recovery) setToast({ message: recovery, type: 'info' });
      } catch {
        if (!cancelled) setToast({ message: '设置加载失败，正在使用本地缓存配置', type: 'info' });
      } finally {
        if (!cancelled) setConfigLoaded(true);
      }
    })();
    return () => { cancelled = true; };
  }, [native, settingsRuntime]);

  const { scan, runScan, organizeNow, undoLastOrganization, scanInFlight } = useDesktopScan({
    categories: preferences.categories,
    showHiddenFiles: preferences.showHiddenFiles,
    automaticScan: desktopNative && preferences.automaticScan,
    automaticOrganize: desktopNative && preferences.automaticOrganize,
    organizeDelaySeconds: preferences.organizeDelaySeconds,
    organizeMode: preferences.organizeMode,
    referencePins: preferences.referencePins,
    rules: preferences.rules,
    interactionActive,
    setToast,
  });

  useNativeShell({
    native: desktopNative,
    categories: preferences.categories,
    preferences,
    runScan,
    scanInFlight,
    setPreferences,
    setPositions,
    setSizes,
    setItemLayouts,
    setPinned,
    setCollapsed,
    setViewModes,
    setSortModes,
    setAdaptivePreset,
    setFolderAlignment,
    setShowSettings,
    setShowCategoryModal,
    setClickThrough,
    setDraggingModal,
    setToast,
  });

  const flushPersistedState = usePersistedState({
    preferences, positions, sizes, itemLayouts, adaptivePreset, folderAlignment, pinned, collapsed, viewModes, sortModes,
    enabled: configLoaded, // 宿主配置加载完成前不保存，避免默认值覆盖 config.json
    onError: (message) => setToast({ message, type: 'info' }),
  });

  // 设置窗口标题栏关闭（X / Alt+F4）：宿主关闭前通知页面立即刷写防抖中的配置
  useEffect(() => {
    if (!settingsRuntime) return undefined;
    const flush = () => flushPersistedState();
    window.addEventListener('pivkey-flush-settings', flush);
    return () => window.removeEventListener('pivkey-flush-settings', flush);
  }, [flushPersistedState, settingsRuntime]);

  // 设置窗口重新激活显示：从宿主重新拉取最新配置，保持多开/多面板状态同步
  useEffect(() => {
    if (!settingsRuntime || !native) return undefined;
    const refresh = () => {
      setShowSettings(true);
      void (async () => {
        try {
          const raw = await nativeWindow.loadConfig();
          const config = parseNativeConfig(raw);
          if (config) {
            if (config.preferences) setPreferences(config.preferences);
            if (config.positions) setPositions(config.positions);
            if (config.sizes) setSizes(config.sizes);
            if (config.collapsed) setCollapsed(config.collapsed);
            if (config.pinned) setPinned(config.pinned);
            if (config.viewModes) setViewModes(config.viewModes);
            if (config.sortModes) setSortModes(config.sortModes);
            if (config.itemLayouts) setItemLayouts(config.itemLayouts);
            if (config.adaptivePreset) setAdaptivePreset(config.adaptivePreset);
            if (config.folderAlignment) setFolderAlignment(config.folderAlignment);
            if (config.notes) setNotes(config.notes);
          }
        } catch { }
      })();
    };
    window.addEventListener('pivkey-refresh-settings', refresh);
    return () => window.removeEventListener('pivkey-refresh-settings', refresh);
  }, [native, settingsRuntime]);

  usePanelSync({
    native: desktopNative,
    preferences: panelPreferences,
    positions,
    sizes,
    pinned,
    collapsed,
    itemLayouts,
    viewModes,
    sortModes,
    items: scan.items,
  });

  useInteractiveRegions({
    native: desktopNative,
    interactionActive,
    draggingModal,
    showCategoryModal,
    showSettings,
    toast,
  });

  // 窗口尺寸或界面缩放变化时，把越界的分区收回到可视区域内
  useEffect(() => {
    const clampAll = () => {
      setPositions((current) => {
        let changed = false;
        const next: ZonePositions = {};
        for (const [id, position] of Object.entries(current)) {
          const clamped = clampPosition(position, sizes[id] ?? { width: 292, height: 238 }, layoutZoom);
          next[id] = clamped;
          if (clamped.x !== position.x || clamped.y !== position.y) changed = true;
        }
        return changed ? next : current;
      });
    };
    clampAll();
    window.addEventListener('resize', clampAll);
    return () => window.removeEventListener('resize', clampAll);
  }, [layoutZoom, sizes]);

  const categories = useMemo(() => preferences.categories, [preferences.categories]);

  const openCategoryModal = () => {
    if (showSettings) setResumeSettingsAfterCategory(true);
    setShowSettings(false);
    setShowCategoryModal(true);
  };

  const closeCategoryModal = () => {
    const shouldResumeSettings = resumeSettingsAfterCategory;
    setShowCategoryModal(false);
    setResumeSettingsAfterCategory(false);
    if (shouldResumeSettings) setShowSettings(true);
  };

  const addCategory = (category: Category) => {
    setPreferences((current) => ({ ...current, categories: [category, ...current.categories] }));
    setPositions((current) => ({ ...current, [category.id]: settingsRuntime ? { x: 28, y: 82 } : clampPosition({ x: 28, y: 82 }) }));
    setSizes((current) => ({ ...current, [category.id]: { width: 292, height: 238 } }));
    closeCategoryModal();
  };

  const resetLayout = (announce = true) => {
    setPositions(settingsRuntime ? {} : loadPositions(categories));
    setSizes(defaultSizes(categories));
    setPinned([]);
    setCollapsed([]);
    setItemLayouts({});
    setAdaptivePreset('manual');
    setFolderAlignment('manual');
    if (announce) setToast({ message: '桌面布局已恢复默认', type: 'success' });
  };

  const resetAllSettings = () => {
    setPreferences(defaultPreferences);
    resetLayout(false);
    setViewModes({});
    setSortModes({});
    setToast({ message: '全部设置已恢复默认', type: 'success' });
  };

  const updateItemLayout = (categoryId: string, layout: PanelItemLayout) => setItemLayouts((current) => ({ ...current, [categoryId]: layout }));

  const applyLayoutPreset = useCallback((preset: LayoutPreset, announce = true) => {
    if (announce) layoutModeVersion.current += 1;
    if (settingsRuntime) {
      setAdaptivePreset(preset);
      setFolderAlignment('manual');
      return;
    }
    // 与原生 Manager.GetLayoutViewport 保持一致：直接采用真实视口，只保留防御性下限，
    // 避免高缩放 / 小屏下被虚构的 800x560 撑大后再被窗口钳制，产生挤压与重叠。
    const width = Math.max(panelMinWidth * 2 + 48, window.innerWidth / layoutZoom);
    const height = Math.max(panelMinHeight, window.innerHeight / layoutZoom);
    const next = createPresetLayout(categories.map((category) => category.id), preset, { width, height, margin: 24, top: 24, bottom: 24, gap: panelLayoutGap });
    setPositions(next.positions);
    setSizes(next.sizes);
    setCollapsed([]);
    setAdaptivePreset(preset);
    setFolderAlignment('manual');
    if (announce) setToast({ message: '布局已优化，并会随工作区尺寸自动调整', type: 'success' });
  }, [categories, layoutZoom, settingsRuntime]);

  useEffect(() => {
    if (adaptivePreset === 'manual') return undefined;
    let timer = 0;
    const version = layoutModeVersion.current;
    const reflow = () => {
      window.clearTimeout(timer);
      timer = window.setTimeout(() => { if (version === layoutModeVersion.current) applyLayoutPreset(adaptivePreset, false); }, 160);
    };
    reflow();
    window.addEventListener('resize', reflow);
    return () => { window.clearTimeout(timer); window.removeEventListener('resize', reflow); };
  }, [adaptivePreset, applyLayoutPreset]);

  const applyFolderAlignment = useCallback((alignment: Exclude<FolderAlignment, 'manual'>, announce = true) => {
    if (announce) layoutModeVersion.current += 1;
    if (settingsRuntime) {
      setAdaptivePreset('manual');
      setFolderAlignment(alignment);
      return;
    }
    // 同 applyLayoutPreset：不再虚构 800x560 下限，只留防御性下限。
    const width = Math.max(panelMinWidth * 2 + 48, window.innerWidth / layoutZoom);
    const height = Math.max(panelMinHeight, window.innerHeight / layoutZoom);
    const next = alignPanelLayout(categories.map((category) => category.id), sizesRef.current, alignment, { width, height, margin: 24, top: 24, bottom: 24, gap: panelLayoutGap });
    setPositions(next.positions);
    setSizes((current) => ({ ...current, ...next.sizes }));
    setCollapsed([]);
    setAdaptivePreset('manual');
    setFolderAlignment(alignment);
    if (announce) setToast({ message: `所有收纳文件夹已${alignment === 'left' ? '靠左' : alignment === 'right' ? '靠右' : '居中'}排列`, type: 'success' });
  }, [categories, layoutZoom, settingsRuntime]);

  useEffect(() => {
    if (folderAlignment === 'manual') return undefined;
    let timer = 0;
    const version = layoutModeVersion.current;
    const reflow = () => {
      window.clearTimeout(timer);
      timer = window.setTimeout(() => { if (version === layoutModeVersion.current) applyFolderAlignment(folderAlignment, false); }, 160);
    };
    reflow();
    window.addEventListener('resize', reflow);
    return () => { window.clearTimeout(timer); window.removeEventListener('resize', reflow); };
  }, [applyFolderAlignment, folderAlignment]);

  const renameCategory = async (categoryId: string, requestedName: string) => {
    const category = categories.find((candidate) => candidate.id === categoryId);
    if (!category) return;
    try {
      const name = normalizeCategoryName(requestedName, categories.filter((candidate) => candidate.id !== categoryId).map((candidate) => candidate.name));
      if (name === category.name) return;
      await renameManagedCategory(scan.desktopPath, category.name, name);
      setPreferences((current) => ({ ...current, categories: current.categories.map((candidate) => candidate.id === categoryId ? { ...candidate, name } : candidate) }));
      setToast({ message: `分区已重命名为“${name}”`, type: 'success' });
    } catch (error) { setToast({ message: error instanceof Error ? error.message : '重命名失败', type: 'info' }); }
  };

  useEffect(() => {
    if (!native) return undefined;
    const rename = (event: Event) => {
      const detail = (event as CustomEvent<{ id: string; name: string }>).detail;
      if (detail) void renameCategory(detail.id, detail.name);
    };
    const togglePin = (event: Event) => {
      const id = String((event as CustomEvent<string>).detail ?? '');
      if (id) setPinned((current) => current.includes(id) ? current.filter((candidate) => candidate !== id) : [...current, id]);
    };
    const toggleCollapse = (event: Event) => {
      const id = String((event as CustomEvent<string>).detail ?? '');
      if (id) setCollapsed((current) => current.includes(id) ? current.filter((candidate) => candidate !== id) : [...current, id]);
    };
    const refresh = () => void runScan(false);
    const changeView = (event: Event) => {
      const detail = (event as CustomEvent<{ id: string; mode: PanelViewMode }>).detail;
      if (detail) setViewModes((current) => ({ ...current, [detail.id]: detail.mode }));
    };
    const changeSort = (event: Event) => {
      const detail = (event as CustomEvent<{ id: string; mode: PanelSortMode }>).detail;
      if (detail) setSortModes((current) => ({ ...current, [detail.id]: detail.mode }));
    };
    window.addEventListener('pivkey-panel-rename', rename);
    window.addEventListener('pivkey-panel-pin', togglePin);
    window.addEventListener('pivkey-panel-collapse', toggleCollapse);
    window.addEventListener('pivkey-panel-refresh', refresh);
    window.addEventListener('pivkey-panel-view', changeView);
    window.addEventListener('pivkey-panel-sort', changeSort);
    return () => {
      window.removeEventListener('pivkey-panel-rename', rename);
      window.removeEventListener('pivkey-panel-pin', togglePin);
      window.removeEventListener('pivkey-panel-collapse', toggleCollapse);
      window.removeEventListener('pivkey-panel-refresh', refresh);
      window.removeEventListener('pivkey-panel-view', changeView);
      window.removeEventListener('pivkey-panel-sort', changeSort);
    };
  }, [native, renameCategory, runScan]);

  const selectedScheme = preferences.colorScheme === 'custom' ? null : schemeColors[preferences.colorScheme];
  const accent = selectedScheme?.accent ?? preferences.customColor;
  const customRgb = colorToRgb(preferences.customColor);
  const lightSurface = selectedScheme?.surface
    ?? (preferences.customSurfaceColor ? colorToRgb(preferences.customSurfaceColor) : customRgb.map((value) => Math.round(value * .08 + 255 * .92))) as unknown as readonly [number, number, number];
  const surface = effectiveTheme === 'dark' ? [31, 31, 30] : lightSurface;
  const rootStyle = { '--header-surface': `rgba(${surface[0]}, ${surface[1]}, ${surface[2]}, ${preferences.glassOpacity / 100})`, '--theme-accent': accent, '--glass-alpha': preferences.glassOpacity / 100, '--ui-zoom': uiZoom } as CSSProperties;
  const closeSettings = () => {
    flushPersistedState();
    if (settingsRuntime) void nativeWindow.closeSettings();
    else setShowSettings(false);
  };

  const handleCreateNote = async (mode: 'todo' | 'note', title?: string) => {
    if (native) {
      await nativeWindow.createNote(mode, title);
      try {
        const raw = await nativeWindow.loadConfig();
        const config = parseNativeConfig(raw);
        if (config?.notes) setNotes(config.notes);
      } catch {}
    } else {
      const newNote: NoteData = {
        id: `note_${Date.now()}_${Math.random().toString(36).slice(2, 6)}`,
        title: title || (mode === 'todo' ? '今日待办' : '随手备忘'),
        mode,
        color: '#ffffff',
        x: 120 + Math.round(Math.random() * 60),
        y: 120 + Math.round(Math.random() * 60),
        width: 260,
        height: mode === 'todo' ? 340 : 280,
        pinned: false,
        collapsed: false,
        noteContent: '',
        todos: mode === 'todo' ? [{ id: `todo_${Date.now()}`, text: '欢迎使用待办清单', done: false, createdAt: Date.now() }] : [],
      };
      setNotes((prev) => {
        const next = [...prev, newNote];
        try { localStorage.setItem(notesKey, JSON.stringify(next)); } catch {}
        return next;
      });
    }
    setToast({ message: `已创建${mode === 'todo' ? '待办清单' : '随手备忘'}`, type: 'success' });
  };

  const handleDeleteNote = async (id: string) => {
    if (native) {
      await nativeWindow.deleteNote(id);
      setNotes((prev) => prev.filter((n) => n.id !== id));
    } else {
      setNotes((prev) => {
        const next = prev.filter((n) => n.id !== id);
        try { localStorage.setItem(notesKey, JSON.stringify(next)); } catch {}
        return next;
      });
    }
    setToast({ message: '已删除便签', type: 'info' });
  };

  // 宿主配置加载完成前渲染空壳，避免闪默认值
  if (settingsRuntime && !configLoaded) {
    return (
      <div className="app app--overlay settings-runtime" style={rootStyle}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100vh', fontSize: 13, color: 'rgba(0, 0, 0, .38)' }}>正在加载设置…</div>
      </div>
    );
  }

  return (
    <div
      className={`app app--overlay ${desktopNative ? 'manager-runtime' : ''} settings-runtime theme-${effectiveTheme} ${preferences.compactView ? 'compact-view' : ''} ${clickThrough ? 'is-click-through' : ''} ${interactionActive ? 'is-dragging' : ''}`}
      style={rootStyle}
    >
      {showCategoryModal && <CategoryModal existingNames={categories.map((category) => category.name)} onClose={closeCategoryModal} onCreate={addCategory} />}
      {showSettings && (
        <SettingsModal
          preferences={preferences}
          itemLayouts={itemLayouts}
          defaultItemLayout={defaultItemLayout}
          adaptivePreset={adaptivePreset}
          onItemLayoutChange={updateItemLayout}
          onApplyLayoutPreset={applyLayoutPreset}
          onChange={setPreferences}
          onReset={resetAllSettings}
          onResetLayout={resetLayout}
          onClose={closeSettings}
          onAddCategory={openCategoryModal}
          onOrganizeNow={() => settingsRuntime ? void nativeWindow.runManagerCommand('organize') : void organizeNow(false)}
          onUndoOrganization={() => settingsRuntime ? void nativeWindow.runManagerCommand('undo-organize') : void undoLastOrganization()}
          onRedoOrganization={() => settingsRuntime ? void nativeWindow.runManagerCommand('redo-organize') : undefined}
          sortModes={sortModes}
          onSortModeChange={(categoryId, mode) => setSortModes((current) => ({ ...current, [categoryId]: mode }))}
          viewModes={viewModes}
          onViewModeChange={(categoryId, mode) => setViewModes((current) => ({ ...current, [categoryId]: mode }))}
          folderAlignment={folderAlignment}
          onFolderAlignmentChange={applyFolderAlignment}
          notes={notes}
          onCreateNote={handleCreateNote}
          onDeleteNote={handleDeleteNote}
          movable={!settingsRuntime}
        />
      )}
      {toast && <Toast message={toast.message} type={toast.type} onClose={() => setToast(null)} />}
    </div>
  );
}
