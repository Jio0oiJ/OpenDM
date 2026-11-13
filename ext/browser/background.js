// OpenDM 浏览器接管 —— 后台服务。
//
// 工作方式：监听浏览器下载事件 → 先问一句本地 OpenDM 在不在 → 在就把链接（连同 Cookie/Referer）
// 转过去，然后取消浏览器自己的下载。连不上就原样放行，绝不把用户正常的下载搞坏。
//
// 如果开了「自动启动」，连不上时会通过 native messaging 让浏览器把 OpenDM 拉起来
// （扩展自己没有执行本地程序的权限，这是唯一无感的方式），然后轮询等它就绪。

const DEFAULTS = { port: 65200, token: '', enabled: true, autoLaunch: true, exePath: '' };

/// native messaging 的 host 名，与 OpenDM 写进注册表的名字一致
const NATIVE_HOST = 'com.opendm.launcher';

async function cfg() {
  return await chrome.storage.local.get(DEFAULTS);
}

const apiUrl = (port, path) => `http://127.0.0.1:${port}${path}`;
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function ping(c) {
  try {
    const r = await fetch(apiUrl(c.port, '/ping'), { cache: 'no-store' });
    if (!r.ok) return null;
    return await r.json();
  } catch {
    return null;
  }
}

// ---------------------------------------------------------------- 唤起主程序

/// 让浏览器把 OpenDM 拉起来。
/// host 就是 OpenDM 本体：它收到 start 会先回一条消息，然后照常开窗口 ——
/// 窗口一直开着，所以回调可能等到进程退出才来，这里自己加个短超时，别把流程挂住。
function launchNative(timeoutMs = 2500) {
  return new Promise(resolve => {
    let done = false;
    const finish = r => { if (!done) { done = true; resolve(r); } };

    const timer = setTimeout(() => finish({ ok: true, pending: true }), timeoutMs);

    try {
      chrome.runtime.sendNativeMessage(NATIVE_HOST, { cmd: 'start' }, resp => {
        clearTimeout(timer);
        const err = chrome.runtime.lastError;   // 必须在回调里读，否则控制台会报未处理
        finish({ ok: !err, resp, error: err ? err.message : '' });
      });
    } catch (e) {
      clearTimeout(timer);
      finish({ ok: false, error: e.message });
    }
  });
}

/// 同一个时刻只发起一次启动，避免连着点几个下载就开一堆进程
let launching = null;

async function ensureOnline(c, waitMs = 9000) {
  const first = await ping(c);
  if (first && first.ok) return first;

  if (!c.autoLaunch) return null;

  if (!launching) {
    launching = launchNative().finally(() => { launching = null; });
  }
  const r = await launching;

  const t0 = Date.now();
  while (Date.now() - t0 < waitMs) {
    await sleep(400);
    const info = await ping(c);
    if (info && info.ok) return info;
  }

  // 唤起命令发出去却没连上，多半是没装启动器 / 路径不对，把原因带回去给上层提示
  return { __fail: true, nativeError: r && r.error ? r.error : '' };
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

/// 下载还在进行中才值得取消转发；几秒钟里已经下完的小文件就别再折腾
async function stillRunning(id) {
  try {
    const [st] = await chrome.downloads.search({ id });
    return !!st && st.state === 'in_progress';
  } catch {
    return true;
  }
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

    const info = await ensureOnline(c);
    if (!info || !info.ok) return;   // 没连上就放行（浏览器那份下载一直在跑）

    if (!(await stillRunning(item.id))) return;

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

  // 用户主动点的，愿意等一会儿：没开就顺手拉起来
  const info2 = await ensureOnline(c, 12000);
  if (!info2 || !info2.ok) {
    toast(launchHint(info2), false);
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

function launchHint(fail) {
  const reason = fail && fail.nativeError ? fail.nativeError : '';
  if (/not found|not registered|Native host has exited/i.test(reason)) {
    return '浏览器里没有注册启动器。请在 OpenDM「设置 → 浏览器自动唤起」点『安装』，然后重启浏览器。';
  }
  return '没能启动 OpenDM：请确认主程序路径正确，或手动打开 OpenDM 后再试。';
}

// ---------------------------------------------------------------- 与弹窗通信

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  (async () => {
    const c = await cfg();

    if (msg.type === 'ping') {
      const info = await ping(c);
      reply({ ok: !!info && info.ok, info, port: c.port, autoLaunch: c.autoLaunch });
      return;
    }

    if (msg.type === 'launch') {
      const info = await ensureOnline(c, 12000);
      if (info && info.ok) reply({ ok: true, info });
      else reply({ ok: false, error: launchHint(info) });
      return;
    }

    if (msg.type === 'add') {
      const info = await ensureOnline(c, 12000);
      if (!info || !info.ok) { reply({ ok: false, error: launchHint(info) }); return; }

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

    if (msg.type === 'setPath') {
      // 把新路径写给 native host：它负责改注册表里那份 host 清单
      try {
        chrome.runtime.sendNativeMessage(NATIVE_HOST, { cmd: 'setPath', path: msg.path }, resp => {
          const err = chrome.runtime.lastError;
          if (err) reply({ ok: false, error: err.message });
          else reply(resp || { ok: false, error: '没有响应' });
        });
      } catch (e) {
        reply({ ok: false, error: e.message });
      }
      return;
    }

    if (msg.type === 'getPath') {
      try {
        chrome.runtime.sendNativeMessage(NATIVE_HOST, { cmd: 'getPath' }, resp => {
          const err = chrome.runtime.lastError;
          if (err) reply({ ok: false, error: err.message });
          else reply(resp || { ok: false, error: '没有响应' });
        });
      } catch (e) {
        reply({ ok: false, error: e.message });
      }
      return;
    }

    reply({ ok: false, error: '未知指令' });
  })();

  return true;   // 异步回复
});
