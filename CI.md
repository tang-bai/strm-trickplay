# 自动化构建与发布（GitHub Actions）

推送代码后自动产出**可直接给 Jellyfin 使用的插件包**；打 tag 时额外发布 Release 并更新插件仓库清单，实现 Jellyfin 内一键安装 + 自动更新。

## 1. 它做了什么

| 触发方式 | 版本号 | 产物 |
|---|---|---|
| **任意 push / PR** | `<plugin.json 前三段>.<run_number>`，例如 `0.1.0.37` | Workflow artifact：`strm-trickplay_0.1.0.37.zip` + `meta.json` + `build-info.json` |
| **推 tag `v0.2.0.0`** | `0.2.0.0` | 同上，并且：创建 GitHub Release（附 zip）、更新 `gh-pages` 上的 `manifest.json` |
| **手动 dispatch** | 可指定；勾选 publish 才发布 | 同对应行 |

流程里包含三个质量闸门：**还原依赖 → 解析器冒烟测试（21 项断言）→ 打包后回读校验**（zip 内 `meta.json` 版本必须与期望一致、zip 可正常解压、自动计算 MD5/SHA256）。

## 2. 首次启用（三步）

1. **推送仓库到 GitHub**（Workflow 位于 `.github/workflows/build.yml`，无需改动即可跑）。
2. **给 Actions 写权限**：仓库 `Settings → Actions → General → Workflow permissions` 选 **Read and write permissions**（发布 Release / 推送 gh-pages 需要）。
3. **发布一次并开启 Pages**：推一个 tag，例如
   ```bash
   git tag v0.2.0.0 && git push origin v0.2.0.0
   ```
   跑完后到 `Settings → Pages`，Source 选 **Deploy from a branch**，分支选 **gh-pages** / `/ (root)`。

之后在 Jellyfin 里：**控制台 → 插件 → 存储库 → 添加**

```
https://tang-bai.github.io/strm-trickplay/manifest.json
```

（URL 规则：`https://<用户名>.github.io/<仓库名>/manifest.json`）

再到 **目录** 里安装 *STRM Trickplay*；以后每次发版，Jellyfin 会提示升级。

## 3. 版本号怎么定

| 位置 | 作用 |
|---|---|
| `plugin.json` → `version` | 基准版本（本地 `build.ps1` 也用它） |
| tag `v0.2.0.0` | 发版版本，优先级最高 |
| CI 自动 | 非 tag 的 push 用 `<基准前三段>.<run_number>`，保证单调递增、便于区分每次构建 |

版本一律规范化为 **4 段**（Jellyfin 内部用 `System.Version` 解析，`0.2.0` 会补成 `0.2.0.0`）。
发新版时推荐：改 `plugin.json` 的 version → commit → 打同名 tag 推送。

## 4. 本地复现 CI 产物

```powershell
# 用 plugin.json 里的版本打包（.dotnet SDK 会自动优先使用）
.\build.ps1

# 指定版本 / 跳过编译（复用已有 bin 产物）
.\build.ps1 -Version 0.2.0.0
.\build.ps1 -Version 0.2.0.0 -SkipBuild

# 等价的手工命令（任何平台）
python ci/package.py --version 0.2.0.0 --changelog "修正 interval 推断"
```

产物都在 `dist/`：

| 文件 | 说明 |
|---|---|
| `strm-trickplay_<version>.zip` | **可直接安装**：解压到 `<jellyfin-data>/plugins/StrmTrickplay/` |
| `meta.json` | 插件清单（版本 / targetAbi / guid / 程序集） |
| `build-info.json` | 版本、zip 名、大小、MD5、SHA256 —— 发布流程用它拼下载地址与校验和 |

生成插件仓库清单（通常由 CI 调用）：

```bash
python ci/repo.py --zip dist/strm-trickplay_0.2.0.0.zip --version 0.2.0.0 \
  --out staging/manifest.json \
  --source-url "https://github.com/<user>/<repo>/releases/download/v0.2.0.0/strm-trickplay_0.2.0.0.zip"
# 合并旧版本：加 --manifest-in <已有 manifest.json>
```

## 5. 换成别的平台（Gitea / GitLab / Jenkins）

`ci/package.py` 与 `ci/repo.py` 只用 Python 标准库，平台无关。要点只有三条：

1. 装 **.NET 10 SDK** 与 **Python 3**；
2. 依次执行 `dotnet restore` → `dotnet run --project tests/ParserSmokeTest`（可选但推荐）→ `python ci/package.py --version <v>`；
3. 把 `dist/*.zip` 作为构建产物上传；需要自动更新就把 `ci/repo.py` 生成的 `manifest.json` 放到一个可公开访问的 URL（Pages / OSS / 反向代理目录均可）。

## 6. 常见问题

| 现象 | 原因 / 处理 |
|---|---|
| Release 或 gh-pages 推送 403 | `Settings → Actions → General → Workflow permissions` 没给写权限 |
| Jellyfin 里添加仓库后看不到插件 | Pages 未开启，或 manifest URL 写错（用浏览器直接打开该 URL 应看到 JSON 数组） |
| 安装时报 "checksum didn't match" | zip 被重新上传/改写过；重新发版（校验和是 zip 的 MD5，由 CI 自动写入 manifest） |
| 插件装上但 Jellyfin 提示 ABI 不匹配 | `plugin.json` 的 `targetAbi` 与服务器版本不一致；改 `targetAbi` 后重新发版 |
| 私有仓库 | Jellyfin 无法带凭据下载，插件源必须是公开可访问的 URL |
| 每次 push 的 dev 版本要不要发 Release | 默认不发（只做 artifact）；需要时用 `workflow_dispatch` 勾选 publish |
