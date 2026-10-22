// Lilt's page helper. Runs in an isolated world: the page can't see or call it, and it shares only the DOM with the page.
// It reports shortcuts, Shift+clicked links (Peek) and media state to Lilt through the __liltKey binding, and does what
// Lilt asks for (play, pause, picture in picture, visualizer data) through globalThis.__lilt.
(() => {
  const send = globalThis.__liltKey;
  if (typeof send !== 'function') return 'no binding';
  if (globalThis.__lilt) return 'already on';
  const post = o => send(JSON.stringify(o));

  // Shortcuts: [{c: command, ctrl, shift, alt, code, key, r: reserved}]. Reserved ones are Lilt's even inside the page
  // (like Ctrl+T in Chrome); the rest only fire if the page didn't handle the key itself.
  let keys = __KEYS__;
  const match = (e, reserved) => keys.find(k => k.r === reserved && e.ctrlKey === k.ctrl && e.shiftKey === k.shift
    && e.altKey === k.alt && !e.metaKey && (e.code === k.code || (k.key && e.key.toUpperCase() === k.key)));
  addEventListener('keydown', e => {
    const k = match(e, true);
    if (!k) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    post({ t: 'key', c: k.c });
  }, true);
  addEventListener('keydown', e => {
    if (e.defaultPrevented || e.isComposing) return;
    // Escape the page didn't use closes Lilt's overlays (Peek).
    if (e.key === 'Escape' && !e.ctrlKey && !e.altKey && !e.shiftKey) { post({ t: 'key', c: 'escape' }); return; }
    const k = match(e, false);
    if (!k) return;
    e.preventDefault();
    post({ t: 'key', c: k.c });
  }, false);

  // Peek: Shift+click a link to preview it over the page.
  addEventListener('click', e => {
    if (!peek || !e.shiftKey || e.button !== 0 || e.ctrlKey || e.altKey || e.metaKey) return;
    const a = e.target instanceof Element ? e.target.closest('a[href]') : null;
    if (!a || !/^https?:/i.test(a.href)) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    post({ t: 'peek', url: a.href.slice(0, 4096) });
  }, true);
  let peek = true;
  // Universal font (pages.font): one style element, icon fonts left alone. The name is cleaned before it goes into CSS.
  let font = '';
  const setFont = f => {
    const name = String(f || '').replace(/[^\p{L}\p{N} -]/gu, '').trim().slice(0, 60);
    if (name === font) return;
    font = name;
    let el = document.getElementById('lilt-font');
    if (!name) { if (el) el.remove(); return; }
    if (!el) el = document.createElement('style');
    el.id = 'lilt-font';
    el.textContent = `:root :not(i):not(svg):not(svg *):not([class*="icon" i]):not([class*="fa-" i]):not([class*="material" i]){font-family:"${name}",system-ui,sans-serif !important}`;
    if (!el.isConnected) (document.head || document.documentElement).appendChild(el);
  };

  // Mouse gestures: hold the right button and draw. Left of the page's own uses of that button: nothing starts when
  // the page handled the press itself, on canvases, video, embeds, frames and text fields, or with the pointer locked.
  let gestures = false, stroke = null, eatMenu = false;
  addEventListener('mousedown', e => {
    stroke = null;
    if (!gestures || e.button !== 2 || e.defaultPrevented || document.pointerLockElement) return;
    if (e.target instanceof Element && e.target.closest('canvas, video, embed, object, iframe, input, textarea, select, [contenteditable]')) return;
    stroke = { x: e.clientX, y: e.clientY, path: '' };
  }, false);
  addEventListener('mousemove', e => {
    if (!stroke) return;
    if (!(e.buttons & 2)) { stroke = null; return; }
    const dx = e.clientX - stroke.x, dy = e.clientY - stroke.y;
    if (Math.abs(dx) < 36 && Math.abs(dy) < 36) return;
    const d = Math.abs(dx) > Math.abs(dy) ? (dx > 0 ? 'R' : 'L') : (dy > 0 ? 'D' : 'U');
    if (!stroke.path.endsWith(d)) stroke.path += d;
    stroke.x = e.clientX;
    stroke.y = e.clientY;
  }, true);
  addEventListener('mouseup', e => {
    if (e.button !== 2 || !stroke) return;
    const path = stroke.path;
    stroke = null;
    if (!path) return;
    eatMenu = true;
    post({ t: 'gesture', g: path.slice(0, 4) });
  }, true);
  addEventListener('contextmenu', e => {
    if (!eatMenu) return;
    eatMenu = false;
    e.preventDefault();
    e.stopImmediatePropagation();
  }, true);

  // See-through websites: the page's own background color stays, translucent, on the root; the body and big
  // containers painted that same color become clear, so the window's material shows through behind the page.
  let glassAlpha = 0, glassed = [];
  const rgb = c => {
    const m = /rgba?\(([^)]+)\)/.exec(c || '');
    if (!m) return null;
    const p = m[1].split(/[\s,/]+/).filter(Boolean).map(parseFloat);
    return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
  };
  const paint = (el, v) => { el.style.setProperty('background-color', v, 'important'); glassed.push(el); };
  const glass = () => {
    for (const el of glassed) el.style.removeProperty('background-color');
    glassed = [];
    const root = document.documentElement;
    if (!glassAlpha || !root) return 'off';
    const dark = /dark/.test(getComputedStyle(root).colorScheme) && matchMedia('(prefers-color-scheme: dark)').matches;
    const page = [root, document.body].map(e => e && rgb(getComputedStyle(e).backgroundColor)).find(c => c && c.a > 0.85)
      || (dark ? { r: 18, g: 18, b: 18 } : { r: 255, g: 255, b: 255 });
    const same = c => c && c.a > 0.85 && Math.abs(c.r - page.r) + Math.abs(c.g - page.g) + Math.abs(c.b - page.b) < 30;
    paint(root, `rgba(${page.r}, ${page.g}, ${page.b}, ${glassAlpha})`);
    if (document.body) paint(document.body, 'transparent');
    const w = innerWidth, h = innerHeight, seen = new Set();
    for (const [x, y] of [[0.5, 0.5], [0.5, 0.2], [0.5, 0.85], [0.15, 0.5], [0.85, 0.5]])
      for (const el of document.elementsFromPoint(w * x, h * y)) {
        if (seen.has(el) || el === root || el === document.body) continue;
        seen.add(el);
        const r = el.getBoundingClientRect();
        if (r.width >= w * 0.5 && r.height >= h * 0.3 && same(rgb(getComputedStyle(el).backgroundColor))) paint(el, 'transparent');
      }
    return 'on';
  };
  // Pages finish building after the helper arrives; look again when they're done.
  addEventListener('load', () => { if (glassAlpha) { glass(); setTimeout(glass, 1500); } });

  // The site's own color, for Lilt's toolbar: its theme-color for the current light or dark mode, else the background
  // along the top of the page. Sent only when it changes.
  let sentColor = null, colorTimer = 0;
  const colorCtx = new OffscreenCanvas(1, 1).getContext('2d');
  const cssColor = v => {
    if (!v) return null;
    colorCtx.fillStyle = '#010203';
    colorCtx.fillStyle = v.trim();
    const f = colorCtx.fillStyle;
    if (f === '#010203' && v.trim().toLowerCase() !== '#010203') return null;
    return f[0] === '#' ? { r: parseInt(f.slice(1, 3), 16), g: parseInt(f.slice(3, 5), 16), b: parseInt(f.slice(5, 7), 16), a: 1 } : rgb(f);
  };
  const siteColor = () => {
    const meta = [...document.querySelectorAll('meta[name="theme-color" i]')].find(m => !m.media || matchMedia(m.media).matches);
    let c = meta && cssColor(meta.content);
    if (!c || c.a < 0.5) {
      c = null;
      for (let el = document.elementFromPoint(innerWidth / 2, 1); el && !c; el = el.parentElement) {
        const b = rgb(getComputedStyle(el).backgroundColor);
        if (b && b.a > 0.5) c = b;
      }
    }
    const hex = c ? '#' + [c.r, c.g, c.b].map(v => Math.round(v).toString(16).padStart(2, '0')).join('') : '';
    if (hex !== sentColor) {
      sentColor = hex;
      post({ t: 'color', c: hex });
    }
    return hex;
  };
  const recolorSoon = () => { clearTimeout(colorTimer); colorTimer = setTimeout(siteColor, 250); };
  addEventListener('load', recolorSoon);
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', recolorSoon);
  // Busy sites rewrite their <head> constantly; a page nobody can see doesn't need to keep up (it catches up when shown).
  if (document.head) new MutationObserver(() => { if (!document.hidden) recolorSoon(); })
    .observe(document.head, { subtree: true, childList: true, attributes: true, attributeFilter: ['content', 'media'] });
  document.addEventListener('visibilitychange', () => { if (!document.hidden) recolorSoon(); });

  // Ctrl+click and middle-click open a link in a tab behind this one; Lilt is told which link so it doesn't switch to it.
  const behind = e => {
    const a = e.target instanceof Element ? e.target.closest('a[href]') : null;
    if (a && /^https?:/i.test(a.href)) post({ t: 'behind', url: a.href.slice(0, 4096) });
  };
  addEventListener('click', e => { if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.button === 0) behind(e); }, true);
  addEventListener('auxclick', e => { if (e.button === 1) behind(e); }, true);

  // Media: the element that's playing, or the one that played last.
  let current = null, last = '';
  const media = () => [...document.querySelectorAll('video, audio')];
  // Several elements can play at once (a site's ad or preview next to the player): the longest is the track.
  const length = m => isFinite(m.duration) ? m.duration : 1e9;
  // A track is a long element: a short sound playing beside it (a UI click, an ad) is never the one shown.
  const pick = () => {
    const playing = media().filter(m => !m.paused && !m.ended);
    const tracks = playing.filter(m => m.readyState > 1 && length(m) > 30);
    return (tracks.length ? tracks : playing.filter(m => m.readyState > 1)).sort((a, b) => length(b) - length(a))[0]
    || (current && current.isConnected ? current : null)
    || media().find(m => m.currentTime > 0) || null;
  };
  const report = () => {
    const m = pick();
    if (m) current = m;
    const md = navigator.mediaSession && navigator.mediaSession.metadata;
    const art = md && md.artwork && md.artwork.length ? md.artwork[md.artwork.length - 1].src : '';
    const s = m ? {
      t: 'media', playing: !m.paused && !m.ended, title: (md && md.title) || document.title || '',
      artist: (md && md.artist) || '', album: (md && md.album) || '', art: String(art || ''),
      video: m.tagName === 'VIDEO' && m.videoWidth > 0, pos: m.currentTime || 0,
      dur: isFinite(m.duration) ? m.duration : 0, rate: m.playbackRate || 1, muted: m.muted,
      pip: !!document.pictureInPictureElement,
    } : { t: 'media', none: true };
    const j = JSON.stringify(s);
    if (j !== last) {
      last = j;
      send(j);
    }
    // While something plays, a check every 5 s catches what changes without a media event (a site updating its
    // track info, the position drifting); nothing runs while nothing plays.
    if (s.playing && !ticking) ticking = setInterval(report, 5000);
    else if (!s.playing && ticking) { clearInterval(ticking); ticking = 0; }
  };
  let ticking = 0;
  let queued = false;
  const soon = () => {
    if (queued) return;
    queued = true;
    setTimeout(() => { queued = false; report(); }, 150);
  };
  for (const ev of ['play', 'playing', 'pause', 'ended', 'emptied', 'loadedmetadata', 'loadeddata', 'canplay', 'durationchange', 'seeked',
    'ratechange', 'volumechange', 'enterpictureinpicture', 'leavepictureinpicture']) document.addEventListener(ev, soon, true);

  // Next/previous for sites whose own buttons Lilt knows, when Windows media controls aren't available.
  const buttons = {
    next: ['.ytp-next-button', 'ytmusic-player-bar .next-button', '[data-testid="control-button-skip-forward"]',
      '.skipControl__next', 'button[aria-label="Next"]', 'button[aria-label="Next track"]'],
    prev: ['ytmusic-player-bar .previous-button', '[data-testid="control-button-skip-back"]', '.skipControl__previous',
      'button[aria-label="Previous"]', 'button[aria-label="Previous track"]'],
  };
  const press = which => {
    for (const sel of buttons[which]) {
      const b = document.querySelector(sel);
      if (b && !b.disabled) { b.click(); return 'ok'; }
    }
    return 'none';
  };

  // Visualizer fed from the page: a capture of the playing element into an analyser (no effect on what you hear).
  let capture = null;
  const startCapture = bars => {
    // The playing element that has sound (a page can play silent video next to its audio).
    let stream = null;
    for (const m of new Set([pick(), ...media().filter(x => !x.paused)])) {
      if (!m || !m.captureStream) continue;
      try { const s = m.captureStream(); if (s.getAudioTracks().length) { stream = s; break; } } catch { }
    }
    if (!stream) return 'none';
    try {
      const ctx = new AudioContext();
      ctx.resume();
      const analyser = ctx.createAnalyser();
      analyser.fftSize = 2048;
      analyser.smoothingTimeConstant = 0.6;
      ctx.createMediaStreamSource(stream).connect(analyser);
      const data = new Uint8Array(analyser.frequencyBinCount);
      const rate = ctx.sampleRate;
      const edges = Array.from({ length: bars + 1 }, (_, b) => Math.max(1, Math.round(40 * Math.pow(16000 / 40, b / bars) * analyser.fftSize / rate)));
      const timer = setInterval(() => {
        analyser.getByteFrequencyData(data);
        const out = [];
        for (let b = 0; b < bars; b++) {
          let peak = 0;
          for (let k = edges[b]; k < Math.max(edges[b + 1], edges[b] + 1); k++) peak = Math.max(peak, data[k] || 0);
          out.push(peak);
        }
        send('v' + out.join(','));
      }, 40);
      capture = { ctx, timer };
      return 'ok';
    } catch (e) {
      return 'error: ' + e.message;
    }
  };
  // The biggest playing video (or else the biggest one), for picture in picture and pop-out.
  const bestVideo = () => media().filter(m => m.tagName === 'VIDEO' && m.videoWidth > 0)
    .sort((a, b) => (b.paused ? 0 : 2) + b.clientWidth * b.clientHeight / 1e7 - (a.paused ? 0 : 2) - a.clientWidth * a.clientHeight / 1e7)[0];
  const stopCapture = () => {
    if (!capture) return;
    clearInterval(capture.timer);
    capture.ctx.close();
    capture = null;
  };

  globalThis.__lilt = {
    setKeys(k) { keys = k; },
    // Lilt's settings for this page: { peek, gestures, glass: 0 (off) or how much page color stays }.
    configure(o) {
      peek = !!o.peek;
      gestures = !!o.gestures;
      const a = Math.max(0, Math.min(1, Number(o.glass) || 0));
      setFont(o.font);
      if (a !== glassAlpha) { glassAlpha = a; return glass(); }
      return 'ok';
    },
    reglass() { return glassAlpha ? glass() : 'off'; },
    recolor() { sentColor = null; recolorSoon(); return 'ok'; },
    report() { last = ''; report(); },
    media(cmd, arg) {
      const m = pick();
      if (cmd === 'next' || cmd === 'prev') return press(cmd);
      if (!m) return 'none';
      if (cmd === 'toggle') m.paused ? m.play() : m.pause();
      else if (cmd === 'play') m.play();
      else if (cmd === 'pause') m.pause();
      else if (cmd === 'seek') m.currentTime = Math.max(0, Math.min(m.duration || Infinity, m.currentTime + arg));
      else if (cmd === 'seekto' && isFinite(m.duration)) m.currentTime = Math.max(0, Math.min(m.duration, arg));
      else if (cmd === 'mute') m.muted = !m.muted;
      return 'ok';
    },
    // Called with a user gesture, which the Picture-in-Picture API requires.
    async pip() {
      if (document.pictureInPictureElement) { await document.exitPictureInPicture(); return 'closed'; }
      if (!document.pictureInPictureEnabled) return 'unsupported';
      const v = bestVideo();
      if (!v || v.disablePictureInPicture) return 'no video';
      await v.requestPictureInPicture();
      return 'open';
    },
    // Pop-out inside Lilt: the video (or an embedded player's frame) fills its page, which Lilt shows in a small floating
    // frame. Element full screen puts it above everything on the page without touching the page's own styles.
    async float(on) {
      if (!on) { if (document.fullscreenElement) await document.exitFullscreen(); return 'off'; }
      const target = bestVideo() || [...document.querySelectorAll('iframe')].filter(f => f.clientWidth > 200)
        .sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight)[0];
      if (!target) return 'no video';
      await target.requestFullscreen({ navigationUI: 'hide' });
      return 'on';
    },
    hasVideo() { return !!bestVideo(); },
    capture(on, bars) { stopCapture(); return on ? startCapture(bars) : 'off'; },
    // What this engine supports, including which DRM systems protected streaming (Netflix, Disney+...) can use.
    async probe() {
      const drm = {};
      const cfg = [{ initDataTypes: ['cenc'], videoCapabilities: [{ contentType: 'video/mp4; codecs="avc1.42E01E"' }],
        audioCapabilities: [{ contentType: 'audio/mp4; codecs="mp4a.40.2"' }] }];
      const late = new Promise(r => setTimeout(() => r('timeout'), 5000));
      for (const ks of ['com.microsoft.playready.recommendation', 'com.microsoft.playready.recommendation.3000', 'com.microsoft.playready',
        'com.widevine.alpha', 'org.w3.clearkey'])
        drm[ks] = await Promise.race([late, navigator.requestMediaKeySystemAccess(ks, cfg)
          .then(a => a.createMediaKeys().then(() => 'yes', e => 'no keys: ' + e.name), e => 'no: ' + e.name)]);
      return JSON.stringify({
        pip: !!document.pictureInPictureEnabled, documentPip: 'documentPictureInPicture' in globalThis,
        mediaSession: 'mediaSession' in navigator, captureStream: !!HTMLMediaElement.prototype.captureStream,
        audioContext: typeof AudioContext === 'function', fullscreen: !!document.fullscreenEnabled, drm,
      });
    },
  };
  report();
  recolorSoon();
  // Media that started before Lilt's helper arrived (autoplay during page load) fired its events already.
  setTimeout(report, 1000);
  send('ready');
  return 'on';
})();
