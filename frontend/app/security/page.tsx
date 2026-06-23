import type { Metadata } from 'next';
import Link from 'next/link';
import { Logo } from '@/src/components/Logo';

export const metadata: Metadata = {
  title: 'Security — KynexOne',
  description: 'How KynexOne protects your workforce data: encryption, access control, audit logging, and regional compliance.',
};

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="border-b border-slate-100 pb-10 last:border-0">
      <h2 className="mb-4 text-xl font-bold text-slate-900">{title}</h2>
      {children}
    </section>
  );
}

function Claim({ label, detail, code }: { label: string; detail: string; code?: string }) {
  return (
    <div className="flex gap-4 py-3">
      <span className="mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-emerald-100 text-emerald-700">
        <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
          <path d="M2 6l3 3 5-5" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </span>
      <div>
        <p className="font-semibold text-slate-900">{label}</p>
        <p className="mt-0.5 text-sm text-slate-500">{detail}</p>
        {code && (
          <p className="mt-1 font-mono text-[11px] text-slate-400">{code}</p>
        )}
      </div>
    </div>
  );
}

export default function SecurityPage() {
  return (
    <div className="min-h-screen bg-[#f8fafc]" dir="ltr">
      {/* Header */}
      <header className="sticky top-0 z-10 border-b border-slate-200 bg-white/90 backdrop-blur-sm">
        <div className="mx-auto flex h-14 max-w-4xl items-center justify-between px-6">
          <Link href="/login">
            <Logo size="sm" />
          </Link>
          <Link
            href="/login"
            className="text-sm font-medium text-slate-500 transition-colors hover:text-slate-900"
          >
            ← Back to sign in
          </Link>
        </div>
      </header>

      <main className="mx-auto max-w-4xl px-6 py-14">
        {/* Title block */}
        <div className="mb-12">
          <p className="mb-2 text-xs font-bold uppercase tracking-widest text-blue-600">Security</p>
          <h1 className="text-4xl font-black tracking-tight text-slate-900">How we protect your data</h1>
          <p className="mt-3 max-w-2xl text-base text-slate-500">
            Every claim on this page maps to a control that exists in the current platform. We do not publish
            aspirational certifications. This page is updated when controls are added or changed.
          </p>
        </div>

        <div className="space-y-10 text-[15px] leading-relaxed text-slate-700">

          <Section title="Data Protection">
            <Claim
              label="AES-256 encryption at rest"
              detail="All data is stored in Neon (PostgreSQL on AWS) which encrypts volumes at rest using AES-256. MFA secrets are additionally encrypted at the application layer before persisting to the database column mfa_secret_encrypted."
              code="ZayraDbContext → users.mfa_secret_encrypted"
            />
            <Claim
              label="TLS 1.2+ in transit"
              detail="All client-to-server traffic is served over HTTPS via Render.com (TLS 1.2 minimum enforced). Database connections from the API to Neon PostgreSQL use mandatory TLS (sslmode=require)."
            />
            <Claim
              label="Multi-tenant data isolation"
              detail="Every table that holds tenant-owned data implements ITenantOwned. An EF Core global query filter automatically scopes every query to the authenticated tenant's ID. This isolation is enforced at the ORM layer, not only at the application layer, and is covered by automated regression tests (Testcontainers, real PostgreSQL)."
              code="ZayraDbContext.OnModelCreating → HasQueryFilter(e => e.TenantId == _tenantId)"
            />
          </Section>

          <Section title="Access Control">
            <Claim
              label="Role-based access control (RBAC)"
              detail="Every protected action checks HasPermission() against the caller's JWT claims before executing. The frontend enforces the same boundaries through PermissionGate. Actions such as payroll export, GL journal access, and WPS file generation each require explicit permission grants."
              code={'PayrollController.HasPermission("payroll.export")'}
            />
            <Claim
              label="TOTP multi-factor authentication"
              detail="TOTP (RFC 6238 / Google Authenticator compatible) MFA is available for all user accounts. When MFA is configured, login issues a short-lived MFA challenge token; full session tokens are not issued until the TOTP code is verified. Failed TOTP attempts are counted and rate-limited."
              code="AuthContext → mfaPending flow · users.mfa_secret_encrypted"
            />
            <Claim
              label="Maker-checker on payroll approvals"
              detail="A payroll run cannot be approved by the same user who processed it (maker-checker enforced in the Approve endpoint). The full Validate → Process → Approve → Lock lifecycle is enforced in the correct sequence; the API rejects out-of-order transitions."
            />
            <Claim
              label="Soft-delete with immutable locking"
              detail="Payroll runs transition to a Locked state after approval. Locked runs reject all mutation attempts (Process, re-approve, delete) at the API level. Entities use soft-delete (IsDeleted flag) rather than hard-delete, preserving the audit trail."
            />
          </Section>

          <Section title="Auditability">
            <Claim
              label="Comprehensive audit logging"
              detail="Payroll, attendance, leave, overtime, and performance events each write to dedicated audit-log tables with actor ID, timestamp, and before/after context. Payroll-specific events (run processed, locked, WPS exported) are recorded in PayrollAuditLog and are tenant-scoped."
              code="ZayraDbContext → PayrollAuditLogs, LeaveAuditLogs, AttendanceAuditLogs, OvertimeAuditLogs, PerformanceAuditLogs"
            />
            <Claim
              label="Balanced GL journal on payroll processing"
              detail="Every payroll processing event generates a double-entry GL journal (salary expense debits + statutory liability credits + net payable credit). The API enforces that debits equal credits before persisting; the /gl-journal endpoint exposes the journal for external reconciliation."
            />
          </Section>

          <Section title="Compliance Posture">
            <p className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
              <strong>We do not claim to hold ISO 27001 or SOC 2 Type II certificates.</strong> The controls
              described on this page align with the spirit of those frameworks, but no external audit has been
              completed or is currently scheduled. Customers with strict certification requirements should
              evaluate accordingly.
            </p>
            <Claim
              label="KSA GOSI statutory compliance"
              detail="Saudi GOSI rates (9% EE annuity + 0.75% SANED, 11.75% ER for Saudis; 2% ER OH only for expats) are enforced in the calculation engine. Covered wage is capped at SAR 45,000 per KSA regulation. Statutory rule overrides can be configured per-tenant."
            />
            <Claim
              label="WPS (Mudad) export"
              detail="Payroll runs generate ISO 20022-formatted SIF files compatible with the UAE/GCC Wages Protection System (Mudad). Export is blocked when employee data fails WPS validation (invalid IBAN, missing government ID). File format follows the Mudad SIF specification."
            />
            <Claim
              label="EOSB calculation"
              detail="End-of-Service Benefit calculations follow GCC labour law rules (graduated accrual, pro-rata for partial years, leave encashment, notice-period deductions)."
            />
            <Claim
              label="Nitaqat / Saudization tracking"
              detail="Nationality-based headcount data is captured on employee profiles to support Nitaqat category reporting."
            />
          </Section>

          <Section title="Infrastructure and Availability">
            <Claim
              label="Hosted on AWS (via Neon + Render.com)"
              detail="The API runs on Render.com (Docker). The database is Neon PostgreSQL on AWS. Both providers publish their own security and availability documentation. We do not operate bare-metal infrastructure."
            />
            <Claim
              label="Automated deployments with integrity checks"
              detail="Every push to main triggers a Docker build and deploy via Render's pipeline. Pre-commit hooks and CI enforce linting and type-checking before code reaches production."
            />
          </Section>

          <Section title="Responsible Disclosure">
            <p className="text-slate-600">
              If you discover a security vulnerability, please email{' '}
              <a href="mailto:security@kodekinetics.com" className="font-medium text-blue-600 hover:underline">
                security@kodekinetics.com
              </a>
              . We aim to acknowledge reports within 2 business days and provide a fix timeline within 5.
              Please do not publicly disclose findings until we have had an opportunity to investigate and respond.
            </p>
          </Section>

        </div>

        <footer className="mt-16 border-t border-slate-200 pt-8 text-sm text-slate-400">
          <div className="flex flex-wrap items-center justify-between gap-4">
            <span>Last updated: June 2026</span>
            <div className="flex gap-6">
              <Link href="/privacy" className="hover:text-slate-600 transition-colors">Privacy Policy</Link>
              <Link href="/terms" className="hover:text-slate-600 transition-colors">Terms of Service</Link>
              <Link href="/login" className="hover:text-slate-600 transition-colors">Sign in</Link>
            </div>
          </div>
        </footer>
      </main>
    </div>
  );
}
