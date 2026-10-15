// Updates once a minute, aligned to the minute; pauses while the page is hidden.
const time = document.getElementById('time');
const date = document.getElementById('date');
let timer;

function render() {
  const now = new Date();
  time.textContent = now.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
  date.textContent = now.toLocaleDateString([], { weekday: 'long', month: 'long', day: 'numeric' });
  timer = setTimeout(render, 60000 - (now.getSeconds() * 1000 + now.getMilliseconds()));
}

document.addEventListener('visibilitychange', () => {
  clearTimeout(timer);
  if (!document.hidden) render();
});
render();
