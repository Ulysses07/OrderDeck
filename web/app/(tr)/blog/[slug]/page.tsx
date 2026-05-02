import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import Link from 'next/link';
import { MDXRemote } from 'next-mdx-remote/rsc';
import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { getAllPosts, getPostBySlug, formatPostDate } from '@/lib/blog';
import { ROUTE_MAP } from '@/lib/i18n';

interface PageProps {
  params: Promise<{ slug: string }>;
}

export async function generateStaticParams() {
  return getAllPosts('tr').map((p) => ({ slug: p.slug }));
}

export async function generateMetadata({ params }: PageProps): Promise<Metadata> {
  const { slug } = await params;
  const post = getPostBySlug('tr', slug);
  if (!post) return {};
  return {
    title: post.frontmatter.title,
    description: post.frontmatter.description,
  };
}

export default async function BlogPostTr({ params }: PageProps) {
  const { slug } = await params;
  const post = getPostBySlug('tr', slug);
  if (!post) notFound();

  return (
    <>
      <Nav locale="tr" routeKey="blog" />
      <main className="mx-auto max-w-3xl px-5 py-16">
        <Link
          href={ROUTE_MAP.blog.tr}
          className="text-sm text-[var(--color-text-mute)] hover:text-[var(--color-accent)]"
        >
          ← Tüm yazılar
        </Link>
        <h1 className="mt-6 text-4xl font-bold tracking-tight md:text-5xl">
          {post.frontmatter.title}
        </h1>
        <p className="mt-3 text-sm text-[var(--color-text-mute)]">
          {formatPostDate(post.frontmatter.date, 'tr')}
          {post.frontmatter.author ? ` · ${post.frontmatter.author}` : ''}
        </p>
        <article className="prose mt-10">
          <MDXRemote source={post.content} />
        </article>
      </main>
      <Footer locale="tr" />
    </>
  );
}
