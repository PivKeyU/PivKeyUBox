import { useState } from 'react';
import { Check, FolderPlus } from '@phosphor-icons/react';
import { normalizeCategoryName } from '../services/categoryValidation';
import type { Category } from '../types';
import { IconPicker } from './IconPicker';
import { Modal } from './Modal';
import { defaultCategoryIcon } from '../data/characterIcons';

const colors = ['#3478f6', '#34a853', '#ff9f0a', '#ff375f', '#af52de', '#18a999', '#8e8e93'];

interface CategoryModalProps {
  existingNames: string[];
  onClose: () => void;
  onCreate: (category: Category) => void;
}

export function CategoryModal({ existingNames, onClose, onCreate }: CategoryModalProps) {
  const [name, setName] = useState('');
  const [extensions, setExtensions] = useState('');
  const [color, setColor] = useState(colors[0]);
  const [acceptsFolders, setAcceptsFolders] = useState(false);
  const [icon, setIcon] = useState<string>(defaultCategoryIcon('custom'));
  const [error, setError] = useState('');

  const submit = () => {
    try {
      const normalizedName = normalizeCategoryName(name, existingNames);
      setError('');
      onCreate({
        id: `custom-${Date.now().toString(36)}`,
        name: normalizedName,
        icon,
        color,
        extensions: [...new Set(extensions.split(/[,，\s]+/).map((value) => value.replace(/^\./, '').toLowerCase()).filter(Boolean))],
        acceptsFolders,
      });
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : '分类名称无效');
    }
  };

  return (
    <Modal title="新建分类" description="为一组文件创建专属收纳规则" onClose={onClose} className="modal--small">
      <form className="form-stack" onSubmit={(event) => { event.preventDefault(); submit(); }}>
        <label className="field"><span>分类名称</span><input autoFocus value={name} maxLength={20} placeholder="例如：设计源文件" aria-invalid={Boolean(error)} onChange={(event) => { setName(event.target.value); setError(''); }} />{error && <small role="alert">{error}</small>}</label>
        <label className="field"><span>文件扩展名</span><input value={extensions} placeholder="psd, ai, sketch" onChange={(event) => setExtensions(event.target.value)} /><small>用逗号或空格分隔，不需要输入句点</small></label>
        <fieldset className="color-field"><legend>标记颜色</legend><div>{colors.map((candidate) => <button key={candidate} type="button" aria-label={`选择颜色 ${candidate}`} className={candidate === color ? 'is-selected' : ''} style={{ backgroundColor: candidate }} onClick={() => setColor(candidate)}>{candidate === color && <Check size={14} />}</button>)}</div></fieldset>
        <label className="field"><span>分类图标</span><IconPicker value={icon} onChange={setIcon} /></label>
        <label className="switch-row"><span><strong>接收文件夹</strong><small>把桌面文件夹也归入此分类</small></span><input type="checkbox" checked={acceptsFolders} onChange={(event) => setAcceptsFolders(event.target.checked)} /><i /></label>
        <footer className="modal__actions"><button type="button" className="button button--secondary" onClick={onClose}>取消</button><button type="submit" className="button button--primary" disabled={!name.trim()}><FolderPlus size={16} />创建分类</button></footer>
      </form>
    </Modal>
  );
}
