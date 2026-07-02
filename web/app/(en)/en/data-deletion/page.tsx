import type { Metadata } from 'next';
import { LegalLayout } from '@/components/LegalLayout';
import { CONTACT_EMAIL, LEGAL_NAME, BRAND } from '@/lib/i18n';

export const metadata: Metadata = {
  title: 'Data Deletion Instructions',
  description: `How to delete data collected by ${BRAND}, including via Facebook Login.`,
};

const EFFECTIVE_DATE = '2026-07-02';

export default function DataDeletionEn() {
  return (
    <LegalLayout
      locale="en"
      routeKey="dataDeletion"
      title="Data Deletion Instructions"
      effectiveDate={EFFECTIVE_DATE}
    >
      <p>
        This page explains how to delete data collected through <strong>{BRAND}</strong>,
        including data obtained via Facebook Login and connected platforms.
        Data controller: {LEGAL_NAME}. Contact:{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>

      <h2>1. Data obtained via Facebook</h2>
      <p>On the operator&apos;s own Facebook Page live broadcast, {BRAND}:</p>
      <ul>
        <li>reads live <strong>comment text</strong> (to take orders),</li>
        <li>receives the commenter&apos;s Facebook-provided <strong>name and page-scoped
          ID (PSID)</strong>,</li>
        <li>stores the <strong>Page access token</strong> granted via Facebook Login
          (encrypted, on the operator&apos;s machine).</li>
      </ul>
      <p>
        The live chat stream itself is transient and is deleted when the app is closed.
        However, the <strong>customer record and order/label</strong> derived from a
        comment are retained so the operator can run their business.
      </p>

      <h2>2. If you are the operator (OrderDeck user)</h2>
      <ul>
        <li>Delete the relevant customer/order records from the <strong>Customer
          Details</strong> screen in the app.</li>
        <li>Use <strong>“Disconnect”</strong> under Settings → Facebook to remove the
          Page access token and OAuth data.</li>
        <li>To delete your entire account and associated data, email{' '}
          <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>.</li>
      </ul>

      <h2>3. If you are a viewer / commenter</h2>
      <p>
        If you commented on an operator&apos;s live broadcast and want your data deleted,
        you have two options:
      </p>
      <ul>
        <li>Contact the <strong>operator (Page owner)</strong> who processes your data, or</li>
        <li>Email <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a> with your Facebook
          name and any comment details. After verifying your request, we delete the
          relevant records <strong>within 30 days</strong>.</li>
      </ul>

      <h2>4. Facebook Login data</h2>
      <p>
        You can remove the permissions granted with your Facebook account at any time from{' '}
        <a href="https://www.facebook.com/settings?tab=business_tools" target="_blank" rel="noopener noreferrer">
          Facebook → Settings → Business Integrations</a>. Once removed, {BRAND} can no
        longer access your data, and any token stored on the machine becomes invalid.
      </p>
    </LegalLayout>
  );
}
