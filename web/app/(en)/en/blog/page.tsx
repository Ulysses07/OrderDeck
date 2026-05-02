import type { Metadata } from 'next';
import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { BlogCard } from '@/components/BlogCard';
import { getAllPosts } from '@/lib/blog';

export const metadata: Metadata = {
  title: 'Blog',
  description: 'OrderDeck blog — streaming tips, product updates, guides for auction streamers.',
};

export default function BlogIndexEn() {
  const posts = getAllPosts('en');

  return (
    <>
      <Nav locale="en" routeKey="blog" />
      <main className="mx-auto max-w-4xl px-5 py-16">
        <h1 className="text-4xl font-bold tracking-tight md:text-5xl">Blog</h1>
        <p className="mt-3 text-base text-[var(--color-text-dim)]">
          Streaming tips, product updates, and guides for live-auction streamers.
        </p>
        <div className="mt-10 grid gap-5 sm:grid-cols-2">
          {posts.length === 0 ? (
            <p className="text-sm text-[var(--color-text-mute)]">No posts yet. Soon!</p>
          ) : (
            posts.map((post) => <BlogCard key={post.slug} post={post} locale="en" />)
          )}
        </div>
      </main>
      <Footer locale="en" />
    </>
  );
}
