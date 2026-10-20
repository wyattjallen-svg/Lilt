// Reads lilt-data.json (written by Lilt) and lays out the page. Bookmark text is set with textContent, never as HTML.
const $ = id => document.getElementById(id);

fetch('lilt-data.json', { cache: 'no-store' })
  .then(r => (r.ok ? r.json() : {}))
  .catch(() => ({}))
  .then(apply);

function apply(data) {
  const home = Object.assign({ clock: true, search: true, tiles: true, glass: false }, data.home);
  for (const [name, value] of Object.entries(data.colors || {})) {
    if (/^#[0-9a-f]{3,8}$/i.test(value)) document.documentElement.style.setProperty('--' + name, value);
  }
  document.body.classList.toggle('glass', !!home.glass);
  if (home.clock) { $('clock').hidden = false; tick(); }
  if (home.search) { $('search').hidden = false; }
  const tiles = (data.tiles || []).filter(t => /^https?:\/\//i.test(t.url));
  if (home.tiles && tiles.length) {
    for (const t of tiles) {
      const a = document.createElement('a');
      a.href = t.url;
      a.title = t.url;
      const badge = document.createElement('span');
      badge.className = 'badge';
      const name = (t.title || new URL(t.url).hostname).trim();
      badge.textContent = name.replace(/^www\./i, '').charAt(0).toUpperCase();
      const label = document.createElement('span');
      label.className = 'label';
      label.textContent = name;
      a.append(badge, label);
      $('tiles').append(a);
    }
    $('tiles').hidden = false;
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
