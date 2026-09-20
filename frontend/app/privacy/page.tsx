import type { Metadata } from 'next';
import Link from 'next/link';
import { Logo } from '@/src/components/Logo';

export const metadata: Metadata = {
  title: 'Privacy Policy — KynexOne',
  description: 'How KynexOne collects, uses, and protects your data.',
};

const EFFECTIVE_DATE = 'September 2026';

export default function PrivacyPolicyPage() {
  return (
    <div className="min-h-screen bg-[#f8fafc]">
      {/* Header */}
      <header className="sticky top-0 z-10 border-b border-slate-200 bg-white/90 backdrop-blur-sm">
        <div className="mx-auto flex h-14 max-w-4xl items-center justify-between px-6">
          <Link href="/login">
            <Logo size="sm" />
          </Link>
          <Link
            href="/login"
            className="text-sm font-medium text-slate-500 hover:text-slate-900 transition-colors"
          >
            ← Back to sign in
          </Link>
        </div>
      </header>

      <main className="mx-auto max-w-4xl px-6 py-14">
        {/* Title block */}
        <div className="mb-12">
          <p className="mb-2 text-xs font-bold uppercase tracking-widest text-blue-600">Legal</p>
          <h1 className="text-4xl font-black tracking-tight text-slate-900">Privacy Policy</h1>
          <p className="mt-3 text-base text-slate-500">
            Effective date: <span className="font-medium text-slate-700">{EFFECTIVE_DATE}</span>
            &ensp;·&ensp;Last updated: <span className="font-medium text-slate-700">{EFFECTIVE_DATE}</span>
          </p>
        </div>

        <div className="space-y-10 text-[15px] leading-relaxed text-slate-700">

          {/* Intro */}
          <Section title="1. Who We Are">
            <p>
              KynexOne is a workforce management platform operated by <strong>Kode Kinetics</strong>
              (&ldquo;we&rdquo;, &ldquo;us&rdquo;, or &ldquo;our&rdquo;). We provide HR, payroll, attendance,
              recruitment, compliance, and related services to organisations (&ldquo;Customers&rdquo;) and
              their employees and contractors (&ldquo;End Users&rdquo;).
            </p>
            <p className="mt-3">
              This Privacy Policy explains how we collect, use, share, and protect personal data when you
              use KynexOne services, and describes the rights you have regarding your data.
            </p>
          </Section>

          {/* Data we collect */}
          <Section title="2. Data We Collect">
            <p className="mb-3">We collect the following categories of personal data:</p>
            <Table rows={[
              ['Account & Identity', 'Full name, work email address, job title, employee ID, profile photo.'],
              ['Authentication', 'Password hash (never stored in plain text), session tokens, MFA credentials, login timestamps and IP addresses.'],
              ['HR Records', 'Employment dates, department, branch, salary band, contract type, performance reviews, leave records, disciplinary notes.'],
              ['Payroll', 'Bank account details (access-controlled and masked in user-facing responses), salary, allowances, deductions, tax identification numbers, payslip data.'],
              ['Attendance', 'Clock-in / clock-out timestamps, biometric device identifiers (hashed), geolocation data (where permitted), device IP.'],
              ['Documents', 'Uploaded employee documents including ID copies, visas, certificates, and expiry dates.'],
              ['Communication', 'Messages sent through in-platform support and HR request workflows.'],
              ['Usage & Technical', 'Browser type, operating system, pages visited, feature usage, API call logs, error reports.'],
            ]} />
          </Section>

          {/* How we use it */}
          <Section title="3. How We Use Your Data">
            <p className="mb-3">We use personal data to:</p>
            <ul className="ml-5 list-disc space-y-1.5">
              <li>Provide, operate, and maintain the KynexOne platform.</li>
              <li>Process payroll, calculate deductions, and generate payslips.</li>
              <li>Track attendance, calculate working hours, and support regularisations.</li>
              <li>Manage leave requests, approvals, and entitlement balances.</li>
              <li>Enforce role-based access controls and audit trail requirements.</li>
              <li>Send transactional notifications (payslips, approvals, password resets).</li>
              <li>Comply with applicable labour law, tax regulation, and statutory reporting obligations.</li>
              <li>Detect and prevent fraud, abuse, and security incidents.</li>
              <li>Improve our services through aggregated, anonymised analytics.</li>
            </ul>
          </Section>

          {/* Legal basis */}
          <Section title="4. Legal Basis for Processing">
            <p className="mb-3">
              Where data protection law requires a legal basis, we rely on:
            </p>
            <Table rows={[
              ['Contract performance', 'Processing necessary to deliver the services agreed in the Customer contract.'],
              ['Legal obligation', 'Processing required by applicable labour, tax, or employment law.'],
              ['Legitimate interests', 'Security monitoring, fraud prevention, product improvement (where not overridden by individual rights).'],
              ['Consent', 'Optional features (e.g., geolocation punch-in) where explicit consent is collected.'],
            ]} />
          </Section>

          {/* Data sharing */}
          <Section title="5. Data Sharing & Sub-processors">
            <p className="mb-3">
              We do <strong>not</strong> sell personal data. We share data only:
            </p>
            <ul className="ml-5 list-disc space-y-1.5">
              <li>
                <strong>With your employer (the Customer)</strong> — your organisation controls your HR data
                through the platform. Kode Kinetics acts as a data processor on their behalf.
              </li>
              <li>
                <strong>With vetted sub-processors</strong> — cloud infrastructure, email delivery, and
                monitoring tools bound by data processing agreements. A current list of sub-processors is
                available on request.
              </li>
              <li>
                <strong>As required by law</strong> — when compelled by a valid legal process, court order,
                or regulatory authority.
              </li>
              <li>
                <strong>Business transfers</strong> — in connection with a merger, acquisition, or asset sale,
                subject to equivalent privacy protections.
              </li>
            </ul>
          </Section>

          {/* Data residency */}
          <Section title="6. Data Residency & International Transfers">
            <p>
              Data residency depends on the database, object-storage, and application regions selected for a
              customer&apos;s deployment. The public service does not promise self-service region selection.
              Any cross-border transfer mechanism, including Standard Contractual Clauses where applicable,
              must be confirmed in the applicable customer agreement or data-processing addendum before reliance.
            </p>
          </Section>

          {/* Security */}
          <Section title="7. Security">
            <p className="mb-3">
              The current platform implements the following technical controls. Exact provider and tenant
              configuration should be confirmed for the deployment in use:
            </p>
            <ul className="ml-5 list-disc space-y-1.5">
              <li>Provider-managed encryption at rest for hosted database and durable object storage.</li>
              <li>Application-layer protection for selected secrets, including MFA and configured integration credentials.</li>
              <li>HTTPS for browser-to-service traffic and a TLS-required API-to-database connection.</li>
              <li>Role-based access control (RBAC) with principle of least privilege.</li>
              <li>Audit records for designated payroll, leave, attendance, overtime, performance, and administrative workflows.</li>
              <li>Automated dependency, secret, static-analysis, and regression checks in the repository CI workflows.</li>
              <li>Independent penetration-test scope and cadence are confirmed separately for each customer engagement.</li>
            </ul>
            <p className="mt-3 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
              KynexOne does not claim ISO 27001 or SOC 2 Type II certification. Framework references describe
              a control-improvement direction and are not third-party assurance.
            </p>
            <p className="mt-3">
              Despite these measures, no system is completely secure. If you believe your data has been
              compromised, contact us immediately at{' '}
              <a href="mailto:security@kodekinetics.com" className="text-blue-600 underline">
                security@kodekinetics.com
              </a>.
            </p>
          </Section>

          {/* Retention */}
          <Section title="8. Data Retention">
            <Table rows={[
              ['Active employee records', 'Duration of employment + period required by applicable labour law (typically 5–7 years).'],
              ['Payroll records', 'Minimum 7 years or as required by tax authority in the relevant jurisdiction.'],
              ['Attendance & leave logs', '3 years after the record date, unless longer retention is required by law.'],
              ['Audit logs', '2 years from event date.'],
              ['Authentication tokens', 'Session tokens expire within 24 hours; refresh tokens within 30 days.'],
              ['Deleted accounts', 'Anonymised within 90 days of account closure, except where legal hold applies.'],
            ]} />
          </Section>

          {/* Your rights */}
          <Section title="9. Your Rights">
            <p className="mb-3">
              Depending on your jurisdiction, you may have the right to:
            </p>
            <ul className="ml-5 list-disc space-y-1.5">
              <li><strong>Access</strong> — request a copy of the personal data we hold about you.</li>
              <li><strong>Rectification</strong> — correct inaccurate or incomplete data.</li>
              <li><strong>Erasure</strong> — request deletion where no legal obligation requires retention.</li>
              <li><strong>Restriction</strong> — limit processing in certain circumstances.</li>
              <li><strong>Portability</strong> — receive your data in a machine-readable format.</li>
              <li><strong>Object</strong> — object to processing based on legitimate interests.</li>
              <li><strong>Withdraw consent</strong> — for processing based on consent only.</li>
            </ul>
            <p className="mt-3">
              Because KynexOne processes personal data on behalf of your employer, many of these rights
              should first be directed to your employer (the data controller). To exercise rights directly
              with Kode Kinetics, email{' '}
              <a href="mailto:privacy@kodekinetics.com" className="text-blue-600 underline">
                privacy@kodekinetics.com
              </a>. We will respond within 30 days.
            </p>
          </Section>

          {/* Cookies */}
          <Section title="10. Cookies & Tracking">
            <p>
              KynexOne uses strictly necessary cookies and session tokens required for authentication and
              security. We do not use third-party advertising cookies or cross-site tracking. A detailed
              cookie inventory is available within the platform settings under <em>Privacy &amp; Cookies</em>.
            </p>
          </Section>

          {/* Children */}
          <Section title="11. Children's Privacy">
            <p>
              KynexOne is a business platform intended for use by employed adults. We do not knowingly collect
              data from individuals under 16 years of age. If you believe a minor's data has been collected
              in error, contact us and we will delete it promptly.
            </p>
          </Section>

          {/* Changes */}
          <Section title="12. Changes to This Policy">
            <p>
              We may update this Privacy Policy from time to time. Where changes are material, we will notify
              Customers via in-platform notice or email at least 30 days before they take effect. Continued
              use of the platform after the effective date constitutes acceptance of the updated policy.
            </p>
          </Section>

          {/* Contact */}
          <Section title="13. Contact & Data Protection Officer">
            <div className="rounded-2xl border border-slate-200 bg-white p-6">
              <p className="mb-4 font-semibold text-slate-900">Kode Kinetics — Privacy Team</p>
              <dl className="space-y-2 text-sm">
                <Row label="General privacy enquiries" value="privacy@kodekinetics.com" href="mailto:privacy@kodekinetics.com" />
                <Row label="Security disclosures" value="security@kodekinetics.com" href="mailto:security@kodekinetics.com" />
                <Row label="Data processing agreements" value="legal@kodekinetics.com" href="mailto:legal@kodekinetics.com" />
              </dl>
              <p className="mt-4 text-sm text-slate-500">
                If you have an unresolved concern, you have the right to lodge a complaint with the data
                protection authority in your country of residence.
              </p>
            </div>
          </Section>

        </div>
      </main>

      <footer className="border-t border-slate-200 bg-white py-8 text-center text-[13px] text-slate-400">
        &copy; {new Date().getFullYear()} Kode Kinetics. All rights reserved.
        &ensp;·&ensp;
        <Link href="/login" className="hover:text-slate-600 transition-colors">Back to sign in</Link>
      </footer>
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section>
      <h2 className="mb-4 text-xl font-bold text-slate-900">{title}</h2>
      {children}
    </section>
  );
}

function Table({ rows }: { rows: [string, string][] }) {
  return (
    <div className="overflow-hidden rounded-xl border border-slate-200">
      <table className="w-full text-sm">
        <tbody>
          {rows.map(([label, desc], i) => (
            <tr key={i} className={i % 2 === 0 ? 'bg-white' : 'bg-slate-50'}>
              <td className="w-48 border-r border-slate-100 px-4 py-3 font-semibold text-slate-800 align-top">{label}</td>
              <td className="px-4 py-3 text-slate-600">{desc}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Row({ label, value, href }: { label: string; value: string; href: string }) {
  return (
    <div className="flex gap-3">
      <dt className="w-44 shrink-0 text-slate-500">{label}</dt>
      <dd>
        <a href={href} className="font-medium text-blue-600 hover:underline">{value}</a>
      </dd>
    </div>
  );
}
