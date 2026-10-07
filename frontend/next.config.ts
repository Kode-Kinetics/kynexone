import type { NextConfig } from 'next';
import bundleAnalyzer from '@next/bundle-analyzer';

const withBundleAnalyzer = bundleAnalyzer({
  enabled: process.env.ANALYZE === 'true',
});

const apiUrl =
  process.env.NEXT_PUBLIC_API_BASE_URL ||
  process.env.NEXT_PUBLIC_API_URL ||
  'http://localhost:5117';

const nextConfig: NextConfig = {
  poweredByHeader: false,
  outputFileTracingRoot: __dirname,
  // Self-hosting (frontend/Dockerfile): emit .next/standalone/server.js with only the traced
  // node_modules it needs. Vercel ignores this and uses its own output; `next start` still works.
  // The /api rewrite below is resolved at BUILD time, so a self-hosted image must be built with
  // NEXT_PUBLIC_API_BASE_URL pointing at its API.
  output: 'standalone',

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

  images: {
    remotePatterns: [{ protocol: 'https', hostname: '**' }],
  },
};

export default withBundleAnalyzer(nextConfig);
