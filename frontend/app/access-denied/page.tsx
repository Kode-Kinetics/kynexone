import Link from 'next/link';

export default function AccessDenied() {
  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-4 bg-lightBg dark:bg-midnight">
      <p className="text-5xl font-semibold text-red-500">403</p>
      <p className="text-xl font-semibold text-slate-700 dark:text-slate-200">Access Denied</p>
      <p className="text-slate-500 dark:text-slate-400 text-center max-w-sm">
        You do not have permission to view this page. Contact your administrator if you believe this is an error.
      </p>
      <Link href="/dashboard" className="btn-primary text-sm">Go to Dashboard</Link>
    </div>
  );
}
