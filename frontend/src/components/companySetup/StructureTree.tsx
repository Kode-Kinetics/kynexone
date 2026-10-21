'use client';

import { Building2, FolderTree, GitBranch, Layers, MapPin, Tag, Landmark, Award } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import type { TreeNode } from '../../lib/companySetupWizard';
import { TagPill } from './wizardForm';

const ICONS: Partial<Record<TreeNode['kind'], typeof Building2>> = {
  company: Building2, branch: GitBranch, location: MapPin, costCentre: Landmark, catalog: FolderTree,
  department: Layers, designation: Tag, grade: Award,
};

/**
 * The live structure: what this company will look like, updated as rows are typed. Arabic names are
 * shown in Arabic when the page is in Arabic, falling back to the English name.
 */
export function StructureTree({ tree }: { tree: TreeNode }) {
  const { t, locale } = useLocale();
  const nameOf = (node: TreeNode) => (node.kind === 'group' || node.kind === 'catalog')
    ? t(node.label)
    : (locale === 'ar' ? node.labelAr || node.label : node.label) || t('Unnamed');
  const render = (node: TreeNode, depth: number) => {
    const Icon = ICONS[node.kind];
    const isGroup = node.kind === 'group';
    return (
      <li key={node.id} className="min-w-0">
        <div className={`flex min-w-0 items-center gap-2 py-1 ${isGroup ? 'text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400' : 'text-sm text-slate-800 dark:text-slate-100'}`}>
          {Icon && <Icon className="h-3.5 w-3.5 shrink-0 text-slate-500 dark:text-slate-400" aria-hidden="true" />}
          <span className="min-w-0 truncate"><bdi>{nameOf(node)}</bdi>{node.code && !isGroup && <span className="ms-1 text-xs text-slate-500 dark:text-slate-400"><bdi>{node.code}</bdi></span>}</span>
          {isGroup && <span className="text-[11px] font-medium normal-case tracking-normal text-slate-500">({node.children.length})</span>}
          {node.tag && node.kind !== 'company' && <TagPill tag={node.tag} />}
        </div>
        {node.children.length > 0 && <ul className="ms-3 border-s border-slate-200 ps-3 dark:border-white/10">{node.children.map(child => render(child, depth + 1))}</ul>}
        {isGroup && node.children.length === 0 && <p className="ms-3 border-s border-slate-200 ps-3 text-xs text-slate-500 dark:border-white/10 dark:text-slate-400">{t('None yet')}</p>}
      </li>
    );
  };
  return (
    <div data-testid="structure-tree" aria-label={t('Company structure')} role="region">
      <ul className="space-y-2">{tree.children.map(node => render(node, 0))}</ul>
    </div>
  );
}
