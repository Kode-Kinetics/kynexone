'use client';

/**
 * iOS / Android platform badges.
 *
 * The mobile app (Expo, bundle `com.kodekinetics.kynexone`) is REAL and runs on
 * devices today through internal EAS builds — attendance with GPS, payslips,
 * leave, overtime, documents and approvals, in English and Arabic. What it is
 * not is LISTED: Apple's lookup API returns resultCount 0 for the bundle id and
 * the Play listing 404s, eas.json's submit.production profile is empty, and both
 * build profiles are `distribution: internal`.
 *
 * So these are a PLATFORM SIGNAL — "there are native apps for both" — and not a
 * call to action. They do not link anywhere, and their wording does not imply a
 * store listing that does not exist. Fill in the two constants below on launch
 * day and they become the official store lockups and real links, with no other
 * change needed.
 *
 * Note for launch: Apple and Google both require their official badge artwork
 * under their brand guidelines. These are faithful inline SVG lockups so there
 * are no binary assets to manage in the repo; swap in the official assets from
 * Apple's Marketing Resources and the Google Play badge generator before any
 * public launch.
 */
export const APP_STORE_URL: string | null = null;
export const PLAY_STORE_URL: string | null = null;

interface StoreBadgesProps {
  /** `dark` sits on a dark surface (light badge), `light` on a light surface. */
  variant?: 'dark' | 'light';
  className?: string;
}

export function StoreBadges({ variant = 'dark', className = '' }: StoreBadgesProps) {
  const unreleased = !APP_STORE_URL && !PLAY_STORE_URL;

  return (
    <div className={`kx-stores ${variant === 'dark' ? 'kx-stores-dark' : 'kx-stores-light'} ${className}`}>
      <p className="kx-stores-eyebrow">
        {unreleased
          ? 'Native iOS and Android apps — rolling out to pilot customers'
          : 'Employee self-service on mobile'}
      </p>

      <div className="kx-stores-row">
        <StoreBadge
          href={APP_STORE_URL}
          label={APP_STORE_URL ? 'Download on the App Store' : 'Native app for iOS'}
          caption={APP_STORE_URL ? 'Download on the' : 'Native app for'}
          name={APP_STORE_URL ? 'App Store' : 'iOS'}
          icon={
            <svg viewBox="0 0 384 512" aria-hidden focusable="false" className="kx-store-icon">
              <path
                fill="currentColor"
                d="M318.7 268.7c-.2-36.7 16.4-64.4 50-84.8-18.8-26.9-47.2-41.7-84.7-44.6-35.5-2.8-74.3 20.7-88.5 20.7-15 0-49.4-19.7-76.4-19.7C63.3 141.2 4 184.8 4 273.5q0 39.3 14.4 81.2c12.8 36.7 59 126.7 107.2 125.2 25.2-.6 43-17.9 75.8-17.9 31.8 0 48.3 17.9 76.4 17.9 48.6-.7 90.4-82.5 102.6-119.3-65.2-30.7-61.7-90-61.7-91.9zm-56.6-164.2c27.3-32.4 24.8-61.9 24-72.5-24.1 1.4-52 16.4-67.9 34.9-17.5 19.8-27.8 44.3-25.6 71.9 26.1 2 49.9-11.4 69.5-34.3z"
              />
            </svg>
          }
        />

        <StoreBadge
          href={PLAY_STORE_URL}
          label={PLAY_STORE_URL ? 'Get it on Google Play' : 'Native app for Android'}
          caption={PLAY_STORE_URL ? 'Get it on' : 'Native app for'}
          name={PLAY_STORE_URL ? 'Google Play' : 'Android'}
          icon={
            <svg viewBox="0 0 512 512" aria-hidden focusable="false" className="kx-store-icon">
              <path fill="#00A0FF" d="M47 0c-13 6.8-21.8 19.2-21.8 35.3v441.3c0 16.1 8.8 28.5 21.8 35.3l256.6-256L47 0z" />
              <path fill="#00D856" d="M325.3 234.3 104.6 13l280.8 161.2-60.1 60.1z" />
              <path fill="#FFBC00" d="m472.2 225.6-88.6-51.3-65.2 65.3 65.2 65.3 90.4-51.3c27.1-21.4 27.1-56.6-1.8-78z" />
              <path fill="#FF3A44" d="M104.6 499l280.8-161.2-60.1-60.1L104.6 499z" />
            </svg>
          }
        />
      </div>

      <style>{`
        .kx-stores-eyebrow {
          display: flex; align-items: center; gap: 0.5rem;
          font-size: 11px; font-weight: 500; letter-spacing: 0.02em;
          margin-bottom: 0.6rem;
        }
        .kx-stores-soon {
          border-radius: 9999px; padding: 1px 7px;
          font-size: 10px; font-weight: 600; letter-spacing: 0.03em;
        }
        .kx-stores-row { display: flex; flex-wrap: wrap; gap: 0.5rem; }

        .kx-store-badge {
          display: inline-flex; align-items: center; gap: 0.55rem;
          border-radius: 10px;
          padding: 7px 13px;
          text-decoration: none;
          border: 1px solid transparent;
          transition: transform 0.18s ease, border-color 0.18s ease, background 0.18s ease;
        }
        .kx-store-icon { width: 20px; height: 20px; flex-shrink: 0; }
        .kx-store-text { display: flex; flex-direction: column; line-height: 1.05; text-align: left; }
        .kx-store-caption { font-size: 9px; font-weight: 500; letter-spacing: 0.06em; text-transform: uppercase; opacity: 0.75; }
        .kx-store-name { font-size: 14px; font-weight: 600; letter-spacing: -0.01em; }

        a.kx-store-badge:hover { transform: translateY(-1px); }
        .kx-store-badge[aria-disabled='true'] { cursor: default; opacity: 0.62; }

        /* On a dark surface */
        .kx-stores-dark .kx-stores-eyebrow { color: #8da3c9; }
        .kx-stores-dark .kx-stores-soon { background: rgba(94,235,255,0.10); color: #7fd5ee; }
        .kx-stores-dark .kx-store-badge {
          background: rgba(255,255,255,0.055);
          border-color: rgba(255,255,255,0.13);
          color: #fff;
        }
        .kx-stores-dark a.kx-store-badge:hover {
          background: rgba(255,255,255,0.09);
          border-color: rgba(255,255,255,0.26);
        }

        /* On a light surface */
        .kx-stores-light .kx-stores-eyebrow { color: #475569; }
        .kx-stores-light .kx-stores-soon { background: rgba(47,107,255,0.10); color: #1d4ed8; }
        .kx-stores-light .kx-store-badge {
          background: #0f172a;
          border-color: rgba(15,23,42,0.9);
          color: #fff;
        }
        .kx-stores-light a.kx-store-badge:hover { background: #1e293b; }

        .kx-store-badge:focus-visible {
          outline: 2px solid #5eebff;
          outline-offset: 2px;
        }

        @media (prefers-reduced-motion: reduce) {
          a.kx-store-badge:hover { transform: none; }
        }
      `}</style>
    </div>
  );
}

function StoreBadge({ href, label, caption, name, icon }: {
  href: string | null;
  label: string;
  caption: string;
  name: string;
  icon: React.ReactNode;
}) {
  const inner = (
    <>
      {icon}
      <span className="kx-store-text">
        <span className="kx-store-caption">{caption}</span>
        <span className="kx-store-name">{name}</span>
      </span>
    </>
  );

  // Not listed: a platform signal, not a link. Announced for what it is rather
  // than as an anchor that goes nowhere.
  if (!href) {
    return (
      <span className="kx-store-badge" role="img" aria-label={label} aria-disabled="true">
        {inner}
      </span>
    );
  }

  return (
    <a className="kx-store-badge" href={href} target="_blank" rel="noopener noreferrer" aria-label={label}>
      {inner}
    </a>
  );
}
