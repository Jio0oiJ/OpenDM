const $ = id => document.getElementById(id);
let currentUrl = '';

async function refresh() {
  const c = await chrome.storage.local.get({ port: 65200, token: '', enabled: true, autoLaunch: true });
  $('enabled').checked = c.enabled;

  // 当前标签页地址
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  currentUrl = tab && tab.url ? tab.url : '';
  $('url').textContent = currentUrl || '—';

  $('dot').className = 'dot';
  $('launch').style.display = 'none';

  if (!c.token) {
    $('stat').textContent = '未配置令牌';
    $('dot').className = 'dot off';
    return;
  }

  try {
    const r = await fetch(`http://127.0.0.1:${c.port}/ping`, { cache: 'no-store' });
    const j = await r.json();
    if (j && j.ok) {
      $('dot').className = 'dot on';
      $('stat').textContent = `进行中 ${j.active} · 排队 ${j.queued}`;
    } else {
      $('dot').className = 'dot off';
      $('stat').textContent = '端口响应异常';
    }
  } catch {
    $('dot').className = 'dot off';
    $('stat').textContent = 'OpenDM 未运行';
    // 只有装了启动器才显示这个按钮，否则点了也是白点
    if (c.autoLaunch) $('launch').style.display = 'block';
  }
}

$('send').addEventListener('click', async () => {
  if (!currentUrl || !/^https?:/i.test(currentUrl)) {
    $('stat').textContent = '这个页面不能下载';
    return;
  }

  $('stat').textContent = '处理中…';
  const res = await chrome.runtime.sendMessage({ type: 'add', url: currentUrl });
  if (res && res.ok) {
    $('stat').textContent = res.queued ? '已排队' : '已交给 OpenDM';
    $('dot').className = 'dot on';
  } else {
    $('stat').textContent = (res && res.error) || '提交失败';
    $('dot').className = 'dot off';
  }
});

$('launch').addEventListener('click', async () => {
  $('stat').textContent = '正在启动…';
  const res = await chrome.runtime.sendMessage({ type: 'launch' });
  if (res && res.ok) {
    $('stat').textContent = '已启动';
    $('dot').className = 'dot on';
    $('launch').style.display = 'none';
  } else {
    $('stat').textContent = (res && res.error) || '启动失败';
    $('dot').className = 'dot off';
  }
});

$('enabled').addEventListener('change', async e => {
  await chrome.storage.local.set({ enabled: e.target.checked });
  refresh();
});

$('opts').addEventListener('click', () => chrome.runtime.openOptionsPage());

refresh();
