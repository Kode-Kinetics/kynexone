import type { Metadata } from 'next';

export const metadata: Metadata = {
  title: 'Pricing Calculator — KynexOne',
  description: 'Estimate your KynexOne subscription cost based on company size, modules, and structure.',
  openGraph: {
    title: 'Pricing Calculator — KynexOne',
    description: 'Estimate your KynexOne subscription cost based on company size, modules, and structure.',
    siteName: 'KynexOne',
    type: 'website',
  },
  twitter: {
    card: 'summary_large_image',
    title: 'Pricing Calculator — KynexOne',
    description: 'Estimate your KynexOne subscription cost based on company size, modules, and structure.',
  },
};

export default function PricingLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
