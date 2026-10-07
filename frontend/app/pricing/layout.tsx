import type { Metadata } from 'next';

export const metadata: Metadata = {
  title: 'Request a Proposal — KynexOne',
  description: 'Tell us your company size, structure and modules, and get a KynexOne proposal within 1 business day.',
  openGraph: {
    title: 'Request a Proposal — KynexOne',
    description: 'Tell us your company size, structure and modules, and get a KynexOne proposal within 1 business day.',
    siteName: 'KynexOne',
    type: 'website',
  },
  twitter: {
    card: 'summary_large_image',
    title: 'Request a Proposal — KynexOne',
    description: 'Tell us your company size, structure and modules, and get a KynexOne proposal within 1 business day.',
  },
};

export default function PricingLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
