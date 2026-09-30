import type { FullConfig, Reporter, Suite, TestCase, TestResult } from '@playwright/test/reporter';

/**
 * Prints the actor(s) each test ran as, next to its outcome, so the CI log answers "passed as whom?"
 * without opening the HTML report. Reads the `actor` annotations e2e/identity/actor.ts stamps.
 *
 * A test with no stamp is printed too, with the session its project injects by default (the
 * `storageState` file), because "no actor recorded" is itself worth seeing.
 */
export default class ActorReporter implements Reporter {
  private rows: string[] = [];

  onBegin(_config: FullConfig, _suite: Suite): void {
    this.rows = [];
  }

  onTestEnd(test: TestCase, result: TestResult): void {
    const actors = [...result.annotations, ...test.annotations]
      .filter((a) => a.type === 'actor' && a.description)
      .map((a) => a.description as string);
    const unique = [...new Set(actors)];
    let who: string;
    if (unique.length) {
      who = unique.join(' | ');
    } else {
      const state = test.parent.project()?.use?.storageState;
      who = typeof state === 'string' ? `(no actor stamped; project session ${state})` : '(no actor stamped)';
    }
    const project = test.parent.project()?.name ?? '';
    this.rows.push(`  ${result.status.padEnd(8)} [${project}] ${test.title}\n           as ${who}`);
  }

  onEnd(): void {
    if (!this.rows.length) return;
    console.log(`\n── Actors (who each test ran as) ${'─'.repeat(40)}\n${this.rows.join('\n')}\n`);
  }

  printsToStdio(): boolean {
    return true;
  }
}
