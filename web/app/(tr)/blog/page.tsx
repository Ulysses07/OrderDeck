import type { Metadata } from 'next';
import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { BlogCard } from '@/components/BlogCard';
import { getAllPosts } from '@/lib/blog';

export const metadata: Metadata = {
  title: 'Blog',
  description: 'OrderDeck blog — yayın ipuçları, ürün güncellemeleri, mezatçılar için rehberler.',
};

export default function BlogIndexTr() {
  const posts = getAllPosts('tr');

  return (
    <>
      <Nav locale="tr" routeKey="blog" />
      <main className="mx-auto max-w-4xl px-5 py-16">
        <h1 className="text-4xl font-bold tracking-tight md:text-5xl">Blog</h1>
        <p className="mt-3 text-base text-[var(--color-text-dim)]">
          Yayın ipuçları, ürün güncellemeleri ve mezatçılar için rehberler.
        </p>
        <div className="mt-10 grid gap-5 sm:grid-cols-2">
          {posts.length === 0 ? (
            <p className="text-sm text-[var(--color-text-mute)]">Henüz yazı yok. Yakında!</p>
          ) : (
            posts.map((post) => <BlogCard key={post.slug} post={post} locale="tr" />)
          )}
        </div>
      </main>
      <Footer locale="tr" />
    </>
  );
}
