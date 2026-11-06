// Injected into every top-level document. The C# host owns the state and pushes it with
// __rc.apply({...}). Steps are real touch swipes sent by the host (see swipeTarget), so
// Instagram advances, recycles and loads reels exactly as on a phone. This script only
// enforces pause, mute and volume, and never moves the page itself.
(() => {
  if (window.top !== window || window.__rc) return;

  const rc = window.__rc = {
    state: { paused: false, muted: false, volume: 0.3, interactive: false },
  };

  // No scrollbars: the overlay is never scrolled by hand. This script runs before the
  // document has any elements, so the first attempts may find nothing to attach to; the
  // enforce tick keeps trying (and restores the style if the page replaces <head>).
  const hideScrollbars = () => {
    if (!document.documentElement || document.getElementById('__rc_style')) return;
    const style = document.createElement('style');
    style.id = '__rc_style';
    style.textContent = '*{scrollbar-width:none!important}::-webkit-scrollbar{display:none!important}';
    (document.head || document.documentElement).appendChild(style);
  };
  hideScrollbars();

  const playing = (v) => !v.paused && !v.ended && v.readyState > 2;

  // Instagram stacks reels on top of each other, so geometry alone cannot say which one is
  // showing. Ask hit testing at the centre, which respects stacking order.
  const videoAtCentre = () => {
    const x = innerWidth / 2, y = innerHeight / 2;
    const hits = document.elementsFromPoint(x, y);
    for (const el of hits) if (el.tagName === 'VIDEO') return el;
    for (const el of hits) {
      const vids = [...el.querySelectorAll('video')].filter((v) => {
        const r = v.getBoundingClientRect();
        return r.left <= x && x <= r.right && r.top <= y && y <= r.bottom;
      });
      if (vids.length === 1) return vids[0];
    }
    return null;
  };

  // The reel being watched: the one Instagram plays, else the last one it played.
  let active = null;
  const activeVideo = () => {
    const live = [...document.querySelectorAll('video')].filter(playing);
    if (live.length === 1) return live[0];
    if (live.length > 1) return live.includes(videoAtCentre()) ? videoAtCentre() : live[0];
    if (active && active.isConnected) return active;
    return videoAtCentre();
  };

  const scrollerOf = (el) => {
    for (let e = el && el.parentElement; e; e = e.parentElement) {
      const s = getComputedStyle(e);
      if (/(auto|scroll)/.test(s.overflowY) && e.scrollHeight > e.clientHeight + 1) return e;
    }
    return null;
  };

  // Where the host should start its swipe and how tall one reel is. The aim is the middle
  // of the reel list's visible area, never of a particular reel: the reel that was playing
  // may already be off screen, and its edge can sit under the header, which does not scroll.
  rc.lastStep = 0;
  rc.swipeTarget = () => {
    rc.lastStep = performance.now();
    const v = activeVideo() || videoAtCentre() || document.querySelector('video');
    const sc = v && scrollerOf(v);
    if (sc) {
      const r = sc.getBoundingClientRect();
      const top = Math.max(r.top, 0), bottom = Math.min(r.bottom, innerHeight);
      return { x: r.left + r.width / 2, y: (top + bottom) / 2, dy: sc.clientHeight };
    }
    return { x: innerWidth / 2, y: innerHeight / 2, dy: innerHeight };
  };

  // The like button of the reel on screen: Instagram labels its icon "Like" or "Unlike".
  // Every loaded reel has one, stacked at different heights; the visible one is in view.
  rc.likeTarget = () => {
    let best = null, bestDist = Infinity;
    for (const svg of document.querySelectorAll('svg[aria-label="Like"], svg[aria-label="Unlike"]')) {
      const button = svg.closest('[role="button"], button') || svg;
      const r = button.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
      if (cx < 0 || cx > innerWidth || cy < 0 || cy > innerHeight) continue;
      if (document.elementFromPoint(cx, cy)?.closest('[role="button"], button') !== button.closest('[role="button"], button')) continue;
      const dist = Math.abs(cy - innerHeight * 0.6);
      if (dist < bestDist) { bestDist = dist; best = { x: cx, y: cy, liked: svg.getAttribute('aria-label') === 'Unlike' }; }
    }
    return best;
  };

  const tryPlay = (v) => { if (v && v.paused) v.play().catch(() => {}); };

  let toastEl = null, toastTimer = 0;
  rc.toast = (text) => {
    if (!text || !document.body) return;
    if (!toastEl || !toastEl.isConnected) {
      toastEl = document.createElement('div');
      toastEl.style.cssText =
        'position:fixed;left:50%;top:40%;transform:translate(-50%,-50%);z-index:2147483647;' +
        'pointer-events:none;padding:8px 14px;border-radius:10px;background:rgba(10,10,12,.72);' +
        'color:#eee;font:600 15px system-ui,sans-serif;transition:opacity .25s;opacity:0';
      document.documentElement.appendChild(toastEl);
    }
    toastEl.textContent = text;
    toastEl.style.opacity = '1';
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { toastEl.style.opacity = '0'; }, 700);
  };

  // resume=false when a step unpauses: Instagram starts the next reel itself, and replaying
  // the old one first would blip its sound.
  rc.apply = (state, toast, resume = true) => {
    const wasPaused = rc.state.paused;
    rc.state = Object.assign({}, rc.state, state);
    enforce();
    if (resume && wasPaused && !rc.state.paused) tryPlay(active && active.isConnected ? active : videoAtCentre());
    rc.toast(toast);
  };

  let idleTicks = 0;
  const enforce = () => {
    hideScrollbars(); // the page may have replaced <head>
    const s = rc.state;
    const vids = [...document.querySelectorAll('video')];
    for (const v of vids) {
      if (v.muted !== s.muted) v.muted = s.muted;
      if (Math.abs(v.volume - s.volume) > 0.001) v.volume = s.volume;
    }
    const live = vids.filter(playing);
    if (live.length) active = live.length === 1 ? live[0] : activeVideo();
    if (s.paused) {
      for (const v of vids) if (!v.paused) v.pause();
      return;
    }
    // Locked, nobody can tap the page, so a reel that stays stopped after Instagram had time
    // to start it (e.g. autoplay was refused) gets a nudge.
    idleTicks = live.length ? 0 : idleTicks + 1;
    if (!s.interactive && idleTicks >= 4 && performance.now() - rc.lastStep > 1200) {
      idleTicks = 0;
      tryPlay(active && active.isConnected ? active : videoAtCentre());
    }
  };

  setInterval(enforce, 250);
})();
