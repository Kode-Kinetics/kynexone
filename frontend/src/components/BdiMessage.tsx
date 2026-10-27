'use client';

import { Fragment } from 'react';
import { useT } from '../hooks/useT';
import { markedParams, messageParts } from '../lib/messageParts';

/**
 * A translated sentence whose interpolated values (account codes and names, cost-centre codes) each sit in their own
 * `<bdi>`: a Latin code beside Arabic words keeps its own order instead of jumbling the line. `<bdi>` is an element, so
 * the text content of the sentence is exactly what the translation says.
 */
export function BdiMessage({ message, values, className, testId }: { message: string; values: Record<string, string>; className?: string; testId?: string }) {
  const t = useT();
  const parts = messageParts(t(message, markedParams(Object.keys(values))), values);
  return (
    <span className={className} data-testid={testId}>
      {parts.map((p, i) => (p.value ? <bdi key={i}>{p.text}</bdi> : <Fragment key={i}>{p.text}</Fragment>))}
    </span>
  );
}
