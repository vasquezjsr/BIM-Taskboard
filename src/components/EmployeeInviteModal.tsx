import { useState } from 'react';
import { createPortal } from 'react-dom';
import { buildInviteMessage, isValidEmail } from '../utils/auth';
import styles from './EmployeeInviteModal.module.css';

export interface EmployeeInviteDetails {
  employeeId: string;
  employeeName: string;
  invitePassword: string;
  email: string;
}

interface EmployeeInviteModalProps {
  invite: EmployeeInviteDetails;
  onClose: () => void;
}

export function EmployeeInviteModal({ invite, onClose }: EmployeeInviteModalProps) {
  const [copied, setCopied] = useState(false);
  const [email, setEmail] = useState(invite.email);

  const inviteMessage = buildInviteMessage(invite.employeeName, invite.invitePassword);

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(inviteMessage);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      setCopied(false);
    }
  };

  const handleSendEmail = () => {
    const trimmedEmail = email.trim();
    if (!trimmedEmail) return;
    const subject = encodeURIComponent('Your BIM Boardroom login invite');
    const body = encodeURIComponent(inviteMessage);
    window.location.href = `mailto:${trimmedEmail}?subject=${subject}&body=${body}`;
  };

  return createPortal(
    <div className={styles.overlay} onClick={onClose}>
      <div
        className={styles.modal}
        onClick={(event) => event.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="employee-invite-title"
      >
        <div className={styles.header}>
          <div>
            <h2 id="employee-invite-title">Send login invite</h2>
            <p className={styles.subtitle}>
              {invite.employeeName} can use this temporary password to sign in.
            </p>
          </div>
          <button type="button" className={styles.closeBtn} onClick={onClose} title="Close">
            ×
          </button>
        </div>

        <div className={styles.body}>
          <div className={styles.passwordBlock}>
            <span className={styles.label}>Temporary password</span>
            <code className={styles.password}>{invite.invitePassword}</code>
          </div>

          <label className={styles.field}>
            <span className={styles.label}>Email</span>
            <input
              className={styles.input}
              type="email"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              placeholder="name@company.com"
              required
            />
          </label>

          <pre className={styles.preview}>{inviteMessage}</pre>
        </div>

        <div className={styles.footer}>
          <button type="button" className={styles.secondaryBtn} onClick={handleCopy}>
            {copied ? 'Copied' : 'Copy invite'}
          </button>
          <button
            type="button"
            className={styles.secondaryBtn}
            onClick={handleSendEmail}
            disabled={!isValidEmail(email)}
          >
            Send email
          </button>
          <button type="button" className={styles.primaryBtn} onClick={onClose}>
            Done
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
