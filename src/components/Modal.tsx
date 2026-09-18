import { DotsSix, X } from '@phosphor-icons/react';
import { useEffect, useId, useRef, useState, type PointerEvent, type ReactNode } from 'react';

interface ModalProps {
  title: string;
  description?: string;
  children: ReactNode;
  onClose: () => void;
  className?: string;
  movable?: boolean;
}

export function Modal({ title, description, children, onClose, className = '', movable = false }: ModalProps) {
  const [closing, setClosing] = useState(false);
  const modalRef = useRef<HTMLElement | null>(null);
  const titleId = `modal-title-${useId().replace(/:/g, '')}`;
  const descriptionId = description ? `${titleId}-description` : undefined;
  const previousFocus = useRef<HTMLElement | null>(null);
  const closeTimer = useRef<number | null>(null);
  const offset = useRef({ x: 0, y: 0 });
  const drag = useRef<{ pointerId: number; captureTarget: HTMLElement; startX: number; startY: number; originX: number; originY: number; currentX: number; currentY: number; rect: DOMRect; zoom: number } | null>(null);
  const escSwallowed = useRef(false);
  const onCloseRef = useRef(onClose);
  useEffect(() => { onCloseRef.current = onClose; });
  const applyModalPosition = (x: number, y: number) => {
    if (!modalRef.current) return;
    modalRef.current.style.left = `${x}px`;
    modalRef.current.style.top = `${y}px`;
  };

  // 退场动画结束后再真正卸载
  const requestClose = () => {
    if (closing) return;
    setClosing(true);
    closeTimer.current = window.setTimeout(() => onCloseRef.current(), 170);
  };

  useEffect(() => {
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    previousFocus.current = previous;
    const modal = modalRef.current;
    if (!modal) return undefined;
    const focusableSelector = 'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [href], [tabindex]:not([tabindex="-1"])';
    const focusFirst = () => {
      const first = modal.querySelector<HTMLElement>(focusableSelector);
      (first ?? modal).focus({ preventScroll: true });
    };
    const trapFocus = (event: KeyboardEvent) => {
      if (event.key !== 'Tab') return;
      const focusable = Array.from(modal.querySelectorAll<HTMLElement>(focusableSelector));
      if (!focusable.length) {
        event.preventDefault();
        modal.focus({ preventScroll: true });
        return;
      }
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };
    focusFirst();
    modal.addEventListener('keydown', trapFocus);
    return () => {
      modal.removeEventListener('keydown', trapFocus);
      if (closeTimer.current !== null) window.clearTimeout(closeTimer.current);
      const focusTarget = previousFocus.current;
      if (focusTarget && document.contains(focusTarget)) focusTarget.focus({ preventScroll: true });
    };
  }, []);

  useEffect(() => {
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || escSwallowed.current) return;
      requestClose();
    };
    window.addEventListener('keydown', escape);
    return () => window.removeEventListener('keydown', escape);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (!movable) return undefined;
    let clampFrame = 0;
    const clampToViewport = () => {
      window.cancelAnimationFrame(clampFrame);
      clampFrame = window.requestAnimationFrame(() => {
        const modal = modalRef.current;
        if (!modal || drag.current) return;
        const rect = modal.getBoundingClientRect();
        const zoom = Number.parseFloat(getComputedStyle(modal).zoom) || 1;
        const deltaX = rect.left < 16 ? 16 - rect.left : rect.right > window.innerWidth - 16 ? window.innerWidth - 16 - rect.right : 0;
        const deltaY = rect.top < 16 ? 16 - rect.top : rect.bottom > window.innerHeight - 16 ? window.innerHeight - 16 - rect.bottom : 0;
        if (!deltaX && !deltaY) return;
        offset.current = { x: Math.round(offset.current.x + deltaX / zoom), y: Math.round(offset.current.y + deltaY / zoom) };
        applyModalPosition(offset.current.x, offset.current.y);
      });
    };
    const move = (event: globalThis.PointerEvent) => {
      const value = drag.current;
      if (!value || event.pointerId !== value.pointerId) return;
      if (event.pointerType === 'mouse' && (event.buttons & 1) === 0) { finish(event); return; }
      const deltaX = (event.clientX - value.startX) / value.zoom;
      const deltaY = (event.clientY - value.startY) / value.zoom;
      const minDeltaX = (16 - value.rect.left) / value.zoom;
      const maxDeltaX = (window.innerWidth - 16 - value.rect.right) / value.zoom;
      const minDeltaY = (16 - value.rect.top) / value.zoom;
      const maxDeltaY = (window.innerHeight - 16 - value.rect.bottom) / value.zoom;
      value.currentX = Math.round(value.originX + Math.max(minDeltaX, Math.min(deltaX, maxDeltaX)));
      value.currentY = Math.round(value.originY + Math.max(minDeltaY, Math.min(deltaY, maxDeltaY)));
      applyModalPosition(value.currentX, value.currentY);
    };
    function finish(event?: globalThis.PointerEvent, cancelled = false) {
      const value = drag.current;
      if (!value || (event && event.pointerId !== value.pointerId)) return;
      const next = cancelled ? { x: value.originX, y: value.originY } : { x: value.currentX, y: value.currentY };
      offset.current = next;
      applyModalPosition(next.x, next.y);
      if (modalRef.current) modalRef.current.classList.remove('is-moving');
      try { if (value.captureTarget.hasPointerCapture(value.pointerId)) value.captureTarget.releasePointerCapture(value.pointerId); } catch { /* Pointer capture was already released. */ }
      drag.current = null;
      window.dispatchEvent(new CustomEvent('pivkey-modal-gesture', { detail: false }));
    }
    const cancel = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && drag.current) {
        finish(undefined, true);
        // 拖动中的 Esc 只取消拖动，不触发关闭
        escSwallowed.current = true;
        window.setTimeout(() => { escSwallowed.current = false; }, 0);
      }
    };
    const blur = () => finish();
    const visibility = () => { if (document.hidden) finish(); };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', finish);
    window.addEventListener('pointercancel', finish);
    window.addEventListener('keydown', cancel);
    window.addEventListener('blur', blur);
    window.addEventListener('resize', clampToViewport);
    document.addEventListener('visibilitychange', visibility);
    modalRef.current?.addEventListener('animationend', clampToViewport);
    clampToViewport();
    return () => {
      window.cancelAnimationFrame(clampFrame);
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', finish);
      window.removeEventListener('pointercancel', finish);
      window.removeEventListener('keydown', cancel);
      window.removeEventListener('blur', blur);
      window.removeEventListener('resize', clampToViewport);
      document.removeEventListener('visibilitychange', visibility);
      modalRef.current?.removeEventListener('animationend', clampToViewport);
      window.dispatchEvent(new CustomEvent('pivkey-modal-gesture', { detail: false }));
      drag.current = null;
    };
  }, [movable]);

  const startMove = (event: PointerEvent<HTMLElement>) => {
    if (!movable || event.button !== 0 || (event.target as HTMLElement).closest('button')) return;
    event.preventDefault();
    if (!modalRef.current || drag.current) return;
    try { event.currentTarget.setPointerCapture(event.pointerId); } catch { /* Pointer capture is unavailable on older WebView2 builds. */ }
    const rect = modalRef.current.getBoundingClientRect();
    modalRef.current.classList.add('is-moving');
    applyModalPosition(offset.current.x, offset.current.y);
    window.dispatchEvent(new CustomEvent('pivkey-modal-gesture', { detail: true }));
    drag.current = { pointerId: event.pointerId, captureTarget: event.currentTarget, startX: event.clientX, startY: event.clientY, originX: offset.current.x, originY: offset.current.y, currentX: offset.current.x, currentY: offset.current.y, rect, zoom: Number.parseFloat(getComputedStyle(modalRef.current).zoom) || 1 };
  };

  return (
    <div className={`modal-backdrop ${closing ? 'is-closing' : ''}`} role="presentation" onMouseDown={requestClose}>
      <section ref={modalRef} className={`modal ${movable ? 'is-movable' : ''} ${className}`} role="dialog" tabIndex={-1} aria-modal="true" aria-labelledby={titleId} aria-describedby={descriptionId} onMouseDown={(event) => event.stopPropagation()}>
        <header className="modal__header" onPointerDown={startMove}>
          {movable && <DotsSix className="modal__grip" size={15} />}
          <div><h2 id={titleId}>{title}</h2>{description && <p id={descriptionId}>{description}</p>}</div>
          <button type="button" className="icon-button" aria-label="关闭" title="关闭" onClick={requestClose}><X size={18} /></button>
        </header>
        {children}
      </section>
    </div>
  );
}
