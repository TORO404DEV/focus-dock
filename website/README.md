# PomoDock website

Static site. Upload this folder to `domains/pomodock.com/public_html`.

- `/` is **PomoDock Web**: the app itself, running in the browser with no account.
  It is built from `src/PomoDock.Web`, so run this first:

  ```powershell
  .\scripts\build-web.ps1
  ```

  That writes `index.html`, `app/` and `_framework/` here (git ignores them).
  `.htaccess` serves the Brotli copies and the WebAssembly types.
- `/es/` and `/en/` are the landing pages: paper/ink like the app, one hero
  (`assets/hero.png`; optional `hero.gif`). Long page on purpose: Pomodoro technique +
  how-to + kit + agent + FAQ, same job as Pomofocus’s landing, for this product.
  The app's title bar links here, and these pages link back to `/`.
