'use client';

import { forwardRef, type InputHTMLAttributes, type SelectHTMLAttributes, type TextareaHTMLAttributes } from 'react';

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
