#!/usr/bin/env python3
"""生成 / 更新 Jellyfin 插件仓库 manifest.json。

Jellyfin 读取的仓库文件是 PackageInfo[]（见 MediaBrowser.Model/Updates/PackageInfo.cs）：
  [{ "guid": ..., "name": ..., "versions": [ { "version", "targetAbi", "sourceUrl", "checksum", "timestamp", "changelog" } ] }]
其中 checksum 是插件包 zip 的 **大写十六进制 MD5**（InstallationManager 用 MD5 校验下载包）。

脚本支持合并已有 manifest，从而保留历史版本。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PLUGIN_JSON = ROOT / "plugin.json"


def log(message: str) -> None:
    print(f"[repo] {message}", flush=True)


def die(message: str) -> "None":
    print(f"[repo] 错误: {message}", file=sys.stderr, flush=True)
    raise SystemExit(1)


def load_plugin() -> dict:
    with PLUGIN_JSON.open(encoding="utf-8") as handle:
        return json.load(handle)


def normalize_version(raw: str) -> str:
    parts = [p for p in (raw or "").strip().lstrip("vV").split(".") if p != ""]
    if not parts:
        die(f"版本号不合法: {raw!r}")
    while len(parts) < 4:
        parts.append("0")
    return ".".join(parts)


def version_key(value: str) -> tuple:
    parts = []
    for item in value.split("."):
        parts.append(int(item) if item.isdigit() else 0)
    while len(parts) < 4:
        parts.append(0)
    return tuple(parts[:4])


def md5_of(path: Path) -> str:
    return hashlib.md5(path.read_bytes()).hexdigest().upper()


def load_existing(path: Path | None) -> list:
    if path is None or not path.is_file():
        return []
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        die(f"已有 manifest 解析失败: {error}")
    if isinstance(data, dict):  # 容错：有人会写成单个对象
        return [data]
    if not isinstance(data, list):
        die("已有 manifest 不是数组")
    return data


def main() -> int:
    plugin = load_plugin()

    parser = argparse.ArgumentParser(description="生成 Jellyfin 插件仓库 manifest.json")
    parser.add_argument("--zip", required=True, help="插件包 zip 路径")
    parser.add_argument("--version", default=plugin.get("version", "0.0.0.0"), help="插件版本")
    parser.add_argument("--out", required=True, help="输出的 manifest.json 路径")
    parser.add_argument("--manifest-in", default=None, help="已有 manifest（用于合并历史版本）")
    parser.add_argument("--source-url", default=None, help="zip 的下载地址（默认指向 GitHub Release）")
    parser.add_argument("--repo-url", default=plugin.get("repository", ""), help="插件仓库地址")
    parser.add_argument("--changelog", default="", help="该版本的更新说明")
    parser.add_argument("--timestamp", default=None, help="ISO8601 时间戳，默认取当前 UTC 时间")
    args = parser.parse_args()

    zip_path = Path(args.zip).resolve()
    if not zip_path.is_file():
        die(f"找不到插件包: {zip_path}")

    version = normalize_version(args.version)
    timestamp = args.timestamp or datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f0Z")
    checksum = md5_of(zip_path)

    source_url = args.source_url or (
        f"{args.repo_url.rstrip('/')}/releases/download/v{version}/{zip_path.name}"
    )
    if not source_url:
        die("必须提供 --source-url 或 plugin.json 里的 repository")

    entry = {
        "version": version,
        "changelog": args.changelog,
        "targetAbi": plugin["targetAbi"],
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": timestamp,
        "repositoryName": plugin["name"],
        "repositoryUrl": args.repo_url,
    }

    packages = load_existing(Path(args.manifest_in).resolve() if args.manifest_in else None)

    package = None
    for item in packages:
        if str(item.get("guid", "")).lower() == plugin["guid"].lower():
            package = item
            break

    if package is None:
        package = {
            "guid": plugin["guid"],
            "name": plugin["name"],
            "description": plugin.get("description", ""),
            "overview": plugin.get("overview", ""),
            "owner": plugin.get("owner", ""),
            "category": plugin.get("category", "General"),
            "imageUrl": plugin.get("imageUrl", ""),
            "versions": [],
        }
        packages.append(package)

    # 基本信息以 plugin.json 为准刷新
    package["name"] = plugin["name"]
    package["description"] = plugin.get("description", "")
    package["overview"] = plugin.get("overview", "")
    package["owner"] = plugin.get("owner", "")
    package["category"] = plugin.get("category", "General")
    package["imageUrl"] = plugin.get("imageUrl", "")

    versions = [v for v in package.get("versions", []) if normalize_version(str(v.get("version", "0"))) != version]
    versions.append(entry)
    versions.sort(key=lambda item: version_key(str(item.get("version", "0"))), reverse=True)
    package["versions"] = versions

    out_path = Path(args.out).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(packages, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    log(f"版本    : {version}")
    log(f"校验和  : {checksum}")
    log(f"下载地址: {source_url}")
    log(f"历史版本: {len(versions) - 1} 个")
    log(f"已写入  : {out_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
