'use client';

export interface ComingSoonProps {
  title: string;
  description: string;
  apis?: { method: string; path: string; note?: string }[];
  plannedFeatures?: string[];
  badge?: 'todo' | 'in-progress' | 'partial';
}

const badgeMap = {
  'todo':        { label: 'Planned',     cls: 'bg-slate-700 text-slate-300' },
  'in-progress': { label: 'In Progress', cls: 'bg-blue-900/60 text-blue-300 border border-blue-700/40' },
  'partial':     { label: 'Partial',     cls: 'bg-amber-900/50 text-amber-300 border border-amber-700/40' },
};

export function ComingSoon({ title, description, apis, plannedFeatures, badge = 'todo' }: ComingSoonProps) {
  const b = badgeMap[badge];
  return (
    <div className="min-h-[60vh] flex flex-col items-center justify-center py-20 px-6">
      <div className="w-full max-w-lg space-y-6">
        {/* Header */}
        <div className="text-center space-y-3">
          <div className="inline-flex items-center gap-2 mb-2">
            <span className={`px-2.5 py-0.5 rounded text-caption font-semibold uppercase tracking-wider ${b.cls}`}>
              {b.label}
            </span>
          </div>
          <h1 className="text-2xl font-semibold text-white">{title}</h1>
          <p className="text-sm text-slate-400 leading-relaxed">{description}</p>
        </div>

        {/* Planned APIs */}
        {apis && apis.length > 0 && (
          <div className="bg-slate-900/60 border border-white/8 rounded-xl overflow-hidden">
            <div className="px-4 py-2.5 border-b border-white/8">
              <p className="text-caption font-semibold text-slate-500 uppercase tracking-wider">
                Backend API Endpoints Required
              </p>
            </div>
            <div className="divide-y divide-white/5">
              {apis.map((api, i) => (
                <div key={i} className="flex items-start gap-3 px-4 py-2.5">
                  <span className={`text-caption font-semibold font-mono shrink-0 mt-0.5 px-1.5 py-0.5 rounded ${
                    api.method === 'GET' ? 'bg-emerald-900/60 text-emerald-400' :
                    api.method === 'POST' ? 'bg-blue-900/60 text-blue-400' :
                    api.method === 'PUT' ? 'bg-amber-900/60 text-amber-400' :
                    'bg-rose-900/60 text-rose-400'
                  }`}>{api.method}</span>
                  <div className="min-w-0">
                    <p className="text-xs text-slate-300 font-mono truncate">{api.path}</p>
                    {api.note && <p className="text-caption text-slate-600 mt-0.5">{api.note}</p>}
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Planned features */}
        {plannedFeatures && plannedFeatures.length > 0 && (
          <div className="space-y-2">
            <p className="text-caption font-semibold text-slate-500 uppercase tracking-wider">Planned Capabilities</p>
            <ul className="space-y-1.5">
              {plannedFeatures.map((f, i) => (
                <li key={i} className="flex items-start gap-2 text-sm text-slate-400">
                  <span className="text-slate-700 mt-0.5">—</span>
                  <span>{f}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </div>
  );
}
