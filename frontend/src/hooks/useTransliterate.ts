'use client';

import { useState } from 'react';
import type { LocaleCode } from '../i18n/translations';
import { localizationApi } from '../api/localization';

/**
 * P0-6 — PII must never leave to a third party.
 *
 * This hook REPLACES the former `useAutoTranslate`, which POSTed employee and company legal
 * names to a public third-party translation API on every debounced keystroke and cached the
 * third-party results in `localStorage` (`kynexone-tx-*`) — an unconsented PDPL/GDPR
 * cross-border transfer. Both the external call and the localStorage cache are removed entirely;
 * transliteration now runs server-side via POST /api/localization/transliterate.
 *
 * The replacement is opt-in and on-demand (memory rule: AI/auto must be opt-in beside a
 * manual path, preview -> approve): `suggest()` runs ONLY on an explicit user action (the
 * "Suggest (AR)" button), calls our own authenticated, tenant-scoped, fully offline backend,
 * and returns a suggestion the user can edit or accept. No keystroke effect, no auto-fill.
 */

/**
 * The backend transliteration service is offline and returns clean text, but we keep a
 * defensive sanitizer on the returned string: it can never be interpreted as markup, so a
 * suggestion can never smuggle a tag/entity into a form field.
 */
const ENTITY_MAP: Record<string, string> = {
  '&quot;': '"',
  '&#39;': "'",
  '&apos;': "'",
  '&amp;': '&',
  '&lt;': '<',
  '&gt;': '>',
  '&nbsp;': ' ',
};

/**
 * Decodes entities exactly ONCE. A sequential replace chain double-unescapes
 * ("&amp;lt;" -> "&lt;" -> "<"); a single alternation pass decodes each entity
 * one time only, so author-escaped text can never resurrect into markup.
 * In the browser the native parser does the same via a textarea (content model
 * is text — tags stay literal, entities decode once, nothing executes).
 */
function decodeEntitiesOnce(s: string): string {
  if (typeof document !== 'undefined') {
    const el = document.createElement('textarea');
    el.innerHTML = s;
    return el.value;
  }
  return s.replace(/&(?:quot|#39|apos|amp|lt|gt|nbsp);/g, (m) => ENTITY_MAP[m] ?? m);
}

/**
 * Removes tag fragments with a run-to-stable loop (a single pass lets
 * "<scr<x>ipt>" reassemble into "<script>"), then removes ANY remaining angle
 * brackets: these values are plain UI label text, so no element may ever
 * survive into the output, categorically.
 */
function stripMarkup(s: string): string {
  let prev: string;
  do {
    prev = s;
    s = s.replace(/<[^>]*>/g, '');
  } while (s !== prev);
  return s.replace(/[<>]/g, '');
}

export function sanitizeTranslation(raw: string): string {
  if (!raw) return '';
  // 1) Decode entities once, THEN strip — entity-encoded markup ("&lt;script&gt;")
  //    becomes visible to the stripper instead of surviving decode-after-strip.
  let s = stripMarkup(decodeEntitiesOnce(raw));
  // 2) Collapse whitespace
  s = s.replace(/\s+/g, ' ').trim();
  // 3) Trim leading/trailing separator punctuation (Latin + Arabic comma/colon,
  //    dashes, pipes) — keep inner punctuation.
  s = s.replace(/^[\s:;،,|–—-]+|[\s:;،,|–—-]+$/g, '').trim();
  return s;
}

/**
 * On-demand transliteration. Returns `suggest(source, target?)` — an async function bound to
 * an explicit user action — and `isTranslating` for button loading state. `suggest` resolves
 * to a sanitized suggestion string, or '' when there is nothing to suggest / the request fails
 * (best-effort; the manual field is always available).
 */
export function useTransliterate() {
  const [isTranslating, setIsTranslating] = useState(false);

  async function suggest(source: string, target: LocaleCode = 'ar'): Promise<string> {
    const trimmed = source.trim();
    if (trimmed.length < 2 || target === 'en') return '';
    setIsTranslating(true);
    try {
      const { suggestion } = await localizationApi.transliterate(trimmed, target);
      return sanitizeTranslation(suggestion ?? '');
    } catch {
      return '';
    } finally {
      setIsTranslating(false);
    }
  }

  return { suggest, isTranslating };
}
