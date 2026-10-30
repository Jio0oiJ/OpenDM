# OpenDM · 开源下载管理器

**[⬇ 下载最新版](https://github.com/Jio0oiJ/OpenDM/releases/latest)**

| 文件 | 说明 |
| --- | --- |
| `OpenDM.exe` | 主程序，单文件免安装（需 .NET 8 桌面运行时） |
| `OpenDM-Extension-v1.0.0.zip` | 浏览器接管扩展，Chrome / Edge 通用，解压后加载即可 |

一个**从零自研**的 Windows 多线程下载器。分段并发、断点续传、队列调度、全局限速、浏览器接管，全部代码在本仓库里，没有引用任何第三方下载内核。

> **关于与 IDM 的关系**：本项目不是 Internet Download Manager 的复刻，也不包含它的任何代码、资源或界面素材。它的定位是「同类工具」—— 借鉴的只是下载器这个品类通用的交互组织方式（任务列表 + 分类侧栏 + 详情面板），实现完全独立。IDM 本身是商业闭源软件，逆向并仿制它既不合法也没必要：多线程分段下载、Range 请求、断点续传的原理在 RFC 7233 和大量开源项目里都是公开的。

## 现在能做什么

| 功能 | 状态 |
| --- | --- |
| 多线程分段下载（**1–64 连接**，按文件大小自动减段） | ✅ |
| 断点续传（暂停/继续）与跨进程恢复 | ✅ |
| **自定义 Cookie / 请求头**（裸 Cookie、cURL、cookies.txt 都能粘） | ✅ |
| **任务队列 + 全局并发上限**（同时 1–16 个，其余排队自动上场） | ✅ |
| **全局限速**（令牌桶，所有任务所有分段共享一个配额） | ✅ |
| **代理**（http / socks5，支持 `user:pass@`，可跟随系统或强制直连） | ✅ |
| **浏览器接管**（本地端口 + Chrome/Edge 扩展，连 Cookie 一起带过去） | ✅ |
| 单实例运行（第二次启动把链接转交给已开着的那个） | ✅ |
| 服务器不支持 Range 时自动降级单流 | ✅ |
| 失败重试（指数退避，4 次） | ✅ |
| 实时速度/剩余时间/分段可视化 | ✅ |
| **完成后一键「打开文件夹 / 打开文件」**（列表行内 + 详情面板，自动选中文件） | ✅ |
| 分类筛选（全部/下载中/已完成/已暂停/出错） | ✅ |
| 命令行直传链接、拖拽链接入窗 | ✅ |
| 媒体嗅探 / 站点批量抓取 | ⬜ 计划中 |

## 队列、限速、代理

**队列**：设置里可设「同时下载任务数」（默认 3）。超出的任务显示为「排队中」，任何任务结束（完成/出错/暂停）都会腾出并发位，队列按加入顺序自动补位 —— 不需要手动点「开始下一个」。重启后排队中的任务会变成暂停状态，不会一开机就自己跑起来。

**限速**：填 KB/s，`1024` 就是 1 MB/s，`0` 为不限速。所有任务、所有分段共用一个令牌桶，所以这个数字是**总带宽**而不是每段速度。实现上允许桶透支成负数再按欠账睡相应时间，这样即使把速度限制到比单次读取块（64 KB）还低也不会死循环。

**代理**：留空 = 跟随系统；`none` = 强制直连；也可填 `http://127.0.0.1:7890`、`socks5://127.0.0.1:1080`，支持 `user:pass@`。换代理不会中断正在传输的请求（旧客户端等退出时统一回收），对进行中的任务下一次分段请求即生效。

## Cookie / 请求头

访问需要登录的资源（网盘直链、付费附件、内网文件）时，在「新建下载」里点开 Cookie 框，以下四种粘法都认，混着写也行：

| 粘什么 | 例子 |
| --- | --- |
| 裸 Cookie 串 | `sid=abc; uid=7` |
| 请求头行（可多行） | `Cookie: sid=abc` / `Referer: https://x.com/` / `User-Agent: …` |
| 浏览器「Copy as cURL」整条命令 | `curl 'https://x/f' -H 'Cookie: sid=abc' -b 'z=9' -A 'UA'` |
| 导出的 cookies.txt（Netscape 7 列） | `.x.com<TAB>TRUE<TAB>/<TAB>FALSE<TAB>0<TAB>sid<TAB>abc` |

- 识别结果实时显示在输入框下方（绿色「已识别 N 项」），解析是在本地做的，不发网络请求。
- `Host`、`Range`、`Content-Length`、`Content-Encoding` 这类由引擎自己控制的头会被**丢弃**，避免用户粘贴的 `Range` 把文件下坏。
- 请求头跟着任务一起存进断点文件，**重启后自动恢复**。
- Cookie 过期导致 403 时，不用重建任务：选中任务 → 工具栏「请求头」→ 改 Cookie → 继续，已下好的部分不会重下。

## 浏览器接管

装了扩展之后，浏览器里点下载的瞬间就被接管：文件进 OpenDM 多线程下，当前标签页的 Cookie 与 Referer 一起带过去（否则登录态资源会 401/403）。

开启方式：

1. 下载 Release 里的 **`OpenDM-Extension-v1.0.0.zip`** 并解压（得到 `OpenDM-Extension` 文件夹，`manifest.json` 在根部）。也可以直接用仓库里的 `ext/browser` 目录。
2. 浏览器 `chrome://extensions` 或 `edge://extensions` → 打开 **开发者模式** → **加载已解压的扩展程序** → 选该文件夹。
3. 先启动 OpenDM，进 **设置 → 浏览器接管**（默认已开，端口 65200，带一个随机令牌），复制端口和令牌。
4. 点扩展的 **选项**，粘贴后保存，显示绿色「已连上 OpenDM」即完成。

详见 [ext/README.md](ext/README.md)。连不上 OpenDM 时扩展会自动放行浏览器原生下载，不会出现「装了扩展什么也下不了」。

本地接口只有三个动作，走 `127.0.0.1` + 令牌，外网访问不到：

```
GET /ping                                    # 探测端口（免令牌）
GET|POST /add?url=…&filename=…&referer=…&cookie=…   # 建任务，需 X-OpenDM-Token
GET /show                                    # 把已有窗口叫到前台
```

- **为什么不用 `HttpListener`**：Windows 上它绑定前缀通常需要管理员权限或事先 `netsh` 注册 urlacl，而这个程序要能免安装双击就跑。裸 `TcpListener` 绑回环地址不需要任何权限。
- **令牌为什么必须有**：否则任意网页用 `<img src="http://127.0.0.1:65200/add?url=…">` 就能往你的下载队列里塞文件。
- **端口的 TIME_WAIT 处理**：上一个实例刚退出时端口会停在 TIME_WAIT，独占绑定会失败 —— 表现为「重启后接管突然失效」。所以独占绑定失败后会带 `SO_REUSEADDR` 重试一次；若真有别的程序在监听，两次都会失败，不会误抢。

## 结构

```
src/OpenDM.Core/           纯 .NET 8 类库，零 UI 依赖，可单独测试
  Engine/HttpProbe.cs      探测长度 / Accept-Ranges / 文件名（HEAD，失败退回 GET+Range:0-0）
  Engine/DownloadJob.cs    任务执行体：多段并发、写入、限速、重试、测速、完成收尾
  Engine/TaskManager.cs    任务增删起停、队列调度、并发位、全局 HttpClient、5 秒自动落盘
  Engine/RateLimiter.cs    全局令牌桶（允许透支，避免块大于配额时死锁）
  Engine/ProxySetting.cs   代理串解析（跟随系统 / 直连 / http / socks5 + 账号密码）
  Engine/LocalBridge.cs    本地接管接口（TcpListener + 极简 HTTP）、单实例转发
  Engine/RequestHeaders.cs Cookie / cURL / cookies.txt 解析与下发
  Engine/ResumeStore.cs    断点状态持久化（%APPDATA%\OpenDM\tasks\{id}.json）
  Engine/SpeedMeter.cs     滑动窗口测速
  Models/                  任务与分段模型
src/OpenDM.App/            WPF 界面（深色主题，纯 XAML 自绘控件）
  Assets/opendm.ico       应用图标（16–256 九档尺寸，自绘生成，非素材库图片）
  NewTaskWindow / HeaderWindow / SettingsWindow
ext/browser/               Chrome/Edge MV3 扩展（下载拦截、右键菜单、选项页）
tests/OpenDM.SelfTest/     引擎自测 + 本地 Range 测试服务器
tools/                     构建期小工具
  IconGen.cs               程序化绘制图标（渐变圆角块 + 下箭头 + 托盘）
  make-icon.ps1            生成 .ico 与扩展 PNG：`powershell -File tools\make-icon.ps1`
  make-ext.py              打包扩展为 dist/OpenDM-Extension-v*.zip：`python tools/make-ext.py`
  UiShot/                  离屏渲染主窗口成 PNG，用来核对排版（不弹窗、不截屏）
```

### 四个关键设计

**1. 分段位点即断点。** 每个 `Segment` 只有 `Start / End / Position`，`Position` 是"下一个要写入的绝对偏移"。续传时不重建分段、不重算偏移，直接拿 `Position` 当 Range 起点再发一次请求。写入是"先落盘、后推进 Position"，所以哪怕进程被杀，最多重复写一小块相同字节，**不可能出现空洞或错位**。

**2. 引擎不碰 UI 线程。** 后台线程只改字段，不发通知；界面用 400ms 定时器在 UI 线程统一 `Refresh()`。这样既没有跨线程绑定异常，也不会有每秒上千次 PropertyChanged 的通知风暴。

**3. 连接池上限跟着分段数走。** 每个分段占一条连接，所以 `MaxConnectionsPerServer` 设成 `MaxConnections * 2`；否则开 64 段时多出来的段会排队等连接，看起来"开满了却不见快"。

**4. 并发位由一个回调驱动。** `DownloadJob` 结束时回调 `TaskManager`，后者腾出位子、从队列里取下一个。调度期间只持有一把队列锁，且启动新任务时不同时持有任务自身的锁 —— 避免「暂停」与「补位」互相等待。

## 构建

需要 .NET 8 SDK（或更高）。

```bash
dotnet build OpenDM.sln -c Release

# 出单文件 exe（依赖已安装的 .NET 8 桌面运行时，约 550 KB）
dotnet publish src/OpenDM.App/OpenDM.App.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist
```

Windows 上也可以直接双击 `build.cmd`。

产物：`dist\OpenDM.exe`，双击即用，无需安装。

## 自测

```bash
dotnet run --project tests/OpenDM.SelfTest
```

自测会起本地 HTTP 服务器（支持 Range、带限速、`/auth` 路径强制校验 Cookie），跑八个用例：

1. **8 段并发 → 41% 处暂停 → 重建 TaskManager（模拟进程重启）→ 续传**：恢复字节数接近暂停点、续传时每段 Range 起点**精确等于持久化断点**、最终 SHA256 一致。
2. **服务器不支持 Range** → 自动降级单段、内容一致。
3. **请求头解析**：裸 Cookie 串、多行头、cURL、cookies.txt、`Range`/`Host` 必须被丢弃。
4. **64 段并发 + Cookie**：不带 Cookie 应 403，带上后 64 段下完且 SHA256 一致，重启后 Cookie 从磁盘恢复。
5. **队列调度**：5 个任务、并发上限 2 → 任意时刻最多 2 个在跑、峰值确实到 2（不是串行）、全部完成、队列排空、5 个文件内容都对。
6. **全局限速**：12 MB @ 4 MB/s → 耗时落在 2.4–8 s（低于 2.4 s 说明限速没生效），内容一致。
7. **本地接管接口**：`/add` 带正确令牌能建成任务并下完；**错误令牌必须 403**。
8. **代理串解析**：空 = 跟随系统、`none` = 直连、省略协议默认 http、socks5 账号密码、不支持的协议要报错。

自测的任务状态写到临时目录（`OPEN_DM_DATA_DIR`），不会污染 `%APPDATA%\OpenDM` 里真实的任务列表。

实测输出：

```
[用例 1] 8 段并发 + 40% 处暂停 + 重启续传
  探测: 大小=12595257  分段=8  支持Range=True
  暂停于 41.6%  状态=Paused
  恢复读到: 已下载=5242880  分段数=8  状态=Paused
  续传结束: 状态=Completed  耗时=0.5s
  SHA256 一致 ✓
  续传 Range 起点: 11676209, 3804174, 2229767, 655360, ...
  持久化断点  : 655360, 2229767, 3804174, 5378581, ...
[用例 4] 64 段并发 + 自定义 Cookie
  无 Cookie → 403 ✓
  探测: 大小=25165824  分段=64  连接数=64
  完成: 24.0 MB  耗时 0.1s  SHA 一致 ✓
[用例 5] 队列调度：5 个任务、并发上限 2
  建立后: 下载中 2  排队 3
  峰值并发 2  总耗时 0.8s  文件全部一致 ✓
[用例 6] 全局限速：12 MB @ 4 MB/s
  耗时 3.0s  实测 3.9 MB/s  ✓
[用例 7] 浏览器接管接口
  端口 52639  令牌校验 生效 ✓  落盘 payload.bin
全部通过 ✓
```

## 已知限制

- 只处理 http/https；没有 BT/磁力。
- 单文件下载，不解析网页里的引用资源（媒体嗅探还没做）。
- 服务器忽略 `Range` 时只能单流下载，不能续传（会明确告知）。
- 文件名冲突时自动加 `(1)`、`(2)` 后缀，不做覆盖确认。
- 接管接口只绑 `127.0.0.1`；如果本机装了会改写回环流量的安全软件，可能需要放行。
- Cookie 与令牌明文存在 `%APPDATA%\OpenDM\` 下（本机文件，没有加密）。令牌泄露等于让别人能往你队列塞任务，换令牌后记得同步改扩展设置。

## 许可证

MIT，见 [LICENSE](LICENSE)。
