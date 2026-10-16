# 自动化构建与发布（GitHub Actions）

推送代码后自动产出**可直接给 Jellyfin 使用的插件包**；**push 新版本时自动发 Release**（附 zip + MD5/SHA256 校验文件）并把插件包与仓库清单更新到 `docs/`，Jellyfin 里添加一次存储库地址即可安装与自动更新。

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
| **push 新版本**（改了 `plugin.json` 的 version，且远端还没有对应的 `v<版本>` tag） | `plugin.json` 里的版本，如 `0.2.0.0` | ① 自动创建 **GitHub Release**（附 zip + MD5/SHA256 校验文件）② 更新 `docs/` 并提交回主分支 ③ 日志里打印存储库地址 |
| **push 同一版本**（远端已有该 tag） | `<前三段>.<run_number>`，如 `0.2.0.37` | 只产出 Workflow artifact（重复 push 不会刷屏 Releases） |
| **推 tag `v0.2.0.0`** | `0.2.0.0` | 同上（等效于手动指定版本发版） |
| **Pull Request** | dev 版本 | 只构建 + 跑测试，永不发版 |
| **手动 dispatch** | 可指定；勾选 publish 才发版 | 同对应行 |

**发版方式：改 `plugin.json` 的 version 然后 push** —— CI 会按该版本打 tag 并建 Release，不需要手动打 tag。

### Release 里的附件（用来验证下载完整性）

| 附件 | 内容 | 怎么验证 |
|---|---|---|
| `strm-trickplay_<版本>.zip` | 插件包本体 | 解压后放进 `<jellyfin-data>/plugins/StrmTrickplay/` |
| `strm-trickplay_<版本>.zip.md5` | md5sum 格式：`<小写MD5>  <文件名>` | Linux/macOS：`md5sum -c strm-trickplay_<版本>.zip.md5`；Windows：`Get-FileHash .\xxx.zip -Algorithm MD5` |
| `strm-trickplay_<版本>.zip.sha256` | sha256sum 格式 | `sha256sum -c xxx.zip.sha256` |
| `build-info.json` | 版本 / targetAbi / 大小 / MD5 / SHA256（机器可读） | 供脚本比对 |

流程里有三道质量闸门：**还原依赖 → 解析器冒烟测试（21 项断言）→ 打包后回读校验**（zip 内 `meta.json` 版本、zip 完整性、DLL 版本、校验文件格式）。

> `docs/` 是 Jellyfin 的分发通道（zip + manifest 同目录）；Release 是给人看的下载与校验入口。
> 每次发版 `docs/` 只增加约 35 KB。

---

## 3. 首次启用（发版前只需两步）

1. **给 Actions 写权限**：仓库 `Settings → Actions → General → Workflow permissions` 选 **Read and write permissions**（发布时要提交 `docs/` 并创建 Release）。
2. **（可选）开启 Pages**：`Settings → Pages` → Source 选 *Deploy from a branch* → 分支 **main**、目录 **/docs**。
   不开也能用 raw 直链，只是地址长一点。

> 现在 `docs/` 里已经有 0.1.0.0 的包，push 后直接添加存储库地址即可安装。
> 由于远端还没有 `v0.1.0.0` tag，**下一次 push 会自动创建这个版本的 Release**（这正是验证流水线的好机会）。

---

## 4. 版本号怎么定

| 位置 | 作用 |
|---|---|
| `plugin.json` → `version` | 版本来源，**改它就是"发新版本"** |
| tag `v0.2.0.0` | 也可手动打 tag 发版（优先级最高） |
| CI 自动 | 同版本的再次 push 用 `<前三段>.<run_number>` 作为开发版本号 |

版本一律规范化为 **4 段**（Jellyfin 用 `System.Version` 解析，`0.2.0` 会补成 `0.2.0.0`）。

发新版（推荐）：

```bash
# 1) 把 plugin.json 的 version 改成 0.2.0.0
# 2) 提交推送，剩下交给 CI
git add plugin.json && git commit -m "chore: bump version to 0.2.0.0" && git push
```

等价的传统方式：`git tag v0.2.0.0 && git push origin v0.2.0.0`。

判定依据是**远端是否已存在 `v<版本>` 这个 tag**：不存在 → 自动发版并打 tag；已存在 → 这次只做开发构建。

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
| `dist/strm-trickplay_<版本>.zip.md5` | md5sum 格式校验文件（Release 附件） |
| `dist/strm-trickplay_<版本>.zip.sha256` | sha256sum 格式校验文件（Release 附件） |
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
| push 后没有自动发版 | 该版本已发布过（远端已有 `v<版本>` tag）；想发新版请改 `plugin.json` 的 version |
| Release 里没有 md5 文件 | 那是旧流程建的 Release；重新发一次版本即可（新流程必定带上 zip + md5 + sha256） |
| 想只发版不更新 docs | 不需要：docs/ 只在发版时更新，日常 push 只产出 artifact |
