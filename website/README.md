# PomoDock website

This folder is a standalone static site. It has no build step and no runtime dependency.

## Deploy to Hostinger

1. Upload the contents of this folder—not the folder itself—to `public_html/` on
   `pomodock.com`.
2. Confirm that `index.html`, `style.css`, `script.js`, `assets/`, `en/`, and `es/`
   sit directly inside `public_html/`.
3. Open the production URL in browsers configured for English and Spanish. The root
   route detects the browser language; both language pages remain directly accessible.
4. Verify the Download button, then submit `https://pomodock.com/sitemap.xml` in
   Google Search Console.

## International SEO

- `/en/` and `/es/` are complete, crawlable pages with reciprocal `hreflang` links.
- `/` is the `x-default` language gateway and honors the browser language or the
  visitor's saved manual choice.
- Every language has localized titles, descriptions, social previews, image alt text,
  visible content, and `SoftwareApplication` structured data.
- `robots.txt` and `sitemap.xml` point to the production domain.

## Add the final recordings

Copy these three files into `assets/`:

- `hero.gif`
- `embedding.gif`
- `productivity.gif`

The page probes those exact paths and swaps them in automatically. Until then, optimized
WebP screenshots provide complete fallbacks without broken media.
