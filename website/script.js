(() => {
  'use strict';

  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const navToggle = document.querySelector('[data-nav-toggle]');
  const nav = document.querySelector('[data-nav]');

  document.querySelectorAll('[data-language]').forEach((link) => {
    link.addEventListener('click', () => {
      try { localStorage.setItem('pomodock-language', link.dataset.language); } catch (_) {}
    });
  });

  const closeNav = () => {
    if (!navToggle || !nav) return;
    navToggle.setAttribute('aria-expanded', 'false');
    nav.classList.remove('is-open');
  };

  navToggle?.addEventListener('click', () => {
    const isOpen = navToggle.getAttribute('aria-expanded') === 'true';
    navToggle.setAttribute('aria-expanded', String(!isOpen));
    nav?.classList.toggle('is-open', !isOpen);
  });

  nav?.querySelectorAll('a').forEach((link) => link.addEventListener('click', closeNav));
  window.addEventListener('resize', () => { if (window.innerWidth > 1000) closeNav(); });
  window.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape' || navToggle?.getAttribute('aria-expanded') !== 'true') return;
    closeNav();
    navToggle?.focus();
  });

  document.querySelectorAll('img[data-gif]').forEach((image) => {
    const gifPath = image.dataset.gif;
    if (!gifPath) return;

    const gif = new Image();
    gif.onload = () => {
      image.src = gifPath;
      image.removeAttribute('data-gif');
    };
    gif.src = gifPath;

    image.addEventListener('error', () => {
      image.hidden = true;
      image.closest('[data-media-frame]')?.classList.add('is-placeholder');
    }, { once: true });
  });

  const revealItems = document.querySelectorAll('[data-reveal]');
  if (reduceMotion || !('IntersectionObserver' in window)) {
    revealItems.forEach((item) => item.classList.add('is-visible'));
  } else {
    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        entry.target.classList.add('is-visible');
        observer.unobserve(entry.target);
      });
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0.08 });
    revealItems.forEach((item) => observer.observe(item));
  }

  const tabList = document.querySelector('[data-tabs]');
  const tabs = [...document.querySelectorAll('[data-tab]')];
  const panels = [...document.querySelectorAll('[data-panel]')];

  const selectTab = (nextTab) => {
    const key = nextTab.dataset.tab;
    tabs.forEach((tab) => tab.setAttribute('aria-selected', String(tab === nextTab)));
    panels.forEach((panel) => { panel.hidden = panel.dataset.panel !== key; });
  };

  tabs.forEach((tab) => tab.addEventListener('click', () => selectTab(tab)));
  tabList?.addEventListener('keydown', (event) => {
    if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
    event.preventDefault();
    const current = tabs.findIndex((tab) => tab.getAttribute('aria-selected') === 'true');
    const direction = ['ArrowRight', 'ArrowDown'].includes(event.key) ? 1 : -1;
    const next = tabs[(current + direction + tabs.length) % tabs.length];
    selectTab(next);
    next.focus();
  });

  const year = document.querySelector('[data-year]');
  if (year) year.textContent = String(new Date().getFullYear());
})();
