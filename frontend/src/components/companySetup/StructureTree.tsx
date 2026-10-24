'use client';

import { useEffect, useState } from 'react';
import { Award, Building2, ChevronDown, GitBranch, Landmark, Layers, MapPin, Tag } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import type { PartId, TreeNode } from '../../lib/companySetupWizard';

const ICONS: Partial<Record<TreeNode['kind'], typeof Building2>> = {
  company: Building2, branch: GitBranch, location: MapPin, costCentre: Landmark,
  department: Layers, designation: Tag, grade: Award,
};

/**
 * The live structure: what this company will look like, updated as rows are typed. Arabic names are
 * shown in Arabic when the page is in Arabic, falling back to the English name.
 *
 * `collapsible` (the wizard's side panel): group headers are collapsed with a count, the current part's group is open
 * and highlighted, and a header jumps to its part when that part is reachable. Without it (the review) every group is
 * open and nothing is a control.
 */
export function StructureTree({ tree, collapsible = false, activePart, activeGroup, onJump, canJump }: {
  tree: TreeNode;
  collapsible?: boolean;
  activePart?: PartId;
  /** A group that is not a part (Branches on step 3): opened, not highlighted as a part. */
  activeGroup?: string;
  onJump?: (part: PartId) => void;
  canJump?: (part: PartId) => boolean;
}) {
  const { t, locale } = useLocale();
  const current = activePart ?? activeGroup;
  const [open, setOpen] = useState<Set<string>>(() => new Set(['company', ...(current ? [current] : [])]));
  // What is being edited opens its group; the reader's own choices for the others are kept.
  useEffect(() => { if (current) setOpen(prev => (prev.has(current) ? prev : new Set([...prev, current]))); }, [current]);
  const nameOf = (node: TreeNode) => node.kind === 'group'
    ? t(node.label)
    : (locale === 'ar' ? node.labelAr || node.label : node.label) || t('Unnamed');
  const toggle = (id: string) => setOpen(prev => { const next = new Set(prev); if (next.has(id)) next.delete(id); else next.add(id); return next; });

  const leaf = (node: TreeNode) => {
    const Icon = ICONS[node.kind];
    const full = node.code && node.kind !== 'company' ? `${nameOf(node)} · ${node.code}` : nameOf(node);
    return (
      <li key={node.id} className="min-w-0">
        <div className="flex min-w-0 items-center gap-2 py-0.5 text-sm text-slate-800 dark:text-slate-100" title={full}>
          {Icon && <Icon className="h-3.5 w-3.5 shrink-0 text-slate-400 dark:text-slate-500" aria-hidden="true" />}
          <span className="min-w-0 truncate" dir="auto"><bdi>{nameOf(node)}</bdi>{node.code && node.kind !== 'company' && <span className="ms-1 text-xs text-slate-500 dark:text-slate-400"><bdi>{node.code}</bdi></span>}</span>
        </div>
        {node.children.length > 0 && <ul className="ms-1.5 border-s border-slate-200 ps-3 dark:border-white/10">{node.children.map(child => child.kind === 'group' ? group(child) : leaf(child))}</ul>}
      </li>
    );
  };

  const group = (node: TreeNode) => {
    const isOpen = !collapsible || open.has(node.id);
    const active = !!activePart && node.part === activePart;
    const fresh = node.children.filter(c => c.tag === 'new').length;
    // Only in existing mode is "new" an exception worth counting; in create mode every row is new.
    const countText = tree.children[0]?.tag === 'existing' && fresh > 0
      ? t('{count} · {fresh} new', { count: node.children.length, fresh })
      : String(node.children.length);
    const jumpable = !!node.part && !active && !!onJump && (canJump?.(node.part) ?? true);
    const headerClass = `flex w-full min-w-0 items-center gap-2 rounded-md px-1.5 py-1 text-start text-xs font-semibold uppercase tracking-wide transition-colors duration-150 ${
      active ? 'bg-sapphire/[0.08] text-sapphire dark:bg-sapphire/20 dark:text-blue-200' : 'text-slate-500 hover:bg-slate-100 hover:text-slate-800 dark:text-slate-400 dark:hover:bg-white/5 dark:hover:text-white'}`;
    const label = <>
      <span className="min-w-0 truncate">{nameOf(node)}</span>
      <span className="text-[11px] font-medium normal-case tracking-normal tabular-nums opacity-80">({countText})</span>
    </>;
    return (
      <li key={node.id} className="min-w-0" data-group={node.id} data-active={active ? 'true' : undefined}>
        {collapsible ? (
          <button type="button" className={headerClass} aria-expanded={isOpen}
              title={jumpable ? t('Go to {step}', { step: nameOf(node) }) : nameOf(node)}
              onClick={() => (jumpable ? onJump!(node.part!) : toggle(node.id))}>
              <ChevronDown className={`h-3.5 w-3.5 shrink-0 transition-transform duration-150 motion-reduce:transition-none ${isOpen ? '' : '-rotate-90 rtl:rotate-90'}`} aria-hidden="true" />
              {label}
          </button>
        ) : (
          <p className="flex min-w-0 items-center gap-2 px-1.5 py-1 text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</p>
        )}
        {isOpen && (node.children.length > 0
          ? <ul className="ms-3 border-s border-slate-200 ps-3 motion-safe:animate-fade-in-fast dark:border-white/10">{node.children.map(child => child.kind === 'group' ? group(child) : leaf(child))}</ul>
          : <p className="ms-3 border-s border-slate-200 ps-3 text-xs text-slate-500 dark:border-white/10 dark:text-slate-400">{t('None yet')}</p>)}
      </li>
    );
  };

  return (
    <div data-testid="structure-tree" aria-label={t('Company structure')} role="region">
      <ul className="space-y-1">{tree.children.map(node => node.kind === 'group' ? group(node) : leaf(node))}</ul>
    </div>
  );
}
