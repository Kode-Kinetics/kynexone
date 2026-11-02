import Link from 'next/link';

export default function NotFound() {
  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-4 bg-lightBg dark:bg-midnight">
      <p className="text-5xl font-semibold text-sapphire">404</p>
      <p className="text-slate-500 dark:text-slate-400">Page not found</p>
      <Link href="/dashboard" className="btn-primary text-sm">Go to Dashboard</Link>
    </div>
  );
}
