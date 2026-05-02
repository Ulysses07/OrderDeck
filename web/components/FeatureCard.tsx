import type { LucideIcon } from 'lucide-react';

interface FeatureCardProps {
  icon: LucideIcon;
  title: string;
  body: string;
}

export function FeatureCard({ icon: Icon, title, body }: FeatureCardProps) {
  return (
    <article className="group relative rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] p-6 transition-colors hover:border-[var(--color-accent)]">
      <div className="grid h-10 w-10 place-items-center rounded-lg bg-[var(--color-bg)] text-[var(--color-accent)]">
        <Icon size={20} aria-hidden />
      </div>
      <h3 className="mt-4 text-lg font-semibold text-[var(--color-text)]">{title}</h3>
      <p className="mt-2 text-sm leading-relaxed text-[var(--color-text-dim)]">{body}</p>
    </article>
  );
}
