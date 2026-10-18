import Link from 'next/link';
import { PermissionGate } from '@/src/components/PermissionGate';

export default function Page() {
  return (
    <PermissionGate permissions={['employees.write']}>
      <main className="mx-auto max-w-7xl space-y-4 px-4 py-6">
        <Link href="/people" className="inline-flex min-h-11 items-center font-semibold text-sapphire hover:underline">Back to People</Link>
        <section className="surface p-6">
          <h1 className="text-2xl font-bold text-slate-950 dark:text-white">Add employee</h1>
          <p className="mt-2 text-sm text-slate-600 dark:text-slate-300">The guided employee creation flow opens in this workspace.</p>
        </section>
      </main>
    </PermissionGate>
  );
}
