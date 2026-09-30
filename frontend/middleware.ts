import { NextRequest, NextResponse } from 'next/server';

export function middleware(request: NextRequest) {
  const url = request.nextUrl.clone();
  if (!url.searchParams.has('impersonate')) return NextResponse.next();

  url.searchParams.delete('impersonate');
  return NextResponse.redirect(url, 307);
}

export const config = {
  matcher: ['/login'],
};
