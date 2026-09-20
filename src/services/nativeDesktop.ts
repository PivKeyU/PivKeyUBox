import { demoItems } from '../data/defaults';
import type { AppPreferences, Category, DesktopItem, NoteData, OrganizationMove, ScanResult } from '../types';
import { normalizeItemLayouts, normalizePreferences } from '../state/preferences';
import type { PanelItemLayouts } from '../state/preferences';
import type { FolderAlignment, LayoutPreset, PanelSortMode, PanelViewMode, ZonePositions, ZoneSizes } from '../state/layout';
import { classifyKind, getExtension, makeItemId, matchCategory } from './classifier';

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage: (message: string) => void;
        addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void;
      };
    };
  }
}

interface NativeDirectoryEntry { entry: string; type: 'FILE' | 'DIRECTORY'; }
interface NativeStats { size?: number; modifiedAt?: number | string; }
interface NativeScannedEntry extends NativeDirectoryEntry, NativeStats { iconUrl?: string; hidden?: boolean; }
interface NativeResponse { id?: string; result?: unknown; error?: string; event?: string; value?: unknown; }
export interface OrganizationHistoryEntry { source: string; destination: string; name: string; }
export interface OrganizationRestoreResult extends OrganizationHistoryEntry { restoredTo?: string; status: 'restored' | 'failed'; error?: string; }
export interface OrganizationPreviewRow { name: string; source: string; destination: string; category: string; categoryId: string; ruleMatched: boolean; }
export interface HistorySummary { undoCount: number; redoCount: number; batches: Array<{ index: number; count: number }>; }

let initialized = false;
let sequence = 0;
const pending = new Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void }>();
function nativeWebview(): NonNullable<Window['chrome']>['webview'] | undefined {
  return typeof window !== 'undefined' ? window.chrome?.webview : undefined;
}

export function isNativeRuntime(): boolean {
  return Boolean(nativeWebview());
}

export function isSettingsRuntime(): boolean {
  return typeof window !== 'undefined' && new URLSearchParams(window.location.search).get('view') === 'settings';
}

export function initializeNative(): void {
  if (!isNativeRuntime() || initialized) return;
  nativeWebview()?.addEventListener('message', (event) => {
    const response = (event.data ?? {}) as NativeResponse;
    if (response.event === 'interactionChanged') {
      window.dispatchEvent(new CustomEvent('pivkey-interaction', { detail: Boolean(response.value) }));
      return;
    }
    if (response.event === 'trayCommand') {
      window.dispatchEvent(new CustomEvent('pivkey-tray-command', { detail: String(response.value ?? '') }));
      return;
    }
    if (response.event === 'gestureCancelled') {
      window.dispatchEvent(new CustomEvent('pivkey-gesture-cancelled'));
      return;
    }
    if (response.event === 'panelGeometry') {
      window.dispatchEvent(new CustomEvent('pivkey-panel-geometry', { detail: response.value }));
      return;
    }
    if (response.event === 'panelRename') {
      window.dispatchEvent(new CustomEvent('pivkey-panel-rename', { detail: response.value }));
      return;
    }
    if (response.event === 'panelPin') {
      window.dispatchEvent(new CustomEvent('pivkey-panel-pin', { detail: response.value }));
      return;
    }
    if (response.event === 'panelCollapse') {
      window.dispatchEvent(new CustomEvent('pivkey-panel-collapse', { detail: response.value }));
      return;
    }
    if (response.event === 'panelRefresh') {
      window.dispatchEvent(new CustomEvent('pivkey-panel-refresh'));
      return;
    }
    if (response.event === 'desktopChanged') {
      window.dispatchEvent(new CustomEvent('pivkey-desktop-changed'));
      return;
    }
    if (response.event === 'settingsChanged') {
      window.dispatchEvent(new CustomEvent('pivkey-settings-changed'));
      return;
    }
    if (response.event === 'panelView') {
      window.dispatchEvent(new CustomEvent('pivkey-panel-view', { detail: response.value }));
      return;
    }
    if (response.event === 'panelSort') {
      window.dispatchEvent(new CustomEvent('pivkey-panel-sort', { detail: response.value }));
      return;
    }
    if (response.event === 'panelItemSize') {
      window.dispatchEvent(new CustomEvent('pivkey-panel-item-size', { detail: response.value }));
      return;
    }
    if (response.event === 'operationError') {
      window.dispatchEvent(new CustomEvent('pivkey-operation-error', { detail: String(response.value ?? '文件操作失败') }));
      return;
    }
    if (!response.id) return;
    const request = pending.get(response.id);
    if (!request) return;
    pending.delete(response.id);
    if (response.error) request.reject(new Error(response.error));
    else request.resolve(response.result);
  });
  initialized = true;
}

function rpc<T>(method: string, args: Record<string, unknown> = {}): Promise<T> {
  initializeNative();
  const webview = nativeWebview();
  if (!webview) return Promise.reject(new Error('当前不是 Windows 桌面模式'));
  const id = `rpc-${Date.now().toString(36)}-${(sequence += 1).toString(36)}`;
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (value: unknown) => void, reject });
    webview.postMessage(JSON.stringify({ id, method, args }));
    window.setTimeout(() => {
      if (!pending.has(id)) return;
      pending.delete(id);
      reject(new Error(`${method} 请求超时`));
    }, 15_000);
  });
}

function joinPath(...parts: string[]): string {
  const separator = parts[0]?.includes('\\') ? '\\' : '/';
  return parts.map((part, index) => index === 0 ? part.replace(/[\\/]$/, '') : part.replace(/^[\\/]|[\\/]$/g, '')).join(separator);
}

function toIsoDate(value: unknown): string {
  const date = new Date(typeof value === 'number' || typeof value === 'string' ? value : Date.now());
  return Number.isNaN(date.getTime()) ? new Date().toISOString() : date.toISOString();
}

export async function scanDesktop(categories: Category[], showHiddenFiles = false, refreshIcons = false): Promise<ScanResult> {
  if (!isNativeRuntime()) {
    await new Promise((resolve) => setTimeout(resolve, 480));
    return { desktopPath: 'C:\\Users\\You\\Desktop', items: demoItems.map((item) => ({ ...item })), scannedAt: new Date().toISOString(), isNative: false };
  }

  const desktopPath = await rpc<string>('getDesktopPath');
  const entries = await rpc<NativeScannedEntry[]>('scanDirectory', { path: desktopPath, refreshIcons });
  const visibleEntries = entries.filter(({ entry, hidden }) => entry !== '片刻收纳' && entry !== '轻屿收纳' && (showHiddenFiles || (!hidden && !entry.startsWith('.'))));

  const toDesktopItem = (entry: NativeScannedEntry, parentPath: string, managed = false, forcedCategoryId?: string) => {
    const { entry: name, type, size = 0, modifiedAt: rawModifiedAt, iconUrl } = entry;
    const path = joinPath(parentPath, name);
    const isDirectory = type === 'DIRECTORY';
    const extension = isDirectory ? '' : getExtension(name);
    const modifiedAt = toIsoDate(rawModifiedAt);
    const kind = classifyKind(extension, isDirectory);
    return { id: makeItemId(path), name, path, extension, kind, size, modifiedAt, categoryId: forcedCategoryId ?? matchCategory(extension, isDirectory, categories), managed, iconUrl } satisfies DesktopItem;
  };

  const items = visibleEntries.map((entry) => toDesktopItem(entry, desktopPath));
  for (const managedName of ['片刻收纳', '轻屿收纳']) {
    const managedPath = joinPath(desktopPath, managedName);
    try {
      const categoryFolders = await rpc<NativeDirectoryEntry[]>('readDirectory', { path: managedPath });
      for (const folder of categoryFolders.filter(({ type }) => type === 'DIRECTORY')) {
        const category = categories.find((candidate) => candidate.name === folder.entry);
        const folderPath = joinPath(managedPath, folder.entry);
        const managedEntries = await rpc<NativeScannedEntry[]>('scanDirectory', { path: folderPath, refreshIcons: false });
        const managedItems = managedEntries.filter(({ entry, hidden }) => showHiddenFiles || (!hidden && !entry.startsWith('.'))).map((entry) => toDesktopItem(entry, folderPath, true, category?.id));
        items.push(...managedItems);
      }
    } catch {
      // A managed root is optional; the new root is created on first organization.
    }
  }
  // 文件夹门户：镜像真实目录内容，managed=true 永不参与自动收纳，也保证面板显示
  for (const category of categories) {
    const portalPath = category.portalPath?.trim();
    if (!portalPath) continue;
    try {
      const portalEntries = await rpc<NativeScannedEntry[]>('scanDirectory', { path: portalPath, refreshIcons: false });
      const portalItems = portalEntries
        .filter(({ entry, hidden }) => entry !== '片刻收纳' && entry !== '轻屿收纳' && (showHiddenFiles || (!hidden && !entry.startsWith('.'))))
        .map((entry) => toDesktopItem(entry, portalPath, true, category.id));
      items.push(...portalItems);
    } catch {
      // 门户目录不存在或扫描失败时跳过，不报错
    }
  }
  return { desktopPath, items, scannedAt: new Date().toISOString(), isNative: true };
}

async function ensureDirectory(path: string): Promise<void> {
  await rpc('createDirectory', { path });
}

async function pathExists(path: string): Promise<boolean> {
  try { await rpc('getStats', { path }); return true; } catch { return false; }
}

async function uniqueDestination(destination: string): Promise<string> {
  if (!(await pathExists(destination))) return destination;
  const extensionIndex = destination.lastIndexOf('.');
  const separatorIndex = Math.max(destination.lastIndexOf('\\'), destination.lastIndexOf('/'));
  const hasExtension = extensionIndex > separatorIndex;
  const base = hasExtension ? destination.slice(0, extensionIndex) : destination;
  const extension = hasExtension ? destination.slice(extensionIndex) : '';
  for (let suffix = 1; suffix <= 999; suffix += 1) {
    const candidate = `${base} (${suffix})${extension}`;
    if (!(await pathExists(candidate))) return candidate;
  }
  throw new Error('目标文件夹中存在过多同名文件');
}

export async function executeOrganization(moves: OrganizationMove[], categories: Category[], desktopPath: string): Promise<OrganizationMove[]> {
  if (!isNativeRuntime()) { await new Promise((resolve) => setTimeout(resolve, 700)); return moves.map((move) => ({ ...move, status: 'moved' })); }
  const rootPath = joinPath(desktopPath, '片刻收纳');
  await ensureDirectory(rootPath);
  const results: OrganizationMove[] = [];
  for (const move of moves) {
    const category = categories.find((candidate) => candidate.id === move.categoryId);
    const folderPath = joinPath(rootPath, category?.name ?? '其他');
    await ensureDirectory(folderPath);
    try {
      const destination = await uniqueDestination(joinPath(folderPath, move.item.name));
      await rpc('move', { source: move.source, destination });
      results.push({ ...move, destination, status: 'moved' });
    } catch (error) {
      results.push({ ...move, status: 'failed', error: error instanceof Error ? error.message : '移动失败' });
    }
  }
  return results;
}

export async function renameManagedCategory(desktopPath: string, oldName: string, newName: string): Promise<void> {
  if (!isNativeRuntime() || oldName === newName) return;
  for (const rootName of ['片刻收纳', '轻屿收纳']) {
    const rootPath = joinPath(desktopPath, rootName);
    const source = joinPath(rootPath, oldName);
    const destination = joinPath(rootPath, newName);
    if (!(await pathExists(source))) continue;
    if (await pathExists(destination)) throw new Error(`已存在名为“${newName}”的收纳文件夹`);
    await rpc('move', { source, destination });
  }
}

export async function restoreOrganization(entries: OrganizationHistoryEntry[]): Promise<OrganizationRestoreResult[]> {
  const results: OrganizationRestoreResult[] = [];
  for (const entry of [...entries].reverse()) {
    try {
      if (!(await pathExists(entry.destination))) throw new Error('原收纳文件已不存在');
      const restoredTo = await uniqueDestination(entry.source);
      await rpc('move', { source: entry.destination, destination: restoredTo });
      results.push({ ...entry, restoredTo, status: 'restored' });
    } catch (error) {
      results.push({ ...entry, status: 'failed', error: error instanceof Error ? error.message : '撤销失败' });
    }
  }
  return results;
}

// ===== 宿主配置（config.json RPC）：与 localStorage 各键一一对应 =====
export interface NativeConfig {
  preferences: AppPreferences;
  positions: ZonePositions;
  sizes: ZoneSizes;
  collapsed: string[];
  pinned: string[];
  viewModes: Record<string, PanelViewMode>;
  sortModes: Record<string, PanelSortMode>;
  itemLayouts: PanelItemLayouts;
  adaptivePreset: LayoutPreset | 'manual';
  folderAlignment: FolderAlignment;
  notes?: NoteData[];
  // 由 useDesktopScan 写入，设置窗口不参与管理；原生化后不再使用，保留字段以完整迁移
  organizationHistory?: OrganizationHistoryEntry[];
}

// 设置窗口需要加载/保存的字段（不含 organizationHistory）
export type NativeSettingsConfig = Partial<Omit<NativeConfig, 'organizationHistory'>>;

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function parseStringArray(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) return undefined;
  return [...new Set(value.filter((item): item is string => typeof item === 'string'))];
}

function parseZonePositions(value: unknown): ZonePositions | undefined {
  const record = asRecord(value);
  if (!record) return undefined;
  const result: ZonePositions = {};
  for (const [id, position] of Object.entries(record)) {
    const point = asRecord(position);
    if (!point || typeof point.x !== 'number' || !Number.isFinite(point.x) || typeof point.y !== 'number' || !Number.isFinite(point.y)) continue;
    result[id] = { x: point.x, y: point.y };
  }
  return result;
}

function parseZoneSizes(value: unknown): ZoneSizes | undefined {
  const record = asRecord(value);
  if (!record) return undefined;
  const result: ZoneSizes = {};
  for (const [id, size] of Object.entries(record)) {
    const zone = asRecord(size);
    if (!zone || typeof zone.width !== 'number' || !Number.isFinite(zone.width) || typeof zone.height !== 'number' || !Number.isFinite(zone.height)) continue;
    result[id] = { width: zone.width, height: zone.height };
  }
  return result;
}

function parseViewModes(value: unknown): Record<string, PanelViewMode> | undefined {
  const record = asRecord(value);
  if (!record) return undefined;
  const result: Record<string, PanelViewMode> = {};
  for (const [id, mode] of Object.entries(record)) {
    if (mode === 'grid' || mode === 'list') result[id] = mode;
  }
  return result;
}

function parseSortModes(value: unknown): Record<string, PanelSortMode> | undefined {
  const record = asRecord(value);
  if (!record) return undefined;
  const result: Record<string, PanelSortMode> = {};
  for (const [id, mode] of Object.entries(record)) {
    if (mode === 'name' || mode === 'modified' || mode === 'size') result[id] = mode;
  }
  return result;
}

function parseItemLayouts(value: unknown): PanelItemLayouts | undefined {
  const record = asRecord(value);
  if (!record) return undefined;
  return normalizeItemLayouts(record);
}

function parseNotes(value: unknown): NoteData[] | undefined {
  if (!Array.isArray(value)) return undefined;
  return value.filter((item): item is NoteData => {
    return Boolean(
      item && typeof item === 'object' &&
      typeof (item as NoteData).id === 'string'
    );
  }).map((item) => ({
    id: String(item.id),
    title: String(item.title || '便签'),
    mode: item.mode === 'note' ? 'note' : 'todo',
    color: String(item.color || 'default'),
    x: typeof item.x === 'number' ? item.x : 240,
    y: typeof item.y === 'number' ? item.y : 160,
    width: typeof item.width === 'number' ? item.width : 260,
    height: typeof item.height === 'number' ? item.height : 340,
    pinned: Boolean(item.pinned),
    collapsed: Boolean(item.collapsed),
    noteContent: typeof item.noteContent === 'string' ? item.noteContent : '',
    todos: Array.isArray(item.todos) ? item.todos.map((t) => ({
      id: String(t?.id || Math.random().toString(36).slice(2)),
      text: String(t?.text || ''),
      done: Boolean(t?.done),
      createdAt: typeof t?.createdAt === 'number' ? t.createdAt : Date.now(),
    })) : [],
  }));
}

const ADAPTIVE_PRESETS: ReadonlyArray<LayoutPreset | 'manual'> = ['balanced', 'grid', 'columns', 'corners', 'right-dock', 'manual'];
const FOLDER_ALIGNMENTS: ReadonlyArray<FolderAlignment> = ['manual', 'left', 'center', 'right'];

// 解析宿主返回的配置：逐字段校验，缺失/类型异常时该字段返回 undefined（调用方回落 localStorage 初始化，不崩溃）
export function parseNativeConfig(raw: unknown): NativeSettingsConfig | null {
  const record = asRecord(raw);
  if (!record) return null;
  const result: NativeSettingsConfig = {};
  if (record.preferences !== undefined) result.preferences = normalizePreferences(record.preferences);
  const positions = parseZonePositions(record.positions);
  if (positions) result.positions = positions;
  const sizes = parseZoneSizes(record.sizes);
  if (sizes) result.sizes = sizes;
  const collapsed = parseStringArray(record.collapsed);
  if (collapsed) result.collapsed = collapsed;
  const pinned = parseStringArray(record.pinned);
  if (pinned) result.pinned = pinned;
  const viewModes = parseViewModes(record.viewModes);
  if (viewModes) result.viewModes = viewModes;
  const sortModes = parseSortModes(record.sortModes);
  if (sortModes) result.sortModes = sortModes;
  const itemLayouts = parseItemLayouts(record.itemLayouts);
  if (itemLayouts) result.itemLayouts = itemLayouts;
  const notes = parseNotes(record.notes);
  if (notes) result.notes = notes;
  if (ADAPTIVE_PRESETS.includes(record.adaptivePreset as LayoutPreset | 'manual')) result.adaptivePreset = record.adaptivePreset as LayoutPreset | 'manual';
  if (FOLDER_ALIGNMENTS.includes(record.folderAlignment as FolderAlignment)) result.folderAlignment = record.folderAlignment as FolderAlignment;
  return result;
}

export const nativeWindow = {
  syncPanels: (panels: Array<{ id: string; name: string; color: string; themeAccent: string; headerSurface?: string; glassOpacity: number; theme: string; compact: boolean; viewMode: 'grid' | 'list'; sortMode: 'name' | 'modified' | 'size'; itemSize: number; iconSize: number; itemGap: number; itemAlignment: 'left' | 'center' | 'right'; itemColumns: number; showLabels: boolean; labelSize: number; x: number; y: number; width: number; height: number; pinned: boolean; collapsed: boolean; readOnly?: boolean; items: Array<{ id: string; name: string; path: string; iconUrl?: string; extension: string; modifiedAt: string; size: number; kind: string; readOnly?: boolean }> }>) => isNativeRuntime() ? rpc<boolean>('syncPanels', { panels }) : Promise.resolve(true),
  setInteractiveRegions: (regions: Array<{ left: number; top: number; right: number; bottom: number; radius?: number }>, full = false, hitRegions = regions, expandInput = false) => isNativeRuntime() ? rpc<boolean>('setInteractiveRegions', { regions, hitRegions, full, expandInput }) : Promise.resolve(true),
  runManagerCommand: (command: string) => isNativeRuntime() ? rpc<boolean>('managerCommand', { command }) : Promise.resolve(true),
  createNote: (mode: 'todo' | 'note', title?: string) => isNativeRuntime() ? rpc<boolean>('createNote', { mode, title }) : Promise.resolve(true),
  deleteNote: (id: string) => isNativeRuntime() ? rpc<boolean>('deleteNote', { id }) : Promise.resolve(true),
  organizationPreview: () => isNativeRuntime() ? rpc<OrganizationPreviewRow[]>('organizationPreview') : Promise.resolve([]),
  historySummary: () => isNativeRuntime() ? rpc<HistorySummary>('historySummary') : Promise.resolve({ undoCount: 0, redoCount: 0, batches: [] }),
  exportConfig: () => isNativeRuntime() ? rpc<string>('exportConfig') : Promise.resolve(''),
  importConfigFile: () => isNativeRuntime() ? rpc<string>('importConfigFile') : Promise.resolve(''),
  closeSettings: () => isNativeRuntime() ? rpc<boolean>('closeSettings') : Promise.resolve(true),
  // 选取文件夹（用户取消返回空字符串）
  pickFolder: () => isNativeRuntime() ? rpc<string>('pickFolder') : Promise.resolve(''),
  // 引用模式：将路径钉选/移入指定分类（不实际移动文件）
  moveIntoCategory: (categoryId: string, paths: string[]) => isNativeRuntime() ? rpc<boolean>('moveIntoCategory', { categoryId, paths }) : Promise.resolve(true),
  // 注册门户文件夹监听（内容变化时通知前端刷新）
  watchPortalFolders: (paths: string[]) => isNativeRuntime() ? rpc<boolean>('watchPortalFolders', { paths }) : Promise.resolve(true),
  // 宿主配置（config.json RPC）：设置窗口/迁移页的配置权威来源
  loadConfig: () => isNativeRuntime() ? rpc<NativeConfig | null | undefined>('loadConfig') : Promise.resolve(null),
  saveConfig: (config: NativeConfig) => isNativeRuntime() ? rpc<boolean>('saveConfig', { config }) : Promise.resolve(true),
  importConfig: (config: NativeConfig) => isNativeRuntime() ? rpc<boolean>('importConfig', { config }) : Promise.resolve(true),
  migrationDone: () => isNativeRuntime() ? rpc<boolean>('migrationDone') : Promise.resolve(true),
  getAppVersion: () => getAppVersionInfo(),
  checkUpdate: (repo?: string) => checkAppUpdate(repo),
  startDownloadUpdate: (downloadUrl: string) => startDownloadAppUpdate(downloadUrl),
  getDownloadProgress: () => getAppUpdateProgress(),
  applyUpdate: (filePath?: string, silent?: boolean) => applyAppUpdate(filePath, silent),
};

export interface AppVersionInfo {
  version: string;
  defaultRepo: string;
}

export interface UpdateCheckResult {
  hasUpdate: boolean;
  currentVersion: string;
  latestVersion: string;
  tagName: string;
  releaseName: string;
  releaseNotes: string;
  publishedAt: string;
  downloadUrl: string;
  assetName: string;
  assetSize: number;
  htmlUrl: string;
  repo: string;
}

export interface UpdateDownloadProgress {
  isDownloading: boolean;
  isDownloaded: boolean;
  percentage: number;
  bytesReceived: number;
  totalBytes: number;
  filePath?: string;
  error?: string;
}

export async function getAppVersionInfo(): Promise<AppVersionInfo> {
  if (!isNativeRuntime()) {
    return { version: '0.1.1 (Web预览)', defaultRepo: 'PivKeyU/PivKeyUBox' };
  }
  return rpc<AppVersionInfo>('getAppVersion');
}

export async function checkAppUpdate(repo?: string): Promise<UpdateCheckResult> {
  if (!isNativeRuntime()) {
    await new Promise((resolve) => setTimeout(resolve, 800));
    return {
      hasUpdate: true,
      currentVersion: '0.1.1',
      latestVersion: '0.1.1',
      tagName: 'v0.1.1',
      releaseName: 'v0.1.1 体验优化版',
      releaseNotes: '1. 支持 Windows 安装包与更新检查\n2. 优化桌面层挂载平滑度\n3. 提升托盘唤起响应性能',
      publishedAt: new Date().toISOString(),
      downloadUrl: 'https://example.com/PivKeyUBox-Setup-v0.1.1.exe',
      assetName: 'PivKeyUBox-Setup-v0.1.1.exe',
      assetSize: 8350000,
      htmlUrl: 'https://github.com/PivKeyU/PivKeyUBox/releases',
      repo: repo || 'PivKeyU/PivKeyUBox',
    };
  }
  return rpc<UpdateCheckResult>('checkUpdate', { repo });
}

export async function startDownloadAppUpdate(downloadUrl: string): Promise<boolean> {
  if (!isNativeRuntime()) {
    return true;
  }
  return rpc<boolean>('startDownloadUpdate', { downloadUrl });
}

export async function getAppUpdateProgress(): Promise<UpdateDownloadProgress> {
  if (!isNativeRuntime()) {
    return {
      isDownloading: false,
      isDownloaded: true,
      percentage: 100,
      bytesReceived: 8350000,
      totalBytes: 8350000,
    };
  }
  return rpc<UpdateDownloadProgress>('getDownloadProgress');
}

export async function applyAppUpdate(filePath?: string, silent = false): Promise<boolean> {
  if (!isNativeRuntime()) {
    return true;
  }
  return rpc<boolean>('applyUpdate', { filePath, silent });
}
