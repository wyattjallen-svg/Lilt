// Reads lilt-data.json (written by Lilt) and lays out the page. Bookmark text is set with textContent, never as HTML.
const $ = id => document.getElementById(id);

fetch('lilt-data.json', { cache: 'no-store' })
  .then(r => (r.ok ? r.json() : {}))
  .catch(() => ({}))
  .then(apply);

function apply(data) {
  const items = Array.isArray(data.items) ? data.items : ['logo', 'search', 'tiles', 'gear'];
  const root = document.documentElement;
  for (const [name, value] of Object.entries(data.colors || {})) {
    if (/^#[0-9a-f]{3,8}$/i.test(value)) root.style.setProperty('--' + name, value);
  }
  if (typeof data.font === 'string' && /^[\w \-]{1,100}$/.test(data.font)) root.style.setProperty('--font', `"${data.font}", "Segoe UI", system-ui, sans-serif`);
  if (typeof data.radius === 'number') root.style.setProperty('--radius', Math.max(0, Math.min(24, data.radius)) + 'px');
  root.style.colorScheme = data.base === 'light' ? 'light' : 'dark';
  if (typeof data.scale === 'number') root.style.zoom = String(Math.max(0.75, Math.min(1.5, data.scale)));
  if (typeof data.hint === 'string') $('hint').textContent = data.hint.slice(0, 300);
  if (typeof data.search === 'string') $('search').q.placeholder = `Search with ${data.search.slice(0, 40)} or type an address`;
  document.body.classList.toggle('glass', !!data.glass);
  background(data);

  for (const id of items) {
    const el = $(id);
    if (!el) continue;
    if (el.parentElement === $('main')) $('main').append(el);
    el.hidden = false;
    if (id === 'clock') tick();
    if (id === 'tiles') tiles(data.tiles || []);
  }
}

function tiles(list) {
  for (const t of list.filter(t => /^https?:\/\//i.test(t.url))) {
    const wrap = document.createElement('div');
    wrap.className = 'tile';
    const a = document.createElement('a');
    a.className = 'open';
    a.href = t.url;
    a.title = t.url;
    const badge = document.createElement('span');
    badge.className = 'badge';
    const name = (t.title || new URL(t.url).hostname).trim();
    if (typeof t.icon === 'string' && /^icons\/[\w.\-]+\.png$/.test(t.icon)) {
      const img = document.createElement('img');
      img.alt = '';
      img.src = t.icon;
      img.onerror = () => { img.remove(); badge.textContent = letter(name); };
      badge.append(img);
    } else {
      badge.textContent = letter(name);
    }
    const label = document.createElement('span');
    label.className = 'label';
    label.textContent = name;
    a.append(badge, label);
    const remove = document.createElement('a');
    remove.className = 'remove';
    remove.href = '#remove-tile=' + encodeURIComponent(t.url);
    remove.title = 'Remove ' + name;
    remove.setAttribute('aria-label', 'Remove ' + name);
    remove.textContent = '×';
    wrap.append(a, remove);
    $('tiles').append(wrap);
  }
  const add = document.createElement('a');
  add.className = 'open add';
  add.href = '#add-tile';
  const plus = document.createElement('span');
  plus.className = 'badge';
  plus.textContent = '+';
  const label = document.createElement('span');
  label.className = 'label';
  label.textContent = 'Add';
  add.append(plus, label);
  $('tiles').append(add);
}

// Lilt does the searching (your search engine) or opens the address, same as the address bar.
$('search').addEventListener('submit', e => {
  e.preventDefault();
  const q = $('search').q.value.trim();
  if (q) location.hash = 'go=' + encodeURIComponent(q.slice(0, 2000));
});

const letter = name => name.replace(/^www\./i, '').charAt(0).toUpperCase();

// "hills", "aurora" and "waves" are drawn from the theme's colors; any other value is an image file next to this page.
function background(data) {
  const bg = typeof data.background === 'string' ? data.background : '';
  const css = n => getComputedStyle(document.documentElement).getPropertyValue('--' + n).trim();
  const accent = css('accent'), muted = css('textMuted'), base = css('background');
  const safe = [accent, muted, base].every(c => /^#[0-9a-f]{3,8}$/i.test(c));
  if (bg === 'aurora' && safe) {
    // Soft blurred light, no animation (nothing to redraw).
    $('scenery').innerHTML = `<svg viewBox="0 0 1440 900" preserveAspectRatio="xMidYMid slice" xmlns="http://www.w3.org/2000/svg">
      <defs><filter id="b" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="90"/></filter></defs>
      ${data.glass ? '' : `<rect width="1440" height="900" fill="${base}"/>`}
      <g filter="url(#b)">
        <ellipse cx="380" cy="260" rx="420" ry="160" fill="${accent}" fill-opacity="0.38"/>
        <ellipse cx="1060" cy="200" rx="380" ry="140" fill="${muted}" fill-opacity="0.22"/>
        <ellipse cx="820" cy="720" rx="520" ry="170" fill="${accent}" fill-opacity="0.22"/>
      </g></svg>`;
  } else if (bg === 'waves' && safe) {
    const lines = Array.from({ length: 7 }, (_, i) => {
      const y = 520 + i * 48, o = (0.08 + i * 0.05).toFixed(2);
      return `<path d="M0 ${y} C 240 ${y - 60} 480 ${y + 60} 720 ${y} S 1200 ${y - 60} 1440 ${y}" fill="none" stroke="${i % 2 ? muted : accent}" stroke-opacity="${o}" stroke-width="2"/>`;
    }).join('');
    $('scenery').innerHTML = `<svg viewBox="0 0 1440 900" preserveAspectRatio="xMidYMax slice" xmlns="http://www.w3.org/2000/svg">
      ${data.glass ? '' : `<rect width="1440" height="900" fill="${base}"/>`}${lines}</svg>`;
  } else if (bg === 'hills') {
    const sky = data.glass ? 'transparent' : base;
    const layers = [
      [muted, 0.18, 'M0 520 C 180 430 320 470 480 420 S 820 330 1000 400 S 1280 380 1440 340 V 900 H 0 Z'],
      [accent, 0.28, 'M0 600 C 200 520 360 560 560 500 S 900 460 1080 520 S 1320 470 1440 480 V 900 H 0 Z'],
      [accent, 0.45, 'M0 690 C 240 610 420 660 640 610 S 1000 580 1200 640 S 1380 620 1440 600 V 900 H 0 Z'],
      [base, 0.85, 'M0 790 C 260 730 520 770 760 740 S 1160 720 1440 760 V 900 H 0 Z'],
    ];
    const svg = `<svg viewBox="0 0 1440 900" preserveAspectRatio="xMidYMax slice" xmlns="http://www.w3.org/2000/svg">
      <defs><linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${sky}" stop-opacity="${data.glass ? 0 : 1}"/><stop offset="1" stop-color="${accent}" stop-opacity="0.18"/></linearGradient></defs>
      <rect width="1440" height="900" fill="url(#sky)"/>
      ${layers.map(([c, o, d]) => `<path d="${d}" fill="${c}" fill-opacity="${o}"/>`).join('')}
    </svg>`;
    if (safe) $('scenery').innerHTML = svg;
  } else if (/^[\w .\-]{1,100}\.(png|jpe?g|webp|gif|svg)$/i.test(bg)) {
    const dim = Math.max(0, Math.min(0.8, Number(data.dim) || 0));
    document.body.style.backgroundImage = `linear-gradient(rgba(0,0,0,${(0.08 + dim).toFixed(2)}), rgba(0,0,0,${(0.24 + dim).toFixed(2)})), url("${encodeURI(bg)}")`;
  }
}

// Updates once a minute, aligned to the minute; stops while the tab is hidden.
let timer;
function tick() {
  const now = new Date();
  $('time').textContent = now.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
  $('date').textContent = now.toLocaleDateString([], { weekday: 'long', month: 'long', day: 'numeric' });
  timer = setTimeout(tick, 60000 - (now.getSeconds() * 1000 + now.getMilliseconds()));
}
document.addEventListener('visibilitychange', () => {
  clearTimeout(timer);
  if (!document.hidden && !$('clock').hidden) tick();
});
