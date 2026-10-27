/**
 * Unicode isolates for a value dropped into a sentence of the other direction (an English name or a time inside an Arabic sentence).
 * FSI (first-strong isolate) lets the value take its own direction; LRI forces left-to-right for times and codes. PDI closes either.
 * Plain strings, so they work wherever a translated sentence is built from parts.
 */
const FSI = '⁨', LRI = '⁦', PDI = '⁩';
export const isolateName = (value: string) => (value ? `${FSI}${value}${PDI}` : '');
export const isolateLtr = (value: string) => (value ? `${LRI}${value}${PDI}` : '');
