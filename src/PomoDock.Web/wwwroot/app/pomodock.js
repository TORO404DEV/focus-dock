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
    const floatHead = target.closest('[data-float-move]');
    if (floatHead && !target.closest('button')) {
      beginFloat(event, floatHead.closest('[data-float]'));
      event.preventDefault();
      return;
    }
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
    if (floating) { moveFloat(event); return; }
    if (gesture) { updateGesture(event); return; }
    if (carousel.candidate) swipeMove(event);
  };

  const onPointerUp = () => {
    if (floating) { endFloat(); return; }
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

  pd.copy = (text) => {
    if (navigator.clipboard) navigator.clipboard.writeText(text).catch(() => { });
  };

  // The To Do row a dragged task would land before: the first row of the same kind whose middle
  // is below the pointer, as TodoBoard.DropIndex measures it.
  pd.taskUnder = (x, y, group) => {
    const hit = document.elementFromPoint(x, y);
    const list = hit && hit.closest('.todo-list');
    if (!list) return null;
    for (const row of list.querySelectorAll('.todo-row')) {
      if (row.classList.contains('done') !== (group === 'done')) continue;
      const box = row.getBoundingClientRect();
      if (y < box.top + box.height / 2) return row.dataset.task;
    }
    return null;
  };

  // Floating post-its move by their header, the way a desktop window is dragged by its title.
  let floating = null;
  const beginFloat = (event, frame) => {
    floating = { frame, x: event.clientX, y: event.clientY, left: frame.offsetLeft, top: frame.offsetTop };
    document.body.classList.add('is-gesturing');
  };
  const moveFloat = (event) => {
    const f = floating;
    const left = Math.min(Math.max(f.left + event.clientX - f.x, 0), window.innerWidth - 80);
    const top = Math.min(Math.max(f.top + event.clientY - f.y, 0), window.innerHeight - 42);
    f.frame.style.left = left + 'px';
    f.frame.style.top = top + 'px';
    f.result = [left, top];
  };
  const endFloat = () => {
    const f = floating;
    floating = null;
    document.body.classList.remove('is-gesturing');
    if (f.result) call('FloatMoved', f.frame.dataset.float, f.result[0], f.result[1]);
  };

  // ------------------------------------------------------------------ rich notes

  // The note editor: a contenteditable that the C# side fills with HTML and reads back as a
  // block model (paragraphs, lists, checklist lines, runs). Same keys as the desktop's
  // RichTextBox: Ctrl+B/I/U, Ctrl+Shift+X strike, Ctrl+Shift+C checklist, Enter continues a
  // checklist and Backspace beside its box turns the line back into text.
  const BLOCKS = new Set(['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'BLOCKQUOTE', 'PRE']);
  const editors = new Map();

  const inlineRuns = (node, look, runs) => {
    for (const child of node.childNodes) {
      if (child.nodeType === Node.TEXT_NODE) {
        const text = child.nodeValue.replace(/ /g, ' ');
        if (text.length) runs.push({ text, bold: look.b, italic: look.i, underline: look.u, strike: look.s, break: false });
        continue;
      }
      if (child.nodeType !== Node.ELEMENT_NODE) continue;
      const tag = child.tagName;
      if (tag === 'BR') { runs.push({ text: '', break: true }); continue; }
      if (BLOCKS.has(tag) || tag === 'UL' || tag === 'OL' || tag === 'LI') { inlineRuns(child, look, runs); continue; }
      const style = child.style || {};
      const decoration = (style.textDecorationLine || style.textDecoration || '').toLowerCase();
      const weight = (style.fontWeight || '').toLowerCase();
      inlineRuns(child, {
        b: look.b || tag === 'B' || tag === 'STRONG' || weight === 'bold' || +weight >= 600,
        i: look.i || tag === 'I' || tag === 'EM' || (style.fontStyle || '') === 'italic',
        u: look.u || tag === 'U' || decoration.includes('underline'),
        s: look.s || tag === 'S' || tag === 'STRIKE' || tag === 'DEL' || decoration.includes('line-through')
      }, runs);
    }
  };

  const plain = { b: false, i: false, u: false, s: false };

  const paragraphOf = (element) => {
    const runs = [];
    inlineRuns(element, plain, runs);
    // The browser keeps a lone <br> so an empty line has height; it is not a line break.
    if (runs.length && runs[runs.length - 1].break) runs.pop();
    const block = { list: false, ordered: false, runs, items: [] };
    if (element.classList && element.classList.contains('check')) block.check = element.dataset.checked === 'true';
    return block;
  };

  const readBlocks = (container) => {
    const blocks = [];
    let loose = null;
    const flush = () => { if (loose) { blocks.push(paragraphOf(loose)); loose = null; } };
    for (const child of container.childNodes) {
      const element = child.nodeType === Node.ELEMENT_NODE ? child : null;
      if (element && (element.tagName === 'UL' || element.tagName === 'OL')) {
        flush();
        const list = { list: true, ordered: element.tagName === 'OL', runs: [], items: [] };
        for (const item of element.children) {
          if (item.tagName !== 'LI') continue;
          const hasBlocks = [...item.children].some((inner) => BLOCKS.has(inner.tagName) || inner.tagName === 'UL' || inner.tagName === 'OL');
          list.items.push(hasBlocks ? readBlocks(item) : [paragraphOf(item)]);
        }
        blocks.push(list);
      } else if (element && BLOCKS.has(element.tagName)) {
        flush();
        const nested = [...element.children].some((inner) => BLOCKS.has(inner.tagName) || inner.tagName === 'UL' || inner.tagName === 'OL');
        if (nested && !element.classList.contains('check')) blocks.push(...readBlocks(element));
        else blocks.push(paragraphOf(element));
      } else {
        // Text typed straight into the editor before any paragraph existed.
        if (!loose) loose = document.createElement('p');
        loose.appendChild(child.cloneNode(true));
      }
    }
    flush();
    return blocks;
  };

  const currentParagraph = (root) => {
    const selection = window.getSelection();
    if (!selection.rangeCount) return null;
    let node = selection.getRangeAt(0).startContainer;
    while (node && node !== root) {
      if (node.nodeType === Node.ELEMENT_NODE && BLOCKS.has(node.tagName)) return node;
      node = node.parentNode;
    }
    return null;
  };

  const selectedParagraphs = (root) => {
    const selection = window.getSelection();
    if (!selection.rangeCount) return [];
    const range = selection.getRangeAt(0);
    const all = [...root.querySelectorAll('p, div, h1, h2, h3, h4, h5, h6, blockquote, pre')]
      .filter((element) => ![...element.children].some((inner) => BLOCKS.has(inner.tagName)));
    if (range.collapsed) { const one = currentParagraph(root); return one ? [one] : []; }
    return all.filter((element) => range.intersectsNode(element));
  };

  const caretAtStart = (paragraph) => {
    const selection = window.getSelection();
    if (!selection.rangeCount || !selection.isCollapsed) return false;
    const range = selection.getRangeAt(0).cloneRange();
    range.setStart(paragraph, 0);
    return range.toString().trim().length === 0;
  };

  const changed = (state) => {
    clearTimeout(state.timer);
    state.timer = setTimeout(() => state.ref.invokeMethod('RichChanged', readBlocks(state.root)), 300);
    state.ref.invokeMethod('RichEditing');
  };

  const toggleChecklist = (state) => {
    const root = state.root;
    let paragraphs = selectedParagraphs(root);
    if (!paragraphs.length) {
      document.execCommand('formatBlock', false, 'p');
      paragraphs = selectedParagraphs(root);
    }
    // A mixed selection becomes a checklist; a selection that already is one stops being one.
    const remove = paragraphs.length > 0 && paragraphs.every((p) => p.classList.contains('check'));
    for (const p of paragraphs) {
      if (remove) { p.classList.remove('check'); delete p.dataset.checked; }
      else if (!p.classList.contains('check')) { p.classList.add('check'); p.dataset.checked = 'false'; }
    }
    changed(state);
  };

  const onRichKey = (state, event) => {
    const root = state.root;
    const control = event.ctrlKey || event.metaKey;
    const key = event.key.toLowerCase();
    if (control && event.shiftKey && key === 'c') { event.preventDefault(); toggleChecklist(state); return; }
    if (control && event.shiftKey && key === 'x') { event.preventDefault(); document.execCommand('strikeThrough'); changed(state); return; }
    const paragraph = currentParagraph(root);
    if (!paragraph || !paragraph.classList.contains('check')) return;
    if (event.key === 'Enter' && !event.shiftKey && !control) {
      // Enter on an empty item ends the list instead of piling up empty boxes.
      if (paragraph.textContent.trim().length === 0) {
        event.preventDefault();
        paragraph.classList.remove('check');
        delete paragraph.dataset.checked;
        changed(state);
        return;
      }
      // The new line is a pending item, never a copy of a finished one.
      setTimeout(() => {
        const next = currentParagraph(root);
        if (next && next !== paragraph) { next.classList.add('check'); next.dataset.checked = 'false'; }
        changed(state);
      }, 0);
    } else if (event.key === 'Backspace' && caretAtStart(paragraph)) {
      // Backspace next to the box turns the item back into plain text, the way lists do.
      event.preventDefault();
      paragraph.classList.remove('check');
      delete paragraph.dataset.checked;
      changed(state);
    }
  };

  pd.rich = {
    attach(id, ref, html) {
      const root = document.getElementById(id);
      if (!root) return;
      const state = { root, ref, timer: 0 };
      editors.set(id, state);
      root.innerHTML = html;
      try { document.execCommand('defaultParagraphSeparator', false, 'p'); } catch (_) { }
      root.addEventListener('input', () => changed(state));
      root.addEventListener('keydown', (event) => onRichKey(state, event));
      // The box sits in the paragraph's left padding: a click there ticks it, not the caret.
      root.addEventListener('mousedown', (event) => {
        const paragraph = event.target.closest && event.target.closest('p.check');
        if (!paragraph || !root.contains(paragraph)) return;
        const box = paragraph.getBoundingClientRect();
        if (event.clientX - box.left > 24) return;
        event.preventDefault();
        paragraph.dataset.checked = paragraph.dataset.checked === 'true' ? 'false' : 'true';
        changed(state);
      });
      root.addEventListener('paste', (event) => {
        // Pasted pages bring their own fonts and colours; a note takes the words.
        const text = event.clipboardData && event.clipboardData.getData('text/plain');
        if (text == null) return;
        event.preventDefault();
        document.execCommand('insertText', false, text);
      });
      root.addEventListener('blur', () => {
        clearTimeout(state.timer);
        state.ref.invokeMethod('RichChanged', readBlocks(root));
      });
    },
    detach(id) {
      const state = editors.get(id);
      if (!state) return;
      clearTimeout(state.timer);
      editors.delete(id);
    },
    command(id, name) {
      const state = editors.get(id);
      if (!state) return;
      state.root.focus();
      if (name === 'checklist') toggleChecklist(state);
      else if (name === 'stamp') { /* inserted from C# with insert() */ }
      else { document.execCommand(name); changed(state); }
    },
    insert(id, text) {
      const state = editors.get(id);
      if (!state) return;
      state.root.focus();
      document.execCommand('insertText', false, text);
      changed(state);
    },
    set(id, html) {
      const state = editors.get(id);
      if (state) state.root.innerHTML = html;
    },
    read(id) {
      const state = editors.get(id);
      return state ? readBlocks(state.root) : [];
    }
  };
})();
