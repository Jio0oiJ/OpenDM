const $ = id => document.getElementById(id);
const DEFAULTS = { port: 65200, token: '', enabled: true, autoLaunch: true, exePath: '' };

function setStatus(text, cls) {
  const el = $('status');
  el.textContent = text;
  el.className = cls || '';
}

function setPathStatus(text, cls) {
  const el = $('pathStatus');
  el.textContent = text;
  el.className = cls || '';
  el.style.color = cls === 'ok' ? 'var(--green)' : cls === 'bad' ? 'var(--red)' : 'var(--muted)';
}

async function load() {
  const c = await chrome.storage.local.get(DEFAULTS);
  $('port').value = c.port;
  $('token').value = c.token;
  $('enabled').checked = c.enabled;
  $('autoLaunch').checked = c.autoLaunch;
  $('exePath').value = c.exePath || '';
}

$('save').addEventListener('click', async () => {
  const port = parseInt($('port').value, 10);
  const token = $('token').value.trim();

  if (!(port >= 1024 && port <= 65535)) {
    setStatus('端口要 1024–65535', 'bad');
    return;
  }

  await chrome.storage.local.set({
    port, token,
    enabled: $('enabled').checked,
    autoLaunch: $('autoLaunch').checked,
    exePath: $('exePath').value.trim()
  });

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
    setStatus('连不上：OpenDM 没在跑（点了下载会自动拉起），或端口不一致', 'bad');
  }
}

// ---------------------------------------------------------------- 主程序路径

$('writePath').addEventListener('click', () => {
  const path = $('exePath').value.trim();
  if (!path) {
    setPathStatus('先填一个 exe 路径', 'bad');
    return;
  }

  setPathStatus('写入中…', '');

  chrome.runtime.sendMessage({ type: 'setPath', path }, res => {
    const err = chrome.runtime.lastError;
    if (err) {
      setPathStatus('没装启动器：请先在 OpenDM「设置 → 浏览器自动唤起」点安装', 'bad');
      return;
    }
    if (res && res.ok) {
      chrome.storage.local.set({ exePath: res.path || path });
      setPathStatus('已写入，重启浏览器后生效', 'ok');
    } else {
      setPathStatus((res && res.error) || '写入失败', 'bad');
    }
  });
});

$('readPath').addEventListener('click', () => {
  setPathStatus('读取中…', '');

  chrome.runtime.sendMessage({ type: 'getPath' }, res => {
    const err = chrome.runtime.lastError;
    if (err) {
      setPathStatus('没装启动器：请先在 OpenDM「设置 → 浏览器自动唤起」点安装', 'bad');
      return;
    }
    if (res && res.ok) {
      const p = res.path || '';
      $('exePath').value = p;
      chrome.storage.local.set({ exePath: p });
      setPathStatus(p ? '这是系统里当前登记的路径' : '系统里还没登记路径', p ? 'ok' : '');
      if (!p && res.exe) setPathStatus('系统里还没登记路径，当前运行的是 ' + res.exe, '');
    } else {
      setPathStatus((res && res.error) || '读取失败', 'bad');
    }
  });
});

load().then(test);
