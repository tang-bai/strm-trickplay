#!/usr/bin/env python3
"""把插件包 + 仓库清单发布到仓库内的 docs/ 目录。

发布后的 docs/ 同时支持两种用法：
  * raw 链接（无需任何设置，push 即可用）
      https://raw.githubusercontent.com/<owner>/<repo>/<branch>/docs/manifest.json
  * GitHub Pages（Settings → Pages → 分支 main，目录 /docs）
      https://<owner>.github.io/<repo>/manifest.json

两者指向同一份文件，Jellyfin 里填任意一个都能用。
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def log(message: str) -> None:
    print(f"[publish] {message}", flush=True)


def die(message: str) -> "None":
    print(f"[publish] 错误: {message}", file=sys.stderr, flush=True)
    raise SystemExit(1)


def run(command: list[str]) -> None:
    log("执行: " + " ".join(command))
    if subprocess.run(command, cwd=ROOT, check=False).returncode != 0:
        die("命令失败: " + " ".join(command))


def detect_repo() -> tuple[str, str]:
    """从 origin 解析 owner/repo。"""
    try:
        url = subprocess.run(
            ["git", "remote", "get-url", "origin"],
            cwd=ROOT,
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
    except Exception:
        die("无法读取 git remote origin，请用 --base-url 手动指定")

    match = re.search(r"github\.com[:/]+([^/]+)/(.+?)(?:\.git)?$", url)
    if not match:
        die(f"无法从 {url} 解析 GitHub owner/repo，请用 --base-url 手动指定")
    return match.group(1), match.group(2)


def main() -> int:
    parser = argparse.ArgumentParser(description="发布插件包与 manifest 到 docs/")
    parser.add_argument("--version", default=None, help="插件版本，默认取 plugin.json")
    parser.add_argument("--docs", default="docs", help="发布目录（默认 docs）")
    parser.add_argument("--branch", default="main", help="raw 链接使用的分支名（默认 main）")
    parser.add_argument("--base-url", default=None, help="覆盖 zip 的下载地址前缀")
    parser.add_argument("--changelog", default="", help="该版本的更新说明")
    parser.add_argument("--skip-build", action="store_true", help="跳过编译，直接用 dist/ 里已有的包")
    args = parser.parse_args()

    plugin = json.loads((ROOT / "plugin.json").read_text(encoding="utf-8"))
    version = args.version or plugin["version"]

    if not args.skip_build:
        run([sys.executable, str(ROOT / "ci" / "package.py"), "--version", version,
             "--changelog", args.changelog])

    info_path = ROOT / "dist" / "build-info.json"
    if not info_path.is_file():
        die("找不到 dist/build-info.json，请先运行 ci/package.py（或去掉 --skip-build）")
    info = json.loads(info_path.read_text(encoding="utf-8"))
    zip_name = info.get("zip")
    if not zip_name:
        die("dist/build-info.json 里没有 zip 名称")
    source_zip = ROOT / "dist" / zip_name
    if not source_zip.is_file():
        die(f"找不到 {source_zip}")

    owner, repo = detect_repo()
    base_url = args.base_url or f"https://raw.githubusercontent.com/{owner}/{repo}/{args.branch}/docs"

    docs = (ROOT / args.docs).resolve()
    docs.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source_zip, docs / zip_name)

    manifest_in = docs / "manifest.json"
    command = [
        sys.executable, str(ROOT / "ci" / "repo.py"),
        "--zip", str(docs / zip_name),
        "--version", version,
        "--out", str(manifest_in),
        "--source-url", f"{base_url}/{zip_name}",
        "--repo-url", plugin.get("repository", ""),
        "--changelog", args.changelog,
    ]
    if manifest_in.is_file():
        command += ["--manifest-in", str(manifest_in)]
    run(command)

    log(f"插件包  : {docs / zip_name}")
    log(f"清单    : {manifest_in}")
    print()
    print("在 Jellyfin 里添加插件存储库（控制台 → 插件 → 存储库 → 添加）：")
    print(f"  {base_url}/manifest.json")
    print()
    print("如果开启了 GitHub Pages（分支 " + args.branch + "，目录 /" + args.docs + "），也可以用：")
    print(f"  https://{owner}.github.io/{repo}/manifest.json")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
