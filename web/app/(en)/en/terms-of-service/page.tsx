import type { Metadata } from 'next';
import { LegalLayout } from '@/components/LegalLayout';
import { CONTACT_EMAIL, LEGAL_NAME, BRAND } from '@/lib/i18n';

export const metadata: Metadata = {
  title: 'Terms of Service',
  description: `${BRAND} terms of service, license and liability terms.`,
};

const EFFECTIVE_DATE = '2026-05-02';

export default function TermsEn() {
  return (
    <LegalLayout
      locale="en"
      routeKey="terms"
      title="Terms of Service"
      effectiveDate={EFFECTIVE_DATE}
    >
      <p>
        These Terms govern the agreement between you and <strong>{LEGAL_NAME}</strong>{' '}
        when you use the <strong>{BRAND}</strong> Windows desktop application or the{' '}
        <strong>orderdeckapp.com</strong> website. By using the service, you accept
        these Terms.
      </p>

      <h2>1. Service description</h2>
      <p>
        {BRAND} is a Windows desktop application designed for live auction streamers,
        primarily in Türkiye. Its core capabilities are:
      </p>
      <ul>
        <li>Multi-platform live-stream chat aggregation in a single panel</li>
        <li>Real-time thermal label printing</li>
        <li>Giveaway management (keyword + spinning wheel)</li>
        <li>Spam and troll filter</li>
        <li>YouTube live moderation integration</li>
      </ul>

      <h2>2. License model</h2>
      <ul>
        <li>A 14-day free full trial is enabled at first install; no payment information required.</li>
        <li>
          The OrderDeck license grants you a <strong>lifetime right to use</strong> the
          application via a one-time payment. You may run the version available at the
          time of purchase indefinitely.
        </li>
        <li>
          One license is bound to one machine and is tied to a single user / business.
          Transfers can be done from the dashboard up to twice per month.
        </li>
        <li>
          <strong>Yearly updates and support pack (optional):</strong> a yearly add-on
          covers new features, API repairs caused by third-party platform changes, and
          priority support. The first year is included with the license. Whether to
          renew it later is entirely your choice; without it, the version you purchased
          continues to work.
        </li>
        <li>
          Without an active update pack, we make no warranty that <strong>third-party
          platform integrations</strong> (Instagram / TikTok / Facebook / YouTube) will
          continue to function over time; repairs to those integrations are delivered
          via the update pack.
        </li>
        <li>Refund policy: full refund within 14 days of the original purchase.</li>
      </ul>

      <h2>3. Acceptable use</h2>
      <p>While using {BRAND} you agree to refrain from:</p>
      <ul>
        <li>Auction fraud, fake bids or otherwise misleading sales practices</li>
        <li>
          Selling goods that violate applicable law (counterfeit, copyright-infringing,
          prohibited substances, etc.)
        </li>
        <li>
          Violating the terms of service of the streaming platforms (Instagram, TikTok,
          Facebook, YouTube)
        </li>
        <li>Reverse-engineering {BRAND} or attempting to bypass license enforcement</li>
        <li>Generating excessive automated API traffic that disrupts the service</li>
        <li>Attempting unauthorised access to other users' accounts or data</li>
      </ul>
      <p>
        Violations of this section may result in account suspension or termination
        without prior notice.
      </p>

      <h2>4. Intellectual property</h2>
      <p>
        The {BRAND} brand, logo, design, source code and documentation are owned by{' '}
        {LEGAL_NAME}. Your license grants you the right to use the application within
        the scope of your license — it does not transfer ownership of the
        underlying source code.
      </p>

      <h2>5. Third-party services</h2>
      <p>
        The application connects to third-party services to function: the YouTube Data
        API (Google), and Instagram, TikTok and Facebook environments via a browser
        extension. Each of these services has its own terms and privacy policies, which
        apply to your use of them. {BRAND} makes no guarantee of uninterrupted
        availability of those third-party services.
      </p>

      <h2>6. Warranty disclaimer</h2>
      <p>
        {BRAND} is provided "as is". No warranty is given that the service will be
        uninterrupted, error-free, or fit for any particular purpose. Hardware
        compatibility, printer drivers, internet connectivity and upstream platform
        changes are your responsibility.
      </p>

      <h2>7. Limitation of liability</h2>
      <p>
        To the maximum extent permitted by law, {LEGAL_NAME} is not liable for any
        indirect, incidental, special or punitive damages (including loss of profit,
        data or business interruption) arising out of or in connection with the
        service. Total aggregate liability for direct damages is limited to the amount
        of fees paid by you to {LEGAL_NAME} in the preceding 12 months.
      </p>

      <h2>8. Suspension and termination</h2>
      <ul>
        <li>Your account may be suspended or terminated if you breach these Terms.</li>
        <li>You may delete your account from the customer dashboard at any time.</li>
        <li>
          Upon account deletion your license rights end; refunds, if any, are evaluated
          per Section 2.
        </li>
      </ul>

      <h2>9. Changes to these Terms</h2>
      <p>
        These Terms may be updated from time to time. For material changes, registered
        users are notified by email at least 30 days in advance. Continued use of the
        service after the change takes effect constitutes acceptance of the new Terms.
      </p>

      <h2>10. Governing law and jurisdiction</h2>
      <p>
        This agreement is governed by the laws of the Republic of Türkiye. Disputes
        shall be settled in the competent courts and enforcement offices of the
        Republic of Türkiye.
      </p>

      <h2>11. Contact</h2>
      <p>
        For questions: <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>
    </LegalLayout>
  );
}
