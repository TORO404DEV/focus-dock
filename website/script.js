(() => {
  'use strict';

  document.querySelectorAll('[data-language]').forEach((link) => {
    link.addEventListener('click', () => {
      try { localStorage.setItem('pomodock-language', link.dataset.language); } catch (_) {}
    });
  });

  document.querySelectorAll('img[data-motion]').forEach((img) => {
    const motion = img.getAttribute('data-motion');
    if (!motion) return;
    const probe = new Image();
    probe.onload = () => { img.src = motion; };
    probe.src = motion;
  });
})();
