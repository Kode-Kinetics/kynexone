import { test } from '@playwright/test';
import { personaForLogin, type Persona } from '../world';

/**
 * Records WHO a test acted as, in the test's own report.
 *
 * A journey that "passed" says nothing unless the report also says whose session it ran under: an
 * HR Manager seeing a payroll run and an Admin seeing it are different facts, and a lane that fell
 * back to a different account (the F07 failure mode) would otherwise look identical in the output.
 *
 * Every shared login helper (e2e/helpers.ts, the security gate's `apiAs`) calls `stampActor`, so a
 * spec that signs in through them is stamped without doing anything. The stamp is:
 *   • a test annotation `{ type: 'actor', description }` — in the HTML/JSON report, and printed per
 *     test by e2e/identity/actor-reporter.ts in the CI log;
 *   • nothing else. Use `asActor` to also wrap a block in a named `test.step`.
 *
 * Outside a running test (setup code, global setup) there is no test to stamp and this is a no-op.
 */

export interface ActorLike {
  email: string;
  tenantSlug: string | null;
  /** Optional context the registry cannot know, e.g. the security gate's role key. */
  via?: string;
}

export function describeActor(persona: Persona | undefined, who: ActorLike): string {
  const via = who.via ? ` via ${who.via}` : '';
  if (!persona) {
    // Still recorded, and flagged: an identity that is not in the registry is exactly what F07 was.
    return `UNREGISTERED ${who.email} @ ${who.tenantSlug ?? 'platform'}${via}`;
  }
  const scope = persona.scope === 'companies' ? `companies=${persona.companyCodes.join('+')}` : persona.scope;
  const role = persona.role ?? 'platform owner';
  return `${persona.email} @ ${persona.tenantSlug ?? 'platform'} · ${role} · ${scope}`
    + `${persona.employeeLinked ? ' · employee-linked' : ''}${via}`;
}

export function stampActor(who: ActorLike): string {
  const description = describeActor(personaForLogin(who.email, who.tenantSlug), who);
  let info: ReturnType<typeof test.info>;
  try {
    info = test.info();
  } catch {
    return description;
  }
  if (!info.annotations.some((a) => a.type === 'actor' && a.description === description)) {
    info.annotations.push({ type: 'actor', description });
  }
  return description;
}

/** Stamp the actor and run `body` inside a `test.step` named after it. */
export async function asActor<T>(who: ActorLike, body: () => Promise<T>): Promise<T> {
  const description = stampActor(who);
  return test.step(`as ${description}`, body);
}
