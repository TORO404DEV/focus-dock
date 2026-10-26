// PomoDock Web: the browser side of the window. The desktop asks Win32 and WPF for these things;
// here the page does them and reports back to MainWindow through one DotNetObjectReference.
(() => {
  'use strict';

  const pd = window.pd = {};
  let win = null;

  const call = (method, ...args) => {
    if (!win) return undefined;
    try { return win.invokeMethod(method, ...args); }
    catch (error) { console.error(method, error); return undefined; }
  };

  // ------------------------------------------------------------------ storage

  pd.store = {
    get(key) { try { return localStorage.getItem('pomodock:' + key); } catch (_) { return null; } },
    set(key, value) { try { localStorage.setItem('pomodock:' + key, value); return true; } catch (_) { return false; } },
    remove(key) { try { localStorage.removeItem('pomodock:' + key); } catch (_) { } }
  };

  // ------------------------------------------------------------------ the second tick

  // A worker keeps its timers when the tab is in the background; the page's own setInterval is
  // throttled to once a minute there, and the timer engine would read that as a suspended machine.
  const startClock = () => {
    const source = 'let t=setInterval(()=>postMessage(0),1000);onmessage=()=>{};';
    try {
      const worker = new Worker(URL.createObjectURL(new Blob([source], { type: 'text/javascript' })));
      worker.onmessage = () => call('Tick');
    } catch (_) {
      setInterval(() => call('Tick'), 1000);
    }
  };

  // ------------------------------------------------------------------ sound

  pd.audio = {
    context: null,
    buffers: new Map(),
    pending: new Map(),
    alarms: new Set(),
    loopVoice: null,
    loopGeneration: 0,

    ctx() {
      if (!this.context) {
        const Context = window.AudioContext || window.webkitAudioContext;
        if (!Context) return null;
        this.context = new Context();
      }
      if (this.context.state === 'suspended') this.context.resume().catch(() => { });
      return this.context;
    },

    has(key) { return this.buffers.has(key) || this.pending.has(key); },

    buffer(key, wave) {
      if (this.buffers.has(key)) return Promise.resolve(this.buffers.get(key));
      if (this.pending.has(key)) return this.pending.get(key);
      if (!wave) return Promise.resolve(null);
      const ctx = this.ctx();
      if (!ctx) return Promise.resolve(null);
      const bytes = wave.buffer.slice(wave.byteOffset, wave.byteOffset + wave.byteLength);
      const decoding = new Promise((resolve) => ctx.decodeAudioData(bytes, resolve, () => resolve(null)))
        .then((buffer) => { this.pending.delete(key); if (buffer) this.buffers.set(key, buffer); return buffer; });
      this.pending.set(key, decoding);
      return decoding;
    },

    voice(buffer, volume, loop) {
      const ctx = this.ctx();
      const source = ctx.createBufferSource();
      source.buffer = buffer;
      source.loop = loop;
      const gain = ctx.createGain();
      gain.gain.value = Math.max(0, Math.min(1, volume / 100));
      source.connect(gain).connect(ctx.destination);
      source.start();
      return source;
    },

    play(key, wave, volume, alarm) {
      this.buffer(key, wave).then((buffer) => {
        if (!buffer || !this.ctx()) return;
        const source = this.voice(buffer, volume, false);
        if (alarm) {
          this.alarms.add(source);
          source.onended = () => this.alarms.delete(source);
        }
      });
    },

    stopAlarms() {
      for (const source of this.alarms) { try { source.stop(); } catch (_) { } }
      this.alarms.clear();
    },

    loop(key, wave, volume) {
      const generation = ++this.loopGeneration;
      this.buffer(key, wave).then((buffer) => {
        if (!buffer || generation !== this.loopGeneration || !this.ctx()) return;
        this.stopVoice();
        this.loopVoice = this.voice(buffer, volume, true);
      });
    },

    stopLoop() {
      this.loopGeneration++;
      this.stopVoice();
    },

    stopVoice() {
      if (!this.loopVoice) return;
      try { this.loopVoice.stop(); } catch (_) { }
      this.loopVoice = null;
    }
  };

  // Browsers only let sound start after the person has touched the page.
  const unlockAudio = () => { if (pd.audio.context) pd.audio.ctx(); };

  // ------------------------------------------------------------------ move and resize

  // Same arithmetic as WidgetCard.UpdateGesture and MainWindow.UpdateTimerGesture: only the edge
  // that owns the canvas origin is floored at zero, the far edge shrinks down to the minimum.
  let gesture = null;

  const canvasOf = (element) => element.closest('.widget-area');

  const beginGesture = (event, frame, edge) => {
    const canvas = canvasOf(frame);
    if (!canvas) return false;
    const rect = canvas.getBoundingClientRect();
    gesture = {
      frame, edge, canvas, pointer: event.pointerId,
      startX: event.clientX, startY: event.clientY,
      x: frame.offsetLeft, y: frame.offsetTop, width: frame.offsetWidth, height: frame.offsetHeight,
      maxX: rect.width, maxY: rect.height,
      minWidth: Math.min(+frame.dataset.minw || 220, rect.width),
      minHeight: Math.min(+frame.dataset.minh || 90, rect.height),
      moved: false
    };
    document.body.classList.add('is-gesturing');
    document.body.style.cursor = getComputedStyle(event.target).cursor;
    return true;
  };

  const updateGesture = (event) => {
    const g = gesture;
    let dx = event.clientX - g.startX, dy = event.clientY - g.startY;
    let x = g.x, y = g.y, width = g.width, height = g.height;
    if (g.edge === 'MOVE') {
      x = Math.min(Math.max(x + dx, 0), Math.max(0, g.maxX - width));
      y = Math.min(Math.max(y + dy, 0), Math.max(0, g.maxY - height));
    } else {
      if (g.edge.includes('W')) dx = Math.max(dx, -g.x);
      if (g.edge.includes('N')) dy = Math.max(dy, -g.y);
      if (g.edge.includes('W')) { width = Math.max(g.minWidth, g.width - dx); x = g.x + g.width - width; }
      if (g.edge.includes('E')) width = Math.max(g.minWidth, g.width + dx);
      if (g.edge.includes('N')) { height = Math.max(g.minHeight, g.height - dy); y = g.y + g.height - height; }
      if (g.edge.includes('S')) height = Math.max(g.minHeight, g.height + dy);
      width = Math.min(width, Math.max(g.minWidth, g.maxX - x));
      height = Math.min(height, Math.max(g.minHeight, g.maxY - y));
    }
    x = Math.max(0, x); y = Math.max(0, y);
    g.result = [x, y, width, height];
    g.moved = true;
    const style = g.frame.style;
    style.left = x + 'px'; style.top = y + 'px'; style.width = width + 'px';
    if (g.edge !== 'MOVE' || !g.frame.classList.contains('is-collapsed')) style.height = height + 'px';
  };

  const endGesture = () => {
    const g = gesture;
    gesture = null;
    document.body.classList.remove('is-gesturing');
    document.body.style.cursor = '';
    if (!g.moved) return;
    const [x, y, width, height] = g.result;
    call('GestureEnded', g.frame.dataset.id, x, y, width, height);
  };

  // ------------------------------------------------------------------ page swipe

  // Same thresholds as MainWindow.Pages: 12 px with a clear axis starts it, and a release past
  // 16 % of the page (58–150 px) or a fast flick commits it.
  const carousel = {
    candidate: null,
    active: false,
    dx: 0, dy: 0,
    offset: 0,
    available: false,
    startedAt: 0,
    animating: false
  };

  const viewport = () => document.querySelector('.carousel-viewport');
  const area = () => document.querySelector('.carousel-viewport > .widget-area');
  const preview = () => document.querySelector('.carousel-viewport > .page-preview');

  const extentFor = (dx) => {
    const box = viewport();
    if (!box) return 1;
    return Math.max(1, dx !== 0 ? box.clientWidth : box.clientHeight);
  };

  const place = (offset) => {
    const page = area(), next = preview();
    if (!page) return;
    const horizontal = carousel.dx !== 0;
    const step = horizontal ? carousel.dx : carousel.dy;
    const extent = extentFor(carousel.dx);
    page.style.transform = horizontal ? `translate3d(${offset}px,0,0)` : `translate3d(0,${offset}px,0)`;
    if (next) {
      const along = offset + step * extent;
      next.style.transform = horizontal ? `translate3d(${along}px,0,0)` : `translate3d(0,${along}px,0)`;
    }
  };

  const interactive = (target) =>
    !!target.closest('.card, .timer-frame, button, a, input, textarea, select, [contenteditable="true"], .no-swipe');

  pd.carousel = {
    animate(dx, dy, commit) {
      carousel.dx = dx; carousel.dy = dy;
      const extent = extentFor(dx);
      const step = dx !== 0 ? dx : dy;
      animateTo(commit ? -step * extent : 0, commit);
    },
    reset() {
      carousel.active = false;
      carousel.animating = false;
      carousel.offset = 0;
      for (const element of [area(), preview()]) {
        if (!element) continue;
        element.style.transition = '';
        element.style.transform = '';
      }
    }
  };

  const animateTo = (target, commit) => {
    const page = area(), next = preview();
    if (!page) { call('FinishCarousel', commit); return; }
    const extent = extentFor(carousel.dx);
    const distance = Math.abs(target - carousel.offset);
    const duration = Math.min(240, Math.max(110, 110 + 130 * distance / extent));
    const ease = `transform ${duration}ms cubic-bezier(0.215, 0.61, 0.355, 1)`;
    carousel.animating = true;
    page.style.transition = ease;
    if (next) next.style.transition = ease;
    // Two frames so the starting transform is committed before the transition begins.
    requestAnimationFrame(() => requestAnimationFrame(() => {
      carousel.offset = target;
      place(target);
      let done = false;
      const finish = () => {
        if (done) return;
        done = true;
        page.removeEventListener('transitionend', finish);
        call('FinishCarousel', commit);
      };
      page.addEventListener('transitionend', finish);
      setTimeout(finish, duration + 80);
    }));
  };

  const configureSwipe = (dx, dy) => {
    carousel.dx = dx; carousel.dy = dy;
    carousel.available = !!call('ConfigureSwipe', dx, dy);
  };

  const swipeMove = (event) => {
    const c = carousel.candidate;
    const dx = event.clientX - c.x, dy = event.clientY - c.y;
    if (!carousel.active) {
      const horizontal = Math.abs(dx) >= 12 && Math.abs(dx) > Math.abs(dy) * 1.15;
      const vertical = Math.abs(dy) >= 12 && Math.abs(dy) > Math.abs(dx) * 1.15;
      if (!horizontal && !vertical) return;
      carousel.active = true;
      // Dragging the workspace is not typing.
      if (document.activeElement && document.activeElement !== document.body) document.activeElement.blur();
      if (horizontal) configureSwipe(dx < 0 ? 1 : -1, 0);
      else configureSwipe(0, dy < 0 ? 1 : -1);
    }
    const newDx = carousel.dx !== 0 ? (dx < 0 ? 1 : -1) : 0;
    const newDy = carousel.dy !== 0 ? (dy < 0 ? 1 : -1) : 0;
    if (newDx !== carousel.dx || newDy !== carousel.dy) configureSwipe(newDx, newDy);

    const extent = extentFor(carousel.dx);
    const delta = carousel.dx !== 0 ? dx : dy;
    if (carousel.available) {
      carousel.offset = Math.min(Math.max(delta, -extent), extent);
      place(carousel.offset);
    } else {
      carousel.offset = Math.sign(delta) * Math.min(extent * 0.10, Math.sqrt(Math.abs(delta)) * 5);
      const page = area();
      if (page) page.style.transform = carousel.dx !== 0 ? `translate3d(${carousel.offset}px,0,0)` : `translate3d(0,${carousel.offset}px,0)`;
    }
  };

  const swipeEnd = () => {
    carousel.candidate = null;
    if (!carousel.active) return;
    carousel.active = false;
    const elapsed = Math.max(0.016, (performance.now() - carousel.startedAt) / 1000);
    const extent = extentFor(carousel.dx);
    const threshold = Math.min(150, Math.max(58, extent * 0.16));
    const commit = carousel.available &&
      (Math.abs(carousel.offset) >= threshold || (Math.abs(carousel.offset / elapsed) >= 720 && Math.abs(carousel.offset) >= 30));
    if (document.documentElement.dataset.motion === 'reduce') { call('FinishCarousel', commit); return; }
    const step = carousel.dx !== 0 ? carousel.dx : carousel.dy;
    animateTo(commit ? -step * extent : 0, commit);
  };

  // ------------------------------------------------------------------ pointer routing

  const onPointerDown = (event) => {
    unlockAudio();
    if (event.button !== 0 || gesture || carousel.animating) return;
    const target = event.target;
    const handle = target.closest('[data-edge]');
    if (handle) {
      const frame = handle.closest('[data-id]');
      if (frame && beginGesture(event, frame, handle.dataset.edge)) {
        call('BeginGesture', frame.dataset.id);
        event.preventDefault();
        return;
      }
    }
    const move = target.closest('[data-move]');
    if (move && !target.closest('button')) {
      const frame = move.closest('[data-id]');
      if (frame && beginGesture(event, frame, 'MOVE')) {
        call('BeginGesture', frame.dataset.id);
        event.preventDefault();
        return;
      }
    }
    const box = viewport();
    if (box && box.contains(target) && !interactive(target) && call('CanBeginSwipe')) {
      carousel.candidate = { x: event.clientX, y: event.clientY };
      carousel.startedAt = performance.now();
    }
  };

  const onPointerMove = (event) => {
    if (gesture) { updateGesture(event); return; }
    if (carousel.candidate) swipeMove(event);
  };

  const onPointerUp = () => {
    if (gesture) { endGesture(); return; }
    if (carousel.candidate || carousel.active) swipeEnd();
  };

  // ------------------------------------------------------------------ keyboard

  const typing = () => {
    const element = document.activeElement;
    if (!element) return false;
    return element.matches('input, textarea, select, [contenteditable="true"], [contenteditable=""]');
  };

  const onKey = (event) => {
    unlockAudio();
    const focusButton = !!(document.activeElement && document.activeElement.closest('button'));
    if (call('OnKey', event.key, event.ctrlKey || event.metaKey, event.shiftKey, typing(), focusButton)) event.preventDefault();
  };

  // ------------------------------------------------------------------ the rest of the page

  pd.attach = (reference) => {
    win = reference;
    document.addEventListener('pointerdown', onPointerDown, true);
    document.addEventListener('pointermove', onPointerMove, true);
    document.addEventListener('pointerup', onPointerUp, true);
    document.addEventListener('pointercancel', onPointerUp, true);
    window.addEventListener('blur', () => { if (gesture) endGesture(); if (carousel.candidate || carousel.active) swipeEnd(); });
    document.addEventListener('keydown', onKey);
    document.addEventListener('fullscreenchange', () => call('FullscreenChanged', !!document.fullscreenElement));
    document.addEventListener('visibilitychange', () => { if (!document.hidden) call('Tick'); });
    window.addEventListener('pagehide', () => call('Closing'));
    window.addEventListener('resize', () => call('ViewportChanged'));
    startClock();
    const boot = document.getElementById('boot');
    if (boot) boot.remove();
  };

  pd.canvasSize = () => {
    const canvas = area();
    return canvas ? [canvas.clientWidth, canvas.clientHeight] : [0, 0];
  };

  pd.fullscreen = () => {
    if (document.fullscreenElement) document.exitFullscreen().catch(() => { });
    else if (document.documentElement.requestFullscreen) document.documentElement.requestFullscreen().catch(() => { });
  };

  pd.title = (text) => { if (document.title !== text) document.title = text; };

  pd.theme = (dark, reduceMotion, accent, accentInk, language) => {
    const root = document.documentElement;
    if (dark) root.dataset.theme = 'dark'; else delete root.dataset.theme;
    if (reduceMotion) root.dataset.motion = 'reduce'; else delete root.dataset.motion;
    root.style.setProperty('--accent', accent);
    root.style.setProperty('--accent-ink', accentInk);
    root.lang = language;
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = dark ? '#0C0D0B' : '#171916';
  };

  pd.hidden = () => document.hidden || !document.hasFocus();

  pd.askNotifications = () => {
    if ('Notification' in window && Notification.permission === 'default') Notification.requestPermission().catch(() => { });
  };

  pd.notify = (title, body) => {
    if (!('Notification' in window) || Notification.permission !== 'granted') return;
    try {
      const note = new Notification(title, { body, icon: 'app/pomodock-mark.svg', tag: 'pomodock-timer' });
      note.onclick = () => { window.focus(); note.close(); };
    } catch (_) { }
  };

  pd.focus = (selector) => {
    requestAnimationFrame(() => {
      const element = document.querySelector(selector);
      if (!element) return;
      element.focus();
      if (element.select) element.select();
    });
  };

  pd.download = (name, text) => {
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
    link.download = name;
    document.body.appendChild(link);
    link.click();
    setTimeout(() => { URL.revokeObjectURL(link.href); link.remove(); }, 1000);
  };

  pd.open = (url) => { window.open(url, '_blank', 'noopener'); };
})();
