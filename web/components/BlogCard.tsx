import Link from 'next/link';
import { ROUTE_MAP, type Locale } from '@/lib/i18n';
import { formatPostDate, type Post } from '@/lib/blog';

export function BlogCard({ post, locale }: { post: Post; locale: Locale }) {
  const href = `${ROUTE_MAP.blog[locale]}${post.slug}/`;
  return (
    <Link
      href={href}
      className="group block rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] p-6 transition-colors hover:border-[var(--color-accent)]"
    >
      <p className="text-xs uppercase tracking-wider text-[var(--color-text-mute)]">
        {formatPostDate(post.frontmatter.date, locale)}
      </p>
      <h2 className="mt-2 text-lg font-semibold text-[var(--color-text)] group-hover:text-[var(--color-accent)]">
        {post.frontmatter.title}
      </h2>
      <p className="mt-2 text-sm leading-relaxed text-[var(--color-text-dim)] line-clamp-2">
        {post.frontmatter.description}
      </p>
    </Link>
  );
}
