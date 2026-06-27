import { useState, type FormEvent } from 'react';
import { useStore } from '../store/useStore';
import styles from './LoginScreen.module.css';

export function LoginScreen() {
  const login = useStore((s) => s.login);

  const [loginId, setLoginId] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    setError(null);

    const trimmedLoginId = loginId.trim();
    if (!trimmedLoginId) {
      setError('Enter your name or email to continue.');
      return;
    }
    if (!password) {
      setError('Enter your password.');
      return;
    }

    const result = login(trimmedLoginId, password);
    if (result === 'not-found') {
      setError('We could not find an account with that name or email.');
      return;
    }
    if (result === 'ambiguous') {
      setError('Multiple accounts match that name. Sign in with your email address instead.');
      return;
    }
    if (result === 'invalid-password') {
      setError('Incorrect password. Try again or contact your manager.');
    }
  };

  return (
    <div className={styles.screen}>
      <div className={styles.card}>
        <div className={styles.brand}>
          <span className={styles.logoIcon} aria-hidden>
            ◈
          </span>
          <div>
            <h1 className={styles.title}>BIM Boardroom</h1>
            <p className={styles.subtitle}>Sign in to continue</p>
          </div>
        </div>

        <form className={styles.form} onSubmit={handleSubmit}>
          <label className={styles.field}>
            <span className={styles.label}>Name or email</span>
            <input
              className={styles.input}
              type="text"
              value={loginId}
              onChange={(e) => setLoginId(e.target.value)}
              autoComplete="username"
              placeholder="Full name or work email"
              autoFocus
            />
          </label>

          <label className={styles.field}>
            <span className={styles.label}>Password</span>
            <input
              className={styles.input}
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
              placeholder="Enter password"
            />
          </label>

          {error && <p className={styles.error}>{error}</p>}

          <button type="submit" className={styles.submitBtn}>
            Sign in
          </button>

          <p className={styles.hint}>
            Use the name and password from your manager. If you were invited recently, use the
            temporary password from your invite.
          </p>
        </form>
      </div>
    </div>
  );
}
