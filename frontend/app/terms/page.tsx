import type { Metadata } from 'next';
import Link from 'next/link';
import { Logo } from '@/src/components/Logo';

export const metadata: Metadata = {
  title: 'Terms of Service — KynexOne',
  description: 'Terms governing use of the KynexOne workforce management platform.',
};

const EFFECTIVE_DATE = 'June 2025';

export default function TermsPage() {
  return (
    <div className="min-h-screen bg-[#f8fafc]">
      <header className="sticky top-0 z-10 border-b border-slate-200 bg-white/90 backdrop-blur-sm">
        <div className="mx-auto flex h-14 max-w-4xl items-center justify-between px-6">
          <Link href="/login">
            <Logo size="sm" />
          </Link>
          <Link href="/login" className="text-sm font-medium text-slate-500 hover:text-slate-900 transition-colors">
            ← Back to sign in
          </Link>
        </div>
      </header>

      <main className="mx-auto max-w-4xl px-6 py-14">
        <div className="mb-12">
          <p className="mb-2 text-xs font-bold uppercase tracking-widest text-blue-600">Legal</p>
          <h1 className="text-4xl font-black tracking-tight text-slate-900">Terms of Service</h1>
          <p className="mt-3 text-base text-slate-500">
            Effective date: <span className="font-medium text-slate-700">{EFFECTIVE_DATE}</span>
          </p>
        </div>

        <div className="space-y-10 text-[15px] leading-relaxed text-slate-700">

          <Section title="1. Acceptance">
            <p>
              By accessing or using KynexOne (the &ldquo;Service&rdquo;), operated by <strong>Kode Kinetics</strong>,
              you agree to be bound by these Terms. If you are using the Service on behalf of an organisation,
              you represent that you have authority to bind that organisation to these Terms.
            </p>
          </Section>

          <Section title="2. Permitted Use">
            <p>
              KynexOne is a business-to-business platform for workforce management. You may use it only for
              lawful purposes and in accordance with these Terms. You must not use the Service to:
            </p>
            <ul className="ms-5 mt-3 list-disc space-y-1.5">
              <li>Violate any applicable law or regulation.</li>
              <li>Upload or transmit malware, viruses, or malicious code.</li>
              <li>Attempt to gain unauthorised access to systems or other tenants&apos; data.</li>
              <li>Reverse-engineer, decompile, or extract source code from the platform.</li>
              <li>Resell or sublicense access to the Service without written consent.</li>
            </ul>
          </Section>

          <Section title="3. Accounts & Access">
            <p>
              You are responsible for maintaining the confidentiality of your credentials and for all activity
              that occurs under your account. Notify us immediately at{' '}
              <a href="mailto:security@kodekinetics.com" className="text-blue-600 underline">security@kodekinetics.com</a>{' '}
              if you suspect unauthorised access.
            </p>
          </Section>

          <Section title="4. Data Ownership">
            <p>
              All employee and organisational data you input into KynexOne remains your property. You grant
              Kode Kinetics a limited licence to process that data solely to provide the Service. We do not
              claim ownership of your data and will not use it for our own commercial purposes.
            </p>
          </Section>

          <Section title="5. Service Availability">
            <p>
              We target 99.9% monthly uptime for core services. Planned maintenance will be communicated
              at least 48 hours in advance. We are not liable for downtime caused by third-party infrastructure
              providers, force majeure events, or actions outside our reasonable control.
            </p>
          </Section>

          <Section title="6. Fees & Payment">
            <p>
              Subscription fees are agreed in your order form or customer agreement. Fees are due in advance
              and non-refundable except as expressly stated. We reserve the right to suspend access for
              accounts more than 30 days overdue.
            </p>
          </Section>

          <Section title="7. Intellectual Property">
            <p>
              KynexOne, its design, features, and underlying technology are the intellectual property of
              Kode Kinetics. These Terms do not transfer any IP rights to you. Feedback or suggestions you
              provide may be used by us without obligation.
            </p>
          </Section>

          <Section title="8. Confidentiality">
            <p>
              Each party agrees to keep the other&apos;s confidential information private and to use it only
              to fulfil obligations under these Terms. This obligation survives termination for three years.
            </p>
          </Section>

          <Section title="9. Limitation of Liability">
            <p>
              To the maximum extent permitted by applicable law, Kode Kinetics&apos; total liability to you
              for any claim arising out of or relating to these Terms or the Service shall not exceed the
              fees paid by you in the 12 months preceding the claim. We are not liable for indirect,
              incidental, consequential, or punitive damages.
            </p>
          </Section>

          <Section title="10. Termination">
            <p>
              Either party may terminate the Service agreement with 30 days&apos; written notice. We may
              terminate or suspend access immediately if you breach these Terms, fail to pay, or engage in
              activity that poses a security or legal risk. Upon termination you may request an export of
              your data within 60 days.
            </p>
          </Section>

          <Section title="11. Governing Law">
            <p>
              These Terms are governed by the laws of the jurisdiction agreed in your Customer agreement. In
              the absence of a specific agreement, the laws of the United Arab Emirates apply. Disputes will
              first be subject to good-faith negotiation, then binding arbitration.
            </p>
          </Section>

          <Section title="12. Changes to These Terms">
            <p>
              We may update these Terms at any time. Material changes will be communicated with at least 30
              days&apos; notice. Continued use after that date constitutes acceptance.
            </p>
          </Section>

          <Section title="13. Contact">
            <div className="rounded-2xl border border-slate-200 bg-white p-6 text-sm">
              <p className="mb-3 font-semibold text-slate-900">Kode Kinetics — Legal Team</p>
              <p>Email: <a href="mailto:legal@kodekinetics.com" className="text-blue-600 hover:underline">legal@kodekinetics.com</a></p>
            </div>
          </Section>

        </div>
      </main>

      <footer className="border-t border-slate-200 bg-white py-8 text-center text-[13px] text-slate-400">
        &copy; {new Date().getFullYear()} Kode Kinetics. All rights reserved.
        &ensp;·&ensp;
        <Link href="/privacy" className="hover:text-slate-600 transition-colors">Privacy Policy</Link>
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
