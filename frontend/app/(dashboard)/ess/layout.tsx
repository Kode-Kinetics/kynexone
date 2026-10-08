import { EssWorkspaceNav } from '@/src/components/ess/EssWorkspaceNav';

/** Every /ess page sits inside the employee's workspace: its sections above, the page below. */
export default function EssLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="mx-auto max-w-[1400px] space-y-5">
      <EssWorkspaceNav />
      {children}
    </div>
  );
}
