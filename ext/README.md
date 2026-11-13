# OpenDM 浏览器接管扩展

Chrome / Edge 通用（Manifest V3）。作用是把浏览器发起的下载交给本机运行的 OpenDM，
并把当前标签页的 Cookie、Referer 一起带过去 —— 需要登录才能下的资源因此也能多线程分段下。

> **两种拿法，内容一样**：① 下载 Release 里的 `OpenDM-Extension-v*.zip` 解压，得到
> `OpenDM-Extension` 文件夹；② 直接用仓库里的 `ext/browser` 目录。下面第 4 步选的就是这个
> **包含 `manifest.json` 的那一层文件夹**，别再多选一层。

## 安装

1. 先启动 OpenDM，打开 **设置 → 浏览器接管**，确认勾选「开启本地接管接口」，记下 **端口** 和 **令牌**。
2. 浏览器地址栏输入：
   - Chrome：`chrome://extensions`
   - Edge：`edge://extensions`
3. 打开右上角（Edge 在左下角）的 **开发者模式**。
4. 点 **加载已解压的扩展程序**，选择本目录 `ext/browser`。
5. 点扩展图标的 **设置**（或右键 → 选项），把端口和令牌填进去，点 **保存**。状态会显示
   「已连上 OpenDM · 进行中 0 · 排队 0」。
6. 想让它自己起来？回到 OpenDM **设置 → 浏览器自动唤起** 点一次「安装」，然后**重启一次浏览器**。

> **升级到 1.2.0 会换 ID**：manifest 里加了 `key` 把扩展 ID 固定成
> `ndhkhjeaooenioieceapibfhpajkkflf`（不固定的话，ID 跟着文件夹路径走，换个目录就变，
> 系统没法授权它调用启动器）。重新加载后原来填的端口和令牌会清空，重填一次即可。

## 自动唤起是怎么做到的

浏览器扩展没有执行本地程序的权限：`file://` 指向 exe 只会被下载，自定义协议每次都要用户
点一次确认。所以走 Chrome 的 **native messaging**：

1. OpenDM 在 `%APPDATA%\OpenDM\native\com.opendm.launcher.json` 写一份 host 清单，
   `path` 指向自己，`allowed_origins` 只放行这一个扩展 ID；
2. 同时写 `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.opendm.launcher`
   （Edge / Chromium / Brave 各一份），默认值 = 清单的绝对路径；
3. 扩展连不上时发 `{"cmd":"start"}`，浏览器按清单把 OpenDM.exe 拉起来；
4. OpenDM 启动时若发现 stdin 是管道，就先按协议回一条消息，然后照常开窗口 —— 窗口起来后
   扩展的轮询就能连上，下载自然转过去。

选项页的「主程序路径」写的就是第 1 步清单里的 `path`：填好后点「写入系统」，由 OpenDM
改文件并重写注册表，下次唤起的就换成那个 exe。填的文件必须有版本信息且产品名是 OpenDM，
否则会被拒绝（避免指错程序后只得到一句「连不上」）。

## 用法

| 操作 | 结果 |
| --- | --- |
| 页面里点下载链接 | 浏览器开始下载的瞬间被取消，文件出现在 OpenDM 里；OpenDM 没开就先拉起来 |
| 右键链接 / 视频 / 图片 | 「用 OpenDM 下载此链接 / 此媒体」 |
| 点扩展图标 | 显示连接状态；可把当前页地址直接交给 OpenDM；没在运行时多一个「启动 OpenDM」按钮 |

## 设计上的取舍

- **连不上就放行**。扩展每次拦截前先 `GET /ping`，OpenDM 没运行或端口不对就立刻 `suggest()`
  让浏览器自己下。绝不出现「装了扩展结果什么也下不了」。
- **`blob:` / `data:` 不接管**。这类地址只在浏览器进程内有意义，交出去必然失败。
- **令牌必填**。本地接口对 `/add` 校验令牌，否则任意网页用 `<img src="http://127.0.0.1:65200/add?url=...">`
  就能往你队列里塞文件。`/ping` 免令牌只是为了探测端口。
- **Cookie 用 `chrome.cookies` 现取**，不缓存。会话 Cookie 变化后下一次下载自然就是新的。
- **只绑 `127.0.0.1`**，外网无法访问该端口。
