# 实测报告 · STRM Trickplay 0.1.0.0（Jellyfin 12.2.0）

本报告记录插件在**真实 Jellyfin 12.2.0 服务端**上的验证过程与结果（不是纸面推演）。

## 0. 测试环境

| 项 | 值 |
|---|---|
| Jellyfin | **12.2.0**（官方 Windows portable 自包含包，自带 runtime 与 ffmpeg 8.1.3） |
| 插件 | Jellyfin.Plugin.StrmTrickplay **0.1.0.0**（targetAbi 12.2.0.0） |
| 测试媒体 | `H:\...\.testmedia\Movies\Sample Movie (2026)\Sample Movie (2026).strm` → `http://127.0.0.1:8899/sample.mkv`（真实 10 分钟 MKV，160x90 瓦片 JPEG 三张） |
| 瓦片目录 | `Sample Movie (2026).trickplay\320 - 10x10\{0,1,2}.jpg` |

## 1. 验证结果总览

| # | 验证项 | 命令 / 触发方式 | 实测结果 | 结论 |
|---|---|---|---|---|
| 1 | 插件被加载 | 重启后看日志 | `Loaded assembly "Jellyfin.Plugin.StrmTrickplay, Version=0.1.0.0"` | ✅ |
| 2 | **DI 覆盖生效** | 日志 | `[StrmTrickplay] ITrickplayManager 装饰器已生效，回退实现: "Jellyfin.Server.Implementations.Trickplay.TrickplayManager"` | ✅ 核心实现被正确包住 |
| 3 | 事件监听 | 日志 | `[StrmTrickplay] 已挂接媒体库变更事件（ItemAdded / ItemUpdated / ItemRemoved）` | ✅ |
| 4 | 计划任务注册 | 日志 | `Daily trigger for "刷新 STRM Trickplay 索引" set to fire at 2026-10-18 03:00:00` | ✅ |
| 5 | **DTO 下发 trickplay** | `GET /Users/{uid}/Items/{id}?Fields=Trickplay` | `{"04a942a8...":{"320":{"Width":320,"Height":9,"TileWidth":10,"TileHeight":10,"ThumbnailCount":300,"Interval":10000,"Bandwidth":6}}}` | ✅ **核心原本恒为空，现在有数据** |
| 6 | 媒体源确实是 remote | 同上（MediaSources） | `id=04a942…, remote=True, proto=Http` | ✅ 证明确实绕过了核心的 remote 过滤 |
| 7 | 瓦片图端点 | `GET /Videos/{id}/Trickplay/320/0.jpg?MediaSourceId={id}` | `200 image/jpeg`（631 bytes，来自侧车目录） | ✅ |
| 8 | trickplay HLS 播放列表 | `GET /Videos/{id}/Trickplay/320/tiles.m3u8?MediaSourceId={id}` | `200`，内容：`#EXTM3U / #EXT-X-TARGETDURATION:3 / #EXT-X-IMAGES-ONLY / #EXTINF:1000, / #EXT-X-TILES:RESOLUTION=320x9,LAYOUT=10x10,DURATION=10 / 0.jpg?...` ×3 / `#EXT-X-ENDLIST` | ✅ 与核心格式一致 |
| 9 | 自检接口 | `POST /Plugins/StrmTrickplay/SelfTest` | `parsedCount:1, Width:320, Height:9, ThumbnailCount:300, Interval:2000, tileFileCount:3` | ✅ 解析器 + 图像尺寸读取正常 |
| 10 | 条目诊断接口 | `GET /Plugins/StrmTrickplay/Item/{id}` | `isShortcut=True`；`candidateRoots[0].exists=True`；`parsedTileSets` 1 套；`cachedResolutions` 有值 | ✅ |
| 11 | 状态接口 | `GET /Plugins/StrmTrickplay/Status` | `trickplayManagerType: …StrmTrickplayManager`、`cachedItems:1` | ✅ |
| 12 | 解析器单元冒烟 | `dotnet run tests/ParserSmokeTest` | **21/21 断言通过**（标准布局/仅宽度/扁平/非顺序命名/缺失/兜底名/无时长/强制覆盖/长时长夹逼） | ✅ |

## 2. 关键日志证据

```
[INF] Emby.Server.Implementations.Plugins.PluginManager: Loaded assembly "Jellyfin.Plugin.StrmTrickplay, Version=0.1.0.0, ..."
[INF] Jellyfin.Plugin.StrmTrickplay.Trickplay.StrmTrickplayManager: [StrmTrickplay] ITrickplayManager 装饰器已生效，回退实现: "Jellyfin.Server.Implementations.Trickplay.TrickplayManager"
[INF] Jellyfin.Plugin.StrmTrickplay.EventHandlers.LibraryChangeListener: [StrmTrickplay] 已挂接媒体库变更事件（ItemAdded / ItemUpdated / ItemRemoved）
[INF] Emby.Server.Implementations.ScheduledTasks.TaskManager: Daily trigger for "刷新 STRM Trickplay 索引" set to fire at 2026-10-18 03:00:00
[INF] Jellyfin.Plugin.StrmTrickplay.Trickplay.SidecarTrickplayIndex: [StrmTrickplay] "Sample Movie (2026).strm" 命中 1 个分辨率（时长未知，interval 取服务器配置值）: "320 - 10x10 (3 tiles, 300 thumbs, 10000ms) @ …"
```

## 3. 实测中发现的两件事（重要）

### 3.1 strm 的时长（RunTimeTicks）在 12.2 上不会自动填充

实测中，即使 strm 指向的是一个**完全可访问、可 ffprobe 的 http 视频**，条目的 `RunTimeTicks` 仍然为空，
Jellyfin 也没有为它发起远端探测。带来两个后果：

1. 插件只能退回"服务器配置的 interval"（`TrickplayOptions.Interval`，默认 10000ms）来标注缩略图间隔。
   如果你的瓦片本来就是 Jellyfin 用同一套配置生成的，那这个值就是**准确的**；
   如果瓦片来自别的工具（间隔不同），预览位置会偏 —— 此时用插件配置里的 **"强制间隔"** 手动指定即可。
2. 因此插件新增了 `UnknownRuntimeCacheSeconds`（默认 300 秒）：时长未知时用短 TTL，
   一旦时长变得可知（例如你手动刷新元数据后），下一次查询就会自动按官方夹逼算法重算间隔。

### 3.2 HLS 主播放列表的 `#EXT-X-IMAGE-STREAM-INF` 需要条目有时长

核心的注入条件是 `!isLiveStream && EnableTrickplay`（`DynamicHlsHelper.cs:336`），
而 `isLiveStream = state.IsSegmentedLiveStream`。时长未知的远端 strm 会被当成"分段直播"，
于是主播放列表里不会出现图像流（实测：`master.m3u8` 返回 200，但只有 `#EXT-X-STREAM-INF`）。

- **对 Web 客户端无影响**：jellyfin-web 走的是 DTO（`item.Trickplay[mediaSourceId]`），已实测有数据；
- 对依赖 HLS 图像流的原生客户端（AVPlayer 系）则拿不到，这属于核心行为，插件不改变它；
- 如果你的 strm 条目能拿到时长，这条链路也会自动具备。

## 4. 复现步骤（照抄即可）

```bash
# 0) 安装插件后重启，确认日志出现 "装饰器已生效"

# 1) 自检（不需要真实媒体）
curl -X POST "http://<server>:8096/Plugins/StrmTrickplay/SelfTest" -H "Authorization: MediaBrowser Token=\"<token>\""

# 2) 拿条目 id
curl "http://<server>:8096/Users/<userId>/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Trickplay" \
     -H "Authorization: MediaBrowser Token=\"<token>\""

# 3) 核心验证：DTO 里必须有 Trickplay
curl "http://<server>:8096/Users/<userId>/Items/<itemId>?Fields=Trickplay" -H "Authorization: MediaBrowser Token=\"<token>\""

# 4) 瓦片与播放列表
curl -I "http://<server>:8096/Videos/<itemId>/Trickplay/320/0.jpg?MediaSourceId=<itemId>" -H "Authorization: MediaBrowser Token=\"<token>\""
curl    "http://<server>:8096/Videos/<itemId>/Trickplay/320/tiles.m3u8?MediaSourceId=<itemId>" -H "Authorization: MediaBrowser Token=\"<token>\""
```

## 5. 尚未验证的部分（需要你来做）

1. **浏览器里悬停进度条的实际观感**：本机实测只覆盖到 API 层（DTO / 瓦片 / 播放列表），
   浏览器渲染需要人来确认。若 DTO 有数据、瓦片 200，悬停就应出图。
2. **你自己的目录规范**：实测用的是 `<文件名>.trickplay/320 - 10x10/*.jpg`。
   如果你的实际目录名/命名有出入，用 `GET /Plugins/StrmTrickplay/Item/{id}` 看 `candidateRoots[].exists` 与 `parsedTileSets` 即可定位。
3. **原生客户端**（Infuse/Swiftfin 等）：见 3.2。
4. 大库性能（上千条目）与网盘挂载（fuse/符号链接）场景。
