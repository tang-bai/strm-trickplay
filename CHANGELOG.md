# 更新日志

> **本文件是更新说明的唯一来源。** 发版时 CI 会自动把对应版本的小节写进三个地方：
> ① GitHub Release 说明、② Jellyfin 插件目录里显示的 changelog、③ 构建产物 `meta.json`。
>
> **规则：`plugin.json` 的版本号一旦提升，必须先在这里补好同名小节，否则发版会直接失败**（不会出现"没有说明"的 Release）。
> 普通 push（开发构建）不需要小节，会退化为提交信息。

## 怎么写

```markdown
## [0.4.0.0] - 2026-09-27
### 变更摘要
一句话说明这个版本干了什么（显示在 Jellyfin 插件目录的版本列表里，建议 ≤ 100 字）

### 新增
- 面向用户的新能力

### 改进
- 行为变化、性能、体验

### 修复
- 修掉了什么问题

### 兼容性
- 升级注意事项（可选）
```

* 标题必须是 `## [x.y.z.w] - YYYY-MM-DD`（方括号、`v` 前缀、日期都可省略；版本号 1~4 段等价，`0.4` 与 `0.4.0.0` 视为同一个）
* `### 变更摘要` 可省略：省略时取小节里的第一条列表项
* 上面的示例标题写在代码块里，解析时会被忽略

---

## [0.4.0.0] - 2026-10-17
### 变更摘要
发版说明自动化：每次发布 Release 都会带上完整更新内容，并在构建/发版页面直接显示

### 改进
- 发版必须提供更新说明：`CHANGELOG.md` 缺少对应版本小节时构建直接失败，不再发布"只显示提交信息"的 Release
- GitHub Release 正文、Jellyfin 插件目录的 changelog、`meta.json` 共用同一份说明
- Actions 运行页面（Summary）会直接显示本次版本的更新内容，构建日志里也会打印
- Release 说明末尾自动附上安装方式与该版本 zip 的 MD5 / SHA256
- 支持在 Actions 页面手动回填历史版本的 Release 说明（无需重新编译）：`Run workflow` → `backfill_version` 填版本号
- 重复发布同一版本时（例如手动重跑工作流），Release 说明会同步刷新

---

## [0.3.0.0] - 2026-10-17
### 变更摘要
新增起播加速：媒体信息已完整的 .strm 在点播时跳过 Jellyfin 核心强制的重复远端 ffprobe

### 新增
- **起播加速**：装饰 `IMediaSourceManager`，对"确实是 .strm + 已有对应媒体流 + 已知时长"的条目在播放路径上跳过核心那次强制远端探测；三条缺一不可，其余情况全部原样透传
- 设置页新增「起播加速」开关（默认开启），顶部状态卡显示其状态
- 诊断接口 `/Plugins/StrmTrickplay/Status` 增加 `fastStartup` 字段

### 说明
- Jellyfin 12.2 的 `MediaSourceManager.GetPlaybackMediaSources` 是按**路径后缀**判断的：只要是 .strm，每次点播都会强制 `RefreshMetadata(EnableRemoteContentProbe = true)`，而 `FFProbeVideoInfo.ProbeVideo` 没有"已有信息就跳过"的判断——这是起播慢的主因，也是"只做预提取"感觉没变快的原因

---

## [0.2.0.0] - 2026-10-17
### 变更摘要
集成 STRM 媒体信息探测（ffprobe 补齐时长/编码/分辨率），并重构插件设置页

### 新增
- **STRM 媒体信息探测**：为缺少媒体信息的 .strm 条目执行远端 ffprobe，补齐时长 / 编码 / 分辨率 / 音轨
- 计划任务「探测 STRM 媒体信息」（默认每天 04:00，可手动运行、可取消）
- 新增 .strm 时自动探测（按防抖时间批量处理，默认 30 秒）
- 探测范围可按媒体库多选；并发数 / 单项超时 / 探测冷却均可配置
- 新接口：`GET Probe/Status?count=true`、`POST Probe/Item/{id}`（返回探测前后时长与媒体流数量）、`POST Probe/Run`、`POST Index/Rebuild`
- 探测成功后自动让 trickplay 索引重算：拿到时长后缩略图间隔改用与官方一致的推断算法

### 改进
- 设置页整体重构：5 个分组 + 顶部状态卡（版本 / 缓存条目 / 探测状态 / 最近一次探测结果）+ 一键操作区（自检、统计待探测、立即探测、重建索引）
- 媒体库多选项由 `ApiClient.getVirtualFolders()` 实时渲染；所有数值项标注取值范围与说明

---

## [0.1.0.0] - 2026-10-17
### 变更摘要
首个可用版本：让 .strm 条目复用同级目录的 .trickplay 瓦片，在播放时显示进度条预览

### 新增
- .strm 条目的 trickplay 进度条预览（装饰 `ITrickplayManager`，Web 客户端与依赖 HLS 图像流的客户端均可用）
- 侧车目录解析：`{name}.trickplay` / `trickplay` / 任意 `*.trickplay` 兜底，规范与 Jellyfin 原生 `SaveTrickplayWithMedia` 一致（`<宽> - <每行张数>x<每列张数>/<序号>.jpg`）
- 缩略图间隔推断与 Jellyfin 12.2 官方算法一致，并支持强制指定间隔校准
- 诊断接口与自检：`/Plugins/StrmTrickplay/Status`、`/Plugins/StrmTrickplay/SelfTest`
- 计划任务「刷新 STRM Trickplay 索引」；媒体库事件自动失效缓存
- GitHub Actions 全自动发版：push 后产出可安装 zip，并同步 Jellyfin 插件存储库 manifest
