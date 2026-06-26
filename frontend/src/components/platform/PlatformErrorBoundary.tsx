'use client';

import { Component, type ReactNode, type ErrorInfo } from 'react';
import { AlertTriangle, RefreshCw } from 'lucide-react';
import { attemptChunkReload, isChunkLoadError } from '@/src/lib/chunkReload';

interface Props { children: ReactNode; fallbackRoute?: string; }
interface State { error: Error | null; }

export class PlatformErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    // Deployment skew: a stale chunk referenced by an old tab is gone after a
    // new deploy. Recover with a single (loop-guarded) hard reload instead of
    // stranding the admin on an error card.
    if (isChunkLoadError(error)) {
      attemptChunkReload();
      return;
    }
    console.error('[PlatformErrorBoundary]', error, info.componentStack);
  }

  render() {
    // While a chunk-recovery reload is in flight, render nothing (avoids a flash
    // of the error card right before the page reloads).
    if (this.state.error && isChunkLoadError(this.state.error)) return null;

    if (this.state.error) {
      return (
        <div className="flex min-h-[60vh] flex-col items-center justify-center gap-4 text-center px-6">
          <div className="h-12 w-12 rounded-2xl bg-rose-500/10 border border-rose-500/20 flex items-center justify-center">
            <AlertTriangle className="h-6 w-6 text-rose-400" />
          </div>
          <div>
            <h3 className="text-base font-semibold text-white mb-1">Something went wrong</h3>
            <p className="text-sm text-slate-500 max-w-sm">
              {this.state.error.message || 'An unexpected error occurred on this page.'}
            </p>
          </div>
          <div className="flex gap-3">
            <button
              type="button"
              onClick={() => this.setState({ error: null })}
              className="flex items-center gap-1.5 text-sm text-white bg-white/[0.06] hover:bg-white/[0.1] border border-white/10 px-4 py-2 rounded-lg transition-colors"
            >
              <RefreshCw className="h-3.5 w-3.5" />
              Try again
            </button>
            {this.props.fallbackRoute && (
              <a
                href={this.props.fallbackRoute}
                className="flex items-center gap-1.5 text-sm text-slate-400 hover:text-white border border-white/10 hover:border-white/20 px-4 py-2 rounded-lg transition-colors"
              >
                Back to dashboard
              </a>
            )}
          </div>
          <details className="mt-2 text-left">
            <summary className="text-[11px] text-slate-700 cursor-pointer hover:text-slate-500">Show error details</summary>
            <pre className="mt-2 text-[10px] text-slate-600 bg-black/30 rounded-lg p-3 max-w-lg overflow-x-auto whitespace-pre-wrap">
              {this.state.error.stack}
            </pre>
          </details>
        </div>
      );
    }
    return this.props.children;
  }
}
