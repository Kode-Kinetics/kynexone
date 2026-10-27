/**
 * Splits a translated sentence into its literal text and the values that were interpolated into it, so a caller can
 * wrap each value in its own `<bdi>` (a mixed-direction run keeps its order inside Arabic text). Callers pass the
 * translation function placeholders built with MESSAGE_PART_MARK, then read the result back here.
 */
export const MESSAGE_PART_MARK = { open: '\uE000', close: '\uE001' } as const;

export interface MessagePart { text: string; value?: true }

export function messageParts(sentence: string, values: Record<string, string>): MessagePart[] {
  const parts: MessagePart[] = [];
  const re = new RegExp(`${MESSAGE_PART_MARK.open}([^${MESSAGE_PART_MARK.close}]*)${MESSAGE_PART_MARK.close}`, 'g');
  let last = 0;
  for (const match of sentence.matchAll(re)) {
    if (match.index > last) parts.push({ text: sentence.slice(last, match.index) });
    const name = match[1];
    parts.push({ text: Object.prototype.hasOwnProperty.call(values, name) ? values[name] : name, ...(Object.prototype.hasOwnProperty.call(values, name) ? { value: true as const } : {}) });
    last = match.index + match[0].length;
  }
  if (last < sentence.length) parts.push({ text: sentence.slice(last) });
  return parts.length ? parts : [{ text: sentence }];
}

/** Placeholder values for `t()`: `t(key, markedParams(['account']))` returns the sentence with each value marked. */
export function markedParams(names: readonly string[]): Record<string, string> {
  return Object.fromEntries(names.map((n) => [n, `${MESSAGE_PART_MARK.open}${n}${MESSAGE_PART_MARK.close}`]));
}
