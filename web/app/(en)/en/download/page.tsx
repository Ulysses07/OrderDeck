import type { Metadata } from 'next';
import { Download, AlertTriangle, CheckCircle2 } from 'lucide-react';
import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { en } from '@/messages/en';
import { LATEST_RELEASE, downloadUrl } from '@/lib/i18n';

export const metadata: Metadata = {
  title: 'Download',
  description:
    'Download OrderDeck desktop app for Windows 10/11 free. ' +
    'Self-contained installer; the in-app first-run wizard walks you through setup in 6 steps.',
};

export default function DownloadEn() {
  const m = en.downloadPage;

  return (
    <>
      <Nav locale="en" routeKey="download" />
      <main className="mx-auto max-w-3xl px-5 py-16">
        <h1 className="text-3xl font-bold tracking-tight md:text-4xl">{m.title}</h1>
        <p className="mt-3 text-base text-[var(--color-text-dim)]">{m.subtitle}</p>

        <div
          className="mt-10 rounded-2xl border border-[var(--color-accent)] bg-[var(--color-surface)] p-8 md:p-10"
          style={{
            backgroundImage:
              'radial-gradient(60% 100% at 50% 0%, rgba(32,197,247,0.12) 0%, transparent 70%)',
          }}
        >
          <div className="grid gap-6 md:grid-cols-3">
            <Stat label={m.versionLabel} value={`v${LATEST_RELEASE.version}`} />
            <Stat label={m.sizeLabel} value={`${LATEST_RELEASE.sizeMB} MB`} />
            <Stat label={m.releasedLabel} value={LATEST_RELEASE.releasedAt} />
          </div>

          <a
            href={downloadUrl()}
            download={LATEST_RELEASE.filename}
            className="mt-8 inline-flex w-full items-center justify-center gap-2 rounded-lg bg-[var(--color-accent)] px-6 py-4 text-base font-semibold text-white hover:bg-[var(--color-accent-hot)] transition-colors md:w-auto"
          >
            <Download size={18} aria-hidden />
            {m.downloadButton}
            <span className="ml-2 text-xs font-normal opacity-80">
              ({LATEST_RELEASE.filename})
            </span>
          </a>
        </div>

        <section className="mt-10 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] p-6">
          <div className="flex items-start gap-3">
            <AlertTriangle
              size={20}
              className="mt-0.5 flex-shrink-0 text-amber-400"
              aria-hidden
            />
            <div>
              <h2 className="font-semibold">{m.smartScreenWarning.title}</h2>
              <p className="mt-2 text-sm text-[var(--color-text-dim)]">
                {m.smartScreenWarning.body}
              </p>
              <ol className="mt-3 list-decimal space-y-1 pl-5 text-sm text-[var(--color-text-dim)]">
                <li>{m.smartScreenWarning.step1}</li>
                <li>{m.smartScreenWarning.step2}</li>
              </ol>
            </div>
          </div>
        </section>

        <section className="mt-6 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] p-6">
          <h2 className="font-semibold">{m.nextSteps.title}</h2>
          <ol className="mt-3 list-decimal space-y-2 pl-5 text-sm text-[var(--color-text-dim)]">
            <li>{m.nextSteps.step1}</li>
            <li>{m.nextSteps.step2}</li>
            <li>{m.nextSteps.step3}</li>
          </ol>
          <p className="mt-4 text-sm">
            <a
              href="https://github.com/Ulysses07/OrderDeck/blob/master/SETUP.md"
              target="_blank"
              rel="noopener noreferrer"
              className="text-[var(--color-accent)] hover:underline"
            >
              {m.nextSteps.docsLink} →
            </a>
          </p>
        </section>

        <section className="mt-6 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] p-6">
          <h2 className="font-semibold">{m.requirements.title}</h2>
          <ul className="mt-3 space-y-2 text-sm text-[var(--color-text-dim)]">
            <Req text={m.requirements.os} />
            <Req text={m.requirements.arch} />
            <Req text={m.requirements.disk} />
            <Req text={m.requirements.net} />
            <Req text={m.requirements.chrome} />
          </ul>
        </section>
      </main>
      <Footer locale="en" />
    </>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <p className="text-xs uppercase tracking-wider text-[var(--color-text-mute)]">
        {label}
      </p>
      <p className="mt-1 text-lg font-semibold">{value}</p>
    </div>
  );
}

function Req({ text }: { text: string }) {
  return (
    <li className="flex items-start gap-2">
      <CheckCircle2
        size={16}
        className="mt-0.5 flex-shrink-0 text-[var(--color-accent)]"
        aria-hidden
      />
      <span>{text}</span>
    </li>
  );
}
