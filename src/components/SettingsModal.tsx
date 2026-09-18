import { ArrowClockwise, ArrowCounterClockwise, Check, Circle, ClockAfternoon, DownloadSimple, GitBranch, GridFour, HardDrive, Layout, List, ListChecks, FolderSimple, MagnifyingGlass, Monitor, NotePencil, PaintBrush, Palette, Plus, ShieldCheck, SlidersHorizontal, SortAscending, Sparkle, SquaresFour, Swatches, TextAlignCenter, TextAlignLeft, TextAlignRight, Warning, X } from '@phosphor-icons/react';
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react';
import type { AppPreferences, NoteData, PanelItemLayout } from '../types';
import { LayoutPreview, type LayoutPreviewState } from './LayoutPreview';
import { IconPicker } from './IconPicker';
import { Modal } from './Modal';
import { nativeWindow, type UpdateCheckResult, type UpdateDownloadProgress } from '../services/nativeDesktop';
import { AdvancedSettings } from './AdvancedSettings';
import { defaultCategoryIcon, characterIconSource } from '../data/characterIcons';

interface SettingsModalProps {
  preferences: AppPreferences;
  onChange: (preferences: AppPreferences) => void;
  onReset: () => void;
  onResetLayout: () => void;
  onClose: () => void;
  onAddCategory?: () => void;
  onOrganizeNow: () => void;
  onUndoOrganization: () => void;
  onRedoOrganization: () => void;
  sortModes: Record<string, 'name' | 'modified' | 'size'>;
  onSortModeChange: (categoryId: string, mode: 'name' | 'modified' | 'size') => void;
  viewModes: Record<string, 'grid' | 'list'>;
  onViewModeChange: (categoryId: string, mode: 'grid' | 'list') => void;
  folderAlignment: 'manual' | 'left' | 'center' | 'right';
  adaptivePreset: 'manual' | 'balanced' | 'grid' | 'columns' | 'corners' | 'right-dock';
  onFolderAlignmentChange: (alignment: 'left' | 'center' | 'right') => void;
  itemLayouts: Record<string, PanelItemLayout>;
  defaultItemLayout: PanelItemLayout;
  onItemLayoutChange: (categoryId: string, layout: PanelItemLayout) => void;
  onApplyLayoutPreset: (preset: 'balanced' | 'grid' | 'columns' | 'corners' | 'right-dock') => void;
  notes?: NoteData[];
  onCreateNote?: (mode: 'todo' | 'note', title?: string) => void;
  onDeleteNote?: (id: string) => void;
  movable?: boolean;
}

const schemes = [
  { id: 'white', name: '纯白', color: '#3478f6', surface: '#ffffff' },
  { id: 'warm', name: '暖纸', color: '#e98687', surface: '#fff9f0' },
  { id: 'ink', name: '粉蓝', color: '#7a8fab', surface: '#f6f8fc' },
  { id: 'forest', name: '薄荷', color: '#78a68d', surface: '#f1f9f4' },
  { id: 'rose', name: '樱粉', color: '#d887a0', surface: '#fdf2f6' },
] as const;

const sectionMeta = {
  appearance: { title: '外观', description: '控制面板外观、显示密度和窗口色彩。' },
  layout: { title: '布局', description: '调整分区位置、项目排列和显示比例。' },
  behavior: { title: '行为', description: '决定扫描、自动收纳和分类规则何时介入桌面。' },
  notes: { title: '便签与待办', description: '管理桌面便签、待办清单与随手备忘。' },
  advanced: { title: '高级', description: '管理规则、历史、工作区和配置备份。' },
  about: { title: '关于与更新', description: '版本信息、在线检查更新与安装包说明。' },
} as const;

const sectionOrder = ['appearance', 'layout', 'behavior', 'notes', 'advanced', 'about'] as const;
type SettingsSection = typeof sectionOrder[number];

function rgbFromHex(value: string): [number, number, number] {
  const hex = value.replace('#', '').padEnd(6, '0').slice(0, 6);
  return [0, 2, 4].map((offset) => Number.parseInt(hex.slice(offset, offset + 2), 16) || 0) as [number, number, number];
}

function hexFromRgb(rgb: [number, number, number]): string {
  return `#${rgb.map((value) => Math.max(0, Math.min(255, Math.round(value))).toString(16).padStart(2, '0')).join('')}`;
}

interface RangeFieldProps {
  label: string;
  hint?: string;
  min: number;
  max: number;
  step: number;
  value: number;
  display: (value: number) => string;
  onChange: (value: number) => void;
  onPreview?: (value: number) => void;
  className?: string;
}

// 滑块本地草稿：input 事件只更新草稿，松手或原生 change（含键盘步进）才提交，
// 拖动期间不触发外层全局偏好更新，保证跟手
function RangeField({ label, hint, min, max, step, value, display, onChange, onPreview, className }: RangeFieldProps) {
  const [draft, setDraft] = useState(value);
  const [dragging, setDragging] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const lastCommitted = useRef(value);

  const commit = (next: number) => {
    if (next === lastCommitted.current) return;
    lastCommitted.current = next;
    onChange(next);
  };
  // 提交函数存入 ref，供原生 change 监听器使用最新闭包
  const commitRef = useRef(commit);
  useEffect(() => {
    commitRef.current = commit;
  });

  // 原生 change：鼠标松手与键盘步进都会触发，兜底结束拖动状态并提交
  useEffect(() => {
    const input = inputRef.current;
    if (!input) return;
    const onNativeChange = () => {
      setDragging(false);
      commitRef.current(Number(input.value));
    };
    input.addEventListener('change', onNativeChange);
    return () => input.removeEventListener('change', onNativeChange);
  }, []);

  // 外部 value 变化且未在拖动时，同步本地草稿
  useEffect(() => {
    if (!dragging) {
      setDraft(value);
      lastCommitted.current = value;
    }
  }, [value, dragging]);

  return (
    <label className={className}>
      <span>
        <strong>{label}</strong>
        <small>{display(draft)}</small>
        {hint ? <em>{hint}</em> : null}
      </span>
      <input
        ref={inputRef}
        type="range"
        min={min}
        max={max}
        step={step}
        value={draft}
        onPointerDown={() => setDragging(true)}
        onPointerUp={(event) => {
          setDragging(false);
          const next = Number(event.currentTarget.value);
          onPreview?.(next);
          commit(next);
        }}
        onPointerCancel={() => setDragging(false)}
        onChange={(event) => {
          const next = Number(event.target.value);
          setDraft(next);
          onPreview?.(next);
        }}
      />
    </label>
  );
}

interface RgbFieldProps {
  label: string;
  value: number;
  onCommit: (value: number) => void;
}

// RGB 数字输入：本地字符串草稿，blur 或 Enter 时钳制后提交
function RgbField({ label, value, onCommit }: RgbFieldProps) {
  const [text, setText] = useState(String(value));
  const [focused, setFocused] = useState(false);

  // 外部 rgb 变化且该字段未聚焦时同步
  useEffect(() => {
    if (!focused) setText(String(value));
  }, [value, focused]);

  const commit = () => {
    const parsed = Number(text);
    onCommit(Number.isNaN(parsed) ? 0 : parsed);
  };

  return (
    <label>
      <span>{label}</span>
      <input
        type="number"
        min="0"
        max="255"
        value={text}
        onFocus={() => setFocused(true)}
        onBlur={() => {
          setFocused(false);
          commit();
        }}
        onChange={(event) => setText(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === 'Enter') event.currentTarget.blur();
        }}
      />
    </label>
  );
}

export function SettingsModal({ preferences, itemLayouts, defaultItemLayout, adaptivePreset, onItemLayoutChange, onApplyLayoutPreset, onChange, onReset, onResetLayout, onClose, onAddCategory, onOrganizeNow, onUndoOrganization, onRedoOrganization, sortModes, onSortModeChange, viewModes, onViewModeChange, folderAlignment, onFolderAlignmentChange, notes = [], onCreateNote, onDeleteNote, movable = true }: SettingsModalProps) {
  const [section, setSection] = useState<SettingsSection>('appearance');
  const [selectedCategory, setSelectedCategory] = useState(preferences.categories[0]?.id ?? '');
  const [previewState, setPreviewState] = useState<LayoutPreviewState>(null);
  const [settingsShellWidth, setSettingsShellWidth] = useState(0);
  const settingsShellRef = useRef<HTMLDivElement>(null);
  const settingsContentRef = useRef<HTMLDivElement>(null);
  const set = <K extends keyof AppPreferences>(key: K, value: AppPreferences[K]) => onChange({ ...preferences, [key]: value });
  const rgb = rgbFromHex(preferences.customColor);
  const surfaceRgb = rgbFromHex(preferences.customSurfaceColor || '#ffffff');
  const itemLayout = { ...defaultItemLayout, ...itemLayouts[selectedCategory] };
  const [previewLabelSize, setPreviewLabelSize] = useState(itemLayout.labelSize);
  const setItemLayout = <K extends keyof PanelItemLayout>(key: K, value: PanelItemLayout[K]) => onItemLayoutChange(selectedCategory, { ...itemLayout, [key]: value });
  const setRgb = (index: number, value: number) => {
    const next = [...rgb] as [number, number, number];
    next[index] = Math.max(0, Math.min(255, value || 0));
    onChange({ ...preferences, colorScheme: 'custom', customColor: hexFromRgb(next) });
  };
  const setSurfaceRgb = (index: number, value: number) => {
    const next = [...surfaceRgb] as [number, number, number];
    next[index] = Math.max(0, Math.min(255, value || 0));
    onChange({ ...preferences, colorScheme: 'custom', customSurfaceColor: hexFromRgb(next) });
  };
  const effectivePreview: LayoutPreviewState = previewState
    ?? (adaptivePreset !== 'manual' ? { kind: 'preset', value: adaptivePreset } : null)
    ?? (folderAlignment !== 'manual' ? { kind: 'alignment', value: folderAlignment } : null);
  const portalPath = preferences.categories.find((category) => category.id === selectedCategory)?.portalPath;
  const pickPortalFolder = async () => {
    const picked = await nativeWindow.pickFolder();
    if (!picked) return;
    set('categories', preferences.categories.map((category) => category.id === selectedCategory ? { ...category, portalPath: picked } : category));
  };
  const clearPortalFolder = () => {
    set('categories', preferences.categories.map((category) => category.id === selectedCategory ? { ...category, portalPath: undefined } : category));
  };

  // 关于与在线更新状态
  const [appVersion, setAppVersion] = useState('0.1.0');
  const [repoUrl, setRepoUrl] = useState('PivKeyU/PivKeyUBox');
  const [isCheckingUpdate, setIsCheckingUpdate] = useState(false);
  const [updateResult, setUpdateResult] = useState<UpdateCheckResult | null>(null);
  const [checkError, setCheckError] = useState<string | null>(null);
  const [downloadProgress, setDownloadProgress] = useState<UpdateDownloadProgress | null>(null);
  const [isStartingDownload, setIsStartingDownload] = useState(false);
  const [isApplyingUpdate, setIsApplyingUpdate] = useState(false);

  useEffect(() => {
    nativeWindow.getAppVersion().then((info) => {
      if (info && info.version) setAppVersion(info.version);
      if (info && info.defaultRepo) setRepoUrl(info.defaultRepo);
    }).catch(() => {});
  }, []);

  const handleCheckUpdate = async () => {
    setIsCheckingUpdate(true);
    setCheckError(null);
    setUpdateResult(null);
    try {
      const res = await nativeWindow.checkUpdate(repoUrl);
      setUpdateResult(res);
    } catch (err: any) {
      setCheckError(err?.message || '检查更新失败，请检查网络或 GitHub 仓库配置');
    } finally {
      setIsCheckingUpdate(false);
    }
  };

  const handleStartDownload = async (url: string) => {
    setIsStartingDownload(true);
    setCheckError(null);
    try {
      await nativeWindow.startDownloadUpdate(url);
      const pollTimer = window.setInterval(async () => {
        try {
          const progress = await nativeWindow.getDownloadProgress();
          setDownloadProgress(progress);
          if (progress.isDownloaded || progress.error) {
            window.clearInterval(pollTimer);
          }
        } catch {
          window.clearInterval(pollTimer);
        }
      }, 500);
    } catch (err: any) {
      setCheckError(err?.message || '启动下载失败');
    } finally {
      setIsStartingDownload(false);
    }
  };

  const handleApplyUpdate = async () => {
    setIsApplyingUpdate(true);
    try {
      await nativeWindow.applyUpdate(downloadProgress?.filePath, false);
    } catch (err: any) {
      setCheckError(err?.message || '启动安装包失败');
      setIsApplyingUpdate(false);
    }
  };

  useEffect(() => {
    setPreviewLabelSize(itemLayout.labelSize);
  }, [selectedCategory, itemLayout.labelSize]);

  // 切换设置分区时回到该页顶部，避免上一页的滚动位置残留造成“跳入半页”的错觉。
  useLayoutEffect(() => {
    if (settingsContentRef.current) settingsContentRef.current.scrollTop = 0;
  }, [section]);

  // 原生设置窗口使用 CSS zoom，ResizeObserver 的 clientWidth 在不同 WebView2
  // 版本里可能返回视觉宽度或未缩放宽度；统一用视觉矩形 / zoom 还原布局宽度。
  // 用 useLayoutEffect 先于绘制更新紧凑结构，避免缩放滑块提交后闪过一帧错位布局。
  useLayoutEffect(() => {
    const node = settingsShellRef.current;
    if (!node || typeof ResizeObserver === 'undefined') return undefined;
    const update = () => {
      const modal = node.closest<HTMLElement>('.modal');
      const zoom = Number.parseFloat(modal ? getComputedStyle(modal).zoom : '1') || 1;
      const visualWidth = node.getBoundingClientRect().width;
      const layoutWidth = visualWidth / Math.max(.01, zoom);
      const next = Math.max(0, Math.round(layoutWidth));
      setSettingsShellWidth((current) => current === next ? current : next);
    };
    update();
    const observer = new ResizeObserver(update);
    observer.observe(node);
    return () => observer.disconnect();
  }, [preferences.uiScale]);
  const compactSettingsLayout = settingsShellWidth > 0 && settingsShellWidth < 660;
  const currentSectionMeta = sectionMeta[section];
  const handleSectionKeyDown = (event: KeyboardEvent<HTMLButtonElement>, current: SettingsSection) => {
    const currentIndex = sectionOrder.indexOf(current);
    if (!['ArrowDown', 'ArrowRight', 'ArrowUp', 'ArrowLeft', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const nextIndex = event.key === 'Home'
      ? 0
      : event.key === 'End'
        ? sectionOrder.length - 1
        : (currentIndex + (event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1 : 1) + sectionOrder.length) % sectionOrder.length;
    const next = sectionOrder[nextIndex];
    setSection(next);
    window.requestAnimationFrame(() => document.getElementById(`settings-tab-${next}`)?.focus());
  };

  return (
    <Modal title="PivKeyUBox 设置" description="外观、布局与桌面行为" onClose={onClose} className="settings-modal" movable={movable}>
      <div ref={settingsShellRef} className={`settings-shell${compactSettingsLayout ? ' is-compact' : ''}`}>
        <nav className="settings-nav" role="tablist" aria-label="设置分类">
          <button id="settings-tab-appearance" type="button" role="tab" aria-selected={section === 'appearance'} aria-controls="settings-panel" tabIndex={section === 'appearance' ? 0 : -1} className={section === 'appearance' ? 'is-active' : ''} onKeyDown={(event) => handleSectionKeyDown(event, 'appearance')} onClick={() => setSection('appearance')}><Palette size={16} /><span>外观</span></button>
          <button id="settings-tab-layout" type="button" role="tab" aria-selected={section === 'layout'} aria-controls="settings-panel" tabIndex={section === 'layout' ? 0 : -1} className={section === 'layout' ? 'is-active' : ''} onKeyDown={(event) => handleSectionKeyDown(event, 'layout')} onClick={() => setSection('layout')}><SquaresFour size={16} /><span>布局</span></button>
          <button id="settings-tab-behavior" type="button" role="tab" aria-selected={section === 'behavior'} aria-controls="settings-panel" tabIndex={section === 'behavior' ? 0 : -1} className={section === 'behavior' ? 'is-active' : ''} onKeyDown={(event) => handleSectionKeyDown(event, 'behavior')} onClick={() => setSection('behavior')}><SlidersHorizontal size={16} /><span>行为</span></button>
          <button id="settings-tab-notes" type="button" role="tab" aria-selected={section === 'notes'} aria-controls="settings-panel" tabIndex={section === 'notes' ? 0 : -1} className={section === 'notes' ? 'is-active' : ''} onKeyDown={(event) => handleSectionKeyDown(event, 'notes')} onClick={() => setSection('notes')}><NotePencil size={16} /><span>便签</span></button>
          <button id="settings-tab-advanced" type="button" role="tab" aria-selected={section === 'advanced'} aria-controls="settings-panel" tabIndex={section === 'advanced' ? 0 : -1} className={section === 'advanced' ? 'is-active' : ''} onKeyDown={(event) => handleSectionKeyDown(event, 'advanced')} onClick={() => setSection('advanced')}><ListChecks size={16} /><span>高级</span></button>
          <button id="settings-tab-about" type="button" role="tab" aria-selected={section === 'about'} aria-controls="settings-panel" tabIndex={section === 'about' ? 0 : -1} className={section === 'about' ? 'is-active' : ''} onKeyDown={(event) => handleSectionKeyDown(event, 'about')} onClick={() => setSection('about')}><ShieldCheck size={16} /><span>关于</span></button>
        </nav>

        <div ref={settingsContentRef} id="settings-panel" className="settings-content" role="tabpanel" aria-labelledby={`settings-tab-${section}`} tabIndex={-1}>
          <header className="settings-content__masthead">
            <h3>{currentSectionMeta.title}</h3>
            <p>{currentSectionMeta.description}</p>
          </header>
          {section === 'appearance' && (
              <div className="settings-page settings-page--appearance" key="appearance">
              <div className="settings-page__identity">
                <img src={characterIconSource('chiikawa-idle')} alt="片刻收纳吉伊角色图标" />
                <div>
                  <strong>片刻收纳</strong>
                  <small>轻量、可爱的桌面整理伙伴</small>
                </div>
                <span>Fluent UI</span>
              </div>
              <section className="settings-section settings-section--display"><div className="settings-section__heading"><Sparkle size={16} /><div><strong>展示模式</strong><small>决定桌面分区如何呈现，切换立即生效</small></div></div>
                <div className="segmented display-mode" aria-label="展示模式"><button type="button" className={!preferences.capsuleMode ? 'is-active' : ''} onClick={() => set('capsuleMode', false)}><SquaresFour size={15} />完整面板</button><button type="button" className={preferences.capsuleMode ? 'is-active' : ''} onClick={() => set('capsuleMode', true)}><Circle size={15} />图标胶囊</button></div>
                <p className="settings-note">{preferences.capsuleMode ? '已开启：所有分区立即折叠为图标胶囊，点击图标展开。' : '完整面板：分区以面板形式平铺在桌面。'}</p>
              </section>
              <section className="settings-section settings-section--theme"><div className="settings-section__heading"><PaintBrush size={16} /><div><strong>界面主题</strong><small>控制标题条与设置窗口的明暗</small></div></div>
                <div className="segmented settings-theme" aria-label="主题"><button type="button" className={preferences.theme === 'light' ? 'is-active' : ''} onClick={() => set('theme', 'light')}>浅色</button><button type="button" className={preferences.theme === 'dark' ? 'is-active' : ''} onClick={() => set('theme', 'dark')}>深色</button><button type="button" className={preferences.theme === 'system' ? 'is-active' : ''} onClick={() => set('theme', 'system')}>跟随系统</button></div>
              </section>
              <section className="settings-section settings-section--palette">
                <div className="settings-section__heading">
                  <Swatches size={16} />
                  <div>
                    <strong>配色方案</strong>
                    <small>选择窗口主题与面板底色</small>
                  </div>
                </div>
                <div className="scheme-grid">
                  {schemes.map((scheme) => (
                    <button
                      type="button"
                      key={scheme.id}
                      className={preferences.colorScheme === scheme.id ? 'is-active' : ''}
                      onClick={() => set('colorScheme', scheme.id)}
                    >
                      <i style={{ background: scheme.surface, borderColor: scheme.color }} />
                      <span>{scheme.name}</span>
                    </button>
                  ))}
                  <button
                    type="button"
                    className={preferences.colorScheme === 'custom' ? 'is-active' : ''}
                    onClick={() => set('colorScheme', 'custom')}
                  >
                    <i
                      style={{
                        background: preferences.customSurfaceColor || '#ffffff',
                        borderColor: preferences.customColor,
                      }}
                    />
                    <span>自定义</span>
                  </button>
                </div>
                {preferences.colorScheme === 'custom' && (
                  <div className="custom-palette-editor">
                    <div className="custom-color-row">
                      <span className="custom-color-label">主题强调色</span>
                      <div className="rgb-editor">
                        <input
                          type="color"
                          value={preferences.customColor}
                          aria-label="自定义主题强调色"
                          onChange={(event) => set('customColor', event.target.value)}
                        />
                        {(['R', 'G', 'B'] as const).map((label, index) => (
                          <RgbField
                            key={label}
                            label={label}
                            value={rgb[index]}
                            onCommit={(value) => setRgb(index, value)}
                          />
                        ))}
                      </div>
                    </div>
                    <div className="custom-color-row">
                      <span className="custom-color-label">收纳框背景色</span>
                      <div className="rgb-editor">
                        <input
                          type="color"
                          value={preferences.customSurfaceColor || '#ffffff'}
                          aria-label="自定义收纳框背景色"
                          onChange={(event) => set('customSurfaceColor', event.target.value)}
                        />
                        {(['R', 'G', 'B'] as const).map((label, index) => (
                          <RgbField
                            key={label}
                            label={label}
                            value={surfaceRgb[index]}
                            onCommit={(value) => setSurfaceRgb(index, value)}
                          />
                        ))}
                      </div>
                    </div>
                    <div className="surface-presets-row">
                      <span className="custom-color-label">常用底色</span>
                      <div className="surface-presets">
                        {[
                          { name: '纯白', value: '#ffffff' },
                          { name: '浅灰', value: '#f8f9fa' },
                          { name: '冷白', value: '#f1f3f5' },
                          { name: '柔米', value: '#fff9f0' },
                          { name: '淡青', value: '#f0f7f4' },
                          { name: '暗调', value: '#242426' },
                        ].map((preset) => (
                          <button
                            type="button"
                            key={preset.value}
                            className={`surface-preset-btn ${
                              (preferences.customSurfaceColor || '#ffffff').toLowerCase() === preset.value.toLowerCase()
                                ? 'is-active'
                                : ''
                            }`}
                            onClick={() => set('customSurfaceColor', preset.value)}
                          >
                            <i style={{ background: preset.value }} />
                            <span>{preset.name}</span>
                          </button>
                        ))}
                      </div>
                    </div>
                  </div>
                )}
              </section>
              <section className="settings-section settings-section--opacity">
                <RangeField className="range-row settings-range" label="标题条不透明度" min={0} max={100} step={1} value={preferences.glassOpacity} display={(value) => `${value}%`} onChange={(value) => set('glassOpacity', value)} />
                <p className="settings-note">0% 时分区背景完全透明，只剩图标、文字与细线边框；百分比越高背景越不透明。</p>
              </section>
              <section className="settings-section settings-section--scale">
                <RangeField className="range-row settings-range" label="界面缩放" min={80} max={130} step={5} value={preferences.uiScale} display={(value) => `${value}%`} onChange={(value) => set('uiScale', value)} />
                <p className="settings-note">放大或缩小分区、设置窗口与通知，适配不同尺寸和分辨率的显示器。</p>
              </section>
              <section className="settings-section settings-section--toggles"><label className="switch-row"><span><strong>紧凑视图</strong><small>缩小分区内部留白，提高信息密度</small></span><input type="checkbox" checked={preferences.compactView} onChange={(event) => set('compactView', event.target.checked)} /><i /></label><label className="switch-row"><span><strong>Magic 分区色</strong><small>分区颜色自动跟随内容类型</small></span><input type="checkbox" checked={preferences.magicColor} onChange={(event) => set('magicColor', event.target.checked)} /><i /></label></section>
              <section className="settings-section settings-section--labels"><div className="settings-section__heading"><Layout size={16} /><div><strong>标签显示</strong><small>统一控制所有面板的文件名显示方式</small></div></div><label className="switch-row"><span><strong>显示扩展名</strong><small>显示 .pdf、.psd 等文件后缀</small></span><input type="checkbox" checked={preferences.showExtensions} onChange={(event) => set('showExtensions', event.target.checked)} /><i /></label><div className="layout-row"><span>标签位置</span><div className="alignment-picker"><button type="button" className={preferences.labelPosition === 'bottom' ? 'is-active' : ''} onClick={() => set('labelPosition', 'bottom')}>图标下方</button><button type="button" className={preferences.labelPosition === 'right' ? 'is-active' : ''} onClick={() => set('labelPosition', 'right')}>图标右侧</button></div></div></section>
              <section className="settings-section settings-section--autohide"><div className="settings-section__heading"><Monitor size={16} /><div><strong>自动隐藏</strong><small>面板空闲后收起，鼠标靠近边缘或面板时唤出</small></div></div><label className="switch-row"><span><strong>启用自动隐藏</strong><small>固定面板不会自动隐藏</small></span><input type="checkbox" checked={preferences.autoHide} onChange={(event) => set('autoHide', event.target.checked)} /><i /></label>{preferences.autoHide ? <label className="range-row settings-range"><span><strong>隐藏延迟</strong><small>{preferences.autoHideDelaySeconds} 秒</small></span><input type="range" min="1" max="10" step="1" value={preferences.autoHideDelaySeconds} onChange={(event) => set('autoHideDelaySeconds', Number(event.target.value))} /></label> : <p className="settings-note">关闭期间隐藏延迟设置会保留，重新开启即按原设置生效。</p>}</section>
            </div>
          )}

          {section === 'layout' && (
            <div className="settings-page settings-page--layout layout-page" key="layout">
              <div className="layout-page__preview">
                <LayoutPreview categories={preferences.categories} state={effectivePreview} />
              </div>
              <div className="layout-page__controls">
                <section className="settings-section"><div className="settings-section__heading"><Monitor size={16} /><div><strong>屏幕自适应预设</strong><small>按当前工作区大小重新计算所有分区的位置和尺寸</small></div></div>
                  <div className="layout-presets">
                    <button type="button" aria-pressed={adaptivePreset === 'balanced'} className={`preset-card preset-card--recommended ${adaptivePreset === 'balanced' ? 'is-active' : ''}`} onClick={() => onApplyLayoutPreset('balanced')} onMouseEnter={() => setPreviewState({ kind: 'preset', value: 'balanced' })} onMouseLeave={() => setPreviewState(null)}><i className="preset-preview preset-preview--balanced" /><span><strong>智能平衡</strong><small>两侧收纳，保留壁纸中心</small></span></button>
                    <button type="button" aria-pressed={adaptivePreset === 'grid'} className={`preset-card ${adaptivePreset === 'grid' ? 'is-active' : ''}`} onClick={() => onApplyLayoutPreset('grid')} onMouseEnter={() => setPreviewState({ kind: 'preset', value: 'grid' })} onMouseLeave={() => setPreviewState(null)}><i className="preset-preview preset-preview--grid" /><span><strong>均匀网格</strong><small>末行自动居中</small></span></button>
                    <button type="button" aria-pressed={adaptivePreset === 'columns'} className={`preset-card ${adaptivePreset === 'columns' ? 'is-active' : ''}`} onClick={() => onApplyLayoutPreset('columns')} onMouseEnter={() => setPreviewState({ kind: 'preset', value: 'columns' })} onMouseLeave={() => setPreviewState(null)}><i className="preset-preview preset-preview--columns" /><span><strong>等宽分栏</strong><small>纵向阅读顺序</small></span></button>
                    <button type="button" aria-pressed={adaptivePreset === 'corners'} className={`preset-card ${adaptivePreset === 'corners' ? 'is-active' : ''}`} onClick={() => onApplyLayoutPreset('corners')} onMouseEnter={() => setPreviewState({ kind: 'preset', value: 'corners' })} onMouseLeave={() => setPreviewState(null)}><i className="preset-preview preset-preview--corners" /><span><strong>屏幕四角</strong><small>减少中心遮挡</small></span></button>
                    <button type="button" aria-pressed={adaptivePreset === 'right-dock'} className={`preset-card ${adaptivePreset === 'right-dock' ? 'is-active' : ''}`} onClick={() => onApplyLayoutPreset('right-dock')} onMouseEnter={() => setPreviewState({ kind: 'preset', value: 'right-dock' })} onMouseLeave={() => setPreviewState(null)}><i className="preset-preview preset-preview--dock" /><span><strong>右侧停靠</strong><small>集中到屏幕右缘</small></span></button>
                  </div>
                  <p className="settings-note layout-adaptive-note">应用后会持续跟随工作区尺寸变化；手动移动或缩放任一分区后切换为手动布局。</p>
                </section>
                <section className="settings-section"><div className="settings-section__heading"><TextAlignLeft size={16} /><div><strong>收纳文件夹对齐</strong><small>控制桌面上所有分类分区本身的位置，不影响里面的快捷方式图标</small></div></div>
                  <div className="folder-alignment-picker">
                    <button type="button" className={folderAlignment === 'left' ? 'is-active' : ''} onClick={() => onFolderAlignmentChange('left')} onMouseEnter={() => setPreviewState({ kind: 'alignment', value: 'left' })} onMouseLeave={() => setPreviewState(null)}><TextAlignLeft size={17} /><span><strong>靠左排列</strong><small>从屏幕左侧开始换行</small></span></button>
                    <button type="button" className={folderAlignment === 'center' ? 'is-active' : ''} onClick={() => onFolderAlignmentChange('center')} onMouseEnter={() => setPreviewState({ kind: 'alignment', value: 'center' })} onMouseLeave={() => setPreviewState(null)}><TextAlignCenter size={17} /><span><strong>居中排列</strong><small>每一行独立居中</small></span></button>
                    <button type="button" className={folderAlignment === 'right' ? 'is-active' : ''} onClick={() => onFolderAlignmentChange('right')} onMouseEnter={() => setPreviewState({ kind: 'alignment', value: 'right' })} onMouseLeave={() => setPreviewState(null)}><TextAlignRight size={17} /><span><strong>靠右排列</strong><small>从屏幕右侧开始换行</small></span></button>
                  </div>
                  {folderAlignment === 'manual' && <p className="settings-note layout-adaptive-note">当前为手动位置。选择一种方式后，所有收纳文件夹会按屏幕大小自动排列。</p>}
                </section>
                <section className="settings-section"><div className="settings-section__heading"><Layout size={16} /><div><strong>分类项目布局</strong><small>每个分类可以使用不同的图标尺寸和排列方式</small></div></div>
                  <label className="settings-select"><span>当前分类</span><select value={selectedCategory} onChange={(event) => setSelectedCategory(event.target.value)}>{preferences.categories.map((category) => <option key={category.id} value={category.id}>{category.name}</option>)}</select></label>
                  <div className="layout-row icon-picker-row"><span>分类图标<small>优先使用手绘角色姿势，也可以上传自己的透明 PNG</small></span><IconPicker defaultValue={defaultCategoryIcon(selectedCategory)} value={preferences.categories.find((category) => category.id === selectedCategory)?.icon ?? defaultCategoryIcon(selectedCategory)} onChange={(next) => set('categories', preferences.categories.map((category) => category.id === selectedCategory ? { ...category, icon: next } : category))} /></div><div className="layout-row portal-row"><span>文件夹门户</span><div className="portal-controls"><button type="button" className="portal-pick" onClick={() => void pickPortalFolder()}><FolderSimple size={14} />选择文件夹</button>{portalPath ? <><span className="portal-path" title={portalPath}>{portalPath}</span><button type="button" className="portal-clear" onClick={clearPortalFolder}><X size={13} />清除</button></> : null}</div></div><p className="settings-note portal-note">门户分区始终只引用不移动；设置后该分区显示文件夹内容。</p>
                  <div className="item-layout-editor">
                    <RangeField label="项目宽度" min={44} max={112} step={1} value={itemLayout.itemSize} display={(value) => `${value}px`} onChange={(value) => setItemLayout('itemSize', value)} />
                    <RangeField label="图标尺寸" min={24} max={72} step={1} value={itemLayout.iconSize} display={(value) => `${value}px`} onChange={(value) => setItemLayout('iconSize', value)} />
                    <RangeField label="项目间距" min={0} max={24} step={1} value={itemLayout.gap} display={(value) => `${value}px`} onChange={(value) => setItemLayout('gap', value)} />
                    <RangeField label="名称字号" min={8} max={14} step={0.5} value={itemLayout.labelSize} display={(value) => `${value}px`} onPreview={setPreviewLabelSize} onChange={(value) => setItemLayout('labelSize', value)} />
                  </div>
                  <div className="font-preview-card">
                    <div className="font-preview-card__heading"><div><strong>收纳文字预览</strong><small>拖动“名称字号”即可实时查看</small></div><span>{previewLabelSize}px</span></div>
                    <div className="font-preview-card__stage">
                      <div className="font-preview-card__sample"><img src={characterIconSource('chiikawa-carry-folder')} alt="" aria-hidden="true" /><span style={{ fontSize: `${previewLabelSize}px` }}>项目文件夹</span></div>
                      <div className="font-preview-card__sample"><img src={characterIconSource('chiikawa-wave')} alt="" aria-hidden="true" /><span style={{ fontSize: `${previewLabelSize}px` }}>快捷方式</span></div>
                    </div>
                  </div>
                  <div className="layout-row"><span>内部图标对齐</span><div className="alignment-picker"><button type="button" className={itemLayout.alignment === 'left' ? 'is-active' : ''} onClick={() => setItemLayout('alignment', 'left')}><TextAlignLeft size={15} />靠左</button><button type="button" className={itemLayout.alignment === 'center' ? 'is-active' : ''} onClick={() => setItemLayout('alignment', 'center')}><TextAlignCenter size={15} />居中</button><button type="button" className={itemLayout.alignment === 'right' ? 'is-active' : ''} onClick={() => setItemLayout('alignment', 'right')}><TextAlignRight size={15} />靠右</button></div></div>
                  <label className="settings-select"><span>列数偏好</span><select value={itemLayout.columns} onChange={(event) => setItemLayout('columns', Number(event.target.value))}><option value="0">自动适应宽度</option>{[1,2,3,4,5,6,7,8].map((count) => <option key={count} value={count}>{count} 列</option>)}</select></label>
                  <div className="layout-row"><span>显示方式</span><div className="alignment-picker">
                    <button type="button" className={(viewModes[selectedCategory] ?? 'grid') === 'grid' ? 'is-active' : ''} onClick={() => onViewModeChange(selectedCategory, 'grid')}><SquaresFour size={14} />图标网格</button>
                    <button type="button" className={(viewModes[selectedCategory] ?? 'grid') === 'list' ? 'is-active' : ''} onClick={() => onViewModeChange(selectedCategory, 'list')}><List size={14} />紧凑列表</button>
                  </div></div>
                  <div className="layout-row"><span>排序方式</span><div className="alignment-picker">
                    <button type="button" className={(sortModes[selectedCategory] ?? 'name') === 'name' ? 'is-active' : ''} onClick={() => onSortModeChange(selectedCategory, 'name')}><SortAscending size={14} />名称</button>
                    <button type="button" className={(sortModes[selectedCategory] ?? 'name') === 'modified' ? 'is-active' : ''} onClick={() => onSortModeChange(selectedCategory, 'modified')}><ClockAfternoon size={14} />时间</button>
                    <button type="button" className={(sortModes[selectedCategory] ?? 'name') === 'size' ? 'is-active' : ''} onClick={() => onSortModeChange(selectedCategory, 'size')}><HardDrive size={14} />大小</button>
                  </div></div>
                  <label className="switch-row"><span><strong>显示项目名称</strong><small>隐藏后只保留图标</small></span><input type="checkbox" checked={itemLayout.showLabels} onChange={(event) => setItemLayout('showLabels', event.target.checked)} /><i /></label>
                </section>
                <section className="settings-section"><div className="settings-section__heading"><GridFour size={16} /><div><strong>桌面分区</strong><small>分区可在右下角缩放，也可用图钉固定位置</small></div></div>
                  <button type="button" className="settings-command" onClick={onAddCategory}><span><Plus size={15} />新建分区</span><small>添加自定义名称、颜色和文件规则</small></button>
                  <button type="button" className="settings-command" onClick={onResetLayout}><span><ArrowCounterClockwise size={15} />恢复默认布局</span><small>重置位置、尺寸、折叠和固定状态</small></button>
                </section>
              </div>
            </div>
          )}

          {section === 'behavior' && (
            <div className="settings-page settings-page--behavior" key="behavior">
              <section className="settings-section"><div className="settings-section__heading"><FolderSimple size={16} /><div><strong>收纳模式</strong><small>决定匹配规则的项目如何进入分区</small></div></div><div className="segmented"><button type="button" className={preferences.organizeMode === 'move' ? 'is-active' : ''} onClick={() => set('organizeMode', 'move')}>移动收纳</button><button type="button" className={preferences.organizeMode === 'reference' ? 'is-active' : ''} onClick={() => set('organizeMode', 'reference')}>只引用，不移动</button></div>{preferences.organizeMode === 'reference' ? <p className="settings-note">文件始终留在原处，分区只是规则的视图；适合不想动文件位置的场景。</p> : <p className="settings-note">匹配规则的项目会自动移动到“桌面\片刻收纳\分类名称”。</p>}</section><section className="settings-section"><div className="settings-section__heading"><MagnifyingGlass size={16} /><div><strong>扫描行为</strong><small>控制桌面内容何时同步到分区</small></div></div>
                <label className="switch-row"><span><strong>自动刷新</strong><small>每 30 秒同步一次桌面变化</small></span><input type="checkbox" checked={preferences.automaticScan} onChange={(event) => set('automaticScan', event.target.checked)} /><i /></label>
                <label className="switch-row"><span><strong>显示隐藏文件</strong><small>包括 Windows 隐藏属性或名称以句点开头的项目</small></span><input type="checkbox" checked={preferences.showHiddenFiles} onChange={(event) => set('showHiddenFiles', event.target.checked)} /><i /></label>
              </section>
              <section className="settings-section"><div className="settings-section__heading"><Sparkle size={16} /><div><strong>自动收纳</strong><small>只移动已经命中分类规则、并且状态稳定的桌面项目</small></div></div>
                <label className={`switch-row${preferences.organizeMode === 'reference' ? ' is-disabled' : ''}`}><span><strong>启用自动收纳</strong><small>未知类型不会移动；正在写入或变化的文件会继续等待</small></span><input type="checkbox" checked={preferences.automaticOrganize} disabled={preferences.organizeMode === 'reference'} onChange={(event) => set('automaticOrganize', event.target.checked)} /><i /></label>{preferences.organizeMode === 'reference' && <p className="settings-note">引用模式下不会移动文件；自动收纳开关与等待时间会保留，切回移动收纳后按原设置生效。</p>}
                <RangeField className="range-row settings-range" label="稳定等待时间" min={5} max={60} step={5} value={preferences.organizeDelaySeconds} display={(value) => `${value} 秒`} onChange={(value) => set('organizeDelaySeconds', value)} />
                <button type="button" className="settings-command organize-now" disabled={preferences.organizeMode === 'reference'} onClick={onOrganizeNow}><span><Sparkle size={15} />立即收纳已匹配项目</span><small>跳过等待，但仍保留未知类型与未分类项目</small></button>
                <button type="button" className="settings-command" disabled={preferences.organizeMode === 'reference'} onClick={onUndoOrganization}><span><ArrowCounterClockwise size={15} />撤销上次收纳</span><small>尝试把最近一批自动收纳的项目放回原位置</small></button>
              </section>
              <section className="settings-section"><div className="settings-section__heading"><ListChecks size={16} /><div><strong>自动分类规则</strong><small>规则确定且可预测，不会读取文件正文</small></div></div>
                <ol className="classification-steps">
                  <li><b>先判断文件夹</b><span>文件夹进入第一个开启“接收文件夹”的分类。</span></li>
                  <li><b>再匹配扩展名</b><span>忽略大小写和开头的点，按分类顺序采用第一个匹配项。</span></li>
                  <li><b>未命中则保留</b><span>无扩展名或未知类型进入“未分类”，不会被自动移动。</span></li>
                </ol>
                <div className="classification-rules">{preferences.categories.map((category) => <div key={category.id}><i style={{ background: category.color }} /><strong>{category.name}</strong><span>{category.acceptsFolders ? '所有文件夹' : category.extensions.length ? category.extensions.map((extension) => `.${extension}`).join('  ') : '未设置规则'}</span></div>)}</div>
                <p className="settings-note">新建的自定义分类排在最前，因此相同扩展名会优先进入自定义分类。实际整理时文件会移动到“桌面\片刻收纳\分类名称”。</p>
              </section>
            </div>
          )}

          {section === 'notes' && (
            <div className="settings-page settings-page--notes" key="notes">
              <div className="settings-page__identity">
                <img src={characterIconSource('chiikawa-read')} alt="便签图标" />
                <div>
                  <strong>桌面便签 & 待办清单</strong>
                  <small>轻量、高颜值的桌面效率纸张</small>
                </div>
                <div style={{ display: 'flex', gap: 8, marginLeft: 'auto' }}>
                  <button
                    type="button"
                    className="button button--primary"
                    style={{ display: 'inline-flex', alignItems: 'center', gap: 6, padding: '6px 12px', fontSize: 12, cursor: 'pointer' }}
                    onClick={() => {
                      if (onCreateNote) onCreateNote('todo');
                      else void nativeWindow.createNote('todo');
                    }}
                  >
                    <Plus size={14} weight="bold" />
                    <span>新建待办清单</span>
                  </button>
                  <button
                    type="button"
                    className="button"
                    style={{ display: 'inline-flex', alignItems: 'center', gap: 6, padding: '6px 12px', fontSize: 12, cursor: 'pointer' }}
                    onClick={() => {
                      if (onCreateNote) onCreateNote('note');
                      else void nativeWindow.createNote('note');
                    }}
                  >
                    <Plus size={14} weight="bold" />
                    <span>新建随手备忘</span>
                  </button>
                </div>
              </div>

              <section className="settings-section">
                <div className="settings-section__heading">
                  <Circle size={16} />
                  <div>
                    <strong>便签收纳模式</strong>
                    <small>控制便签折叠时的桌面形态，实时生效</small>
                  </div>
                </div>
                <div className="segmented display-mode" aria-label="便签收纳模式">
                  <button
                    type="button"
                    className={!preferences.noteCapsuleMode ? 'is-active' : ''}
                    onClick={() => set('noteCapsuleMode', false)}
                  >
                    <SquaresFour size={15} />胶囊条
                  </button>
                  <button
                    type="button"
                    className={preferences.noteCapsuleMode ? 'is-active' : ''}
                    onClick={() => set('noteCapsuleMode', true)}
                  >
                    <Circle size={15} />小图标
                  </button>
                </div>
                <p className="settings-note">
                  {preferences.noteCapsuleMode
                    ? '已开启小图标：折叠时收纳为 48×48 桌面小图标，显示未完成红点，点击即刻展开。'
                    : '胶囊条模式：折叠为 30px 长条标题栏，展示标题、完成进度与展开按钮。'}
                </p>
              </section>

              <section className="settings-section">
                <div className="settings-section__heading">
                  <Monitor size={16} />
                  <div>
                    <strong>Windows 桌面右键菜单</strong>
                    <small>在桌面空白处右键快速新建待办与随手备忘</small>
                  </div>
                </div>
                <label style={{ display: 'flex', alignItems: 'center', gap: 10, cursor: 'pointer', fontSize: 13, userSelect: 'none' }}>
                  <input
                    type="checkbox"
                    checked={preferences.desktopContextMenu !== false}
                    onChange={(e) => set('desktopContextMenu', e.target.checked)}
                    style={{ cursor: 'pointer', width: 16, height: 16 }}
                  />
                  <span>在 Windows 桌面空白处右键菜单中显示「新建片刻便签」</span>
                </label>
                <p className="settings-note">
                  开启后，可在桌面任意位置右键，直接点击「新建待办清单」或「新建随手备忘」，新便签直接降落在光标所在位置。
                </p>
              </section>

              <section className="settings-section">
                <div className="settings-section__heading">
                  <ListChecks size={16} />
                  <div>
                    <strong>当前便签列表</strong>
                    <small>当前已在桌面展示的待办卡片与备忘记录</small>
                  </div>
                </div>

                {(!notes || notes.length === 0) ? (
                  <div style={{ textAlign: 'center', padding: '28px 16px', background: 'rgba(0,0,0,0.02)', borderRadius: 10, border: '1px dashed rgba(0,0,0,0.1)' }}>
                    <p style={{ margin: 0, fontSize: 13, color: 'var(--chi-muted, #888)' }}>暂无桌面便签。点击右上角按钮立即创建一张待办清单或随手备忘！</p>
                  </div>
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                    {notes.map((note) => {
                      const isTodo = note.mode === 'todo';
                      const doneCount = isTodo && note.todos ? note.todos.filter((t) => t.done).length : 0;
                      const totalCount = isTodo && note.todos ? note.todos.length : 0;
                      return (
                        <div
                          key={note.id}
                          style={{
                            display: 'flex',
                            alignItems: 'center',
                            justifyContent: 'space-between',
                            padding: '10px 14px',
                            borderRadius: 8,
                            border: '1px solid rgba(0,0,0,0.08)',
                            background: 'var(--color-card, rgba(255,255,255,0.7))',
                          }}
                        >
                          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                            <span
                              style={{
                                display: 'inline-flex',
                                alignItems: 'center',
                                gap: 4,
                                fontSize: 11,
                                fontWeight: 600,
                                padding: '2px 8px',
                                borderRadius: 6,
                                background: isTodo ? 'rgba(52, 120, 246, 0.12)' : 'rgba(217, 119, 6, 0.12)',
                                color: isTodo ? '#3478f6' : '#d97706',
                              }}
                            >
                              {isTodo ? '待办清单' : '随手备忘'}
                            </span>
                            <div>
                              <strong style={{ fontSize: 13, display: 'block' }}>{note.title || (isTodo ? '待办清单' : '随手备忘')}</strong>
                              <small style={{ color: 'var(--chi-muted, #888)', fontSize: 11 }}>
                                {isTodo
                                  ? `已完成 ${doneCount} / ${totalCount} 项${note.pinned ? ' · 置顶' : ' · 底层'}`
                                  : `${note.noteContent ? (note.noteContent.slice(0, 24) + (note.noteContent.length > 24 ? '…' : '')) : '暂无文本'}${note.pinned ? ' · 置顶' : ' · 底层'}`}
                              </small>
                            </div>
                          </div>

                          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                            <button
                              type="button"
                              title="删除此便签"
                              style={{
                                background: 'transparent',
                                border: 'none',
                                color: '#dc2626',
                                cursor: 'pointer',
                                padding: '4px 6px',
                                borderRadius: 4,
                                display: 'inline-flex',
                                alignItems: 'center',
                              }}
                              onClick={() => {
                                if (onDeleteNote) onDeleteNote(note.id);
                                else void nativeWindow.deleteNote(note.id);
                              }}
                            >
                              <X size={15} />
                            </button>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </section>

              <section className="settings-section">
                <div className="settings-section__heading">
                  <Sparkle size={16} />
                  <div>
                    <strong>操作贴士</strong>
                    <small>了解便签与待办的便捷操作技巧</small>
                  </div>
                </div>
                <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: 10 }}>
                  <div style={{ padding: 10, borderRadius: 8, background: 'rgba(0,0,0,0.02)', border: '1px solid rgba(0,0,0,0.06)' }}>
                    <strong style={{ fontSize: 12, display: 'block', marginBottom: 4 }}>💡 托盘随时新建</strong>
                    <small style={{ color: 'var(--chi-muted, #666)', fontSize: 11, lineHeight: 1.5, display: 'block' }}>
                      右键任务栏右下角托盘图标，可直接点击「新建待办清单」或「新建随手备忘」。
                    </small>
                  </div>
                  <div style={{ padding: 10, borderRadius: 8, background: 'rgba(0,0,0,0.02)', border: '1px solid rgba(0,0,0,0.06)' }}>
                    <strong style={{ fontSize: 12, display: 'block', marginBottom: 4 }}>⚡ 分区右键就近创建</strong>
                    <small style={{ color: 'var(--chi-muted, #666)', fontSize: 11, lineHeight: 1.5, display: 'block' }}>
                      在桌面上任意收纳分区的标题栏右键，菜单中均可就近创建待办清单或备忘。
                    </small>
                  </div>
                  <div style={{ padding: 10, borderRadius: 8, background: 'rgba(0,0,0,0.02)', border: '1px solid rgba(0,0,0,0.06)' }}>
                    <strong style={{ fontSize: 12, display: 'block', marginBottom: 4 }}>📌 便签内直接新建</strong>
                    <small style={{ color: 'var(--chi-muted, #666)', fontSize: 11, lineHeight: 1.5, display: 'block' }}>
                      点击桌面便签顶部的「+」即可快速新建；点击「⇄」可随时在待办清单与备忘录之间切换。
                    </small>
                  </div>
                </div>
              </section>
            </div>
          )}

          {section === 'advanced' && <AdvancedSettings preferences={preferences} onChange={onChange} onOrganizeNow={onOrganizeNow} onUndoOrganization={onUndoOrganization} onRedoOrganization={onRedoOrganization} />}

          {section === 'about' && (
            <div className="settings-page settings-page--about" key="about">
              <div className="settings-page__identity">
                <img src={characterIconSource('chiikawa-idle')} alt="PivKeyUBox 角色图标" />
                <div>
                  <strong>PivKeyUBox (片刻收纳)</strong>
                  <small>轻量、纯净的 Windows 桌面挂载整理伙伴</small>
                </div>
                <span className="version-badge">v{appVersion}</span>
              </div>

              <section className="settings-section">
                <div className="settings-section__heading">
                  <ArrowClockwise size={16} />
                  <div>
                    <strong>在线更新与版本检测</strong>
                    <small>通过 GitHub Releases 获取最新发布与功能改进</small>
                  </div>
                </div>

                <div className="update-form">
                  <label className="repo-label">
                    <span>GitHub 仓库</span>
                    <div className="repo-input-wrapper">
                      <GitBranch size={16} />
                      <input
                        type="text"
                        value={repoUrl}
                        placeholder="owner/repo (例如 PivKeyU/PivKeyUBox)"
                        onChange={(e) => setRepoUrl(e.target.value)}
                      />
                    </div>
                  </label>
                  <button
                    type="button"
                    className="button button--primary"
                    disabled={isCheckingUpdate || isStartingDownload || Boolean(downloadProgress?.isDownloading)}
                    onClick={() => handleCheckUpdate()}
                  >
                    <ArrowClockwise size={15} className={isCheckingUpdate ? 'spin-animation' : ''} />
                    <span>{isCheckingUpdate ? '正在检测...' : '检查更新'}</span>
                  </button>
                </div>

                {checkError && (
                  <div className="update-status-box update-status-box--error">
                    <Warning size={18} />
                    <div>
                      <strong>检查更新失败</strong>
                      <p>{checkError}</p>
                    </div>
                  </div>
                )}

                {updateResult && !updateResult.hasUpdate && (
                  <div className="update-status-box update-status-box--latest">
                    <Check size={18} />
                    <div>
                      <strong>当前已是最新版本 (v{updateResult.currentVersion})</strong>
                      <p>未在 GitHub Releases 检测到更高版本。</p>
                    </div>
                  </div>
                )}

                {updateResult && updateResult.hasUpdate && (
                  <div className="update-status-box update-status-box--available">
                    <div className="update-box-header">
                      <div>
                        <span className="update-badge">发现新版本</span>
                        <strong className="update-version-title">{updateResult.tagName} {updateResult.releaseName ? `(${updateResult.releaseName})` : ''}</strong>
                        {updateResult.publishedAt && (
                          <small className="update-date">发布时间：{new Date(updateResult.publishedAt).toLocaleDateString()}</small>
                        )}
                      </div>
                      {updateResult.htmlUrl && (
                        <a href={updateResult.htmlUrl} target="_blank" rel="noreferrer" className="button button--quiet update-link">
                          在网页查看
                        </a>
                      )}
                    </div>

                    {updateResult.releaseNotes && (
                      <div className="update-notes-wrapper">
                        <div className="update-notes-title">更新说明：</div>
                        <pre className="update-notes-content">{updateResult.releaseNotes}</pre>
                      </div>
                    )}

                    {updateResult.downloadUrl ? (
                      <div className="update-actions">
                        {!downloadProgress?.isDownloading && !downloadProgress?.isDownloaded && (
                          <button
                            type="button"
                            className="button button--primary"
                            disabled={isStartingDownload}
                            onClick={() => handleStartDownload(updateResult.downloadUrl)}
                          >
                            <DownloadSimple size={16} />
                            <span>下载更新安装包 {updateResult.assetSize ? `(${Math.round(updateResult.assetSize / 1024 / 1024 * 10) / 10} MB)` : ''}</span>
                          </button>
                        )}

                        {downloadProgress?.isDownloading && (
                          <div className="download-progress-container">
                            <div className="download-progress-bar">
                              <div className="download-progress-fill" style={{ width: `${downloadProgress.percentage}%` }} />
                            </div>
                            <div className="download-progress-text">
                              <span>正在下载：{downloadProgress.percentage}%</span>
                              <span>{Math.round(downloadProgress.bytesReceived / 1024 / 1024 * 10) / 10} MB / {Math.round(downloadProgress.totalBytes / 1024 / 1024 * 10) / 10} MB</span>
                            </div>
                          </div>
                        )}

                        {downloadProgress?.isDownloaded && (
                          <div className="download-ready-container">
                            <div className="download-ready-tip">
                              <Check size={16} />
                              <span>更新安装包已下载完成！</span>
                            </div>
                            <button
                              type="button"
                              className="button button--primary"
                              disabled={isApplyingUpdate}
                              onClick={handleApplyUpdate}
                            >
                              <span>{isApplyingUpdate ? '正在启动安装包...' : '立即重启并安装更新'}</span>
                            </button>
                          </div>
                        )}
                      </div>
                    ) : (
                      <p className="settings-note">该 Release 附件中未找到 .exe 安装包，请点击上方按钮前往网页下载。</p>
                    )}
                  </div>
                )}
              </section>

              <section className="settings-section">
                <div className="settings-section__heading">
                  <ShieldCheck size={16} />
                  <div>
                    <strong>关于安装器与软件信息</strong>
                    <small>系统集成、开机启动与目录规范</small>
                  </div>
                </div>
                <div className="about-info-grid">
                  <div className="about-info-card">
                    <strong>📦 Inno Setup 纯净安装</strong>
                    <p>安装于用户本地目录（<code>%LOCALAPPDATA%\Programs\片刻收纳</code>），免 UAC 管理员提权，在线覆盖更新无感平滑。</p>
                  </div>
                  <div className="about-info-card">
                    <strong>⚡ 开机自启与快捷方式</strong>
                    <p>支持安装向导一键注册开机自启（注册表 Run 项）、开始菜单与桌面快捷方式。</p>
                  </div>
                  <div className="about-info-card">
                    <strong>🛡️ 用户数据安全保障</strong>
                    <p>更新或卸载时完整保护用户配置、分类规则、便签与收纳历史，不会丢失个性化数据。</p>
                  </div>
                </div>
              </section>
            </div>
          )}
        </div>
      </div>
      <footer className="modal__actions modal__actions--split"><button type="button" className="button button--quiet" onClick={() => { if (window.confirm('恢复全部设置为默认值？自定义分区、分类规则和布局都会被清除。')) onReset(); }}><ArrowCounterClockwise size={15} />恢复全部设置</button><button type="button" className="button button--primary" onClick={onClose}>完成</button></footer>
    </Modal>
  );
}
