'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { ArrowLeft } from 'lucide-react';
import { learningApi, type Credential } from '../api/learning';
import { CertificateView } from '../components/learning/CertificateView';
import { ErrorBlock, LearningGate, LoadingRows } from '../components/learning/LearningParts';
import { useLocale } from '../contexts/LocaleContext';
import { learningError } from '../lib/learning';

/** The employee's own certificate (GET /api/ess/learning/credentials/{id}); anyone else's is "not found". */
export function LearningCertificatePage({ credentialId }: { credentialId: string }) {
  return (
    <LearningGate>
      <CertificateBody credentialId={credentialId} />
    </LearningGate>
  );
}

function CertificateBody({ credentialId }: { credentialId: string }) {
  const { t } = useLocale();
  const [credential, setCredential] = useState<Credential | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    setError(null);
    try { setCredential(await learningApi.myCredential(credentialId)); }
    catch (e) { setError(learningError(e, t)); }
  }, [credentialId, t]);
  useEffect(() => { void load(); }, [load]);

  return (
    <div className="mx-auto max-w-3xl space-y-4">
      <Link href="/ess/learning" className="inline-flex items-center gap-1 text-xs font-semibold text-sapphire hover:underline print:hidden dark:text-cyanAccent">
        <ArrowLeft className="h-3.5 w-3.5 rtl:rotate-180" aria-hidden="true" />{t('Back to my learning')}
      </Link>
      {error ? <ErrorBlock message={error} onRetry={() => void load()} />
        : credential ? <CertificateView credential={credential} />
          : <LoadingRows label={t('Loading the certificate')} rows={1} />}
    </div>
  );
}
