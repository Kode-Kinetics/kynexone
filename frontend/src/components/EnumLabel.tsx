'use client';

/** Renders a backend enum value in the viewer's language. The lookup rules live in i18n/enumLabel.ts. */

import { useT } from '../hooks/useT';
import { enumLabel, type EnumName } from '../i18n/enumLabel';

export { enumKey, enumLabel, humaniseEnumValue, normaliseEnumValue, type EnumName } from '../i18n/enumLabel';

export function EnumLabel({ enum: enumName, value }: { enum: EnumName; value: string | null | undefined }) {
  const t = useT();
  return <>{enumLabel(t, enumName, value)}</>;
}
