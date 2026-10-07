import { Fragment, type ReactNode } from 'react';

/**
 * Fill a translated template's {placeholders} with React nodes, so a code, email, URL or @domain
 * can sit inside an Arabic sentence as an isolated left-to-right run (<Ltr>). The i18n ratchet
 * recognises `fill(t('… {x} …'), { x })` and checks the names.
 */
export function fill(template: string, nodes: Record<string, ReactNode>): ReactNode {
  const parts = template.split(/(\{[A-Za-z0-9_]+\})/g);
  return parts.map((part, i) => {
    const m = /^\{([A-Za-z0-9_]+)\}$/.exec(part);
    return <Fragment key={i}>{m && m[1] in nodes ? nodes[m[1]] : part}</Fragment>;
  });
}

/** A code, email, URL or @domain: always left to right, isolated from the Arabic around it. */
export function Ltr({ children, className }: { children: ReactNode; className?: string }) {
  return <bdi dir="ltr" className={className}>{children}</bdi>;
}
