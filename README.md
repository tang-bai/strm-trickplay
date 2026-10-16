# STRM Trickplay

> 让 Jellyfin 里的 **`.strm` 条目**（网盘 / 远端直链）复用**同级目录下已经生成好的 `.trickplay` 瓦片**，在播放时显示进度条缩略图预览（trickplay / scrub preview）。

| 项目 | 值 |
|---|---|
| 适配 | **Jellyfin 12.2**（targetAbi `12.2.0.0`，net10.0） |
| 当前版本 | 0.1.0.0 |
| 存储库地址 | `https://raw.githubusercontent.com/tang-bai/strm-trickplay/main/docs/manifest.json` |
| 状态 | 已在真实 **Jellyfin 12.2.0** 服务端实测通过（见 [TEST-REPORT.md](TEST-REPORT.md)） |

---

## 1. 为什么需要这个插件

### 你遇到的现象

网盘 / 影视库用 `.strm` 挂载后，**播放时进度条上悬停没有缩略图预览**，而同样是这个媒体，如果当初是以本地文件入库的，预览却是正常的。自己把 `<影片名>.trickplay` 目录放到 strm 旁边也没用。

### 根本原因：Jellyfin 核心对 strm 有**三重阻断**

| # | 事实 | 源码位置（v12.2 实测核实） |
|---|---|---|
| 1 | `.strm` 条目被标记为 **shortcut** | `Emby.Server.Implementations/Library/Resolvers/BaseVideoResolver.cs:147` |
| 2 | strm 里是 http(s) 直链时，它的媒体源被标记 **`IsRemote = true`**（Jellyfin 出于安全只接受远端 URL） | `MediaBrowser.Controller/Entities/BaseItem.cs:1239-1250` |
| 3 | **核心的 trickplay 清单会跳过 remote 媒体源** → `BaseItemDto.Trickplay` 恒为空 → 网页端拿不到任何预览数据 | `Jellyfin.Server.Implementations/Trickplay/TrickplayManager.cs:746` |
| 4 | 12.x 虽然已经能"发现磁盘上现成的 `.trickplay` 目录"，但触发它的入口在 `CanGenerateTrickplay` 里**明确排除 shortcut**，所以对 strm 永不执行 | 同文件 `:285` 调用、`:650` `if (video.IsShortcut) return false;` |
| 5 | 而 Web 客户端的预览**只认 DTO 里那个字段** | jellyfin-web `apps/legacy/controllers/playback/video/index.js:144`：`item.Trickplay?.[mediaSourceId]` |

**结论**：不是你的目录放错了，也不是没刮削到 —— 是核心在"出口"把 strm 的 trickplay 数据丢掉了。
顺带一提，**只把数据写进 Jellyfin 数据库也没用**（第 3 条仍会在出口过滤掉）。

### 插件做了什么

用一层 **`ITrickplayManager` 装饰器**接管 trickplay 的**读路径**，只对 `.strm` 条目补齐数据：

```
jellyfin-web / 原生客户端
        │  GET /Users/{uid}/Items/{id}?Fields=Trickplay      ← DTO 清单
        │  GET /Videos/{id}/Trickplay/{width}/{index}.jpg    ← 瓦片图
        │  GET /Videos/{id}/Trickplay/{width}/tiles.m3u8     ← trickplay 播放列表
        ▼
   ITrickplayManager ──► StrmTrickplayManager（本插件）
                              │  ① .strm 条目：从同级 <名字>.trickplay 目录解析并返回（不跳过 remote）
                              │  ② 其它一切：原样透传
                              ▼
                        核心 TrickplayManager
```

设计原则：**只补读路径，不写 Jellyfin 数据库，不碰本地媒体的既有行为**。插件对非 strm 条目完全透明，卸载后无残留。

### 装前 / 装后对比（实测）

| | 装插件前 | 装插件后 |
|---|---|---|
| DTO `Trickplay` 字段 | 空 | `{"320":{"Width":320,"Height":180,"TileWidth":10,...}}` |
| `Videos/{id}/Trickplay/320/0.jpg` | 404 | `200 image/jpeg`（来自你的 `.trickplay` 目录） |
| `.../Trickplay/320/tiles.m3u8` | 404 | 正常的 `#EXT-X-TILES` 播放列表 |
| 网页播放时悬停进度条 | 无预览 | 缩略图气泡 |

---

## 2. 安装教程

### 准备工作

* Jellyfin **12.2.x**（其它大版本需要按该版本重新编译，见文末）；
* 一个能正常播放的 `.strm` 条目 + 它同级目录里已经存在的 `.trickplay` 瓦片目录（存放规范见 [第 3 节](#3-目录文件存放规范)）。

### 方式 A：在线安装（推荐，可自动更新）

1. 打开 Jellyfin：**控制台 → 插件 → 存储库**（Dashboard → Plugins → Repositories）；
2. 点 **添加 / +**：
   * 名称：`STRM Trickplay`（随便填）
   * URL：`https://raw.githubusercontent.com/tang-bai/strm-trickplay/main/docs/manifest.json`
3. 保存后切到 **目录**（Catalog）标签，搜索 **STRM Trickplay**，点 **安装**；
4. 安装完按提示 **重启 Jellyfin**（控制台右上角 ⚙ → 重启）；
5. 以后每次发新版，插件页会直接提示升级。

> 不想用 raw 长链接？在 GitHub 仓库 `Settings → Pages` 里把 Source 设为 **分支 main / 目录 /docs**，
> 之后可以用更短的 `https://tang-bai.github.io/strm-trickplay/manifest.json`（内容完全一样）。

### 方式 B：离线安装（zip）

1. 到 [Releases](https://github.com/tang-bai/strm-trickplay/releases) 下载 `strm-trickplay_<版本>.zip`；
2. **校验下载完整性**（推荐，Release 里同时提供校验文件）：

   ```bash
   # Linux / macOS / Git Bash
   md5sum -c strm-trickplay_0.1.0.0.zip.md5        # 期望输出 ...: OK
   ```
   ```powershell
   # Windows PowerShell
   (Get-FileHash .\strm-trickplay_0.1.0.0.zip -Algorithm MD5).Hash
   ```

3. 解压到 Jellyfin 的插件目录，**目录名可自定义但内容必须同级**：

   ```
   <jellyfin-data>/plugins/StrmTrickplay/
   ├── Jellyfin.Plugin.StrmTrickplay.dll
   └── meta.json
   ```

   | 部署方式 | 插件目录 |
   |---|---|
   | Windows（安装版 / 便携版） | `%LOCALAPPDATA%\jellyfin\plugins\` |
   | Linux（服务 / 包管理安装） | `/var/lib/jellyfin/plugins/` |
   | Linux（用户态运行） | `~/.local/share/jellyfin/plugins/` |
   | Docker | `/config/plugins/`（即宿主机映射到 `/config` 的那个目录） |
   | macOS | `~/.local/share/jellyfin/plugins/` |

4. **重启 Jellyfin**。

### 方式 C：只拷 DLL（不推荐）

直接放 `Jellyfin.Plugin.StrmTrickplay.dll` 也能加载，但缺少 `meta.json` 时插件页显示的版本信息会不准，也无法被存储库识别。

### 安装成功的三个标志

1. **控制台 → 插件** 里出现 `STRM Trickplay`，版本 0.1.0.0，状态为启用；
2. 日志里出现这两行（关键字 `[StrmTrickplay]`）：

   ```
   [INF] Jellyfin.Plugin.StrmTrickplay.Trickplay.StrmTrickplayManager: [StrmTrickplay] ITrickplayManager 装饰器已生效，回退实现: "Jellyfin.Server.Implementations.Trickplay.TrickplayManager"
   [INF] Jellyfin.Plugin.StrmTrickplay.EventHandlers.LibraryChangeListener: [StrmTrickplay] 已挂接媒体库变更事件（ItemAdded / ItemUpdated / ItemRemoved）
   ```

3. 跑一次自检接口（把 `<APIKey>` 换成你的密钥；管理员账号可在 控制台 → API 密钥 里创建）：

   ```bash
   curl -X POST "http://<服务器>:8096/Plugins/StrmTrickplay/SelfTest" \
        -H "Authorization: MediaBrowser Token=\"<APIKey>\""
   ```

   期望返回 `parsedCount: 1`、`Width: 320`、`tileFileCount: 3`。
   这一条过了，说明插件已加载、DI 覆盖生效、解析器正常。

### 升级与卸载

* **升级**：存储库方式在插件页点升级后重启；离线方式用新 zip 覆盖同目录文件后重启。
* **卸载**：控制台 → 插件 → 卸载（或直接删掉 `plugins/StrmTrickplay/`）→ 重启。
  插件不写 Jellyfin 数据库，卸载后**没有任何残留**，媒体目录里的 `.trickplay` 原样保留。

---

## 3. 目录文件存放规范

### 3.0 前置条件

* `.strm` 文件里必须是**远端直链**：`http://`、`https://`、`rtsp://`、`rtp://`。
  本地路径会被 Jellyfin 忽略（安全策略），那种情况下条目根本不会有远端媒体源，插件也无从挂载预览。
* 文件里可以有注释行（以 `#` 开头）和空行，Jellyfin 取**第一个有效行**作为直链。
* 瓦片图片格式只认 **`.jpg`**（`.jpeg` / `.png` 不会被识别）。

### 3.1 命名规则

```
<媒体目录>/<媒体文件名>.trickplay/<缩略图宽> - <每行张数>x<每列张数>/<序号>.jpg
```

* **`<媒体文件名>`** = `.strm` 去掉扩展名，中间的点要保留：
  `流浪地球2.strm` → `流浪地球2.trickplay`；
  `Show.S01E01.1080p.strm` → `Show.S01E01.1080p.trickplay`（⚠️ 不是 `Show.S01E01.trickplay`）
* **`<缩略图宽>`** = 单张预览图的宽度（px），例如 `320`；
* **`<每行张数>x<每列张数>`** = 一张瓦片图里横向 / 纵向拼了多少张缩略图，例如 `10x10` 表示 100 张拼一张；
* **`<序号>`** = 瓦片序号，从 **`0.jpg`** 开始，按**数字**排序。

### 3.2 完整示例

**电影（单分辨率）**

```
📁 Movies/
└── 📁 流浪地球2 (2023)/
    ├── 流浪地球2 (2023).strm                  ← 内容：https://pan.example.com/xxx.mkv
    └── 📁 流浪地球2 (2023).trickplay/
        └── 📁 320 - 10x10/
            ├── 0.jpg                          ← 100 张缩略图拼成 1 张瓦片
            ├── 1.jpg
            └── ...
```

**剧集**

```
📁 TV/Show (2024)/Season 01/
├── Show.S01E01.1080p.strm
├── 📁 Show.S01E01.1080p.trickplay/320 - 10x10/0.jpg, 1.jpg, ...
├── Show.S01E02.1080p.strm
└── 📁 Show.S01E02.1080p.trickplay/320 - 10x10/0.jpg, 1.jpg, ...
```

**多分辨率（可选，客户端会自动挑一档）**

```
📁 流浪地球2 (2023).trickplay/
├── 📁 320 - 10x10/0.jpg, 1.jpg, ...
└── 📁 480 - 10x10/0.jpg, 1.jpg, ...
```

**同一个影片的多个版本**：每个 `.strm` 配自己的侧车目录，互不影响。

### 3.3 会被识别的布局（解析规则）

| 布局 | 是否识别 | 说明 |
|---|---|---|
| `<名字>.trickplay/320 - 10x10/*.jpg` | ✅ 主用法 | 与 Jellyfin 原生 `SaveTrickplayWithMedia` **完全一致** |
| `<名字>.trickplay/320/*.jpg` | ✅ | 目录名只有宽度时，瓦片网格用插件默认值 `10x10`（可配） |
| `<名字>.trickplay/*.jpg` | ✅ | 瓦片直接放在侧车根目录，宽度/网格取插件默认值 |
| `<名字>.trickplay/480 - 10x10/…` 与 `320 - 10x10/…` 并存 | ✅ | 多个宽度全部上报，客户端自动挑 ≤ 屏幕宽 20% 的那一档 |
| 目录名为 `trickplay`（而非 `<名字>.trickplay`） | ✅ | 默认开启，可在配置里去掉 |
| 目录名 `320 - 10x10` 里带空格差异（`320-10x10`） | ✅ | 空白字符不敏感 |
| 同目录任意 `*.trickplay` | ⚙️ | 默认关闭（防止误匹配），媒体被改名后可开启 |
| `tile_001.jpg`、`chapter1.jpg` 这类非纯数字文件名 | ⚠️ | 不会崩，但会排在所有纯数字文件**之后**；建议改成 `0.jpg`、`1.jpg` |
| `.jpeg` / `.png` / `.webp` | ❌ | 只识别 `.jpg` |
| `<名字>.trickplay/320 - 10x10/0.jpeg` | ❌ | 同上 |

> 瓦片目录里的图片从哪来？两种都行：① Jellyfin 自己生成的（库设置里勾选 *将 trickplay 图片保存在媒体文件夹*，它就会写成这个布局）；
> ② 任何第三方工具生成的同布局文件。

### 3.4 插件会读取哪些信息、怎么推算

| 字段 | 来源 |
|---|---|
| 缩略图宽 `Width` | 瓦片子目录名（如 `320 - 10x10` 里的 320） |
| 瓦片网格 `TileWidth` / `TileHeight` | 子目录名（缺失时用插件默认值 10x10） |
| 缩略图高 `Height` | **读瓦片图的真实像素高 ÷ 每列张数**（读不到时按 16:9 估算） |
| 缩略图间隔 `Interval` | 能用视频时长时，采用与 Jellyfin 官方 12.2 相同的夹逼算法并优先服务器配置值；**strm 的时长通常探测不到，此时直接用服务器 `TrickplayOptions.Interval`（默认 10 秒）** |
| 缩略图总数 `ThumbnailCount` | 同上推算（只影响播放列表尾部时长） |
| 码率 `Bandwidth` | 由瓦片文件大小与间隔估算 |

**间隔不准怎么办？** 如果你的瓦片是用非 10 秒间隔生成的，悬停时缩略图位置会偏移。
在 **控制台 → 插件 → STRM Trickplay → 强制缩略图间隔（毫秒）** 填上真实值即可（例如 `2000`）

### 3.5 ❌ 常见错误布局

```
✗ 流浪地球2.trickplay/320x10x10/0.jpg      # 分隔符必须是 " - "（减号），网格用 "x"
✗ 流浪地球2.trickplay/320 - 10X10/0.jpg    # 网格用小写 "x"（大写 X 不识别）
✗ 流浪地球 2.trickplay/                    # 目录名与 strm 主文件名不一致（多了空格）
✗ 流浪地球2.trickplay/tile_0.jpg           # 有瓦片但排序靠后；且缺网格信息，走默认 10x10
✗ 流浪地球2.trickplay/320 - 10x10/0.jpeg   # 只认 .jpg
```

### 3.6 三分钟自检：确认你的目录被正确识别

先拿到条目 id（Web 打开该影片，URL 里 `?id=<32位hex>` 那串就是），然后：

```bash
curl "http://<服务器>:8096/Plugins/StrmTrickplay/Item/<32位itemId>" \
     -H "Authorization: MediaBrowser Token=\"<APIKey>\""
```

重点看三个字段：

* `isShortcut` —— 必须是 `true`（否则它不是真正的 strm 条目）；
* `candidateRoots` —— 插件找了哪些目录，命中的那个 `exists` 必须是 `true`；
* `parsedTileSets` —— 解析出的宽度 / 网格 / 瓦片数 / 间隔；为空说明目录名或文件不符合 3.1~3.3 的规则。

---

## 4. 配置项（控制台 → 插件 → STRM Trickplay）

| 参数 | 默认 | 说明 |
|---|---|---|
| 启用插件 | 开 | 关闭后完全透传核心行为（strm 将没有预览） |
| 侧车目录名 | `{name}.trickplay`, `trickplay` | `{name}` = 媒体主文件名；可自行增删 |
| 允许任意 `*.trickplay` 兜底 | 关 | 媒体被改名后仍能找到瓦片 |
| 兜底宽度 / 瓦片网格 | 320 / 10x10 | 目录名里没有这些信息时使用 |
| **强制缩略图间隔** | 0（自动） | 自动推断与官方 12.2 算法一致；**显式填值则以你为准**（预览位置偏移时用这个校准） |
| 命中缓存 | 600 秒 | 有时长信息时的索引缓存 |
| 未命中缓存 | 60 秒 | 没有瓦片时的负缓存 |
| 未知时长缓存 | 300 秒 | 时长未知时用短缓存，时长一旦可知就自动重算间隔 |
| 详细日志 | 关 | 排查时打开 |
| 允许生成器抽帧 | 关 | 预留位，当前只读取现成文件，不会用 ffmpeg 抽帧 |

另有计划任务 **「刷新 STRM Trickplay 索引」**（默认每天 03:00），用于媒体目录被外部工具批量改动后重建缓存；
平时的改动靠媒体库事件自动失效，新增文件立即生效。

---

## 5. 故障排查

| 症状 | 可能原因 | 处理 |
|---|---|---|
| 控制台看不到插件 / 日志报 ABI 不匹配 | 服务端版本不是 12.2.x | 按你的版本重新编译并同步改 `plugin.json` 的 `targetAbi` |
| `/Plugins/StrmTrickplay/*` 返回 404 | 插件未加载或未重启 | 看日志有无 `[StrmTrickplay]` 前缀 |
| `candidateRoots[].exists` 全为 false | 目录名不是 `<媒体名>.trickplay` | 改成一致的名字，或打开"允许任意 `*.trickplay` 兜底" |
| `parsedTileSets` 为空 | 目录里没有 `.jpg`，或子目录名不符合规范 | 对照 3.1~3.3；子目录至少要能看出宽度 |
| DTO 里 `Trickplay` 仍为空 | 该条目 `isShortcut=false`（不是真 strm），或插件未生效 | 看诊断接口的 `isShortcut`，并确认日志里的装饰器起始行 |
| 有图但缩略图位置对不上 | 间隔推断不准（strm 时长探测不到） | 在配置里填"强制缩略图间隔" |
| 预览气泡尺寸怪异 | 瓦片图高度读取失败，走了 16:9 兜底 | 看自检返回的 `height`：正常应为 瓦片高 ÷ 每列张数 |
| 悬停完全没反应，但接口都正常 | 客户端缓存 | 强制刷新页面（Ctrl+F5）后重试 |

日志关键字：`[StrmTrickplay]`

---

## 6. 已知限制

1. **不生成瓦片**：只读取现成文件。想要"插件自己用 ffmpeg 抽帧"需要另外实现（接口已预留 `ITrickplayGenerator`）。
2. **间隔靠推断**：瓦片目录里没有"每张缩略图间隔"的元数据，strm 的远端时长在 12.2 上通常探测不到，此时取服务器 `TrickplayOptions.Interval`；偏差时用"强制缩略图间隔"校准。
3. **HLS 图像流需要用时长**：核心只在非"分段直播"时才往主播放列表注入 `#EXT-X-IMAGE-STREAM-INF`，而时长未知的远端 strm 会被当成分段直播 —— **Web 客户端不受影响**（走 DTO），依赖 HLS 图像流的 AVPlayer 系原生客户端拿不到缩略图。
4. **不写 Jellyfin 数据库**：索引只在内存中，重启后按需重扫；卸载无残留。
5. **覆盖核心单例服务属于非官方扩展点**：Jellyfin 大版本升级后需按新接口重新编译（编译期即报错，不会静默失效）。
6. 若同时安装了另一个同样覆盖 `ITrickplayManager` 的插件，本插件会把"先前最后一次注册"包成链；以启动日志里的"回退实现"为准。

---

## 7. 相关文档与开发

| 文档 | 内容 |
|---|---|
| [CI.md](CI.md) | 自动化构建与发版：push 新版本自动出 Release（zip + MD5/SHA256）、存储库清单如何生成、本地复现命令 |
| [TEST-REPORT.md](TEST-REPORT.md) | 在真实 Jellyfin 12.2.0 上的实测记录（命令与原始输出） |
| [PLAN.md](PLAN.md) | 方案设计与源码级依据 |

本机开发：

```powershell
.\build.ps1                 # 编译 + 打包（版本取自 plugin.json，产物在 dist/）
.\.dotnet\dotnet.exe run --project .\tests\ParserSmokeTest\ParserSmokeTest.csproj -c Release   # 解析器冒烟测试
```

目录结构：

```
Jellyfin.Plugin.StrmTrickplay/
├── Plugin.cs                        主插件类
├── PluginServiceRegistrator.cs      ★ 装饰 ITrickplayManager 的 DI 注册
├── Api/StrmTrickplayController.cs   诊断 / 自检接口
├── Configuration/                   配置 + 配置页
├── EventHandlers/                   媒体库事件监听（缓存失效）
├── Tasks/                           计划任务（重建索引）
└── Trickplay/
    ├── StrmTrickplayManager.cs      ★ 只覆盖读路径，其余透传
    ├── SidecarTrickplayParser.cs    ★ 目录规范解析（与官方算法一致）
    ├── SidecarTrickplayIndex.cs     索引 + 缓存 + 单飞锁
    ├── HlsPlaylistBuilder.cs        trickplay m3u8 生成
    └── ITrickplayGenerator.cs       生成器扩展点（预留）
```
