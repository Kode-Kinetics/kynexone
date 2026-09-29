import type { Metadata } from 'next';
import Link from 'next/link';
import { Logo } from '@/src/components/Logo';

/*
 * THIS PAGE DESCRIBES THE SYSTEM AS SHIPPED, NOT AS PLANNED.
 *
 * Every sentence here must be true of production today. The previous version promised
 * anonymisation within 90 days, a litigation-hold exception, a cookie inventory screen, consent
 * records, masked bank details and 24-hour sessions — none of which the product did. A published
 * promise that nothing in the code enforces is the same defect ApprovalPoliciesController
 * refuses to recreate: "configuration that is stored but never applied".
 *
 * Before changing a sentence, check the code that makes it true. The claim-by-claim evidence
 * (file:line for every statement kept, the absence for every statement removed) and the list
 * of points a lawyer must decide are in scratchpad/privacy-policy.md.
 *
 * Several statements depend on live Render configuration that the repository cannot show:
 * AI_PROVIDER / AI_MODEL (the model named in section 5), Storage__Region (section 6), and the
 * absence of DataRetention__* and QIWA_USE_LIVE_ADAPTER overrides (sections 4 and 8). If any
 * of those change, this page is wrong until it is updated.
 *
 * The page is pinned dir="ltr" lang="en" like app/security/page.tsx: it is English-only, and
 * English prose laid out right-to-left misplaces its punctuation. Layout uses logical
 * properties throughout, so a translation can drop the pin and mirror correctly.
 */

export const metadata: Metadata = {
  title: 'Privacy Policy — KynexOne',
  description:
    'What the KynexOne platform does with personal data today: what it holds, who receives it, where it is stored, how long it is kept, and what you can do about it.',
};

const EFFECTIVE_DATE = '21 September 2026';

const SECTIONS = [
  ['who', '1. Who we are and who controls your data'],
  ['collect', '2. What the platform holds'],
  ['use', '3. What the data is used for'],
  ['share', '4. Who else receives the data'],
  ['ai', '5. AI features'],
  ['residency', '6. Where the data is stored'],
  ['security', '7. Security'],
  ['retention', '8. What is kept, and for how long'],
  ['rights', '9. Your rights, and what the platform lets you do'],
  ['cookies', '10. Cookies, browser storage and tracking'],
  ['others', '11. Children and other people'],
  ['changes', '12. Changes to this page'],
  ['contact', '13. Contact'],
] as const;

type SectionId = (typeof SECTIONS)[number][0];

function title(id: SectionId) {
  return SECTIONS.find(([key]) => key === id)![1];
}

export default function PrivacyPolicyPage() {
  return (
    <div className="min-h-screen bg-[#f8fafc] text-slate-700 dark:bg-slate-950 dark:text-slate-300" dir="ltr" lang="en">
      <header className="sticky top-0 z-10 border-b border-slate-200 bg-white/90 backdrop-blur-sm dark:border-slate-800 dark:bg-slate-900/90">
        <div className="mx-auto flex h-14 max-w-4xl items-center justify-between gap-4 px-4 sm:px-6">
          <Link href="/login" aria-label="KynexOne sign in">
            <Logo size="sm" />
          </Link>
          <Link
            href="/login"
            className="text-sm font-medium text-slate-500 transition-colors hover:text-slate-900 dark:text-slate-400 dark:hover:text-white"
          >
            ← Back to sign in
          </Link>
        </div>
      </header>

      <main className="mx-auto max-w-4xl px-4 py-10 sm:px-6 sm:py-14">
        <div className="mb-10">
          <p className="mb-2 text-xs font-bold uppercase tracking-widest text-blue-600 dark:text-blue-400">Legal</p>
          <h1 className="text-3xl font-black tracking-tight text-slate-900 sm:text-4xl dark:text-white">Privacy Policy</h1>
          <p className="mt-3 text-base text-slate-500 dark:text-slate-400">
            Effective and last updated:{' '}
            <span className="font-medium text-slate-700 dark:text-slate-200">{EFFECTIVE_DATE}</span>
          </p>
          <p className="mt-4 max-w-2xl text-[15px] leading-relaxed">
            This page describes what the KynexOne platform does with personal data as it operates on the date
            above, including where what it does is limited. It does not replace the data-processing terms in your
            organisation&apos;s agreement with Kode Kinetics.
          </p>
        </div>

        <nav
          aria-label="On this page"
          className="mb-12 rounded-xl border border-slate-200 bg-white p-4 text-sm dark:border-slate-800 dark:bg-slate-900"
        >
          <p className="mb-2 font-semibold text-slate-900 dark:text-white">On this page</p>
          <ol className="grid gap-x-6 gap-y-1 sm:grid-cols-2">
            {SECTIONS.map(([id, label]) => (
              <li key={id}>
                <a href={`#${id}`} className="text-blue-700 hover:underline dark:text-blue-400">
                  {label}
                </a>
              </li>
            ))}
          </ol>
        </nav>

        <div className="space-y-12 text-[15px] leading-relaxed">
          <Section id="who">
            <p>
              KynexOne is a workforce platform operated by <strong>Kode Kinetics</strong> (&ldquo;we&rdquo;,
              &ldquo;us&rdquo;). Organisations (&ldquo;customers&rdquo;) use it to run HR, payroll, attendance,
              leave, recruitment and related work for their employees, job candidates and other people.
            </p>
            <p className="mt-3">
              Your employer, or the organisation that gave you access, decides what data is entered into KynexOne
              and what it is used for. We run the platform on that organisation&apos;s behalf. Questions about why
              your data is held, and most requests about it, should go to that organisation first.
            </p>
          </Section>

          <Section id="collect">
            <p className="mb-4">
              Depending on what your organisation uses and fills in, the platform can hold the following. Your
              organisation may not use every field.
            </p>
            <DefinitionTable
              rows={[
                ['Identity and contact', 'Full name in English and Arabic, preferred name, employee code, work and personal email address, phone number, profile photo.'],
                ['Personal details', 'Gender, date of birth, marital status, nationality, and whether you are Saudi or non-Saudi.'],
                ['Government identifiers', 'Iqama, passport, visa, national ID, residency, work-permit and labour-card numbers with their issue and expiry dates; GOSI and Qiwa references; sponsor name.'],
                ['Employment', 'Department, job title, grade, manager, work location, contract type and dates, probation and notice period, performance reviews and goals, disciplinary records, termination reason.'],
                ['Pay and bank details', 'Exact salary, allowance and deduction amounts, bank name, IBAN and account details, payslips, end-of-service and final-settlement figures.'],
                ['Health', 'A free-text medical information field, where your organisation records it.'],
                ['Other people', 'Your emergency contact’s name and phone number. Your dependants’ names, relationship to you, national ID numbers, dates of birth and visa expiry dates.'],
                ['Attendance', 'Clock-in and clock-out times, the device or method used, and the IP address the punch came from. Punches from the mobile app can include your location (latitude and longitude). Where an attendance device supplies one, a reference to a photo taken at the punch, and the raw data the device sent.'],
                ['Documents', 'Files uploaded by HR or by you, for example ID copies, visas, contracts and certificates, with their expiry dates.'],
                ['Requests and questions', 'HR requests and the comments exchanged on them. Questions asked of the AI features and the answers given (section 5).'],
                ['Recruitment', 'Job candidates’ names, current job title, experience, education, skills and application records.'],
                ['Sign-in and security', 'Email address, password hash and multi-factor authentication settings. Every sign-in attempt, successful or not, with the email address entered, IP address, browser user-agent string and time. The IP address and user-agent are also recorded on audited actions. For the mobile app, a device identifier and push-notification token.'],
              ]}
            />
            <p className="mt-4">
              The platform does not record which pages you visit or which features you use, and contains no
              analytics.
            </p>
          </Section>

          <Section id="use">
            <p className="mb-3">The platform uses this data to:</p>
            <ul className="ms-5 list-disc space-y-1.5">
              <li>run the HR, payroll, attendance, leave, performance and recruitment work your organisation uses it for, including payslips, WPS salary files, GOSI contributions and end-of-service calculations;</li>
              <li>calculate Saudization (Nitaqat) figures from nationality;</li>
              <li>decide who can see and change what, and keep an audit record of changes;</li>
              <li>send the notifications your organisation has set up (section 4);</li>
              <li>produce AI-generated answers and suggestions when an authorised user uses an AI feature (section 5);</li>
              <li>record sign-in attempts and detect reuse of sign-in tokens.</li>
            </ul>
            <p className="mt-3">
              We do not sell personal data and do not use it for advertising. Your organisation determines the
              legal basis on which your data is processed; this page does not state one on its behalf.
            </p>
          </Section>

          <Section id="share">
            <p className="mb-4">
              These third parties receive personal data because the platform cannot run without them:
            </p>
            <div className="grid gap-3">
              <Provider
                name="Render"
                does="Runs the KynexOne application server."
                receives="All data, while the server processes it."
                where="Outside the Kingdom of Saudi Arabia."
              />
              <Provider
                name="Neon"
                does="Hosts the database."
                receives="Everything listed in section 2 except uploaded files."
                where="Outside the Kingdom of Saudi Arabia."
              />
              <Provider
                name="Backblaze (B2)"
                does="Stores uploaded documents, profile photos and payslip-template logos."
                receives="The files themselves."
                where="United States."
              />
              <Provider
                name="Vercel"
                does="Serves the web application and relays every request between your browser and the application server."
                receives="All data shown or entered in the web application, in transit, plus your IP address and browser details."
                where="Vercel’s network; not confined to the Kingdom."
              />
              <Provider
                name="Ollama (Ollama Cloud)"
                does="Runs the AI model used by the AI features."
                receives="The data described in section 5."
                where="Ollama’s infrastructure. We do not control where it processes requests."
              />
              <Provider
                name="Google (Google Fonts)"
                does="Supplies the typefaces the pages use."
                receives="Your IP address and browser details, each time a page loads."
                where="Google’s network."
              />
            </div>

            <p className="mb-3 mt-6">These receive data only if your organisation turns them on:</p>
            <ul className="ms-5 list-disc space-y-1.5">
              <li>
                <strong>Your organisation&apos;s own mail server.</strong> The platform sends email only through an
                outgoing mail (SMTP) server your organisation configures; we do not operate a separate email service.
                Emails can include your name, password-reset links, payslip-ready notices and scheduled reports
                attached as files, which can contain HR and payroll data.
              </li>
              <li>
                <strong>Expo push notifications.</strong> If your organisation enables mobile push notifications,
                they are sent through Expo&apos;s push service, which receives your device&apos;s push token and the
                notification text.
              </li>
            </ul>

            <p className="mt-4">
              The platform does not send data to Qiwa, GOSI, Mudad or any bank. Where it produces a file for one of
              these, such as a WPS salary file, your organisation downloads the file and submits it.
            </p>
            <p className="mt-3">
              We may also disclose data where a court, regulator or other authority lawfully requires it. If Kode
              Kinetics&apos;s business is sold or merged, the platform and the data in it may pass to the new owner.
            </p>
          </Section>

          <Section id="ai">
            <Notice>
              The AI features send employee data to a model run by a third party. On the date of this page that
              model is <strong>deepseek-v4-pro:cloud</strong>, running on Ollama&apos;s hosted service (Ollama Cloud,
              ollama.com). These requests leave our infrastructure.
            </Notice>
            <p className="mb-3 mt-4">What each feature sends to the model:</p>
            <DefinitionTable
              rows={[
                ['HR assistant', 'The question exactly as the user typed it, plus data looked up to answer it. That can include headcount by department, leave balances, pending approval counts, attendance, absence and lateness counts, and overtime hours. For a named employee it can also include their full name, employee code, department, job title, grade, work location, joining date, employment type and status, the last four characters of their Iqama and passport numbers, and, for feedback drafts, their current review-cycle status.'],
                ['Policy questions', 'Passages from documents your organisation uploaded as policy documents, with each document’s file name, and the question asked. The passages are sent as written.'],
                ['Candidate screening', 'The job opening’s title, description and requirements, and each candidate’s name, current job title, years of experience, education level and skills. The model returns a shortlist, maybe or reject suggestion.'],
                ['Roster planning', 'Each employee’s name, gender and department, and the shift definitions.'],
                ['Setup assistant', 'The company’s country, industry, size, currency and any notes entered. No employee data.'],
              ]}
            />
            <ul className="ms-5 mt-4 list-disc space-y-1.5">
              <li>
                Apart from shortening Iqama and passport numbers to their last four characters, the platform
                does not remove or disguise personal data before sending it to the model.
              </li>
              <li>
                Suggestions from candidate screening and roster planning are shown to the user. The platform does
                not change a candidate&apos;s status or save a roster on the strength of a suggestion; a user has to
                do that.
              </li>
              <li>
                The AI features are available only to users whose role gives them access, such as HR and
                administrator roles. An individual employee cannot exclude their own data from them.
              </li>
              <li>
                Each HR-assistant request is recorded in your organisation&apos;s data: who asked, when, a shortened
                copy of the request with some values masked, and the model&apos;s full answer. These records are not
                deleted automatically.
              </li>
              <li>
                Our requests to the provider carry no instruction about retaining the data or using it for
                training. How the provider handles them is governed by its terms, not by this page.
              </li>
            </ul>
          </Section>

          <Section id="residency">
            <p>
              None of the platform&apos;s data is stored in the Kingdom of Saudi Arabia. The database and the
              application server are hosted outside the Kingdom, and uploaded documents and profile photos are
              stored with Backblaze B2 in a United States region. AI requests are processed by Ollama (section 5).
              The platform has no setting that keeps a customer&apos;s data in a particular country.
            </p>
            <p className="mt-3">
              Any legal mechanism relied on for transferring data outside the Kingdom
              must be confirmed in the applicable customer agreement or data-processing addendum.
            </p>
          </Section>

          <Section id="security">
            <p className="mb-3 font-semibold text-slate-900 dark:text-white">In place:</p>
            <ul className="ms-5 list-disc space-y-1.5">
              <li>Passwords are stored only as salted PBKDF2-SHA256 hashes.</li>
              <li>
                Authenticator-app (TOTP) multi-factor authentication is available for every account. The platform
                encrypts the MFA secrets, the stored Qiwa credentials, and stored SMS, WhatsApp and push-notification
                provider credentials. The encryption keys are held in the same database.
              </li>
              <li>
                What each user can see and do is set by permissions attached to their role. Requests without a
                valid sign-in are refused unless the endpoint is explicitly public.
              </li>
              <li>
                Salary, bank details, government ID numbers and medical information are withheld from users whose
                role does not permit them. Users whose role does permit them see them in full, and employees see
                their own salary, bank and ID details in full.
              </li>
              <li>
                Each organisation&apos;s data is separated by filters in the application&apos;s data layer, not by
                separate databases or database-level row security.
              </li>
              <li>
                Uploaded files are never served from public links. Every download goes through the signed-in
                application server, which checks that the file belongs to the requester&apos;s organisation.
              </li>
              <li>
                The central audit log and the payroll audit log cannot be changed or deleted through the
                application, and are hash-chained so that alteration can be detected. The payroll audit log is also
                protected by a database rule.
              </li>
              <li>Browser traffic uses HTTPS, and the application server sends a Strict-Transport-Security header.</li>
              <li>Automated dependency, secret-scanning, static-analysis and regression checks run in the repository CI workflows.</li>
            </ul>

            <p className="mb-3 mt-6 font-semibold text-slate-900 dark:text-white">Not in place:</p>
            <ul className="ms-5 list-disc space-y-1.5">
              <li>
                The application does not encrypt HR or payroll fields, including IBANs, ID numbers and medical
                information. They are protected by access control and by whatever encryption the database provider
                applies to its storage.
              </li>
              <li>
                The outgoing mail-server (SMTP) password is stored unencrypted. It is hidden when settings are
                displayed, but not encrypted in the database.
              </li>
              <li>
                The application does not encrypt uploaded files or ask the document store to. Whether stored files
                are encrypted depends on how the storage bucket is configured.
              </li>
              <li>
                The database connection is configured to use TLS, but the application does not check that it
                does, and the documented connection settings do not verify the database server&apos;s certificate.
              </li>
              <li>
                In the web application, sign-in tokens are kept in the browser&apos;s local storage, where scripts
                running on the page can read them, rather than in cookies that scripts cannot read.
              </li>
            </ul>

            <p className="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-200">
              KynexOne does not claim ISO 27001 or SOC 2 Type II certification. This page makes no claim that an
              independent penetration test of the platform has been carried out.
            </p>
            <p className="mt-4">
              To report a security problem, email{' '}
              <a href="mailto:security@kodekinetics.com" className="text-blue-700 underline dark:text-blue-400">
                security@kodekinetics.com
              </a>
              .
            </p>
          </Section>

          <Section id="retention">
            <Notice>
              The platform does not automatically delete or anonymise personal data. Everything in section 2 is kept
              until someone deletes it, and some of it cannot be fully deleted through the platform at all.
            </Notice>
            <div className="mt-4">
              <DefinitionTable
                rows={[
                  ['Current employees', 'Kept for as long as your organisation uses the platform.'],
                  ['Deleted employee records', 'When HR deletes an employee, the record is moved out of the active employee lists and kept in full, marked to be kept for seven years from the date of deletion. Nothing anonymises or deletes it at the end of that period, or at any other time.'],
                  ['Payroll records', 'Payroll runs, payslips, WPS files, GOSI and final-settlement records are kept with no automatic deletion. They carry their own copies of employee names and IBANs.'],
                  ['Attendance, leave and HR requests', 'Kept, with no automatic deletion.'],
                  ['Change history and audit records', 'Kept, with no automatic deletion. The application cannot change or delete the central or payroll audit logs. Change-history entries can include copies of personal details as they were at the time.'],
                  ['Uploaded files', 'The platform has no function that deletes a file from the document store. Deleting a document, an employee or an organisation’s account leaves the file in storage.'],
                  ['AI request records', 'Kept, with no automatic deletion.'],
                  ['Sign-in sessions', 'Access tokens expire after 30 minutes. A session cannot be kept going past a limit your organisation sets: 8 hours by default, and never more than 24 hours. Refresh tokens expire after a period your organisation sets, between 1 and 90 days. Expired tokens and sign-in records are not deleted automatically.'],
                  ['Closed organisation accounts', 'When an organisation’s account is closed, its users are deactivated and signed out, and its data is kept. Nothing deletes it automatically. Our staff can permanently delete a closed organisation’s database records. That deletion keeps audit records, does not remove uploaded files, and can leave some records behind.'],
                ]}
              />
            </div>
            <p className="mt-4">
              Saudi labour and social-insurance rules require employers to keep payroll and employment records for a
              period after employment ends. Where a request to erase your data conflicts with such an obligation,
              the data needed to meet it is kept, so erasure of employee data is not unconditional.
            </p>
          </Section>

          <Section id="rights">
            <p className="mb-4">
              Depending on the law that applies to you, you may be entitled to ask for the following. The platform
              has limited features for these; most have to be requested.
            </p>
            <div className="grid gap-3">
              <Right
                name="See your data"
                platform="In the mobile app, employee self-service shows your profile, payslips, documents, attendance and leave balance. The web application shows a summary, your HR requests and your roster. Neither shows everything held about you, there is no download of it, and medical information, disciplinary records and termination reasons are not shown to employees."
                otherwise="Ask your organisation."
              />
              <Right
                name="Correct your data"
                platform="In the mobile app you can request changes to six fields: preferred name, personal email, phone number, marital status, and emergency contact name and phone. HR approves or rejects each request. The web application has no self-service correction."
                otherwise="Your organisation’s HR team makes all other corrections."
              />
              <Right
                name="Delete your data"
                platform="There is no self-service deletion, and the platform has no function that erases one person’s data. When HR deletes an employee, the record is moved out of the active lists and kept (section 8)."
                otherwise="Ask your organisation. Data it is legally required to keep, such as payroll records, is kept."
              />
              <Right
                name="Get a copy in a machine-readable format"
                platform="Not available. In the mobile app you can download your payslips as PDF files and your own documents as the original files."
                otherwise="Your organisation’s HR team can export employee records as CSV. There is no export of one person’s complete data."
              />
              <Right
                name="Restrict or object to processing"
                platform="The platform has no feature for this."
                otherwise="Ask your organisation."
              />
              <Right
                name="Withdraw consent"
                platform="The platform does not record consent for any processing. In the mobile app, clocking in from the employee dashboard requires the phone’s location permission; kiosk punches do not."
                otherwise="Ask your organisation."
              />
            </div>
            <p className="mt-4">
              You can also write to{' '}
              <a href="mailto:privacy@kodekinetics.com" className="text-blue-700 underline dark:text-blue-400">
                privacy@kodekinetics.com
              </a>
              . Because your organisation controls your data, we may need to refer your request to it.
            </p>
          </Section>

          <Section id="cookies">
            <p>
              The application sets no cookies. In the web application, your browser&apos;s local storage holds your
              sign-in tokens, your language and colour theme, and screen preferences such as the selected company
              and recently opened pages. The mobile app keeps its sign-in tokens in the phone&apos;s secure storage.
            </p>
            <p className="mt-3">
              Every page loads its typefaces from Google Fonts, so your browser sends your IP address and browser
              details to Google. The platform contains no analytics, advertising or error-reporting code.
            </p>
          </Section>

          <Section id="others">
            <p>
              KynexOne is used by organisations for their staff, but it also holds data about people who do not use
              it: employees&apos; emergency contacts, employees&apos; dependants, who may be children (name,
              relationship, national ID, date of birth and visa expiry), and job candidates.
            </p>
          </Section>

          <Section id="changes">
            <p>
              The date at the top of this page is when it was last changed. How customers are told about changes is
              set out in the customer agreement.
            </p>
          </Section>

          <Section id="contact">
            <div className="rounded-2xl border border-slate-200 bg-white p-5 sm:p-6 dark:border-slate-800 dark:bg-slate-900">
              <p className="mb-4 font-semibold text-slate-900 dark:text-white">Kode Kinetics</p>
              <dl className="space-y-3 text-sm sm:space-y-2">
                <ContactRow label="Privacy questions and requests" value="privacy@kodekinetics.com" />
                <ContactRow label="Security reports" value="security@kodekinetics.com" />
                <ContactRow label="Data-processing agreements" value="legal@kodekinetics.com" />
              </dl>
              <p className="mt-4 text-sm text-slate-500 dark:text-slate-400">
                You can complain to the data protection authority where you live. In Saudi Arabia that is the Saudi
                Data &amp; AI Authority (SDAIA).
              </p>
            </div>
          </Section>
        </div>
      </main>

      <footer className="border-t border-slate-200 bg-white py-8 text-center text-[13px] text-slate-400 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-500">
        &copy; {new Date().getFullYear()} Kode Kinetics
        &ensp;·&ensp;
        <Link href="/security" className="transition-colors hover:text-slate-600 dark:hover:text-slate-300">Security</Link>
        &ensp;·&ensp;
        <Link href="/terms" className="transition-colors hover:text-slate-600 dark:hover:text-slate-300">Terms</Link>
        &ensp;·&ensp;
        <Link href="/login" className="transition-colors hover:text-slate-600 dark:hover:text-slate-300">Sign in</Link>
      </footer>
    </div>
  );
}

function Section({ id, children }: { id: SectionId; children: React.ReactNode }) {
  return (
    <section id={id} aria-labelledby={`${id}-h`} className="scroll-mt-20">
      <h2 id={`${id}-h`} className="mb-4 text-xl font-bold text-slate-900 dark:text-white">
        {title(id)}
      </h2>
      {children}
    </section>
  );
}

/** A plain statement of a limitation the reader must not miss. Not a warning icon, not a feature. */
function Notice({ children }: { children: React.ReactNode }) {
  return (
    <p className="rounded-lg border-s-4 border-slate-400 bg-slate-100 px-4 py-3 text-slate-800 dark:border-slate-500 dark:bg-slate-900 dark:text-slate-200">
      {children}
    </p>
  );
}

/** Label/description pairs. Stacks on narrow screens so nothing is squeezed into a column. */
function DefinitionTable({ rows }: { rows: [string, string][] }) {
  return (
    <dl className="divide-y divide-slate-200 overflow-hidden rounded-xl border border-slate-200 bg-white text-sm dark:divide-slate-800 dark:border-slate-800 dark:bg-slate-900">
      {rows.map(([label, desc]) => (
        <div key={label} className="grid gap-1 px-4 py-3 sm:grid-cols-[11rem_minmax(0,1fr)] sm:gap-4">
          <dt className="font-semibold text-slate-800 dark:text-slate-100">{label}</dt>
          <dd className="text-slate-600 dark:text-slate-300">{desc}</dd>
        </div>
      ))}
    </dl>
  );
}

function Provider({ name, does, receives, where }: { name: string; does: string; receives: string; where: string }) {
  return (
    <div className="rounded-xl border border-slate-200 bg-white p-4 text-sm dark:border-slate-800 dark:bg-slate-900">
      <p className="font-semibold text-slate-900 dark:text-white">{name}</p>
      <p className="mt-1 text-slate-600 dark:text-slate-300">{does}</p>
      <dl className="mt-2 grid gap-1 sm:grid-cols-[6rem_minmax(0,1fr)] sm:gap-x-3">
        <dt className="text-slate-500 dark:text-slate-400">Receives</dt>
        <dd className="text-slate-700 dark:text-slate-200">{receives}</dd>
        <dt className="text-slate-500 dark:text-slate-400">Location</dt>
        <dd className="text-slate-700 dark:text-slate-200">{where}</dd>
      </dl>
    </div>
  );
}

function Right({ name, platform, otherwise }: { name: string; platform: string; otherwise: string }) {
  return (
    <div className="rounded-xl border border-slate-200 bg-white p-4 text-sm dark:border-slate-800 dark:bg-slate-900">
      <p className="font-semibold text-slate-900 dark:text-white">{name}</p>
      <dl className="mt-2 grid gap-1 sm:grid-cols-[9rem_minmax(0,1fr)] sm:gap-x-3">
        <dt className="text-slate-500 dark:text-slate-400">In the platform</dt>
        <dd className="text-slate-700 dark:text-slate-200">{platform}</dd>
        <dt className="text-slate-500 dark:text-slate-400">Otherwise</dt>
        <dd className="text-slate-700 dark:text-slate-200">{otherwise}</dd>
      </dl>
    </div>
  );
}

function ContactRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col gap-0.5 sm:flex-row sm:gap-3">
      <dt className="text-slate-500 sm:w-56 sm:shrink-0 dark:text-slate-400">{label}</dt>
      <dd>
        <a href={`mailto:${value}`} className="break-all font-medium text-blue-700 hover:underline dark:text-blue-400">
          {value}
        </a>
      </dd>
    </div>
  );
}
