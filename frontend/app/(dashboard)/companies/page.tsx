import { permanentRedirect } from 'next/navigation';

// IA de-duplication: company management is canonically owned by Setup → Companies
// (list/create/edit/delete/import/export/suspend/reactivate). This standalone route
// is kept alive as a permanent (308) redirect so existing bookmarks and inbound
// deep-links to /companies resolve to the canonical Setup tab.
export default function CompaniesRedirect() {
  permanentRedirect('/setup?tab=companies');
}
