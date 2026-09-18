import { CheckCircle, Info, X } from '@phosphor-icons/react';
import { useEffect, useRef, useState } from 'react';

interface ToastProps { message: string; action?: string; onAction?: () => void; onClose: () => void; type?: 'success' | 'info' }

export function Toast({ message, action, onAction, onClose, type = 'success' }: ToastProps) {
  const Icon = type === 'success' ? CheckCircle : Info;
  const [closing, setClosing] = useState(false);
  const onCloseRef = useRef(onClose);
  useEffect(() => { onCloseRef.current = onClose; });

  // 自动消失：先播放退场动画，再通知父组件卸载
  useEffect(() => {
    const timer = window.setTimeout(() => setClosing(true), 2400);
    return () => window.clearTimeout(timer);
  }, []);
  useEffect(() => {
    if (!closing) return undefined;
    const timer = window.setTimeout(() => onCloseRef.current(), 180);
    return () => window.clearTimeout(timer);
  }, [closing]);

  return <div className={`toast toast--${type} ${closing ? 'is-closing' : ''}`} role="status"><Icon size={18} /><span>{message}</span>{action && <button type="button" onClick={onAction}>{action}</button>}<button type="button" className="toast__close" aria-label="关闭通知" onClick={() => setClosing(true)}><X size={15} /></button></div>;
}
