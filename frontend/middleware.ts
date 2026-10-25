import { NextRequest, NextResponse } from 'next/server';
import { legacySetupHref } from './src/lib/setupLegacyRoutes';

/** Must match backend ClientIpResolver. */
const CLIENT_IP_HEADER = 'x-kynexone-client-ip';
const PROXY_SECRET_HEADER = 'x-kynexone-proxy-secret';
/** Tells the API this request came through the web proxy, so its peer address is shared by everyone. */
const VIA_PROXY_HEADER = 'x-kynexone-via-proxy';

/**
 * The browser's own address, as the hosting edge reports it. Vercel sets x-real-ip and REPLACES
 * x-forwarded-for with the connecting client's address, so neither is client-controlled there.
 */
function clientIp(request: NextRequest): string | null {
  const real = request.headers.get('x-real-ip')?.trim();
  if (real) return real;
  const forwarded = request.headers.get('x-forwarded-for')?.split(',')[0]?.trim();
  return forwarded || null;
}

/**
 * /api/* is rewritten to the API (next.config.ts). From there the API sees this proxy's egress
 * address, so every user shares one rate-limit bucket. When PROXY_CLIENT_IP_SECRET is set (the same
 * value as the API's Proxy__ClientIpSecret), forward the real client IP with the secret that proves
 * it came from here. Unset (the default): forward neither. Either way, never pass through copies of
 * these headers that a caller supplied.
 */
function forwardClientIp(request: NextRequest): NextResponse {
  const headers = new Headers(request.headers);
  headers.delete(CLIENT_IP_HEADER);
  headers.delete(PROXY_SECRET_HEADER);
  // Always marked, secret or not: without a verified client IP the API must not apply per-IP
  // failure budgets to this (shared) address.
  headers.set(VIA_PROXY_HEADER, '1');
  const secret = process.env.PROXY_CLIENT_IP_SECRET;
  const ip = clientIp(request);
  if (secret && ip) {
    headers.set(CLIENT_IP_HEADER, ip);
    headers.set(PROXY_SECRET_HEADER, secret);
  }
  return NextResponse.next({ request: { headers } });
}

export function middleware(request: NextRequest) {
  if (request.nextUrl.pathname.startsWith('/api/')) return forwardClientIp(request);

  // A screen that moved into Setup (src/lib/setupLegacyRoutes.ts): old links, bookmarks and server-sent links arrive at
  // its Setup section, keeping their company and other parameters.
  const moved = legacySetupHref(request.nextUrl.pathname, request.nextUrl.searchParams);
  if (moved) return NextResponse.redirect(new URL(moved, request.nextUrl), 307);

  const url = request.nextUrl.clone();
  if (!url.searchParams.has('impersonate')) return NextResponse.next();

  url.searchParams.delete('impersonate');
  return NextResponse.redirect(url, 307);
}

export const config = {
  // Static by Next's rules: keep the old addresses in step with SETUP_LEGACY_PATHS (unit/setupConsolidation.spec.ts).
  matcher: [
    '/login', '/api/:path*',
    '/payroll/templates', '/tax-policies', '/compliance-profiles', '/opening-balances', '/tenant-admin', '/user-management',
    '/leave', '/shifts', '/overtime', '/attendance', '/benefits', '/loans', '/performance', '/hr-letters', '/engagement', '/saudi-compliance',
  ],
};
