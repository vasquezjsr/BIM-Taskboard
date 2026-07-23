import { StrictMode, Component, type ErrorInfo, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { useStore } from './store/useStore';
import { startBoardroomApiBridge } from './boardroomApiBridge';
import { pushDevStoreSyncIfNewer } from './utils/devStoreSyncStorage';
import { installCtrlScrollZoom } from './utils/appZoom';
import './styles/global.css';

/** Ctrl/Cmd + scroll (and Ctrl+/- / Ctrl+0) — persists zoom for accessibility. */
installCtrlScrollZoom();

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

async function bootstrapDevStoreSync() {
  try {
    // Push-only: never pull shared disk state over this window (refresh was wiping projects).
    await Promise.race([
      pushDevStoreSyncIfNewer(),
      new Promise<void>((resolve) => window.setTimeout(resolve, 2500)),
    ]);

    window.addEventListener('focus', () => {
      void pushDevStoreSyncIfNewer();
    });
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

  startBoardroomApiBridge();

  if (import.meta.env.DEV) {
    (window as Window & { __BIM_STORE__?: typeof useStore }).__BIM_STORE__ = useStore;
    void bootstrapDevStoreSync();
  }
}

startApp();