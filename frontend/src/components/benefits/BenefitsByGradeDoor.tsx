'use client';

import Link from 'next/link';
import { useAuth } from '../../contexts/AuthContext';
import { useT } from '../../hooks/useT';
import { EmptyState } from '../ui/EmptyState';

/**
 * The one door to Benefits by grade (/benefits/by-grade), the home for what each grade receives. Every other
 * place that mentions it links here, and the link is shown only to someone the page will admit
 * (`entitlements.read`). Without it the door is a restricted state that names who can grant access, never a link that ends
 * on Access Denied, and never the editor hidden with nothing in its place (register: Grade editor, Grade packages,
 * Benefit plan rules gated on the release flag alone).
 *   inline  one sentence with the link, inside a form or panel
 *   panel   a block of its own (the /benefits page header)
 */
export function BenefitsByGradeDoor({ intro, variant = 'inline', className = '' }: { intro: string; variant?: 'inline' | 'panel'; className?: string }) {
  const t = useT();
  const { hasPermission } = useAuth();
  const allowed = hasPermission('entitlements.read');
  if (allowed) {
    return variant === 'panel'
      ? (
        <div data-benefits-door className={`surface flex flex-wrap items-center justify-between gap-3 p-4 ${className}`.trim()}>
          <p className="min-w-0 flex-1 text-body text-fg-muted">{intro}</p>
          <Link href="/benefits/by-grade" className="btn-secondary">{t('Open Benefits by grade')}</Link>
        </div>
      )
      : (
        <p data-benefits-door className={`text-meta text-fg-muted ${className}`.trim()}>
          {intro}{' '}
          <Link href="/benefits/by-grade" className="font-semibold text-primary-fg underline">{t('Open Benefits by grade')}</Link>
        </p>
      );
  }
  const title = t("You can't see Benefits by grade");
  const reason = t('What each grade is entitled to is set there, and access is given by your HR administrator.');
  return variant === 'panel'
    ? <div data-benefits-door="restricted" className={`surface ${className}`.trim()}><EmptyState kind="restricted" title={title} description={reason} /></div>
    : <p data-benefits-door="restricted" className={`text-meta text-fg-muted ${className}`.trim()}>{intro} {title}. {reason}</p>;
}
