(() => {
  'use strict';

  document.querySelectorAll('[data-language]').forEach((link) => {
    link.addEventListener('click', () => {
      try { localStorage.setItem('pomodock-language', link.dataset.language); } catch (_) {}
    });
  });
})();
