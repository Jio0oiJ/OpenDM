// OpenDM 浏览器接管 —— 后台服务。
//
// 工作方式：监听浏览器下载事件 → 先问一句本地 OpenDM 在不在 → 在就把链接（连同 Cookie/Referer）
// 转过去，然后取消浏览器自己的下载。连不上就原样放行，绝不把用户正常的下载搞坏。

const DEFAULTS = { port: 65200, token: '', enabled: true };

async function cfg() {
  return await chrome.storage.local.get(DEFAULTS);
}

const apiUrl = (port, path) => `http://127.0.0.1:${port}${path}`;

async function ping(c) {
  try {
    const r = await fetch(apiUrl(c.port, '/ping'), { cache: 'no-store' });
    if (!r.ok) return null;
    return await r.json();
  } catch {
    return null;
  }
}

async function cookieHeader(url) {
  try {
    const list = await chrome.cookies.getAll({ url });
    return list.map(c => `${c.name}=${c.value}`).join('; ');
  } catch {
    return '';
  }
}

async function send(c, payload) {
  const q = new URLSearchParams({ url: payload.url });
  if (payload.filename) q.set('filename', payload.filename);
  if (payload.referer) q.set('referer', payload.referer);
  if (payload.cookie) q.set('cookie', payload.cookie);

  try {
    const r = await fetch(apiUrl(c.port, '/add?' + q.toString()), {
      headers: { 'X-OpenDM-Token': c.token },
      cache: 'no-store'
    });
    return await r.json().catch(() => ({ ok: false, error: '响应不是 JSON' }));
  } catch (e) {
    return { ok: false, error: '连接 OpenDM 失败：' + e.message };
  }
}

function toast(message, ok) {
  try {
    chrome.notifications.create({
      type: 'basic',
      iconUrl: 'icons/128.png',
      title: ok ? '已交给 OpenDM' : 'OpenDM 接管失败',
      message: message || ''
    });
  } catch {
    // 通知权限被收回也不影响主流程
  }
}

function baseName(p) {
  if (!p) return '';
  const parts = p.split(/[\\/]/);
  return parts[parts.length - 1] || '';
}

// ---------------------------------------------------------------- 下载拦截

chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
  // blob: / data: 这类地址离开浏览器就没法取，交给浏览器自己下
  if (!/^https?:/i.test(item.url || '')) {
    suggest();
    return;
  }

  // 先让浏览器继续，回调里再取消 —— 这是官方文档给的时序，直接不回 suggest 会把下载卡住
  suggest();

  (async () => {
    const c = await cfg();
    if (!c.enabled || !c.token) return;

    const info = await ping(c);
    if (!info || !info.ok) return;   // 没连上就放行

    chrome.downloads.cancel(item.id, () => chrome.downloads.erase({ id: item.id }));

    const res = await send(c, {
      url: item.url,
      filename: baseName(item.filename),
      referer: item.referrer || '',
      cookie: await cookieHeader(item.url)
    });

    if (res.ok) toast(res.queued ? `${res.file} 已排队` : res.file, true);
    else toast(res.error, false);
  })();
});

// ---------------------------------------------------------------- 右键菜单

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: 'opendm-link',
    title: '用 OpenDM 下载此链接',
    contexts: ['link']
  });
  chrome.contextMenus.create({
    id: 'opendm-media',
    title: '用 OpenDM 下载此媒体',
    contexts: ['video', 'audio', 'image']
  });
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const url = info.linkUrl || info.srcUrl;
  if (!url) return;

  const c = await cfg();
  if (!c.token) {
    toast('还没配置令牌，请打开扩展的选项页填写', false);
    return;
  }

  const res = await send(c, {
    url,
    referer: tab && tab.url ? tab.url : '',
    cookie: /^https?:/i.test(url) ? await cookieHeader(url) : ''
  });

  if (res.ok) toast(res.queued ? `${res.file} 已排队` : res.file, true);
  else toast(res.error, false);
});

// ---------------------------------------------------------------- 与弹窗通信

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  (async () => {
    const c = await cfg();

    if (msg.type === 'ping') {
      const info = await ping(c);
      reply({ ok: !!info && info.ok, info, port: c.port });
      return;
    }

    if (msg.type === 'add') {
      const res = await send(c, {
        url: msg.url,
        referer: msg.referer || '',
        cookie: /^https?:/i.test(msg.url) ? await cookieHeader(msg.url) : ''
      });
      reply(res);
      return;
    }

    if (msg.type === 'config') {
      reply({ ...c });
      return;
    }

    reply({ ok: false, error: '未知指令' });
  })();

  return true;   // 异步回复
});
