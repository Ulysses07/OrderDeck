/**
 * EN message dictionary — mirror of /messages/tr.ts. Keys MUST match exactly,
 * otherwise TypeScript compilation will fail (the `Messages` type is derived
 * from the TR side).
 */
import type { Messages } from './tr';

export const en: Messages = {
  nav: {
    features: 'Features',
    pricing: 'Pricing',
    blog: 'Blog',
    faq: 'FAQ',
    contact: 'Contact',
    download: 'Download',
    login: 'Sign in',
  },

  footer: {
    tagline: 'Live-stream chat, label printing and giveaways for Turkish auction streamers.',
    productLabel: 'Product',
    legalLabel: 'Legal',
    companyLabel: 'Company',
    privacy: 'Privacy Policy',
    terms: 'Terms of Service',
    rights: 'All rights reserved.',
  },

  hero: {
    eyebrow: 'For live auction streamers',
    title: 'Every order from your live stream, in one window.',
    subtitle:
      'Aggregates Instagram, TikTok, Facebook and YouTube live chat into a single feed. Pick a message, set a price, send the label to your printer. Run keyword giveaways with a spinning wheel on screen.',
    ctaPrimary: 'Try free',
    ctaSecondary: 'See features',
    runtimeHint: 'Windows 10/11 · 14-day free trial · No credit card required',
  },

  features: {
    title: 'Everything you need on stream',
    subtitle: 'Run your auction without breaking flow — one tool for all of it.',
    items: [
      {
        title: '4 platforms in one panel',
        body:
          'Listens to Instagram, TikTok, Facebook and YouTube live chats simultaneously and renders them in a single chronological feed.',
      },
      {
        title: 'Real-time label printing',
        body:
          'Pick a message, type a price, send the label to your thermal printer. Made a mistake? One-click cancel — preset reasons included.',
      },
      {
        title: 'Spinning wheel giveaways',
        body:
          'Announce a keyword, OrderDeck collects participants, the wheel spins on screen so your audience watches the winner being drawn live.',
      },
      {
        title: 'Spam and troll filter',
        body:
          'Links, repeated messages, all-caps spam and operator-defined blocked words are dropped before they reach your view.',
      },
      {
        title: 'YouTube moderation API',
        body:
          'Right-click a message to delete it, or ban the user — OrderDeck talks directly to YouTube via the official Data API.',
      },
      {
        title: 'Local-first, on your machine',
        body:
          'Your data stays on your computer. Messages are never sent to OrderDeck servers. Open it, connect, close it, gone.',
      },
    ],
  },

  pricing: {
    title: 'Simple pricing',
    subtitle:
      'One plan, one upfront payment. Use it for life — opt into the yearly support pack only if you want updates.',
    yearly: 'yearly',
    perYear: '/year',
    cta: 'Buy license',
    contactCta: 'Contact us',
    plan: {
      badge: 'Lifetime',
      name: 'OrderDeck License',
      tagline: 'Pay once, use it on every platform forever.',
      price: '₺100,000',
      priceNote: 'one-time',
      priceSubnote: '14-day free trial · No credit card required',
      features: [
        'All 4 platforms (Instagram + TikTok + Facebook + YouTube)',
        'Real-time label printing',
        'Spinning-wheel giveaways',
        'YouTube moderation API integration',
        'Advanced spam and troll filter',
        'Label history and reporting',
        'Single-seat license (one operator / business)',
        'Lifetime use of the version you purchase',
      ],
    },
    support: {
      name: 'Yearly updates + support pack',
      tagline:
        'New features, API repairs when platforms change, and priority support. Optional — without it your purchased version keeps working.',
      price: '₺10,000',
      priceNote: '/year',
      features: [
        'Access to all new features',
        'API repairs for Instagram, TikTok, Facebook and YouTube',
        'Priority email support (24-hour response)',
        'Updates for new Windows versions',
      ],
      note:
        'First year is included. After that, you can renew from your dashboard whenever you like — pause and resume any time.',
    },
    note:
      'VAT included. Contact us for company / multi-seat licensing or refund questions.',
  },

  faq: {
    title: 'Frequently asked questions',
    items: [
      {
        q: 'What is OrderDeck?',
        a: 'OrderDeck is a Windows application that aggregates Instagram, TikTok, Facebook and YouTube live chat into one feed, prints labels in real-time and runs giveaways. Built for sellers who run auctions on live streams.',
      },
      {
        q: 'Which printers are supported?',
        a: 'Any Windows-installed thermal label printer works. We tested with Argox, Zebra and TSC models during development.',
      },
      {
        q: 'Do I need separate API keys for each platform?',
        a: 'No. For Instagram, TikTok and Facebook we listen via a browser extension. For YouTube we use OrderDeck\'s own verified YouTube API project — you just sign in with your YouTube account.',
      },
      {
        q: 'Are my messages stored anywhere?',
        a: 'No. All messages live only on your machine, in a memory ring buffer of up to 500 messages. They are dropped when you close the app. Nothing is sent to OrderDeck servers.',
      },
      {
        q: 'How does the trial work?',
        a: 'You get a 14-day free full trial on first install. When it expires, the app drops to read-only mode if you don\'t purchase a license — your existing labels are preserved but you can\'t start new sessions.',
      },
      {
        q: 'Can I run my license on multiple machines?',
        a: 'A license is bound to a single machine. You can transfer it between machines yourself from the dashboard (twice per month) without contacting support.',
      },
      {
        q: 'How does YouTube moderation (delete message, ban user) work?',
        a: 'It\'s built into the license. After signing in to YouTube and granting permission, you can right-click a chat message to delete it or ban the author. The action is sent directly to YouTube.',
      },
      {
        q: 'How long do I get updates after a one-time purchase?',
        a: 'You can keep using the version available at purchase forever. The first year of updates is free. After that, if you want new features and API repairs, you can opt into the ₺10,000/year update pack. Skip it and your purchased version keeps working.',
      },
      {
        q: 'Why a lifetime license instead of a subscription?',
        a: 'OrderDeck depends on third-party platforms (Instagram, TikTok, Facebook, YouTube) that change often, so we maintain it continuously. You pay once and own the version. The optional update pack funds the ongoing maintenance — fair for both sides.',
      },
    ],
  },

  contact: {
    title: 'Contact',
    subtitle:
      'Email is the fastest way to reach us with questions, requests or feedback.',
    emailLabel: 'Email',
    responseTime: 'We typically respond within 24 hours on business days.',
    privacyLink: 'Our privacy policy',
    privacyText:
      'explains how we handle personal data. Your email address is used only to reply to you.',
  },

  cta: {
    title: 'Get started',
    subtitle:
      'No credit card required. Try Pro free for 14 days — keep going if it works for you.',
    button: 'Download free',
  },

  downloadPage: {
    title: 'Download OrderDeck',
    subtitle:
      'For Windows 10 (22H2+) and Windows 11. Self-contained installer — no .NET or other runtime install required.',
    versionLabel: 'Version',
    sizeLabel: 'Size',
    releasedLabel: 'Released',
    downloadButton: 'Download now',
    smartScreenWarning: {
      title: 'If you see a SmartScreen warning',
      body: 'OrderDeck ships unsigned for now (code-signing certificate coming in 2026 Q3). If Windows shows "Windows protected your PC":',
      step1: 'Click "More info"',
      step2: 'Click "Run anyway"',
    },
    nextSteps: {
      title: 'After install',
      step1: 'A first-run setup wizard launches automatically',
      step2: 'The wizard walks you through license activation, YouTube channel setup, Chrome extension install, and OBS browser source URLs in 6 steps',
      step3: 'Chrome extension is awaiting Web Store approval; until then the wizard guides you through the sideload flow',
      docsLink: 'Full setup guide (SETUP.md)',
    },
    requirements: {
      title: 'System requirements',
      os: 'Windows 10 (22H2 or later) or Windows 11',
      arch: '64-bit processor (Intel/AMD or ARM64)',
      disk: '~500 MB free disk space',
      net: 'Internet connection for license activation',
      chrome: 'Google Chrome (for livestream chat ingestion)',
    },
  },

  langSwitch: {
    label: 'Language',
    tr: 'Türkçe',
    en: 'English',
  },
};
