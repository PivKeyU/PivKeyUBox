export type ItemKind = 'shortcut' | 'folder' | 'document' | 'image' | 'media' | 'archive' | 'other';

export interface DesktopItem {
  id: string;
  name: string;
  path: string;
  extension: string;
  kind: ItemKind;
  size: number;
  modifiedAt: string;
  categoryId: string;
  managed?: boolean;
  iconUrl?: string;
}

export interface Category {
  id: string;
  name: string;
  icon: string;
  color: string;
  extensions: string[];
  acceptsFolders?: boolean;
  portalPath?: string;
}

export type PanelItemAlignment = 'left' | 'center' | 'right';

export interface PanelItemLayout {
  itemSize: number;
  iconSize: number;
  gap: number;
  alignment: PanelItemAlignment;
  columns: number;
  showLabels: boolean;
  labelSize: number;
}

export type OrganizationRuleMatch = 'extension' | 'name' | 'path';

export interface OrganizationRule {
  id: string;
  name: string;
  enabled: boolean;
  match: OrganizationRuleMatch;
  pattern: string;
  categoryId: string;
  priority: number;
}

export interface WorkspacePreset {
  id: string;
  name: string;
  positions: Record<string, { x: number; y: number }>;
  sizes: Record<string, { width: number; height: number }>;
  collapsed: string[];
  pinned: string[];
  viewModes: Record<string, 'grid' | 'list'>;
  sortModes: Record<string, 'name' | 'modified' | 'size'>;
  itemLayouts: Record<string, PanelItemLayout>;
  adaptivePreset: 'manual' | 'balanced' | 'grid' | 'columns' | 'corners' | 'right-dock';
  folderAlignment: 'manual' | 'left' | 'center' | 'right';
}

export interface OrganizationMove {
  item: DesktopItem;
  source: string;
  destination: string;
  categoryId: string;
  status: 'pending' | 'moved' | 'failed';
  error?: string;
}

export interface ScanResult {
  desktopPath: string;
  items: DesktopItem[];
  scannedAt: string;
  isNative: boolean;
}

export interface AppPreferences {
  categories: Category[];
  automaticScan: boolean;
  automaticOrganize: boolean;
  organizeDelaySeconds: number;
  organizeMode: 'move' | 'reference';
  magicColor: boolean;
  referencePins: Record<string, string[]>;
  showHiddenFiles: boolean;
  compactView: boolean;
  capsuleMode: boolean;
  noteCapsuleMode?: boolean;
  desktopContextMenu?: boolean;
  glassOpacity: number;
  theme: 'light' | 'dark' | 'system';
  colorScheme: 'white' | 'warm' | 'ink' | 'forest' | 'rose' | 'custom';
  customColor: string;
  customSurfaceColor?: string;
  uiScale: number;
  showExtensions: boolean;
  labelPosition: 'bottom' | 'right';
  autoHide: boolean;
  autoHideDelaySeconds: number;
  rules: OrganizationRule[];
  favorites: string[];
  recentItems: string[];
  pinnedItems: string[];
  workspaces: WorkspacePreset[];
  activeWorkspaceId: string;
}

export interface TodoItem {
  id: string;
  text: string;
  done: boolean;
  createdAt: number;
}

export interface NoteData {
  id: string;
  title: string;
  mode: 'todo' | 'note';
  color: string;
  x: number;
  y: number;
  width: number;
  height: number;
  pinned: boolean;
  collapsed: boolean;
  noteContent: string;
  todos: TodoItem[];
}
