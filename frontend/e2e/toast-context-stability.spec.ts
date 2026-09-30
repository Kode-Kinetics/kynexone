import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');

/**
 * THE DEFECT: AppToastProvider built its context value as a fresh object literal on every render.
 * PermissionGate lists that value in its effect's dependencies and posts an "Access Denied" toast
 * from the effect. Posting a toast re-renders the provider, which hands every consumer a new object,
 * which re-runs the gate's effect, which posts another toast — a loop that only ends when the
 * redirect unmounts the gate, and that React reports as "Maximum update depth exceeded". The same
 * churn rebuilt useApiCall's `call` after every toast anywhere on the page.
 *
 * The fix is referential stability, so that is what is pinned: the value is memoized over stable
 * callbacks, and the consumers that depend on it are named so the reason survives a refactor.
 */
test.describe('AppToast context — referential stability', () => {
  test('the provider memoizes its value over stable helpers', () => {
    const source = read('src/components/ui/AppToast.tsx');

    expect(source).toMatch(/const ctx = useMemo<AppToastCtx>\(\(\) => \(\{/);
    expect(source).toContain('[showToast, success, error, warn, info]');
    for (const helper of ['success', 'error', 'warn', 'info']) {
      expect(source).toMatch(new RegExp(`const ${helper} = useCallback\\(`));
    }
    // A plain object literal assigned to ctx is exactly the regression.
    expect(source).not.toMatch(/const ctx: AppToastCtx = \{/);
  });

  test('the consumers that make stability load-bearing still depend on the value', () => {
    // If these stop depending on the toast object the memo is merely tidy; while they do, it is required.
    expect(read('src/components/PermissionGate.tsx')).toContain('[user, hasAccess, router, toast]');
    expect(read('src/hooks/useApiCall.ts')).toMatch(/\[toast\],?\s*\)/);
  });
});
