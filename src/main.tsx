import { StrictMode, Component, type ErrorInfo, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { useStore } from './store/useStore';
import { pullDevStoreSync, pushDevStoreSyncIfNewer } from './utils/devStoreSyncStorage';
import './styles/global.css';

/** Match Electron default zoom when running in the browser dev server. */
if (!window.electronAPI) {
  document.documentElement.style.zoom = '0.8';
}

class AppErrorBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state = { error: null as Error | null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('App render failed', error, info);
  }

  render() {
    if (this.state.error) {
      return (
        <div style={{ padding: 32, maxWidth: 640, margin: '0 auto', fontFamily: 'system-ui, sans-serif' }}>
          <h1 style={{ marginTop: 0 }}>BIM Boardroom failed to load</h1>
          <p style={{ color: '#666' }}>
            Try a hard refresh (Ctrl+Shift+R). If the problem continues, close all BIM Boardroom
            windows and reopen the app.
          </p>
          <pre
            style={{
              padding: 12,
              background: '#f5f5f5',
              borderRadius: 8,
              overflow: 'auto',
              fontSize: 12,
            }}
          >
            {this.state.error.message}
          </pre>
        </div>
      );
    }
    return this.props.children;
  }
}

async function withTimeout<T>(promise: Promise<T>, ms: number, fallback: T): Promise<T> {
  return Promise.race([
    promise,
    new Promise<T>((resolve) => {
      window.setTimeout(() => resolve(fallback), ms);
    }),
  ]);
}

async function bootstrapDevStoreSync() {
  try {
    const pulled = await withTimeout(pullDevStoreSync(), 2500, false);
    if (pulled) {
      await Promise.race([
        Promise.resolve(useStore.persist.rehydrate()),
        new Promise<void>((resolve) => window.setTimeout(resolve, 2500)),
      ]);
    }
    await Promise.race([
      pushDevStoreSyncIfNewer(),
      new Promise<void>((resolve) => window.setTimeout(resolve, 2500)),
    ]);

    const pullAndRehydrate = async () => {
      const updated = await withTimeout(pullDevStoreSync(), 2500, false);
      if (updated) {
        await Promise.race([
          Promise.resolve(useStore.persist.rehydrate()),
          new Promise<void>((resolve) => window.setTimeout(resolve, 2500)),
        ]);
      }
    };

    window.addEventListener('focus', () => {
      void pullAndRehydrate();
    });

    window.setInterval(() => {
      void pullAndRehydrate();
    }, 3000);
  } catch (error) {
    console.error('Dev store sync failed', error);
  }
}

function startApp() {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <AppErrorBoundary>
        <App />
      </AppErrorBoundary>
    </StrictMode>
  );

  if (import.meta.env.DEV) {
    void bootstrapDevStoreSync();
  }
}

startApp();