const $ = id => document.getElementById(id);
const DEFAULTS = { port: 65200, token: '', enabled: true };

function setStatus(text, cls) {
  const el = $('status');
  el.textContent = text;
  el.className = cls || '';
}

async function load() {
  const c = await chrome.storage.local.get(DEFAULTS);
  $('port').value = c.port;
  $('token').value = c.token;
  $('enabled').checked = c.enabled;
}

$('save').addEventListener('click', async () => {
  const port = parseInt($('port').value, 10);
  const token = $('token').value.trim();

  if (!(port >= 1024 && port <= 65535)) {
    setStatus('端口要 1024–65535', 'bad');
    return;
  }

  await chrome.storage.local.set({ port, token, enabled: $('enabled').checked });
  setStatus('已保存', 'ok');
  test();
});

$('test').addEventListener('click', test);

async function test() {
  const port = parseInt($('port').value, 10) || 65200;
  setStatus('连接中…', '');

  try {
    const r = await fetch(`http://127.0.0.1:${port}/ping`, { cache: 'no-store' });
    const j = await r.json();
    if (j && j.ok) {
      setStatus(`已连上 OpenDM · 进行中 ${j.active} · 排队 ${j.queued}`, 'ok');
    } else {
      setStatus('有响应但不是 OpenDM', 'bad');
    }
  } catch {
    setStatus('连不上：请先启动 OpenDM，并确认端口一致', 'bad');
  }
}

load().then(test);
