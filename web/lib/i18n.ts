/**
 * Minimalist i18n — sadece iki dil + statik export uyumlu.
 *
 * next-intl gibi büyük bir kütüphane yerine düz tip-güvenli mesaj objeleri
 * tutuyoruz; her sayfa kendi locale'ini biliyor (TR rotalar root altında,
 * EN rotalar /en altında). LangSwitch komponenti karşılık gelen URL'lere
 * yönlendiriyor.
 */

export type Locale = 'tr' | 'en';

export const LOCALES: readonly Locale[] = ['tr', 'en'] as const;

export const DEFAULT_LOCALE: Locale = 'tr';

/**
 * Aynı sayfanın TR ve EN URL eşleşmesi — LangSwitch bu tabloyla URL üretiyor.
 * Her iki yönde simetrik tutmak için key'ler aynı: TR-side ve EN-side.
 */
export const ROUTE_MAP: Record<string, { tr: string; en: string }> = {
  home:    { tr: '/',                       en: '/en/' },
  features:{ tr: '/ozellikler/',            en: '/en/features/' },
  pricing: { tr: '/fiyatlandirma/',         en: '/en/pricing/' },
  faq:     { tr: '/sss/',                   en: '/en/faq/' },
  blog:    { tr: '/blog/',                  en: '/en/blog/' },
  privacy: { tr: '/gizlilik-politikasi/',   en: '/en/privacy-policy/' },
  terms:   { tr: '/kullanim-kosullari/',    en: '/en/terms-of-service/' },
  contact: { tr: '/iletisim/',              en: '/en/contact/' },
};

export const SITE_URL = 'https://orderdeckapp.com';
export const CONTACT_EMAIL = 'support@orderdeckapp.com';
export const LEGAL_NAME = 'Musa Sevinç';
export const BRAND = 'OrderDeck';
