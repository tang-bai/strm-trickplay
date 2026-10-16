# STRM Trickplay (Jellyfin 12.2 插件 · 测试版 0.1.0.0)

让 Jellyfin 里的 **`.strm` 条目**（网盘/远端直链）复用**同级目录下已经生成好的 `影片名.trickplay` 瓦片目录**，
在客户端播放时显示进度条缩略图预览（trickplay / scrub preview）。

> ✅ **已在真实 Jellyfin 12.2.0 服务端实测通过**：DTO 下发 trickplay、瓦片图 200、trickplay m3u8 正确、DI 覆盖确认生效。
> 完整证据见 [TEST-REPORT.md](TEST-REPORT.md)。

---

## 1. 为什么需要这个插件

Jellyfin 12.2 核心对 strm 有三重阻断（均已对 v12.2 源码核实）：

| 现象 | 原因 | 源码位置 |
|---|---|---|
| strm 的 `BaseItemDto.Trickplay` 恒为空 | 核心 trickplay 清单**跳过 remote 媒体源**，而 strm 的 http 源一定被标记 `IsRemote = true` | `TrickplayManager.cs:746`、`BaseItem.cs:1239-1250` |
| 12.2 明明能"发现磁盘上现成的 `.trickplay` 目录"，却对 strm 不生效 | 触发它的 `RefreshTrickplayDataAsync` 开头就被 `CanGenerateTrickplay` 排除 shortcut | `TrickplayManager.cs:285`、`:650` |
| 所以只把数据写进数据库也没用 | 出口那道 remote 过滤仍然会把数据丢掉 | 同上 |

本插件用一层 **`ITrickplayManager` 装饰器**接管 trickplay 的**读路径**（清单 / 瓦片路径 / HLS 播放列表），
只在 `.strm` 条目上补齐数据，**其余全部透传核心**，不写 Jellyfin 数据库、不改动任何原有行为。

---

## 2. 安装

**方式 A（推荐）**：解压 `dist/strm-trickplay_<版本>.zip`（CI 产物同名），把里面两个文件放进 Jellyfin 数据目录的 `plugins/StrmTrickplay/`：

```
<jellyfin-data>/plugins/StrmTrickplay/
├── Jellyfin.Plugin.StrmTrickplay.dll
└── meta.json
```

**方式 B**：直接拷 `dist/Jellyfin.Plugin.StrmTrickplay.dll`（缺 `meta.json` 时仍会加载，但版本信息不准）。

放好后 **重启 Jellyfin**。Windows 典型路径 `%LOCALAPPDATA%\jellyfin\plugins\`，Docker 为 `/config/plugins/StrmTrickplay/`。

安装成功应能在 **控制台 → 插件** 看到 *STRM Trickplay 0.1.0.0*，且日志出现（实测原文）：

```
[INF] Jellyfin.Plugin.StrmTrickplay.Trickplay.StrmTrickplayManager: [StrmTrickplay] ITrickplayManager 装饰器已生效，回退实现: "Jellyfin.Server.Implementations.Trickplay.TrickplayManager"
[INF] Jellyfin.Plugin.StrmTrickplay.EventHandlers.LibraryChangeListener: [StrmTrickplay] 已挂接媒体库变更事件（ItemAdded / ItemUpdated / ItemRemoved）
```

> ⚠️ 插件必须挂在 `<jellyfin-data>/plugins` 下；`meta.json` 的 `targetAbi` 为 `12.2.0.0`，
> 服务器版本不是 12.2.x 时 Jellyfin 会拒绝加载，需要按你的版本重新编译（见第 8 节）。

---

## 3. 目录要求

`.strm` 同级必须有对应的 `.trickplay` 目录，规范与 Jellyfin 原生 `SaveTrickplayWithMedia` **完全一致**：

```
📁 电影/
├── 流浪地球2.strm
└── 📁 流浪地球2.trickplay/
    └── 📁 320 - 10x10/
        ├── 0.jpg     ← 10x10 = 100 张缩略图拼成的一张瓦片
        ├── 1.jpg
        └── ...
```

兼容的变体（实测均通过）：

| 形式 | 处理 |
|---|---|
| `<名字>.trickplay/320 - 10x10/*.jpg` | 主用法 |
| `<名字>.trickplay/320/*.jpg` | 目录名只有宽度 → 瓦片网格用插件默认值 10x10 |
| `<名字>.trickplay/*.jpg` | 瓦片直接躺在侧车根目录 |
| `<名字>.trickplay/480 - 10x10/…` + `320 - 10x10/…` | 多个宽度全部识别，客户端自动挑一档 |
| 目录名 `trickplay/` | 默认也支持（可配置） |

---

## 4. 测试步骤

### 第 0 步：自检（不需要真实媒体）

```bash
curl -X POST "http://<server>:8096/Plugins/StrmTrickplay/SelfTest" \
     -H "Authorization: MediaBrowser Token=\"<APIKey>\""
```

期望（实测结果）：`parsedCount:1`、`Width:320`、`Height:9`、`tileFileCount:3`、`Interval:2000`。
过了这一条 ⇒ 插件已加载、DI 覆盖生效、解析器正常。

### 第 1 步：确认 DTO 已下发（**最关键**）

```bash
curl "http://<server>:8096/Users/<userId>/Items/<32位itemId>?Fields=Trickplay" \
     -H "Authorization: MediaBrowser Token=\"<APIKey>\""
```

期望看到非空 `Trickplay`（实测原文）：

```json
"Trickplay": {
  "<itemId的N格式>": {
    "320": { "Width": 320, "Height": 180, "TileWidth": 10, "TileHeight": 10,
             "ThumbnailCount": 300, "Interval": 10000, "Bandwidth": 12345 }
  }
}
```

> 装插件前这里是 `{}` —— 这就是整个问题的核心。

### 第 2 步：瓦片与播放列表

```bash
curl -I "http://<server>:8096/Videos/<itemId>/Trickplay/320/0.jpg?MediaSourceId=<itemId>"
curl    "http://<server>:8096/Videos/<itemId>/Trickplay/320/tiles.m3u8?MediaSourceId=<itemId>" \
        -H "Authorization: MediaBrowser Token=\"<APIKey>\""
```

期望：`200 image/jpeg`，以及含 `#EXT-X-IMAGES-ONLY` / `#EXT-X-TILES:RESOLUTION=…,LAYOUT=…,DURATION=…` 的播放列表。

### 第 3 步：Web 端实际观感

播放该 strm，鼠标移到进度条上：出现缩略图气泡即成功；没有则按第 6 节排查。

---

## 5. 参数（控制台 → 插件 → STRM Trickplay）

| 参数 | 默认 | 说明 |
|---|---|---|
| 启用插件 | 开 | 关闭后完全透传核心行为 |
| 侧车目录名 | `{name}.trickplay`, `trickplay` | `{name}` = 媒体主文件名 |
| 允许任意 `*.trickplay` 兜底 | 关 | 媒体被改名后仍能找到 |
| 兜底宽度 / 瓦片网格 | 320 / 10x10 | 目录名里没有这些信息时使用 |
| **强制间隔** | 0（自动） | 自动模式与官方 12.2 夹逼算法一致；**显式填值则以你为准**（推荐在预览位置偏移时使用） |
| 命中缓存 | 600 秒 | 时长已知时的索引 TTL |
| 未命中缓存 | 60 秒 | 没有瓦片时的负缓存 |
| 未知时长缓存 | 300 秒 | 有时长才能算间隔；strm 常常探测不到时长，用短 TTL 以便自动重算 |
| 详细日志 | 关 | 排查时打开 |
| 允许生成器抽帧 | 关 | 预留位，当前无实现 |

另有计划任务 **「刷新 STRM Trickplay 索引」**（每天 03:00），用于媒体目录被外部工具批量改动后重建缓存；
平时靠库事件自动失效，新增文件立即生效。

---

## 6. 故障排查

| 症状 | 可能原因 | 处理 |
|---|---|---|
| 看不到插件 / 日志报 ABI 不匹配 | 服务器版本不是 12.2.x | 告知确切版本，按该版本重编译并同步改 `targetAbi` |
| `/Plugins/StrmTrickplay/*` 404 | 插件未加载或未重启 | 看日志有无 `[StrmTrickplay]` |
| `candidateRoots[].exists` 全 false | 目录名不是 `<媒体名>.trickplay` | 打开"允许任意 `*.trickplay` 兜底"，或自定义目录名 |
| `parsedTileSets` 为空 | 里面没有 `.jpg`，或子目录名不匹配 | 子目录需形如 `320 - 10x10`（只有宽度也行） |
| DTO 里 `Trickplay` 仍为空 | 该条目不是真 strm（`isShortcut=false`），或插件未生效 | 看诊断接口的 `isShortcut` 与日志起始行 |
| 有图但缩略图位置对不上 | interval 推断不准 | 配置里显式填"强制间隔"（毫秒） |
| 预览气泡尺寸怪异 | 瓦片图高度读取失败走了 16:9 兜底 | 看自检 `height` 字段（正常应为瓦片高 ÷ 网格行数） |

日志关键字：`[StrmTrickplay]`

---

## 7. 已知限制

1. **不生成**：只读取现成瓦片；ffmpeg 抽帧接口已预留（`ITrickplayGenerator`），本期不实现。
2. **interval 靠推断**：瓦片目录里没有间隔元数据。strm 的远端时长在 12.2 上通常探测不到（实测），
   此时取服务器 `TrickplayOptions.Interval`；若你的瓦片由别的工具生成且间隔不同，用"强制间隔"指定。
3. **HLS 图像流**：核心只在 `!isLiveStream` 时注入 `#EXT-X-IMAGE-STREAM-INF`，而时长未知的远端 strm 会被当成分段直播，
   因此主播放列表不含图像流 —— **Web 客户端不受影响**（走 DTO），依赖 HLS 图像流的 AVPlayer 系原生客户端拿不到。
4. **不写核心数据库**：索引仅内存，重启后按需重扫；卸载无残留（`.trickplay` 目录原样保留）。
5. **覆盖核心单例服务属于非官方扩展点**：大版本升级后需按新接口重编译（编译期报错，不会静默失效）。
6. 若另有插件同样覆盖 `ITrickplayManager`，本插件会把"先前最后一次注册"包成链；以启动日志的"回退实现"为准。

---

## 8. 自动化构建与发布（CI）

推送到 GitHub 后自动产出**可直接给 Jellyfin 安装**的插件包，详见 [CI.md](CI.md)：

| 触发 | 版本号 | 产物 |
|---|---|---|
| 任意 push / PR | `<plugin.json 前三段>.<run_number>` | Workflow artifact：`strm-trickplay_<版本>.zip`（含 `meta.json`） |
| 推 tag `v0.2.0.0` | `0.2.0.0` | 同上 + GitHub Release + 更新 `gh-pages` 上的 `manifest.json` → Jellyfin 可自动更新 |
| 手动 dispatch | 自定（勾选 publish 才发布） | 同上 |

首次启用三步：① 仓库 `Settings → Actions → General` 权限设为 **Read and write**；② 推一个 tag；③ `Settings → Pages` 选 `gh-pages` 分支。
之后在 Jellyfin 里 **控制台 → 插件 → 存储库** 添加：`https://tang-bai.github.io/strm-trickplay/manifest.json`。

本地等价命令（版本号唯一来源是 [plugin.json](plugin.json)，CI 与本地共用同一套脚本）：

```powershell
# 编译 + 打包（版本默认取 plugin.json，产物在 dist/）
.\build.ps1
.\build.ps1 -Version 0.2.0.0

# 等价的跨平台命令
python ci/package.py --version 0.2.0.0 --changelog "本次改动说明"
python ci/repo.py --zip dist/strm-trickplay_0.2.0.0.zip --version 0.2.0.0 --out staging/manifest.json
```

---

## 9. 自己重新编译

仓库自带 `.dotnet/`（.NET 10 SDK，仅供本地构建）：

```powershell
# 编译 + 打包（产物 dist/strm-trickplay_<版本>.zip 与 dist/meta.json）
.\build.ps1

# 跳过编译，只重新打包
.\build.ps1 -SkipBuild

# 解析器冒烟测试（21 项断言，不需要 Jellyfin 服务端）
.\.dotnet\dotnet.exe run --project .\tests\ParserSmokeTest\ParserSmokeTest.csproj -c Release
```

目录结构：

```
.github/workflows/build.yml          ★ push 即出包；tag 即发版 + 更新插件仓库清单
ci/package.py                        ★ 生成 meta.json 并打包出 zip（跨平台）
ci/repo.py                           ★ 生成 Jellyfin 插件仓库 manifest.json（含 MD5 校验和）
plugin.json                          ★ 唯一版本/元数据来源（version / targetAbi / guid / 描述）
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

---

## 10. 仓库内的测试环境（可选删除，约 1.1 GB）

| 目录 | 内容 | 说明 |
|---|---|---|
| `.dotnet/` | .NET 10 SDK | 重新编译需要，勿删 |
| `.testserver/` | Jellyfin 12.2 实测实例（含 199MB 安装包与解压后的服务端） | 仅本次实测用，可整体删除 |
| `.testmedia/` | 实测用媒体（10 分钟测试视频 + 瓦片） | 可删除 |

想复跑实测实例：

```powershell
# 终端 1：起测试视频的 HTTP 服务
py -m http.server 8899 --bind 127.0.0.1 --directory .\.testmedia\web
# 终端 2：起 Jellyfin（含已安装的插件）
cd .\.testserver\app\jellyfin
.\jellyfin.exe --datadir ..\..\data --configdir ..\..\config --cachedir ..\..\cache
# 管理员账号 admin / admin123，端口 8096
```
