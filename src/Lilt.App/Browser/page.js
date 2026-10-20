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
  const pick = () => media().find(m => !m.paused && !m.ended && m.readyState > 1)
    || (current && current.isConnected ? current : null)
    || media().find(m => m.currentTime > 0) || null;
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
    setPeek(on) { peek = !!on; },
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
    probe() {
      return JSON.stringify({
        pip: !!document.pictureInPictureEnabled, documentPip: 'documentPictureInPicture' in globalThis,
        mediaSession: 'mediaSession' in navigator, captureStream: !!HTMLMediaElement.prototype.captureStream,
        audioContext: typeof AudioContext === 'function', fullscreen: !!document.fullscreenEnabled,
      });
    },
  };
  report();
  // Media that started before Lilt's helper arrived (autoplay during page load) fired its events already.
  setTimeout(report, 1000);
  send('ready');
  return 'on';
})();
