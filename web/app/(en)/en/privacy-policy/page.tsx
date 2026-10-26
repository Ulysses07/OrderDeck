import type { Metadata } from 'next';
import { LegalLayout } from '@/components/LegalLayout';
import { CONTACT_EMAIL, LEGAL_NAME, BRAND } from '@/lib/i18n';

export const metadata: Metadata = {
  title: 'Privacy Policy',
  description: `How ${BRAND} handles personal data — scope, retention, sharing, user rights.`,
};

const EFFECTIVE_DATE = '2026-09-04';

/**
 * Effective date of the Turkish policy (web/app/(tr)/gizlilik-politikasi/page.tsx). While this translation lags behind, the note
 * below says the Turkish text prevails; PolicyEffectiveDateParityTests (server tests) keeps this equal to the Turkish date.
 * When the full translation is published with the Turkish effective date, delete this constant and the note.
 */
const TURKISH_EFFECTIVE_DATE = '2026-10-21';

export default function PrivacyEn() {
  return (
    <LegalLayout
      locale="en"
      routeKey="privacy"
      title="Privacy Policy"
      effectiveDate={EFFECTIVE_DATE}
    >
      <p>
        <strong>Note:</strong> This English text is a convenience translation. The Turkish version at{' '}
        <a href="/gizlilik-politikasi/">orderdeckapp.com/gizlilik-politikasi</a>, effective {TURKISH_EFFECTIVE_DATE},
        prevails. It currently contains sections not yet reflected here, including its wider scope (broadcaster panel,
        customer app and registration form), the legal grounds for the registration form, Section 1.2a on the broadcaster&apos;s customer and sales records held on the server, Section
        1.4 on the processing of broadcasters&apos; bank account transactions, Section 1.5 on
        the customer app and payment receipts, Section 1.6 on service providers and transfers abroad, Section 1.7 on
        WhatsApp conversations, and the updated retention periods, rights, application channels, data security and
        policy-change sections.
      </p>
      <p>
        This Privacy Policy describes how the <strong>{BRAND}</strong> Windows desktop
        application and the <strong>orderdeckapp.com</strong> website collect, use,
        store and share personal data.
      </p>
      <p>
        <strong>Data controller:</strong> {LEGAL_NAME} (sole proprietor). Postal address: Gazi Mah. Yeşilırmak Cad.
        No:172/A Kepez/ANTALYA, Türkiye. Registered electronic mail (KEP): emarglobal@hs01.kep.tr. Contact:{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>

      <h2>1. Data we collect</h2>
      <p>{BRAND} works with the following kinds of data:</p>

      <h3>1.1. Account and license data</h3>
      <ul>
        <li>Email address (for sign-up and license management)</li>
        <li>License key, machine fingerprint</li>
        <li>
          License and update-pack status (license type, term and number of devices that can be activated). The license
          fee is paid by bank transfer, with no payment institution in between. No separate record of the license
          payment is kept on the {BRAND} server; payment details (payer name, amount, date) are kept in our invoicing
          and accounting records (Section 1.1 of the Turkish version). For broadcasters&apos; bank account
          transactions, see Section 1.4 of the Turkish version
        </li>
        <li>
          IP address (in security logs; deleted after 90 days) and login time (in session records until the account is
          deleted; in audit records, together with the email address, for 24 months, see Section 5)
        </li>
        <li>
          Staff accounts the broadcaster adds in the panel: the staff member&apos;s email address, name and role, a hash
          used to verify their password (the password itself is not stored), and the times the account was added and
          last signed in. This data is kept until the broadcaster removes the staff account or the broadcaster&apos;s
          account is deleted; for the email address and name in the staff add/remove records, see Section 5
        </li>
      </ul>

      <h3>1.2. Live-stream chat data</h3>
      <p>
        The application reads live-stream chat from the platforms you connect (Instagram,
        TikTok, Facebook, YouTube). These messages are:
      </p>
      <ul>
        <li><strong>Stored only on your own computer</strong> in a memory ring buffer (max 500 messages)</li>
        <li><strong>Discarded entirely</strong> when the application closes</li>
        <li><strong>Not sent to {BRAND} servers</strong> (except in the two cases below) and never shared with third parties</li>
      </ul>
      <p>
        <strong>First exception:</strong> comments the broadcaster{' '}
        <strong>converts into an order/label</strong>. The text of such a
        comment is kept as part of the order record on the broadcaster&apos;s
        computer and is transferred to {BRAND} servers together with the order
        data, so the broadcaster can run their business (orders, shipping,
        dispute tracking). When a commenter places an order or enters a giveaway run by the broadcaster, their username
        and display name (and, when the broadcaster blocks them from chat, their username) enter the broadcaster&apos;s
        customer records and are transferred to {BRAND} servers with those records (Section 1.2a of the Turkish
        version); giveaway and block records do not contain the comment text. See the{' '}
        <a href="/en/data-deletion">Data Deletion</a> page for details.
      </p>
      <p>
        <strong>Second exception: Instagram registration link.</strong> For broadcasters who have connected their
        Instagram account to {BRAND} for the registration-link flow (&quot;!kayıt&quot;), Meta also forwards the comments
        posted on that account&apos;s live broadcasts (comment text and the commenter&apos;s Instagram username and ID) to
        the {BRAND} server. The server only sends a private Instagram message with the broadcaster&apos;s registration-form
        link to viewers who type &quot;!kayıt&quot;; all other comments are discarded without being stored. A forwarded
        notification stays in the server&apos;s job queue for 1 day after it is processed and is then deleted; if
        processing fails, it may stay until the error is examined and the entry deleted. The broadcaster is the data
        controller for this processing; {BRAND} processes it on the broadcaster&apos;s behalf. Apart from these two
        exceptions, the raw chat stream is not sent to our servers.
      </p>

      <h3>1.3. Buyer information collected via the registration form</h3>
      <p>
        Broadcasters may create a personal registration link (web form) to handle their
        viewers&apos; order, shipping, and invoicing. Information collected from buyers who
        fill out this form:
      </p>
      <ul>
        <li>Platform usernames (Instagram, YouTube, Facebook, TikTok — as declared by the buyer)</li>
        <li>
          If the buyer chooses the optional &quot;Sign in with Google&quot; or
          &quot;Sign in with Facebook&quot; button on the form: the verified identity of
          their own account — for YouTube the channel title, handle and channel ID; for
          Facebook the display name (see Section 2 for details of the YouTube flow)
        </li>
        <li>Full name, email, phone (WhatsApp) number, and shipping address</li>
        <li>Optional Turkish national ID (T.C. Kimlik No) for invoicing (only if the buyer chooses to provide it)</li>
        <li>Explicit consent to receive informational messages via WhatsApp and/or SMS (checkboxes)</li>
        <li>
          The IP address and browser details from which the form was submitted (deleted from the form record after 90
          days; if the buyer gives SMS consent, they are also kept as proof of the consent action in the SMS consent
          records, where they are retained for 3 more years after the broadcaster leaves the platform, see Section 5),
          and the version of the notice and of the broadcaster&apos;s data-controller information shown to the buyer
        </li>
      </ul>
      <p>
        This data is processed for the relevant broadcaster&apos;s order, payment, shipping
        and invoicing purposes, including identifying which customer a bank transfer to the
        broadcaster&apos;s account belongs to (see Section 1.4 of the Turkish version); it is
        never shared or sold to third parties for marketing. Consent for informational and
        marketing messages is requested with the checkboxes on the form, and the buyer may
        withdraw it at any time; marketing SMS are not sent through {BRAND} to numbers without
        SMS consent. Messages about the buyer&apos;s order, payment and shipping are sent to the
        WhatsApp number the buyer provides. The broadcaster who created the form is the data controller for this
        information; {BRAND} hosts it on the broadcaster&apos;s behalf and delivers it to the
        broadcaster&apos;s application. To measure the accuracy of that transfer matching,
        {' '}{BRAND} uses this data on its own behalf.
      </p>

      <h2>2. YouTube API usage</h2>
      <p>
        {BRAND} uses the YouTube Data API v3 in two separate flows. {BRAND}'s use
        of information received from YouTube APIs adheres to the{' '}
        <a href="https://developers.google.com/terms/api-services-user-data-policy" target="_blank" rel="noreferrer">
          Google API Services User Data Policy
        </a>, including the <strong>Limited Use</strong> requirements.
      </p>

      <h3>2.1. Desktop application (broadcaster&apos;s own account)</h3>
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
      <p>Data obtained via the YouTube API in the desktop flow:</p>
      <ul>
        <li>
          Apart from comments converted into an order/label and the channel name and channel ID of commenters who enter
          the broadcaster&apos;s customer records (those who order, enter a giveaway or are blocked from chat by the
          broadcaster; for someone blocked from chat who has no other record, only the channel ID) (Section 1.2 above;
          Section 1.2a of the Turkish version), is processed only on the user&apos;s own machine. The customer, order and giveaway records on the broadcaster&apos;s computer, together with these
          commenters&apos; profile picture URLs, are also contained in the encrypted backups the desktop application
          uploads to the server (Section 5 of the Turkish version)
        </li>
        <li>
          Apart from these exceptions, is not transmitted to {BRAND} servers; where the broadcaster has a bank
          connection, the channel names and channel IDs transmitted to the server are also used in the transfer
          matching and accuracy measurement described in Section 1.4 of the Turkish version
        </li>
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

      <h3>2.2. Registration form &quot;Sign in with Google&quot; (buyer&apos;s own channel)</h3>
      <p>
        On the buyer registration form (Section 1.3), a buyer may optionally press
        &quot;Sign in with Google&quot; instead of typing their YouTube handle by hand.
        This flow requests a single OAuth scope:
      </p>
      <ul>
        <li>
          <code>https://www.googleapis.com/auth/youtube.readonly</code> — used for one
          server-side call to <code>channels.list(mine=true)</code> that reads only the
          title, handle (customUrl) and channel ID of the buyer&apos;s <strong>own</strong>{' '}
          channel. No subscriptions, playlists, videos, analytics or any other data are
          read; nothing is ever posted, modified or deleted.
        </li>
      </ul>
      <p>In this flow:</p>
      <ul>
        <li>
          The OAuth access token is used once, server-side, and immediately discarded —
          we store <strong>no</strong> access or refresh tokens
        </li>
        <li>
          The retrieved channel title, handle and channel ID are stored solely as part of
          that buyer&apos;s own registration and are visible to the broadcaster the buyer is
          registering with; where the broadcaster has a bank connection, they are also used in
          the matching and accuracy measurement described in Section 1.4 of the Turkish version
          and may be seen by {BRAND}&apos;s authorised administrator (retention: Section 5,
          &quot;Buyer information&quot;)
        </li>
        <li>The data is not used for advertising, profiling, or training machine-learning models, and is not sold or transferred to third parties</li>
        <li>
          Buyers may revoke the app&apos;s access at any time via{' '}
          <a href="https://myaccount.google.com/permissions" target="_blank" rel="noreferrer">
            https://myaccount.google.com/permissions
          </a>{' '}
          and may request deletion of their registration as described in Section 6
        </li>
      </ul>
      <p>
        {BRAND} uses YouTube API Services. Google&apos;s processing of user data is governed
        by the <strong>Google Privacy Policy</strong>{' '}
        (<a href="https://policies.google.com/privacy" target="_blank" rel="noreferrer">
          http://www.google.com/policies/privacy
        </a>).
      </p>

      <h2>3. Browser extension (TikTok only)</h2>
      <p>
        Live-stream chat for YouTube, Instagram and Facebook is read through the
        platforms&apos; official APIs. Because TikTok does not offer an open public
        API, OrderDeck uses a Chrome/Edge browser extension for TikTok only. The
        extension only:
      </p>
      <ul>
        <li>Reads chat messages from the live-stream page the user opens</li>
        <li>Forwards messages over a local WebSocket connection to the desktop app</li>
        <li>
          Sends chat data to no other destination; it only downloads up-to-date page selectors (technical definitions
          used to locate the chat area on the page) from the {BRAND} server. That request carries no chat data; its IP
          address and browser details end up in the server access logs (Section 5)
        </li>
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
        <li>
          Account data (email, license): until the account is deleted; after deletion, the email address remains in audit
          records (records of sign-ins, session renewals and sign-outs, data exports, adding and removing panel staff,
          license issuance and account deletion) for at most 24 months from the date of each record and is then deleted
          automatically. The email address
          and name of staff the broadcaster added in the panel likewise remain in the staff add/remove records for the
          same period
        </li>
        <li>
          Staff accounts the broadcaster adds in the panel (Section 1.1): until the broadcaster removes the staff account
          or the broadcaster&apos;s account is deleted
        </li>
        <li>Buyer information from the registration form: on the server, until the broadcaster&apos;s account is deleted (see Section 5 of the Turkish version)</li>
        <li>
          SMS consents (Section 1.3): the current consent record is deleted 30 days after the broadcaster leaves the
          platform; proof of consent actions (phone number, consent or refusal, its date and source, and the IP address
          and browser details of the request) is kept until the broadcaster leaves and for 3 years afterwards (Law No.
          6563, Art. 13). Proof of a consent given or withdrawn in the customer app while the broadcaster had no
          verified SMS brand is kept until the broadcaster&apos;s account (license) is deleted and for 3 years afterwards
        </li>
        <li>
          Backups the desktop application uploads to the server (the encrypted copy of the customer, order, label, stream
          and giveaway records on the broadcaster&apos;s computer): the last 5 backups and the first backup of each month
          are kept for each broadcaster. The last 5 are rotated as new backups arrive; the first backup of each month is
          deleted 12 months after it was taken. The broadcaster&apos;s newest backup is not deleted even if it is older
          than 12 months, and if the broadcaster stops uploading backups, their last backups remain until the
          broadcaster&apos;s account is deleted. These backups are used for restores and restore drills; in addition, {BRAND}&apos;s authorised
          administrator may, only upon the broadcaster&apos;s technical support request, view the contents of a backup
          (customer, label, giveaway and stream records) in the admin panel on the broadcaster&apos;s behalf. For
          viewing, the backup is opened in a temporary copy on the server, which is deleted as soon as the page&apos;s
          data has been read; every view is written to the audit log
        </li>
        <li>Server access logs: 30 days</li>
        <li>IP addresses and browser details in security logs: 90 days</li>
        <li>
          Live-stream chat messages: only while the application is open
          (except the text of comments converted into an order/label — that
          text is retained as part of the order record, for as long as the
          order record itself)
        </li>
        <li>
          OAuth tokens: until the user revokes consent (stored on user&apos;s machine). The broadcaster&apos;s Meta access
          tokens for the Instagram registration link (Section 1.2) and the WhatsApp line are kept encrypted on the server:
          the WhatsApp token until the line is disconnected, the Instagram token until the broadcaster&apos;s account is
          deleted
        </li>
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
        Transit is encrypted with TLS 1.2+. Remote backups of the server database are
        encrypted with OpenPGP (AES-256), and backups uploaded by the desktop application with
        AES-256-GCM; the short-lived backup copy on the server&apos;s own disk is also encrypted.
        JWT tokens are signed with a secure secret. That said, no method of internet
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
