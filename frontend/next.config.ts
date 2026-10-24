import type { NextConfig } from 'next';
import bundleAnalyzer from '@next/bundle-analyzer';

const withBundleAnalyzer = bundleAnalyzer({
  enabled: process.env.ANALYZE === 'true',
});

const apiUrl =
  process.env.NEXT_PUBLIC_API_BASE_URL ||
  process.env.NEXT_PUBLIC_API_URL ||
  'http://localhost:5117';

export const SECURITY_HEADERS = [
  { key: 'X-Content-Type-Options', value: 'nosniff' },
  { key: 'Referrer-Policy', value: 'strict-origin-when-cross-origin' },
  { key: 'X-Frame-Options', value: 'SAMEORIGIN' },
  { key: 'Content-Security-Policy', value: "frame-ancestors 'self'" },
  {
    key: 'Permissions-Policy',
    // geolocation=(self): Setup's "Use my current location" pins a site from the admin's own device. Only this origin
    // may ask (never an embedded frame), and the browser still asks the person. A per-route value would not work:
    // the policy is fixed by the first page loaded, and the wizard is reached by client-side navigation.
    value: 'camera=(), microphone=(), geolocation=(self), payment=(), usb=(), serial=(), hid=(), bluetooth=(), browsing-topics=()',
  },
];

const nextConfig: NextConfig = {
  // Isolated local QA may run beside the existing dev server without sharing its build cache.
  distDir: process.env.NEXT_DIST_DIR || '.next',
  poweredByHeader: false,
  outputFileTracingRoot: __dirname,
  // Self-hosting (frontend/Dockerfile): emit .next/standalone/server.js with only the traced
  // node_modules it needs. Vercel ignores this and uses its own output; `next start` still works.
  // The /api rewrite below is resolved at BUILD time, so a self-hosted image must be built with
  // NEXT_PUBLIC_API_BASE_URL pointing at its API.
  output: 'standalone',
  // A bounded policy section can take 90s (primary extraction + focused projection).
  // Keep the local/self-hosted rewrite alive through that backend budget plus transport
  // overhead. This does not extend deployment ingress limits or retry failed POSTs.
  experimental: { proxyTimeout: 120_000 },

  async rewrites() {
    return [
      {
        source: '/api/:path*',
        destination: `${apiUrl}/api/:path*`,
      },
    ];
  },

  async headers() {
    return [
      {
        // Baseline browser hardening for every page. /api/* is left out: the API sets its own,
        // stricter set (SecurityHeaders.cs), and a second copy of a header would conflict with it.
        // Framing is limited to this origin, not refused outright: offer-letter and payslip PDF
        // previews are same-origin blob: iframes. The web app never asks for the camera, microphone
        // (selfie attendance is the mobile app), so those are switched off; location is same-origin only (see above).
        // A script-src Content-Security-Policy is deliberately not set here yet.
        source: '/:path((?!api/).*)',
        headers: SECURITY_HEADERS,
      },
      {
        // Hashed JS/CSS bundles are content-addressed — safe to cache forever in browser + CDN.
        source: '/_next/static/:path*',
        headers: [{ key: 'Cache-Control', value: 'public, max-age=31536000, immutable' }],
      },
      {
        // Favicons and public images: 1 hour browser cache.
        source: '/:path(favicon.ico|.*\\.png|.*\\.svg|.*\\.jpg|.*\\.webp)',
        headers: [{ key: 'Cache-Control', value: 'public, max-age=3600' }],
      },
      {
        // API proxy: never cache — the backend sets its own Cache-Control per endpoint.
        source: '/api/:path*',
        headers: [{ key: 'Cache-Control', value: 'no-store' }],
      },
      {
        // First sign-in: the address may carry a welcome code in its fragment until the page
        // scrubs it. Fragments never travel in a Referer, but nothing about this page needs one.
        source: '/welcome',
        headers: [
          { key: 'Referrer-Policy', value: 'no-referrer' },
          { key: 'Cache-Control', value: 'no-store' },
        ],
      },
    ];
  },

  // next/image is not used. No remotePatterns, so /_next/image cannot be used to fetch other sites.
};

export default withBundleAnalyzer(nextConfig);
