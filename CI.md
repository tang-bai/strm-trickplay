# 自动化构建与发布（GitHub Actions）

推送代码后自动产出**可直接给 Jellyfin 使用的插件包**；打 tag 时自动发版并把最新的插件包与仓库清单更新到 `docs/`，Jellyfin 里添加一次存储库地址即可安装与自动更新。

---

## 1. 现在就能用的存储库地址

仓库里的 `docs/` 目录同时存放**插件包**和 `manifest.json`，push 之后下面两个地址都能用（内容完全一样）：

| 方式 | 地址 | 前置条件 |
|---|---|---|
| **raw 直链（推荐，零配置）** | `https://raw.githubusercontent.com/tang-bai/strm-trickplay/main/docs/manifest.json` | 只要 push 过就能用 |
| GitHub Pages（域名更好看） | `https://tang-bai.github.io/strm-trickplay/manifest.json` | 需在 `Settings → Pages` 选 **分支 main / 目录 /docs** |

在 Jellyfin 里：**控制台 → 插件 → 存储库 → 添加**，粘贴上面任一地址，保存后到 **目录** 里安装 *STRM Trickplay*。
验证方法：用浏览器直接打开该地址，应看到一段 JSON 数组（以 `[` 开头）。

---

## 2. 触发规则

| 触发方式 | 版本号 | 产物 |
|---|---|---|
| **任意 push / PR** | `<plugin.json 前三段>.<run_number>`，如 `0.1.0.37` | Workflow artifact：`strm-trickplay_0.1.0.37.zip`（可直接装；不写进 docs/） |
| **推 tag `v0.2.0.0`** | `0.2.0.0` | ① 更新 `docs/`（插件包 + manifest）并提交回主分支 ② 创建 GitHub Release ③ 打印存储库地址 |
| **手动 dispatch** | 可指定；勾选 publish 才发版 | 同上 |

流程里有三道质量闸门：**还原依赖 → 解析器冒烟测试（21 项断言）→ 打包后回读校验**（zip 内 `meta.json` 版本、zip 完整性、DLL 版本、MD5/SHA256）。

> 说明：`docs/` 是唯一的分发通道（zip + manifest 同目录），所以不依赖 Release 是否存在；Release 只是额外提供变更记录与下载入口。
> 每次发版 `docs/` 只增加约 35 KB。

---

## 3. 首次启用（发版前只需两步）

1. **给 Actions 写权限**：仓库 `Settings → Actions → General → Workflow permissions` 选 **Read and write permissions**（发布时要提交 `docs/` 并创建 Release）。
2. **（可选）开启 Pages**：`Settings → Pages` → Source 选 *Deploy from a branch* → 分支 **main**、目录 **/docs**。
   不开也能用 raw 直链，只是地址长一点。

> 想立刻安装而不发版：当前 `docs/` 里已经有 0.1.0.0 的包，push 后直接添加存储库地址安装即可。

---

## 4. 版本号怎么定

| 位置 | 作用 |
|---|---|
| `plugin.json` → `version` | 基准版本（本地 `build.ps1` / `ci/package.py` 也用它） |
| tag `v0.2.0.0` | 发版版本，优先级最高 |
| CI 自动 | 非 tag 的 push 用 `<基准前三段>.<run_number>`，保证单调递增、便于区分每次构建 |

版本一律规范化为 **4 段**（Jellyfin 内部用 `System.Version` 解析，`0.2.0` 会补成 `0.2.0.0`）。
发新版推荐流程：改 `plugin.json` 的 version → commit → 打同名 tag 推送：

```bash
git tag v0.2.0.0 && git push origin v0.2.0.0
```

---

## 5. 本地复现

```powershell
# 编译 + 打包（版本默认取 plugin.json，产物在 dist/）
.\build.ps1
.\build.ps1 -Version 0.2.0.0

# 等价命令（任何平台）
python ci/package.py --version 0.2.0.0 --changelog "本次改动说明"

# 把包与清单发布到 docs/（CI 发版时做的就是这一步）
python ci/publish.py --version 0.2.0.0 --changelog "本次改动说明"
python ci/publish.py --skip-build          # 复用 dist/ 里已有的包，不重新编译
```

| 产物 | 说明 |
|---|---|
| `dist/strm-trickplay_<版本>.zip` | **可直接安装**：解压到 `<jellyfin-data>/plugins/StrmTrickplay/` |
| `dist/meta.json` | 插件清单（版本 / targetAbi / guid / 程序集） |
| `dist/build-info.json` | 版本、zip 名、大小、MD5、SHA256 |
| `docs/manifest.json` | Jellyfin 存储库清单（历史版本会累积） |
| `docs/strm-trickplay_<版本>.zip` | 存储库指向的插件包 |

单独生成清单（需要自己指定下载地址时）：

```bash
python ci/repo.py --zip docs/strm-trickplay_0.2.0.0.zip --version 0.2.0.0 \
  --out docs/manifest.json --source-url "https://raw.githubusercontent.com/tang-bai/strm-trickplay/main/docs/strm-trickplay_0.2.0.0.zip"
```

---

## 6. 换成别的平台（Gitea / GitLab / Jenkins）

`ci/package.py`、`ci/publish.py`、`ci/repo.py` 只用 Python 标准库，平台无关。要点：

1. 装 **.NET 10 SDK** 与 **Python 3**；
2. 依次执行 `dotnet restore` → `dotnet run --project tests/ParserSmokeTest`（可选但推荐）→ `python ci/package.py --version <v>`；
3. `python ci/publish.py --skip-build --version <v> --base-url <你的静态文件前缀>`，把 `docs/` 发布到任意可公开访问的静态目录。

---

## 7. 常见问题

| 现象 | 原因 / 处理 |
|---|---|
| Jellyfin 里添加存储库后看不到插件 | 地址打错；用浏览器打开该地址应看到以 `[` 开头的 JSON |
| 安装时报 "checksum didn't match" | zip 被改写过；重新发版（校验和是 zip 的 MD5，由 CI 自动写入 manifest） |
| 插件装上但提示 ABI 不匹配 | `plugin.json` 的 `targetAbi` 与服务器版本不一致，改后重新发版 |
| 发版时提交 `docs/` 或创建 Release 报 403 | `Settings → Actions → General → Workflow permissions` 没给写权限 |
| Pages 地址 404 | 没开 Pages，或选错目录（要选 **/docs**）；raw 直链不受影响 |
| 私有仓库 | Jellyfin 无法带凭据下载，存储库必须是公开可访问的地址 |
| 每次 push 的 dev 版本要不要发版 | 默认不发（只做 artifact）；需要时用 `workflow_dispatch` 勾选 publish |
