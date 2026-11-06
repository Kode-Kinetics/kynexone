'use client';

import {
  Children, cloneElement, forwardRef, isValidElement, useId,
  type InputHTMLAttributes, type ReactElement, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes,
} from 'react';
import { useT } from '../../hooks/useT';
import { InfoTip } from '../InfoTip';

type Size = 'sm' | 'md' | 'lg';
const SIZE: Record<Size, string> = { sm: 'input-sm', md: '', lg: 'input-lg' };

/** Text input on the shared control scale (28 / 36 / 40, 44 on touch). Pass `invalid` to set aria-invalid. */
export const Input = forwardRef<HTMLInputElement, Omit<InputHTMLAttributes<HTMLInputElement>, 'size'> & { size?: Size; invalid?: boolean }>(
  function Input({ size = 'md', invalid, className = '', ...rest }, ref) {
    return <input ref={ref} aria-invalid={invalid || undefined} className={`input ${SIZE[size]} ${className}`.trim()} {...rest} />;
  },
);

export const Select = forwardRef<HTMLSelectElement, Omit<SelectHTMLAttributes<HTMLSelectElement>, 'size'> & { size?: Size; invalid?: boolean }>(
  function Select({ size = 'md', invalid, className = '', ...rest }, ref) {
    return <select ref={ref} aria-invalid={invalid || undefined} className={`select ${SIZE[size]} ${className}`.trim()} {...rest} />;
  },
);

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement> & { invalid?: boolean }>(
  function Textarea({ invalid, className = '', ...rest }, ref) {
    return <textarea ref={ref} aria-invalid={invalid || undefined} className={`textarea ${className}`.trim()} {...rest} />;
  },
);

type ControlProps = { id?: string; 'aria-describedby'?: string; 'aria-invalid'?: boolean | 'true' | 'false'; 'aria-required'?: boolean; children?: ReactNode };
const NATIVE_CONTROLS = new Set(['input', 'select', 'textarea']);

/**
 * One labelled field. Replaces the local `Field` helpers (Screen Standard 3b and section 8d).
 *
 *   label        the visible label; it is bound to the control (`htmlFor`), whichever of input / select / textarea
 *                comes first in `children` (or the one child that already has an `id`)
 *   required     draws the required mark and sets aria-required on the control
 *   info/infoKey the `i` (InfoTip): `info` is the built-in text (a translation key), `infoKey` the per-company
 *                override key (Setup, Field help text)
 *   noInfo       "reason": the field is exempt from the `i` and says why in code, so the omission is a decision
 *                (name, phone, email, address lines, notes, a plain date). The info-coverage ratchet counts it.
 *   hint         format and limits, visible under the field (required information never hides behind hover)
 *   description  one sentence between the label and the control
 *   error        a message under the control; sets aria-invalid and role="alert"
 *   layout       'stacked' (default) or 'inline' (label at the start, control at the end of one row)
 *   size         'sm' (default, the 12 px label most screens use) or 'md' (14 px, forms)
 */
export interface FieldProps {
  label: ReactNode;
  children: ReactNode;
  required?: boolean;
  info?: string;
  infoKey?: string;
  noInfo?: string;
  hint?: ReactNode;
  description?: ReactNode;
  error?: ReactNode;
  layout?: 'stacked' | 'inline';
  size?: 'sm' | 'md';
  className?: string;
}

export function Field({ label, children, required, info, infoKey, noInfo, hint, description, error, layout = 'stacked', size = 'sm', className = '' }: FieldProps) {
  const t = useT();
  const generated = useId();
  const hintId = `${generated}-hint`;
  const descriptionId = `${generated}-description`;
  const errorId = `${generated}-error`;
  const describedBy = [description ? descriptionId : null, hint ? hintId : null, error ? errorId : null].filter(Boolean).join(' ') || undefined;

  // Bind the label to the first native control (looking through wrappers), or to the one that already has an id.
  let controlId = generated;
  let bound = false;
  const bind = (nodes: ReactNode): ReactNode => Children.map(nodes, node => {
    if (!isValidElement(node)) return node;
    const element = node as ReactElement<ControlProps>;
    const isControl = (typeof element.type === 'string' && NATIVE_CONTROLS.has(element.type)) || element.type === Input || element.type === Select || element.type === Textarea;
    if (!bound && isControl) {
      bound = true;
      controlId = element.props.id ?? generated;
      return cloneElement(element, {
        id: controlId,
        'aria-describedby': [element.props['aria-describedby'], describedBy].filter(Boolean).join(' ') || undefined,
        ...(error ? { 'aria-invalid': true } : {}),
        ...(required ? { 'aria-required': true } : {}),
      });
    }
    return element.props.children ? cloneElement(element, {}, bind(element.props.children)) : element;
  });
  let control = bind(children);
  // No native control in sight: a single custom control (a picker, a wrapper component) gets the id, as the GL helper did.
  if (!bound && isValidElement(children)) {
    const only = children as ReactElement<ControlProps>;
    controlId = only.props.id ?? generated;
    control = cloneElement(only, {
      id: controlId,
      'aria-describedby': [only.props['aria-describedby'], describedBy].filter(Boolean).join(' ') || undefined,
      ...(error ? { 'aria-invalid': true } : {}),
    });
  }

  const labelClass = size === 'md'
    ? 'text-sm font-medium text-slate-700 dark:text-slate-300'
    : 'text-xs font-medium text-slate-600 dark:text-slate-300';
  const text = typeof label === 'string' ? label : undefined;
  const head = (
    <div className={`flex items-center gap-1.5 ${labelClass} ${layout === 'inline' ? 'shrink-0' : 'mb-1.5'}`}>
      {/* The mark is inside the label so the control's accessible name is "Rate *", as the GL screens always had. */}
      <label htmlFor={controlId}>{label}{required && <> <span className="text-danger-fg" title={t('Required')}>*</span></>}</label>
      {info && <InfoTip text={info} fieldKey={infoKey} label={text} />}
    </div>
  );
  return (
    <div className={`min-w-0 ${layout === 'inline' ? 'flex items-center justify-between gap-3' : ''} ${className}`.trim()} data-no-info={noInfo && !info ? noInfo : undefined}>
      {head}
      <div className={layout === 'inline' ? 'min-w-0 flex-1' : undefined}>
        {description && <p id={descriptionId} className="mb-1.5 text-meta text-fg-muted">{description}</p>}
        {control}
        {hint && <p id={hintId} className="mt-1 text-caption leading-snug text-slate-500 dark:text-slate-400">{hint}</p>}
        {error && <p id={errorId} role="alert" className="mt-1 text-meta font-medium text-danger-fg">{error}</p>}
      </div>
    </div>
  );
}
