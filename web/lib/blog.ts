import fs from 'node:fs';
import path from 'node:path';
import matter from 'gray-matter';
import type { Locale } from './i18n';

export interface PostFrontmatter {
  title: string;
  description: string;
  date: string;        // ISO YYYY-MM-DD
  author?: string;
  tags?: string[];
}

export interface Post {
  slug: string;
  locale: Locale;
  frontmatter: PostFrontmatter;
  content: string;     // raw MDX body
}

/**
 * Blog posts'ları file-system'dan okur. /content/blog/{tr,en}/<slug>.mdx
 * dosyalarını gray-matter ile parse eder, frontmatter + içerik döner.
 *
 * Build-time'da çalışır (Next.js statik export). Runtime FS erişimi yok.
 */
export function getAllPosts(locale: Locale): Post[] {
  const dir = path.join(process.cwd(), 'content', 'blog', locale);
  if (!fs.existsSync(dir)) return [];

  const files = fs.readdirSync(dir).filter((f) => f.endsWith('.mdx'));
  const posts: Post[] = files.map((file) => {
    const slug = file.replace(/\.mdx$/, '');
    const raw = fs.readFileSync(path.join(dir, file), 'utf8');
    const { data, content } = matter(raw);
    return {
      slug,
      locale,
      frontmatter: data as PostFrontmatter,
      content,
    };
  });

  // En yeni yazı önce.
  return posts.sort((a, b) =>
    a.frontmatter.date < b.frontmatter.date ? 1 : -1,
  );
}

export function getPostBySlug(locale: Locale, slug: string): Post | null {
  const file = path.join(process.cwd(), 'content', 'blog', locale, `${slug}.mdx`);
  if (!fs.existsSync(file)) return null;
  const raw = fs.readFileSync(file, 'utf8');
  const { data, content } = matter(raw);
  return {
    slug,
    locale,
    frontmatter: data as PostFrontmatter,
    content,
  };
}

export function formatPostDate(iso: string, locale: Locale): string {
  const d = new Date(iso);
  return d.toLocaleDateString(locale === 'tr' ? 'tr-TR' : 'en-US', {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
  });
}
