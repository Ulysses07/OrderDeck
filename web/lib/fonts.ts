import { Bricolage_Grotesque, IBM_Plex_Sans, JetBrains_Mono } from 'next/font/google';

/**
 * Landing redesign fontları (Claude design handoff). next/font self-host eder
 * (static export uyumlu), CSS değişkenleri olarak globals.css + landing.css'e
 * bağlanır. Türkçe karakterler için 'latin-ext' subset şart.
 */
export const fontDisplay = Bricolage_Grotesque({
  subsets: ['latin', 'latin-ext'],
  display: 'swap',
  variable: '--font-disp',
});

export const fontSansOd = IBM_Plex_Sans({
  subsets: ['latin', 'latin-ext'],
  weight: ['400', '500', '600', '700'],
  display: 'swap',
  variable: '--font-sans-od',
});

export const fontMonoOd = JetBrains_Mono({
  subsets: ['latin', 'latin-ext'],
  weight: ['400', '500', '600', '700'],
  display: 'swap',
  variable: '--font-mono-od',
});

/** Üç fontun CSS değişken sınıfları — layout body'sine eklenir. */
export const fontVars = `${fontDisplay.variable} ${fontSansOd.variable} ${fontMonoOd.variable}`;
