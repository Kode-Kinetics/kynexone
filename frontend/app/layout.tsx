import type { Metadata, Viewport } from 'next';
import { Providers } from '@/src/components/Providers';
import { Archivo, IBM_Plex_Mono } from 'next/font/google';

import '@/src/styles/index.css';

/** The login object's two voices: a grotesque for text, a mono for the
 *  etched legends, readouts and the service stamp. */
const archivo = Archivo({
  subsets: ['latin'], weight: ['400', '500', '600'],
  variable: '--font-kx-sans', display: 'swap',
});
const plexMono = IBM_Plex_Mono({
  subsets: ['latin'], weight: ['400', '500'],
  variable: '--font-kx-mono', display: 'swap',
});

export const metadata: Metadata = {
  title: 'KynexOne — One Platform for Every Workforce Operation',
  description: 'HR, payroll, recruitment, attendance and compliance — unified.',
};

export const viewport: Viewport = {
  themeColor: '#0B1020',
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className={`${archivo.variable} ${plexMono.variable}`}>
      <head>
        <link rel="preconnect" href="https://fonts.googleapis.com" />
        <link rel="preconnect" href="https://fonts.gstatic.com" crossOrigin="" />
        <link
          href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700;800&display=swap"
          rel="stylesheet"
        />
      </head>
      <body>
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
