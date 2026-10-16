# strm-trickplay-for-Jellyfin 可行方案（v1）

> 目标：让 Jellyfin 里的 `.strm` 条目（网盘/远端直链）使用**同级目录里已经生成好的 `影片名.trickplay/` 目录**，在客户端播放时显示进度条缩略图预览（trickplay）。
> 目标环境：Jellyfin **12.2**（.NET 10），目录规范为 Jellyfin 原生 `SaveTrickplayWithMedia` 布局 `影片名.trickplay/<宽> - <瓦片宽>x<瓦片高>/<序号>.jpg`。
> 本期范围：**只读取现成文件**，不做 ffmpeg 抽帧；但预留生成器扩展点。

---

## 0. 结论摘要

**可行，且只需一个关键介入点：替核心的 `ITrickplayManager` 做一层装饰（decorator），把 `.strm` 条目被上游刻意跳过的这部分数据补回去。**

其余是工程活：解析侧车目录 → 缓存索引 → 事件失效 → 配置页 → 打包发布。工作量约 **3.5 人日**（M0–M4），其中真正有技术风险的部分集中在 M1（半天内可用 PoC 验证）。

---

## 1. 问题根因（源码级证据，均已对 v12.2 发布版核实）

| # | 事实 | 位置 |
|---|---|---|
| 1 | `.strm` 条目被标记为 shortcut | `Emby.Server.Implementations/Library/Resolvers/BaseVideoResolver.cs:147` → `video.IsShortcut = extension.Equals(".strm")` |
| 2 | strm 内是 http(s) 直链时，它的 MediaSource 被标记 `IsRemote = true` | `MediaBrowser.Controller/Entities/BaseItem.cs:1239-1250`（非 File 协议才赋值，本地路径被安全策略忽略） |
| 3 | **核心的 trickplay 清单会跳过 remote source** → `BaseItemDto.Trickplay` 恒为空 | `Jellyfin.Server.Implementations/Trickplay/TrickplayManager.cs:746` `if (mediaSource.IsRemote \|\| !Guid.TryParse(...)) continue;` |
| 4 | 12.2 已经能"发现磁盘上现成的 `.trickplay` 目录"，但它只由 `RefreshTrickplayDataAsync` 触发 | 同文件 `:140 DiscoverExistingTrickplayAsync`，调用点在 `:296` |
| 5 | 而 `RefreshTrickplayDataAsync` 开头就被 `CanGenerateTrickplay` 挡掉 —— **它明确排除 shortcut** | 同文件 `:285` 调用，`:650` `if (video.IsShortcut) return false;` |
| 6 | Web 客户端预览只认 DTO 清单：`item.Trickplay[mediaSourceId]` | jellyfin-web `src/apps/legacy/controllers/playback/video/index.js:144`、`:1517`（拼 `Videos/{id}/Trickplay/{width}/{index}.jpg`） |
| 7 | **插件的服务注册发生在核心注册之后** → 后注册者胜出，可以覆盖单例服务 | `Emby.Server.Implementations/ApplicationHost.cs:490-492`（先 `RegisterServices` 再 `_pluginManager.RegisterServices`）；核心唯一注册点 `Jellyfin.Server/CoreAppHost.cs:89` |

**推论**：
- 你把文件放到 `影片名.trickplay/320 - 10x10/0.jpg` 是**对的**——这正是 Jellyfin 自己的命名规范，但 12.2 对 strm **既不会生成、也不会发现、更不会下发**。
- 单纯"把数据塞进 Jellyfin 数据库"（调用 `SaveTrickplayInfo`）**没用**：第 3 条会在出口把 remote source 丢掉。
- 唯一干净的做法是让 `ITrickplayManager` 的**读路径**（清单/瓦片路径/播放列表）看见 strm。这正是本方案。

---

## 2. 总体架构

```
                 ┌──────────────────────────── Jellyfin 12.2 进程 ────────────────────────────┐
  jellyfin-web   │  TrickplayController ──┐                                                  │
  播放中悬停进度条 │  DtoService ───────────┼──► ITrickplayManager ──► StrmTrickplayManager ──┐    │
  HLS 客户端      │  DynamicHlsHelper ─────┘        (插件装饰实现)      │  ①strm 命中侧车索引 │
                 │                                                  │  ②其余全部透传 ▼    │
                 │                                       核心 TrickplayManager（原注册实例） │
                 │                                                  │                      │
                 │                                        Jellyfin DB / 默认目录            │
                 │                                                                          │
                 │  StrmTrickplayManager ◄── ISidecarTrickplayIndex（内存缓存 + 懒探测）      │
                 │                                   ▲                                      │
                 │  LibraryEvents(Added/Updated/Removed) ── 失效                       │
                 │  ScheduledTask「重建 strm trickplay 索引」 ── 全量重建                  │
                 └──────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
                    媒体目录：  电影A.strm   电影A.trickplay/320 - 10x10/0.jpg, 1.jpg ...
```

设计要点：
1. **只做读路径的补丁**，写路径（生成/迁移/删除 DB）一律透传核心，因此不会破坏现有行为，也不写 Jellyfin 数据库。
2. **只对 `IsShortcut`（strm）条目介入**，本地媒体行为零变化；将来上游若取消 remote 过滤，本插件自动退化为空操作。
3. 索引**懒探测 + 缓存 + 事件失效**，保证 DtoService 的同步调用在微秒级返回（`DtoService.cs:1451` 是 `GetAwaiter().GetResult()` 的同步阻塞调用，这里绝不能做重 IO）。

---

## 3. 详细设计

### 3.1 侧车目录规范与解析

识别顺序（可配置，默认按此序）：

1. `<媒体目录>/<strm 主文件名>.trickplay/`（**你的布局**，等价于 `Path.ChangeExtension(item.Path, ".trickplay")`）
2. `<媒体目录>/trickplay/`（兜底，可关）
3. 可选：同目录下任意 `*.trickplay`（默认关，防止同名冲突）

子目录命名解析：

| 形式 | 处理 |
|---|---|
| `320 - 10x10` | 主规则：`^(?<w>\d+)\s*-\s*(?<tw>\d+)x(?<th>\d+)$` |
| `320` | 兜底规则：瓦片网格取插件配置默认值（10x10） |
| 其它 | 忽略并记 debug 日志 |

每套瓦片集生成一个 `TrickplayInfo`：

- `Width` = 目录名里的宽；`TileWidth/TileHeight` = 目录名里的网格
- `Height` = `ceil(瓦片图实际像素高 / TileHeight)`，用 `IImageEncoder.GetImageSize()`（`MediaBrowser.Controller/Drawing/IImageEncoder.cs`）
- `Interval` / `ThumbnailCount`：**直接照抄官方 12.2 `DiscoverExistingTrickplayAsync` 的推断算法**（`TrickplayManager.cs:214-246`）——用 `RunTimeTicks` 上下界夹逼，命中服务器 `TrickplayOptions.Interval` 就用它，否则取中点并按秒取整。好处：将来上游自己发现这些目录时，行为与插件一致，不会出现"换个客户端预览错位"。
- `Bandwidth` = `max(ceil(tileBytes*8 / (tw*th) / (interval/1000)))`（同官方）
- 瓦片文件按 `*.jpg` 收集并按**数字序号**排序（官方是 Ordinal 字符串序，我们按数字更稳，两者对 `0..99` 等价）

> 预留：若侧车根目录存在 `trickplay.json`（`{ "interval": 2000, "tiles": [...] }`），以清单为准。这是给"以后换生成工具"留的口子，本期只留读取分支。

### 3.2 装饰 `ITrickplayManager`

对 12.2 接口的 10 个成员逐一处置：

| 成员 | 处置 |
|---|---|
| `GetTrickplayManifest(BaseItem)` | **覆盖**：先取核心结果，再对 strm 条目把侧车解析结果按 `mediaSource.Id` 键补进去（**刻意不跳过 `IsRemote`**）。这是预览生效的关键一行 |
| `GetTrickplayResolutions(Guid)` | **覆盖**：索引命中返回侧车，否则透传（HLS 主播放列表注入 `#EXT-X-IMAGE-STREAM-INF` 依赖它，见 `DynamicHlsHelper.cs:339/736`） |
| `GetTrickplayTilePathAsync(item,width,index,saveWithMedia)` | **覆盖**：strm 命中索引直接返回 `<侧车>/<w> - <tw>x<th>/<index>.jpg`；**忽略 `saveWithMedia` 参数**（该参数来自库选项，与文件真实位置无关，控制器里就是这么传的） |
| `GetHlsPlaylist(itemId,width,apiKey)` | **覆盖**：命中索引时按核心同款格式自建 m3u8（约 60 行，直接移植）；否则透传 |
| `GetTrickplayDirectory(...)` | **覆盖**：strm 命中索引返回真实目录，否则透传 |
| `RefreshTrickplayDataAsync` | 透传（核心对 shortcut 已自动 no-op；预留生成器钩子，见 3.7） |
| `CreateTiles` | 透传 |
| `GetTrickplayItemsAsync` | 透传 |
| `SaveTrickplayInfo` | 透传 |
| `DeleteTrickplayDataAsync` | 透传 |
| `MoveGeneratedTrickplayDataAsync` | 透传 |

> ⚠️ 注意：装饰实现里持有的是**核心的原始实例**，所以核心内部对 `GetTrickplayResolutions` 的自调用不会回到我们这里——凡是需要我们自己数据的路径（`GetHlsPlaylist`）必须自己实现，不能指望透传。

### 3.3 DI 注册（装饰器 + 保留原注册）

```csharp
// Jellyfin.Plugin.StrmTrickplay/PluginServiceRegistrator.cs
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<ISidecarTrickplayIndex, SidecarTrickplayIndex>();
        services.AddSingleton<ITrickplayGenerator, NoopTrickplayGenerator>();
        services.AddSingleton<StrmTrickplayIndexer>();

        // 核心唯一注册点在 CoreAppHost.cs:89；插件注册在 ApplicationHost.cs:492（之后）
        // => 我们最后注册 => GetRequiredService<ITrickplayManager>() 拿到我们的实现
        var previous = services.LastOrDefault(d => d.ServiceType == typeof(ITrickplayManager));

        services.AddSingleton<ITrickplayManager>(sp => new StrmTrickplayManager(
            fallback: ResolvePrevious(sp, previous)
                      ?? throw new InvalidOperationException("未找到核心 ITrickplayManager 注册"),
            index: sp.GetRequiredService<ISidecarTrickplayIndex>(),
            imageEncoder: sp.GetRequiredService<IImageEncoder>(),
            config: StrmTrickplayPlugin.Instance.Configuration,
            logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<StrmTrickplayManager>()));
    }

    // 兼容"别人也注册过 ITrickplayManager"的情况：实例/工厂/实现类型三种描述符都能包装
    private static ITrickplayManager? ResolvePrevious(IServiceProvider sp, ServiceDescriptor? d) => d switch
    {
        null => null,
        { ImplementationInstance: ITrickplayManager i } => i,
        { ImplementationFactory: { } f } => f(sp) as ITrickplayManager,
        { ImplementationType: { } t } => ActivatorUtilities.CreateInstance(sp, t) as ITrickplayManager,
        _ => null,
    };
}
```

要点：
- **不需要引用 `Jellyfin.Server.Implementations`**（那是宿主程序集，插件不应依赖），用 `ActivatorUtilities.CreateInstance(sp, t)` 按描述符构造即可。
- 捕获的是"我们之前最后一次注册"，所以与其它插件的装饰链**可组合**（谁后加载谁在外层）。
- 启动时打一条 `Information` 日志写明 fallback 类型，便于排查"插件没生效"。

### 3.4 侧车索引与缓存

```csharp
public interface ISidecarTrickplayIndex
{
    IReadOnlyDictionary<int, TrickplayInfo> GetResolutions(Video video);      // width -> info（返回深拷贝）
    IReadOnlyDictionary<int, TrickplayInfo> GetResolutions(Guid itemId);      // HLS 路径用
    string? GetTilePath(Video video, int width, int index);
    string? GetDirectory(Video video, int width, int tileWidth, int tileHeight);
    void Invalidate(Guid itemId);      // 事件触发
    void Clear();                      // 计划任务全量重建前调用
}

public sealed class SidecarTrickplayIndex : ISidecarTrickplayIndex
{
    private sealed record Entry(DateTimeOffset ExpiresAt, Dictionary<int, TrickplayInfo> Resolutions, string Root);
    private readonly ConcurrentDictionary<Guid, Entry> _cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();  // 防并发重复扫描
    private readonly TimeSpan _ttl;              // 配置项，默认 10 分钟
    // GetResolutions: 命中且未过期 -> 返回副本；否则单飞(single-flight)探测一次磁盘并写缓存
}
```

- 缓存值必须是**深拷贝出口**（`TrickplayInfo` 是可变类，调用方/核心可能持有引用）。
- 索引未命中且目录不存在 → 缓存一个"空结果"（短 TTL，如 30s），避免每个 Items 查询都打盘。
- 单飞锁保证同一 item 不会并发扫盘。

### 3.5 生命周期：事件 + 计划任务

- `ILibraryManager.ItemAdded / ItemUpdated`（`MediaBrowser.Controller/Library/ILibraryManager.cs:36/41`）：`ItemChangeEventArgs.Item` 是 shortcut 且路径以 `.strm` 结尾 → `Invalidate(item.Id)`（只失效，不主动扫盘，保证事件回调极快）。
- `ILibraryManager.ItemRemoved`：移除缓存条目。
- `IScheduledTask`「重建 STRM Trickplay 索引」：默认每天凌晨跑，遍历库里所有 shortcut 视频，预扫一遍把结果灌进缓存（`InternalItemsQuery { Recursive = true, IsVirtualItem = false, IncludeItemTypes = [Movie, Episode, Video, MusicVideo] }` 后按 `IsShortcut` 过滤）。用途：媒体目录被外部工具批量改动、但没触发库事件时不用等 TTL。
- 全部回调包 try/catch，异常只记日志，绝不冒泡到库扫描流程。

### 3.6 插件配置（`BasePluginConfiguration` + 配置页）

| 键 | 默认 | 说明 |
|---|---|---|
| `Enabled` | true | 总开关；关掉后装饰实现直接全量透传 |
| `SidecarFolderNames` | `["{name}.trickplay", "trickplay"]` | `{name}` 替换为 strm 主文件名；第二个可整体停用 |
| `DefaultTileWidth/Height` | 10 / 10 | 子目录没有 `- WxH` 时的兜底网格 |
| `IntervalOverrideMs` | 0 | 0 = 用服务器 `TrickplayOptions.Interval` + 官方夹逼算法；>0 = 强制 |
| `CacheSeconds` | 600 | 索引缓存 TTL |
| `VerboseLogging` | false | 打印每次解析结果 |
| `EnableGeneration` | false | 预留（见 3.7） |

配置页：`IHasWebPages` + 内嵌 `configPage.html`（照抄 jellyfin-plugin-template 的做法）。

### 3.7 生成器扩展点（本期不实现）

```csharp
public interface ITrickplayGenerator
{
    bool IsAvailable { get; }
    Task<bool> TryGenerateAsync(Video video, CancellationToken ct);   // 成功返回 true
}
// 本期注册 NoopTrickplayGenerator.IsAvailable => false
// StrmTrickplayManager.RefreshTrickplayDataAsync:
//   if (_config.EnableGeneration && video.IsShortcut && _generator.IsAvailable)
//        return _generator.TryGenerateAsync(...);   // 生成完写回 <name>.trickplay/，索引失效即可
//   return _fallback.RefreshTrickplayDataAsync(...);
```
将来接 ffmpeg（远端抽帧）时只加一个实现类 + 队列/限速/失败重试，不动读路径。

---

## 4. 项目骨架

```
strm-trickplay-for-Jellyfin/
├─ PLAN.md                        ← 本文档
├─ build.yaml                     ← jprm 打包描述（targetAbi/framework/artifacts）
├─ Jellyfin.Plugin.StrmTrickplay/
│  ├─ Jellyfin.Plugin.StrmTrickplay.csproj
│  ├─ Plugin.cs                        // BasePlugin<PluginConfiguration>, IHasWebPages, 静态 Instance
│  ├─ PluginServiceRegistrator.cs      // ★ 3.3
│  ├─ Configuration/
│  │  ├─ PluginConfiguration.cs
│  │  └─ configPage.html
│  ├─ Trickplay/
│  │  ├─ StrmTrickplayManager.cs       // ★ 3.2 装饰实现
│  │  ├─ SidecarTrickplayIndex.cs      // ★ 3.4 索引 + 缓存
│  │  ├─ SidecarTrickplayParser.cs     // ★ 3.1 目录解析（纯函数，好单测）
│  │  ├─ HlsPlaylistBuilder.cs         //   3.2 的 m3u8 生成（移植核心逻辑）
│  │  └─ ITrickplayGenerator.cs + NoopTrickplayGenerator.cs
│  ├─ Tasks/RefreshStrmTrickplayIndexTask.cs
│  └─ EventHandlers/LibraryChangeListener.cs
└─ tests/Jellyfin.Plugin.StrmTrickplay.Tests/   // 解析器/清单合并/缓存失效单测
```

关键构建配置：

```xml
<!-- csproj 要点 -->
<TargetFramework>net10.0</TargetFramework>          <!-- 12.2 的包是 net10.0 组 -->
<Nullable>enable</Nullable>
<GenerateDocumentationFile>true</GenerateDocumentationFile>
<ItemGroup>
  <PackageReference Include="Jellyfin.Controller" Version="12.2.0"><ExcludeAssets>runtime</ExcludeAssets></PackageReference>
  <PackageReference Include="Jellyfin.Model" Version="12.2.0"><ExcludeAssets>runtime</ExcludeAssets></PackageReference>
  <!-- TrickplayInfo 位于 Jellyfin.Database.Implementations.Entities；若编译期解析不到就显式加这一条 -->
  <PackageReference Include="Jellyfin.Database.Implementations" Version="12.2.0"><ExcludeAssets>runtime</ExcludeAssets></PackageReference>
</ItemGroup>
```

```yaml
# build.yaml
name: "STRM Trickplay"
guid: "<生成一个新 GUID>"
version: "0.1.0.0"
targetAbi: "12.2.0.0"      # 必须是运行中的服务端版本，否则 Jellyfin 拒绝加载
framework: "net10.0"
overview: "让 .strm 条目复用同级目录下已生成的 .trickplay 瓦片，在播放时显示进度条预览"
category: "General"
artifacts:
  - "Jellyfin.Plugin.StrmTrickplay.dll"
```

打包/部署：
```powershell
jprm plugin build .                       # 产出 zip
# 或开发期直接拷贝：
Copy-Item .\Jellyfin.Plugin.StrmTrickplay\bin\Debug\net10.0\*.dll "<jellyfin-data>\plugins\StrmTrickplay\" -Force
# 然后 Dashboard -> 重启
```

---

## 5. 可行性验证（建议先做这 3 步，半天内出结论）

1. **DI 覆盖是否生效**：空壳插件只注册装饰器，构造时打日志；重启后在 Dashboard 日志里确认 `fallback type` 是核心 `TrickplayManager`，且 `GetTrickplayManifest` 被调用。
2. **手工造一个侧车目录**：从本地影片（有 trickplay 的）拷一个 `320 - 10x10/` 到某个 strm 同级的 `影片名.trickplay/`，调接口确认清单出现：
   ```bash
   curl -s "http://<host>/Users/<userId>/Items/<itemId>?Fields=Trickplay" \
        -H "Authorization: MediaBrowser Token=<apiKey>" | jq '.Trickplay'
   # 期望：{ "<itemId的N格式>": { "320": { "Width":320,"Height":180,"TileWidth":10,"TileHeight":10,"Interval":10000,"ThumbnailCount":N } } }
   ```
   （若返回空对象，说明第 3 步之前的读路径没打通，可据此定位是注册没生效还是解析没命中。）
3. **瓦片与播放列表**：
   ```bash
   curl -sI "http://<host>/Videos/<itemId>/Trickplay/320/0.jpg?MediaSourceId=<N格式id>"   # 期望 200 image/jpeg
   curl -s  "http://<host>/Videos/<itemId>/Trickplay/320/tiles.m3u8?MediaSourceId=<N格式id>"  # 期望 #EXT-X-TILES 清单
   ```
   最后在 Web 里播放该 strm，悬停进度条看缩略图。

---

## 6. 测试计划

**单测（无服务端依赖）**
- 目录解析：`320 - 10x10` / `320-10x10` / `320` / 非法名 / 空目录 / 非 jpg 
- 序号排序：`0.jpg, 1.jpg, ..., 10.jpg` 顺序正确；`0001.jpg, 0002.jpg` 也正确
- interval/thumbnailCount 夹逼算法：与官方 `DiscoverExistingTrickplayAsync` 的用例对齐（时长 24min/2h/无 RunTimeTicks）
- 缓存：TTL 过期重扫、Invalidate 立即失效、并发 100 次只扫盘 1 次

**集成/手工验收清单**
- [ ] 单版本 strm：DTO 有 Trickplay，Web 悬停有图，HLS m3u8 有 `#EXT-X-IMAGE-STREAM-INF`
- [ ] 多版本（同名不同分辨率）strm：每个 mediaSourceId 各自有清单
- [ ] 本地 mkv 媒体：行为与装插件前完全一致（回归）
- [ ] 侧车目录缺失/损坏 jpg/只有一张瓦片：不抛异常，退化为无预览
- [ ] 同目录两个 strm（`A.strm`、`A.1080p.strm`）：互不串目录
- [ ] 库扫描/元数据刷新（含"替换 trickplay 图像"）不会删掉侧车目录（核心对 shortcut 已 no-op，需实测确认）
- [ ] 1000 条目库：`/Items` 列表查询耗时不因本插件劣化（缓存生效）
- [ ] 关闭插件总开关后完全透传

---

## 7. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| 覆盖核心单例服务属于"非官方扩展点" | Jellyfin 升级接口变动 → 编译期即报错（好事）；运行期行为变化 | 锁定 targetAbi；装饰类**完整实现接口全部成员**，非读路径一律透传；README 写明适配版本 |
| 另一个插件也注册 `ITrickplayManager` | 先注册者被包住/后注册者遮蔽我们 | 注册时捕获"我们之前最后一次注册"并包装成链（3.3）；启动日志打印 fallback 类型；README 说明加载顺序影响 |
| `Interval` 无法从文件读出 | 预览位置偏差（每张图错位） | 与官方 12.2 完全相同的夹逼算法 + 允许 `trickplay.json` 清单覆盖 + `IntervalOverrideMs` 手动强制 |
| `ThumbnailCount` 推断不准 | 只影响 HLS 播放列表的最后一个 tile 时长 | 同上；DTO 客户端只用 Interval/TileW/H/W，不受影响 |
| `DtoService` 同步阻塞调用本插件 | 若做重 IO 会拖慢/卡住 `/Items` | 索引全部走内存缓存（TTL + 事件失效），未命中走单飞探测；绝不在 manifest 里 await 网络/DB |
| 文件系统大小写/挂载（网盘 fuse、符号链接） | 目录判定失败 | 路径比较用 `OrdinalIgnoreCase`；`item.ContainingFolderPath` 已是映射后路径；失败仅记日志 |
| 用户改过目录命名习惯 | 解析不到 | `SidecarFolderNames` 可配 + 默认含 `{name}.trickplay` 与 `trickplay` |
| 不写 Jellyfin DB | 其它直接读 DB 的第三方工具看不到 | 已知：核心内除 TrickplayManager 外无人直接读 `TrickplayInfos`（仅备份/迁移任务）。如需要，后续可加"同步写入 DB"开关，但**不能替代**本装饰（remote 过滤仍在） |

**已排除的替代方案**
- 只写 DB（`SaveTrickplayInfo`）+ 把文件放进默认目录：被 #3 的 remote 过滤挡住，DTO 依旧为空 → 无效。
- 让库把 strm 当本地文件：Jellyfin 明确忽略 strm 里的本地路径 → 不可行。
- 中间件改写 `/Items` 的 JSON 响应：脆弱（流式响应/并发/其它端点），且要重复实现 DTO 逻辑 → 不采用。
- 自建 API + 改 jellyfin-web 前端：只对 Web 生效，原生客户端无效，还要长期维护前端 → 不采用（可作为未来的 UI 增强）。

---

## 8. 里程碑

| 阶段 | 内容 | 交付 | 估时 |
|---|---|---|---|
| M0 | 环境与骨架：net10.0 工程、jprm 打包、部署到 12.2 开发实例、插件被识别加载 | 能装、能打日志 | 0.5 d |
| M1 | **读路径打通**：装饰注册 + 解析器 + manifest/tile/m3u8 覆盖 | 第 5 节 3 条 curl 全绿，Web 悬停出图 | 1.0 d |
| M2 | 索引生命周期：缓存/单飞/事件失效/计划任务 | 新增媒体无需重启即可预览 | 0.5 d |
| M3 | 配置页 + 日志 + 开关 | Dashboard 可配 | 0.5 d |
| M4 | 硬化：单测、异常边界、性能、多版本回归、README/发布 | 可发布 zip | 1.0 d |

---

## 9. 附：关键源码位置速查（v12.2）

- 接口：`MediaBrowser.Controller/Trickplay/ITrickplayManager.cs`（10 个成员，含 `DeleteTrickplayDataAsync`）
- 核心实现：`Jellyfin.Server.Implementations/Trickplay/TrickplayManager.cs`
  - `:140` 现成目录发现（**可作为我们解析算法的抄写对象**）
  - `:746` remote 过滤（要绕过的那一行）
  - `:650` shortcut 排除（strm 不生成的原因）
  - `:763` 瓦片路径 = `GetTrickplayDirectory(...)/<index>.jpg`
  - `:848` 目录 = `<TrickplayPath>/<id前2位>/<id>/<w> - <tw>x<th>`（save-with-media 时为 `<目录>/<文件名>.trickplay/...`）
  - `:775` m3u8 生成（移植对象）
- DTO：`MediaBrowser.Model/Dto/TrickplayInfoDto.cs`；`BaseItemDto.Trickplay`（`BaseItemDto.cs:572`，`Dictionary<mediaSourceId, Dictionary<width, TrickplayInfoDto>>`）
- 注入点：`Emby.Server.Implementations/Dto/DtoService.cs:1449-1457`（`Fields=Trickplay` 时调 `GetTrickplayManifest`）
- API：`Jellyfin.Api/Controllers/TrickplayController.cs`（`Videos/{itemId}/Trickplay/{width}/{index}.jpg`、`.../tiles.m3u8`）
- HLS 注入：`Jellyfin.Api/Helpers/DynamicHlsHelper.cs:339,736-762`
- 客户端消费：jellyfin-web `src/apps/legacy/controllers/playback/video/index.js:140-158, 1456-1520`
- 插件服务注册顺序：`Emby.Server.Implementations/ApplicationHost.cs:490-492`；`Jellyfin.Server/CoreAppHost.cs:89`
