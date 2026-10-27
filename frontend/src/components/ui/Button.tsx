'use client';

import { forwardRef, type ButtonHTMLAttributes } from 'react';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export type ButtonSize = 'sm' | 'md' | 'lg';

const VARIANT: Record<ButtonVariant, string> = {
  primary: 'btn-primary',
  secondary: 'btn-secondary',
  ghost: 'btn-ghost',
  danger: 'btn-danger',
};
const SIZE: Record<ButtonSize, string> = { sm: 'btn-sm', md: '', lg: 'btn-lg' };

/**
 * The shared button. A thin typed wrapper over the .btn-* classes in styles/index.css so a
 * button is the same height, radius and focus ring whether it is the component or the class.
 * `busy` disables the button and sets aria-busy; the caller owns the busy label.
 */
export const Button = forwardRef<HTMLButtonElement, ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant;
  size?: ButtonSize;
  busy?: boolean;
}>(function Button({ variant = 'secondary', size = 'md', busy = false, className = '', disabled, type = 'button', ...rest }, ref) {
  return (
    <button
      ref={ref}
      type={type}
      disabled={disabled || busy}
      aria-busy={busy || undefined}
      className={`${VARIANT[variant]} ${SIZE[size]} ${className}`.trim()}
      {...rest}
    />
  );
});
