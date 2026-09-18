import type { AppPreferences, Category, DesktopItem } from '../types';
import { defaultCategoryIcon } from './characterIcons';

export const defaultCategories: Category[] = [
  { id: 'shortcuts', name: '快捷方式', icon: defaultCategoryIcon('shortcuts'), color: '#3478f6', extensions: ['lnk', 'url', 'appref-ms'] },
  { id: 'documents', name: '工作文档', icon: defaultCategoryIcon('documents'), color: '#a9cbe4', extensions: ['doc', 'docx', 'pdf', 'txt', 'rtf', 'odt', 'md', 'pages'] },
  { id: 'sheets', name: '表格数据', icon: defaultCategoryIcon('sheets'), color: '#9ecfc0', extensions: ['xls', 'xlsx', 'csv', 'numbers', 'ods'] },
  { id: 'slides', name: '演示文稿', icon: defaultCategoryIcon('slides'), color: '#f4c968', extensions: ['ppt', 'pptx', 'key', 'odp'] },
  { id: 'images', name: '图片素材', icon: defaultCategoryIcon('images'), color: '#c5b7df', extensions: ['jpg', 'jpeg', 'png', 'gif', 'webp', 'svg', 'heic', 'bmp'] },
  { id: 'media', name: '影音文件', icon: defaultCategoryIcon('media'), color: '#5856d6', extensions: ['mp3', 'wav', 'm4a', 'flac', 'mp4', 'mov', 'mkv', 'avi'] },
  { id: 'archives', name: '压缩文件', icon: defaultCategoryIcon('archives'), color: '#dfb07b', extensions: ['zip', 'rar', '7z', 'tar', 'gz'] },
  { id: 'folders', name: '文件夹', icon: defaultCategoryIcon('folders'), color: '#9ecfc0', extensions: [], acceptsFolders: true },
];

const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

export const demoItems: DesktopItem[] = [
  { id: 'demo-1', name: '季度产品复盘.pdf', path: 'C:\\Users\\You\\Desktop\\季度产品复盘.pdf', extension: 'pdf', kind: 'document', size: 4_823_040, modifiedAt: minutesAgo(8), categoryId: 'documents' },
  { id: 'demo-2', name: '项目排期.xlsx', path: 'C:\\Users\\You\\Desktop\\项目排期.xlsx', extension: 'xlsx', kind: 'document', size: 832_512, modifiedAt: minutesAgo(24), categoryId: 'sheets' },
  { id: 'demo-3', name: '灵感参考.png', path: 'C:\\Users\\You\\Desktop\\灵感参考.png', extension: 'png', kind: 'image', size: 2_346_120, modifiedAt: minutesAgo(46), categoryId: 'images' },
  { id: 'demo-4', name: 'Figma.lnk', path: 'C:\\Users\\You\\Desktop\\Figma.lnk', extension: 'lnk', kind: 'shortcut', size: 2_048, modifiedAt: minutesAgo(60), categoryId: 'shortcuts' },
  { id: 'demo-5', name: '客户资料', path: 'C:\\Users\\You\\Desktop\\客户资料', extension: '', kind: 'folder', size: 0, modifiedAt: minutesAgo(77), categoryId: 'folders' },
  { id: 'demo-6', name: '品牌手册.pdf', path: 'C:\\Users\\You\\Desktop\\品牌手册.pdf', extension: 'pdf', kind: 'document', size: 8_734_720, modifiedAt: minutesAgo(95), categoryId: 'documents' },
  { id: 'demo-7', name: '会议录音.m4a', path: 'C:\\Users\\You\\Desktop\\会议录音.m4a', extension: 'm4a', kind: 'media', size: 14_680_064, modifiedAt: minutesAgo(121), categoryId: 'media' },
  { id: 'demo-8', name: '交付文件.zip', path: 'C:\\Users\\You\\Desktop\\交付文件.zip', extension: 'zip', kind: 'archive', size: 24_961_024, modifiedAt: minutesAgo(180), categoryId: 'archives' },
  { id: 'demo-9', name: '市场方案.pptx', path: 'C:\\Users\\You\\Desktop\\市场方案.pptx', extension: 'pptx', kind: 'document', size: 6_291_456, modifiedAt: minutesAgo(250), categoryId: 'slides' },
  { id: 'demo-10', name: 'Notion.url', path: 'C:\\Users\\You\\Desktop\\Notion.url', extension: 'url', kind: 'shortcut', size: 1_024, modifiedAt: minutesAgo(320), categoryId: 'shortcuts' },
  { id: 'demo-11', name: '社媒封面.webp', path: 'C:\\Users\\You\\Desktop\\社媒封面.webp', extension: 'webp', kind: 'image', size: 1_728_512, modifiedAt: minutesAgo(440), categoryId: 'images' },
  { id: 'demo-12', name: '待归档', path: 'C:\\Users\\You\\Desktop\\待归档', extension: '', kind: 'folder', size: 0, modifiedAt: minutesAgo(720), categoryId: 'folders' },
];

export const defaultPreferences: AppPreferences = {
  categories: defaultCategories,
  automaticScan: false,
  automaticOrganize: false,
  organizeDelaySeconds: 10,
  organizeMode: 'move',
  magicColor: false,
  referencePins: {},
  showHiddenFiles: false,
  compactView: false,
  capsuleMode: false,
  noteCapsuleMode: false,
  desktopContextMenu: true,
  glassOpacity: 88,
  theme: 'light',
  colorScheme: 'white',
  customColor: '#3478f6',
  customSurfaceColor: '#ffffff',
  uiScale: 100,
  showExtensions: false,
  labelPosition: 'bottom',
  autoHide: false,
  autoHideDelaySeconds: 2,
  rules: [],
  favorites: [],
  recentItems: [],
  pinnedItems: [],
  workspaces: [],
  activeWorkspaceId: '',
};
