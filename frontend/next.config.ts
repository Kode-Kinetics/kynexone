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
    ];
  },

  images: {
    remotePatterns: [{ protocol: 'https', hostname: '**' }],
  },
};

export default withBundleAnalyzer(nextConfig);
