import type { Metadata } from 'next';
import { LegalLayout } from '@/components/LegalLayout';
import { CONTACT_EMAIL, LEGAL_NAME, BRAND } from '@/lib/i18n';

export const metadata: Metadata = {
  title: 'Privacy Policy',
  description: `How ${BRAND} handles personal data — scope, retention, sharing, user rights.`,
};

const EFFECTIVE_DATE = '2026-05-02';

export default function PrivacyEn() {
  return (
    <LegalLayout
      locale="en"
      routeKey="privacy"
      title="Privacy Policy"
      effectiveDate={EFFECTIVE_DATE}
    >
      <p>
        This Privacy Policy describes how the <strong>{BRAND}</strong> Windows desktop
        application and the <strong>orderdeckapp.com</strong> website collect, use,
        store and share personal data.
      </p>
      <p>
        <strong>Data controller:</strong> {LEGAL_NAME} (sole proprietor). Contact:{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>

      <h2>1. Data we collect</h2>
      <p>The {BRAND} desktop application processes two kinds of data:</p>

      <h3>1.1. Account and license data</h3>
      <ul>
        <li>Email address (for sign-up and license management)</li>
        <li>License key, machine fingerprint</li>
        <li>Payment amount, license and update-pack status (payment details are stored only by our payment processors, never by us)</li>
        <li>IP address and login timestamp (for security logs, deleted within 90 days)</li>
      </ul>

      <h3>1.2. Live-stream chat data</h3>
      <p>
        The application reads live-stream chat from the platforms you connect (Instagram,
        TikTok, Facebook, YouTube). These messages are:
      </p>
      <ul>
        <li><strong>Stored only on your own computer</strong> in a memory ring buffer (max 500 messages)</li>
        <li><strong>Discarded entirely</strong> when the application closes</li>
        <li><strong>Never sent to {BRAND} servers</strong> and never shared with third parties</li>
      </ul>

      <h2>2. YouTube API usage</h2>
      <p>
        The YouTube live-stream functionality uses the YouTube Data API v3. {BRAND}'s use
        of information received from YouTube APIs adheres to the{' '}
        <a href="https://developers.google.com/terms/api-services-user-data-policy" target="_blank" rel="noreferrer">
          Google API Services User Data Policy
        </a>, including the <strong>Limited Use</strong> requirements.
      </p>
      <p>OAuth scopes requested:</p>
      <ul>
        <li>
          <code>https://www.googleapis.com/auth/youtube</code> — to read channel info
          (display name, profile picture) and live-chat messages
        </li>
        <li>
          <code>https://www.googleapis.com/auth/youtube.force-ssl</code> — for delete
          and ban moderation actions, executed only when the operator explicitly clicks
          inside the application
        </li>
      </ul>
      <p>Data obtained via the YouTube API:</p>
      <ul>
        <li>Is processed only on the user's own machine</li>
        <li>Is never transmitted to {BRAND} servers</li>
        <li>Is not used for advertising, profiling, or training machine-learning models</li>
        <li>Is not sold or transferred to third parties</li>
      </ul>
      <p>
        OAuth refresh tokens are stored on the user's machine, encrypted with the
        Windows Data Protection API (DPAPI). Users may revoke this access at any time
        via{' '}
        <a href="https://myaccount.google.com/permissions" target="_blank" rel="noreferrer">
          https://myaccount.google.com/permissions
        </a>.
      </p>

      <h2>3. Browser extension (Instagram, TikTok, Facebook)</h2>
      <p>
        Because these three platforms do not offer an open public API, OrderDeck uses a
        Chrome/Edge browser extension. The extension only:
      </p>
      <ul>
        <li>Reads chat messages from the live-stream page the user opens</li>
        <li>Forwards messages over a local WebSocket connection to the desktop app</li>
        <li>Sends data to no other destination</li>
      </ul>

      <h2>4. Cookies and site analytics</h2>
      <p>
        The <strong>orderdeckapp.com</strong> website does not currently use third-party
        analytics or advertising cookies. Only standard server access logs (IP, request
        path, user agent) from our Caddy reverse proxy are kept, and they are deleted
        within 30 days.
      </p>

      <h2>5. Data retention</h2>
      <ul>
        <li>Account data (email, license): until the account is deleted</li>
        <li>Server access logs: 30 days</li>
        <li>Security logs: 90 days</li>
        <li>Live-stream chat messages: only while the application is open</li>
        <li>OAuth tokens: until the user revokes consent (stored on user's machine)</li>
      </ul>

      <h2>6. User rights</h2>
      <p>Under GDPR and Türkiye's KVKK you have the right to:</p>
      <ul>
        <li>Access your data</li>
        <li>Request correction</li>
        <li>Request deletion (full account closure)</li>
        <li>Object to processing</li>
        <li>Data portability</li>
      </ul>
      <p>
        To exercise any of these rights, write to{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>. We respond within 30 days.
      </p>

      <h2>7. Data security</h2>
      <p>
        Transit is encrypted with TLS 1.2+. Backups are encrypted with AES-256-GCM. JWT
        tokens are signed with a secure secret. That said, no method of internet
        transmission is 100% secure; you are also responsible for keeping your password
        confidential.
      </p>

      <h2>8. Children</h2>
      <p>
        {BRAND} is not intended for users under 18. We do not knowingly collect data
        from anyone under 18.
      </p>

      <h2>9. Policy changes</h2>
      <p>
        When this policy is updated, the "Effective date" at the top changes. For
        material changes, we notify registered users by email.
      </p>

      <h2>10. Contact</h2>
      <p>
        For any privacy-related question:{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>
    </LegalLayout>
  );
}
