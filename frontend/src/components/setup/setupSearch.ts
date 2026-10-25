import { msg } from '../../i18n/translations';
import {
  SETUP_AREAS, SETUP_SECTION_GROUPS, SETUP_SETTING_SECTIONS, SETUP_TOOLS, destinationAvailability,
  type SetupAccessContext, type SetupDestination, type SetupNavigationModel,
} from '../../lib/setupAreas';
import { SETUP_MODULE_SECTIONS } from '../../lib/setupModuleSections';

/**
 * "Find a setting": one index of what a user can open in Setup, and the matcher that searches it. Pure, so it is
 * unit-tested without a browser (unit/setupSearch.spec.ts). Setup's section column and the ⌘K palette's Settings group
 * both build their entries here from the same navigation model, so neither can offer a section the other hides.
 *
 * Labels, descriptions and captions are dictionary keys (English). The matcher searches them in the reader's language
 * and in English, so a reader using Arabic who types "grades" still finds «الدرجات».
 */

export interface SetupSearchEntry {
  /** Unique across the index. A section's id is its navigation id (`grades`, `guidedSetup`). */
  id: string;
  label: string;
  description?: string;
  /** The small caption under a result: the section's group, the destination's area, or "Payroll accounting". */
  group: string;
  /** The entry's own address (a real link: Cmd/Ctrl-click opens it in a new tab). */
  href: string;
  /** A Setup section opens through the page's navigation (and its unsaved-changes guard); anything else by its href. */
  navId?: string;
  /** Other words people use for it (synonyms, abbreviations), searched like the label. */
  keywords?: readonly string[];
}

export interface SetupSearchHit {
  entry: SetupSearchEntry;
  /** Translated, for display. */
  label: string;
  description?: string;
  group: string;
}

type Translate = (key: string) => string;

const STATUTORY_SCHEMES = ['GOSI', 'SANED', 'GPSSA', 'GRSIA', 'التأمينات الاجتماعية', 'ساند'];

/** Words people type for a setting that its name does not contain. Keyed by entry id. */
export const SETUP_SEARCH_SYNONYMS: Readonly<Record<string, readonly string[]>> = {
  statutoryRules: STATUTORY_SCHEMES,
  glMapping: STATUTORY_SCHEMES,
  'gl:statutory': STATUTORY_SCHEMES,
  masterData: ['dropdown', 'drop down', 'picklist', 'pick list', 'reference lists'],
  emailConfig: ['SMTP', 'mail server'],
  numberingRules: ['numbers', 'sequence', 'employee number'],
  gccSettings: ['work week', 'Ramadan', 'WPS', 'weekend'],
};

/**
 * Payroll accounting's own tabs (components/gl/GlSetupPanel.tsx), with the permission each one is shown for there.
 * They open the Payroll accounting section; the panel itself picks the first tab the reader may see.
 */
const PAYROLL_ACCOUNTING_TABS: readonly { id: string; label: string; visible: (has: (p: string) => boolean, hasRole: (r: string) => boolean) => boolean }[] = [
  { id: 'coa', label: msg('Accounts & Mapping'), visible: has => has('finance.gl.read') || has('finance.gl.manage') },
  { id: 'drivers', label: msg('Posting Drivers'), visible: has => has('finance.gl.read') || has('finance.gl.manage') || has('finance.gl.drivers.manage') },
  { id: 'companyRates', label: msg('Company Rates'), visible: has => has('payroll.rates.read') || has('payroll.rates.manage') },
  { id: 'statutory', label: msg('Statutory Rates'), visible: has => has('payroll.rates.read') || has('payroll.rates.manage') || has('payroll.rates.statutory_override') },
  { id: 'costAccounting', label: msg('Cost accounting'), visible: has => has('finance.gl.read') || has('finance.gl.manage') },
  { id: 'periods', label: msg('Accounting periods'), visible: has => has('finance.gl.read') || has('finance.gl.manage') },
  { id: 'eosb', label: msg('End-of-service provision'), visible: has => has('finance.gl.read') || has('finance.gl.manage') },
  { id: 'erpApi', label: msg('ERP API access'), visible: (_has, hasRole) => hasRole('Admin') },
];

/**
 * Lower case, without diacritics (Latin accents and Arabic harakat), with Arabic letter variants folded (أ إ آ → ا,
 * ى → ي, ة → ه), British "centre" read as "center", and punctuation read as a space.
 */
export function normalizeSearchText(value: string): string {
  return value
    .normalize('NFKD')
    .replace(/\p{M}/gu, '')
    .replace(/ـ/g, '')
    .replace(/[أإآٱ]/g, 'ا')
    .replace(/ى/g, 'ي')
    .replace(/ة/g, 'ه')
    .replace(/ؤ/g, 'و')
    .replace(/ئ/g, 'ي')
    .toLowerCase()
    .replace(/centre/g, 'center')
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim();
}

const compact = (value: string) => value.replace(/\s+/g, '');

function fieldMatches(field: string, tokens: readonly string[], whole: string): boolean {
  if (!field) return false;
  if (tokens.every(token => field.includes(token))) return true;
  return whole.length > 0 && compact(field).includes(whole);
}

/**
 * The entries that match the query, best first: a label that starts with it, then a word of a label that does, then
 * a label containing it, then a synonym, then a description. Every word of the query must match (in any order).
 */
export function matchSetupEntries(entries: readonly SetupSearchEntry[], query: string, translate: Translate): SetupSearchHit[] {
  const normalized = normalizeSearchText(query);
  if (!normalized) return [];
  const tokens = normalized.split(' ');
  const whole = compact(normalized);
  const scored: { hit: SetupSearchHit; score: number; index: number }[] = [];
  entries.forEach((entry, index) => {
    const label = translate(entry.label);
    const description = entry.description ? translate(entry.description) : undefined;
    const labels = [normalizeSearchText(label), normalizeSearchText(entry.label)];
    const descriptions = description ? [normalizeSearchText(description), normalizeSearchText(entry.description!)] : [];
    const keywords = (entry.keywords ?? []).map(normalizeSearchText);
    let score = -1;
    if (labels.some(text => text.startsWith(normalized) || compact(text).startsWith(whole))) score = 0;
    else if (labels.some(text => text.split(' ').some(word => word.startsWith(tokens[0])) && fieldMatches(text, tokens, whole))) score = 1;
    else if (labels.some(text => fieldMatches(text, tokens, whole))) score = 2;
    else if (keywords.some(text => fieldMatches(text, tokens, whole))) score = 3;
    else if (descriptions.some(text => fieldMatches(text, tokens, whole))) score = 4;
    else {
      // Words split across fields: "payroll gosi" matches Payroll accounting by its name and a synonym.
      const all = [...labels, ...descriptions, ...keywords].join(' ');
      if (tokens.every(token => all.includes(token))) score = 5;
    }
    if (score < 0) return;
    scored.push({ hit: { entry, label, description, group: translate(entry.group) }, score, index });
  });
  return scored.sort((a, b) => a.score - b.score || a.index - b.index).map(item => item.hit);
}

export interface SetupSearchIndexOptions {
  model: SetupNavigationModel;
  /** Destination availability (role, feature flags, path ownership), exactly as All setup areas reads it. */
  access: SetupAccessContext & { hasRole: (role: string) => boolean };
  /** A section's own address on this page; defaults to `/setup?tab={id}` (or the tool's address). */
  sectionHref?: (id: string) => string;
  /** An All setup areas page or embedded setting. Defaults to the plain address. */
  areaHref?: (areaId: string, setting?: string) => string;
  /** A destination that is a screen of its own. Defaults to its path. */
  routeHref?: (destination: SetupDestination) => string;
}

const defaultAreaHref = (areaId: string, setting?: string) =>
  `/setup?tab=aiSetup&path=configuration&area=${encodeURIComponent(areaId)}${setting ? `&setting=${encodeURIComponent(setting)}` : ''}`;

/**
 * Everything "Find a setting" may offer this user: the navigation's own sections (never one the navigation hides),
 * then, for setup writers, All setup areas' areas and the destinations it would let them open, then Payroll accounting's
 * tabs the panel would show them.
 */
export function buildSetupSearchIndex({ model, access, sectionHref, areaHref = defaultAreaHref, routeHref = destination => destination.path }: SetupSearchIndexOptions): SetupSearchEntry[] {
  const entries: SetupSearchEntry[] = [];
  const groupLabel = (id: string) => SETUP_SECTION_GROUPS.find(group => group.id === id)?.label ?? id;
  const visibleSettings = new Set<string>(model.settings.map(section => section.id));
  const hrefFor = (id: string, fallback: string) => sectionHref?.(id) ?? fallback;

  for (const tool of model.tools) {
    const { label, description, href } = SETUP_TOOLS[tool];
    entries.push({ id: tool, navId: tool, label, description, group: groupLabel('getStarted'), href: hrefFor(tool, href) });
  }
  for (const section of model.settings) {
    entries.push({
      id: section.id, navId: section.id, label: section.label, description: section.description, group: groupLabel(section.group),
      href: hrefFor(section.id, `/setup?tab=${section.id}`), keywords: SETUP_SEARCH_SYNONYMS[section.id],
    });
  }

  // All setup areas is a setup writer's page (the navigation offers it only to them).
  if (model.tools.includes('allSetupAreas')) {
    const areasGroup = SETUP_TOOLS.allSetupAreas.label;
    for (const area of SETUP_AREAS) {
      // An area is offered only when this user can open something in it; otherwise it is a page of locked rows.
      if (!area.destinations.some(destination => destinationAvailability(destination, access) === 'available')) continue;
      entries.push({ id: `area:${area.id}`, label: area.label, description: area.description, group: areasGroup, href: areaHref(area.id) });
      for (const destination of area.destinations) {
        // A setting that is also a section is found as that section, once (and one the navigation hides, not at all).
        if (destination.path.startsWith('/setup?tab=')) {
          const settingId = destination.path.slice('/setup?tab='.length);
          if (SETUP_SECTION_IDS.has(settingId)) continue;
        }
        if (destinationAvailability(destination, access) !== 'available') continue;
        entries.push({
          id: `destination:${area.id}:${destination.id}`,
          label: destination.label,
          group: area.label,
          href: destination.presentation === 'embedded' ? areaHref(area.id, destination.id) : routeHref(destination),
          keywords: SETUP_SEARCH_SYNONYMS[destination.id],
        });
      }
    }
  }

  if (visibleSettings.has('glMapping')) {
    const group = SETUP_SETTING_SECTIONS.find(section => section.id === 'glMapping')!.label;
    for (const tab of PAYROLL_ACCOUNTING_TABS) {
      if (!tab.visible(access.hasPermission, access.hasRole)) continue;
      // The tab travels in the address (`glTab`), which GlSetupPanel opens, falling back to the first tab the reader may
      // see. Opened by its href, not as the section, so the tab is never dropped on the way.
      const section = hrefFor('glMapping', '/setup?tab=glMapping');
      entries.push({ id: `gl:${tab.id}`, label: tab.label, group, href: `${section}${section.includes('?') ? '&' : '?'}glTab=${tab.id}`, keywords: SETUP_SEARCH_SYNONYMS[`gl:${tab.id}`] });
    }
  }
  return entries;
}

const SETUP_SECTION_IDS = new Set<string>([...SETUP_SETTING_SECTIONS, ...SETUP_MODULE_SECTIONS].map(section => section.id));
