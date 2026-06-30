'use client';

import { useEffect, useRef, useState } from 'react';
import type { LocaleCode } from '../i18n/translations';

const DEBOUNCE_MS = 600;
const CACHE_PREFIX = 'kynexone-tx-';

// MyMemory language pair codes
const LANG_PAIR: Partial<Record<LocaleCode, string>> = {
  ar: 'en|ar',
  fr: 'en|fr',
  es: 'en|es',
};

function cacheKey(locale: string, source: string): string {
  return `${CACHE_PREFIX}${locale}:${source.trim().toLowerCase()}`;
}

/**
 * MyMemory returns crowd-sourced translation-memory segments that frequently
 * carry CAT-tool noise: inline placeholder tags (`<g id="1">…</g>`, XLIFF/TMX
 * markers), HTML entities, and stray separator punctuation from the original
 * label (e.g. a trailing colon). Strip all of it so the field receives clean
 * target text only.
 */
export function sanitizeTranslation(raw: string): string {
  if (!raw) return '';
  let s = raw
    // 1) Drop any XML/HTML-like tag: <g id="1">, </g>, <x/>, <bpt>, <ph> …
    .replace(/<[^>]*>/g, '')
    // 2) Decode the handful of entities MyMemory emits
    .replace(/&quot;/g, '"')
    .replace(/&#39;|&apos;/g, "'")
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&nbsp;/g, ' ')
    // 3) Collapse whitespace
    .replace(/\s+/g, ' ')
    .trim();
  // 4) Trim leading/trailing separator punctuation left behind by TM segments
  //    (Latin + Arabic comma/colon, dashes, pipes) — keep inner punctuation.
  s = s.replace(/^[\s:;،,|–—-]+|[\s:;،,|–—-]+$/g, '').trim();
  // 5) Ignore MyMemory's plain-text warning/echo responses
  if (/^MYMEMORY WARNING/i.test(s) || /PLEASE SELECT TWO DISTINCT LANGUAGES/i.test(s)) return '';
  return s;
}

function getCache(locale: string, source: string): string | null {
  if (typeof window === 'undefined') return null;
  try { return localStorage.getItem(cacheKey(locale, source)); } catch { return null; }
}

function setCache(locale: string, source: string, translation: string): void {
  if (typeof window === 'undefined') return;
  try { localStorage.setItem(cacheKey(locale, source), translation); } catch { /* quota */ }
}

/**
 * Debounced English → target-locale auto-translation via MyMemory (free, no key required).
 * Results are cached in localStorage to avoid repeat API calls.
 * Falls back silently on network errors — translation is best-effort.
 */
export function useAutoTranslate(source: string, locale: LocaleCode = 'ar') {
  const [translation, setTranslation] = useState('');
  const [isTranslating, setIsTranslating] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => {
    const trimmed = source.trim();

    // English or unsupported locale — no translation needed
    if (!trimmed || trimmed.length < 2 || locale === 'en') {
      setTranslation('');
      return;
    }

    const pair = LANG_PAIR[locale];
    if (!pair) { setTranslation(''); return; }

    // Serve from cache immediately (sanitize too — older cache entries may
    // predate this cleanup and still contain raw TM tags).
    const cached = getCache(locale, trimmed);
    if (cached) {
      setTranslation(sanitizeTranslation(cached));
      return;
    }

    const timer = setTimeout(async () => {
      abortRef.current?.abort();
      abortRef.current = new AbortController();
      setIsTranslating(true);

      try {
        const res = await fetch(
          `https://api.mymemory.translated.net/get?q=${encodeURIComponent(trimmed)}&langpair=${pair}`,
          { signal: abortRef.current.signal },
        );
        const data: { responseStatus: number; responseData?: { translatedText: string } } = await res.json();
        if (data.responseStatus === 200 && data.responseData?.translatedText) {
          const tx = sanitizeTranslation(data.responseData.translatedText);
          if (tx) {
            setCache(locale, trimmed, tx);
            setTranslation(tx);
          }
        }
      } catch {
        // Best-effort — silently ignore network / abort errors
      } finally {
        setIsTranslating(false);
      }
    }, DEBOUNCE_MS);

    return () => {
      clearTimeout(timer);
      abortRef.current?.abort();
    };
  }, [source, locale]);

  return { translation, isTranslating };
}
