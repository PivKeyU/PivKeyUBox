import { useRef } from 'react';
import {
  Archive, Book, Briefcase, Code, FileText, Folder, Heart, Images, MusicNote, RocketLaunch, Star, Stack, UploadSimple, Video,
} from '@phosphor-icons/react';
import type { Icon } from '@phosphor-icons/react';
import folderMascot from '../assets/icons/pivkey-folder.png?inline';
import shortcutMascot from '../assets/icons/pivkey-shortcut.png?inline';
import { CHARACTER_ICONS } from '../data/characterIcons';

interface BuiltinIcon {
  name: string;
  component: Icon;
}

// 内置图标：name 为同步契约名，component 对应 @phosphor-icons/react 导出
const BUILTIN_ICONS: BuiltinIcon[] = [
  { name: 'folder', component: Folder },
  { name: 'stack', component: Stack },
  { name: 'images', component: Images },
  { name: 'music-note', component: MusicNote },
  { name: 'file-text', component: FileText },
  { name: 'video', component: Video },
  { name: 'archive', component: Archive },
  { name: 'book', component: Book },
  { name: 'star', component: Star },
  { name: 'heart', component: Heart },
  { name: 'briefcase', component: Briefcase },
  { name: 'code', component: Code },
  { name: 'rocket', component: RocketLaunch },
];

const PRESET_IMAGES = [
  { name: 'pivkey-folder', label: '软萌文件夹', source: folderMascot },
  { name: 'pivkey-shortcut', label: '软萌快捷方式', source: shortcutMascot },
] as const;

interface IconPickerProps {
  value: string;
  onChange: (icon: string) => void;
  defaultValue?: string;
}

// 读取图片文件 → canvas 64×64 等比 cover 裁剪（透明背景）→ PNG data URL
function fileToDataUrl(file: File, onDone: (dataUrl: string) => void): void {
  const reader = new FileReader();
  reader.onload = () => {
    const result = reader.result;
    if (typeof result !== 'string') return;
    const image = new Image();
    image.onload = () => {
      try {
        const size = 64;
        const scale = Math.max(size / image.width, size / image.height);
        const sourceWidth = size / scale;
        const sourceHeight = size / scale;
        const canvas = document.createElement('canvas');
        canvas.width = size;
        canvas.height = size;
        const context = canvas.getContext('2d');
        if (!context) return;
        context.drawImage(
          image,
          (image.width - sourceWidth) / 2,
          (image.height - sourceHeight) / 2,
          sourceWidth,
          sourceHeight,
          0,
          0,
          size,
          size,
        );
        onDone(canvas.toDataURL('image/png'));
      } catch (error) {
        console.error('自定义图标处理失败', error);
      }
    };
    image.onerror = () => console.error('自定义图标加载失败');
    image.src = result;
  };
  reader.onerror = () => console.error('自定义图标读取失败');
  reader.readAsDataURL(file);
}

export function IconPicker({ value, onChange, defaultValue = 'folder' }: IconPickerProps) {
  const fileRef = useRef<HTMLInputElement>(null);
  const isCustom = value.startsWith('data:');

  return (
    <div className="icon-picker">
      <div className="icon-picker__grid">
        {CHARACTER_ICONS.map(({ name, label, source }) => {
          const selected = value === name;
          return (
            <button
              key={name}
              type="button"
              className={selected ? 'is-active icon-picker__character' : 'icon-picker__character'}
              title={`吉伊姿势：${label}`}
              aria-label={`吉伊姿势：${label}`}
              aria-pressed={selected}
              onClick={() => onChange(name)}
            >
              <img src={source} alt="" aria-hidden="true" />
            </button>
          );
        })}
        {BUILTIN_ICONS.map(({ name, component: IconComponent }) => {
          const selected = value === name;
          return (
            <button
              key={name}
              type="button"
              className={selected ? 'is-active' : undefined}
              title={name}
              aria-label={`内置图标 ${name}`}
              aria-pressed={selected}
              onClick={() => onChange(name)}
            >
              <IconComponent size={24} weight={selected ? 'fill' : 'regular'} />
            </button>
          );
        })}
        {PRESET_IMAGES.map(({ name, label, source }) => {
          const selected = value === source;
          return (
            <button
              key={name}
              type="button"
              className={selected ? 'is-active icon-picker__preset' : 'icon-picker__preset'}
              title={label}
              aria-label={label}
              aria-pressed={selected}
              onClick={() => onChange(source)}
            >
              <img src={source} alt="" aria-hidden="true" />
            </button>
          );
        })}
        <button
          type="button"
          className={isCustom ? 'icon-picker__upload is-active' : 'icon-picker__upload'}
          title="上传自定义图标"
          aria-label="上传自定义图标"
          onClick={() => fileRef.current?.click()}
        >
          <UploadSimple size={24} />
        </button>
        <input
          ref={fileRef}
          type="file"
          accept="image/*"
          hidden
          onChange={(event) => {
            const file = event.target.files?.[0];
            event.target.value = '';
            if (file) fileToDataUrl(file, onChange);
          }}
        />
      </div>
      {isCustom && (
        <div className="icon-picker__custom">
          <img src={value} alt="当前自定义图标" />
          <span>自定义图标</span>
          <button type="button" className="icon-picker__reset" onClick={() => onChange(defaultValue)}>恢复内置</button>
        </div>
      )}
    </div>
  );
}
