#!/usr/bin/env python3
"""构建并打包 Jellyfin 插件。

产物（默认输出到 dist/）：
  * meta.json                        —— 放进 plugins/<插件名>/ 的清单
  * strm-trickplay_<version>.zip     —— 可直接给 Jellyfin 使用的插件包
  * build-info.json                  —— 版本 / zip 文件名 / MD5 / SHA256 / 大小（供 release 流程使用）

版本号来源优先级：--version > plugin.json 的 version。
所有元数据（guid / targetAbi / 描述 / 程序集列表）统一取自 plugin.json，避免多处不一致。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PLUGIN_JSON = ROOT / "plugin.json"


def log(message: str) -> None:
    print(f"[package] {message}", flush=True)


def die(message: str) -> "None":
    print(f"[package] 错误: {message}", file=sys.stderr, flush=True)
    raise SystemExit(1)


def load_plugin() -> dict:
    if not PLUGIN_JSON.is_file():
        die(f"找不到 {PLUGIN_JSON}")
    with PLUGIN_JSON.open(encoding="utf-8") as handle:
        return json.load(handle)


def normalize_version(raw: str) -> str:
    """Jellyfin 用 System.Version 解析版本号，统一成 4 段。"""
    parts = [p for p in (raw or "").strip().lstrip("vV").split(".") if p != ""]
    if not parts or len(parts) > 4:
        die(f"版本号格式不合法: {raw!r}（应为 1~4 段数字，例如 0.2.0.0）")
    for part in parts:
        if not part.isdigit():
            die(f"版本号必须是数字: {raw!r}")
    while len(parts) < 4:
        parts.append("0")
    return ".".join(parts)


def md5_of(path: Path) -> str:
    return hashlib.md5(path.read_bytes()).hexdigest().upper()


def sha256_of(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def resolve_dotnet(explicit: str | None) -> str:
    candidates = []
    if explicit:
        candidates.append(explicit)
    if os.environ.get("DOTNET"):
        candidates.append(os.environ["DOTNET"])
    candidates.append(str(ROOT / ".dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")))
    candidates.append("dotnet")

    for candidate in candidates:
        found = shutil.which(candidate) if os.path.sep not in candidate else candidate
        if found and Path(found).is_file():
            return found
    die("找不到 dotnet SDK，请安装 .NET SDK 或用 --dotnet 指定路径")
    return "dotnet"


def run_build(dotnet: str, project: Path, configuration: str, version: str) -> None:
    command = [
        dotnet,
        "build",
        str(project),
        "-c",
        configuration,
        f"-p:Version={version}",
        "--nologo",
    ]
    log("执行: " + " ".join(command))
    result = subprocess.run(command, cwd=ROOT, check=False)
    if result.returncode != 0:
        die(f"dotnet build 失败（退出码 {result.returncode}）")


def write_meta(plugin: dict, version: str, output: Path, changelog: str, timestamp: str) -> None:
    meta = {
        "category": plugin.get("category", "General"),
        "changelog": changelog or plugin.get("changelog", ""),
        "description": plugin.get("description", ""),
        "guid": plugin["guid"],
        "imageUrl": plugin.get("imageUrl", ""),
        "name": plugin["name"],
        "overview": plugin.get("overview", ""),
        "owner": plugin.get("owner", ""),
        "targetAbi": plugin["targetAbi"],
        "timestamp": timestamp,
        "version": version,
        "status": "",
        "autoUpdate": False,
        "assemblies": plugin.get("artifacts", []),
    }
    output.write_text(json.dumps(meta, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def make_zip(zip_path: Path, files: list[Path]) -> None:
    if zip_path.exists():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for item in files:
            archive.write(item, arcname=item.name)


def verify_assembly_version(dll: Path, expected_version: str) -> None:
    """DLL 的 Win32 版本资源里应包含目标版本（UTF-16LE）；不匹配说明编译时版本没生效或是旧产物。"""
    needle = expected_version.encode("utf-16-le")
    if needle not in dll.read_bytes():
        die(
            f"{dll.name} 里找不到版本 {expected_version}："
            "说明 -p:Version 未生效（检查 csproj 是否硬写了 AssemblyVersion）或产物是旧的。"
        )


def verify_zip(zip_path: Path, expected_names: set[str], expected_version: str) -> None:
    with zipfile.ZipFile(zip_path) as archive:
        names = set(archive.namelist())
        missing = expected_names - names
        if missing:
            die(f"打包结果缺少文件: {sorted(missing)}")
        with archive.open("meta.json") as handle:
            meta = json.loads(handle.read().decode("utf-8"))
        if meta.get("version") != expected_version:
            die(f"zip 内 meta.json 版本({meta.get('version')}) 与期望({expected_version})不一致")
    bad = zipfile.ZipFile(zip_path).testzip()
    if bad is not None:
        die(f"zip 损坏: {bad}")


def append_github_output(values: dict) -> None:
    target = os.environ.get("GITHUB_OUTPUT")
    if not target:
        return
    with open(target, "a", encoding="utf-8") as handle:
        for key, value in values.items():
            handle.write(f"{key}={value}\n")


def main() -> int:
    plugin = load_plugin()

    parser = argparse.ArgumentParser(description="构建并打包 Jellyfin 插件")
    parser.add_argument("--version", default=plugin.get("version", "0.0.0.0"), help="插件版本（1~4 段）")
    parser.add_argument("--configuration", default="Release", help="dotnet 构建配置")
    parser.add_argument("--dist", default="dist", help="输出目录")
    parser.add_argument("--changelog", default="", help="写入 meta.json / manifest 的更新说明")
    parser.add_argument("--dotnet", default=None, help="dotnet 可执行文件路径")
    parser.add_argument("--skip-build", action="store_true", help="跳过 dotnet build（直接打包已有产物）")
    parser.add_argument("--no-zip", action="store_true", help="只生成 meta.json，不打包")
    args = parser.parse_args()

    version = normalize_version(args.version)
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f0Z")
    dist = (ROOT / args.dist).resolve()
    project = ROOT / plugin["project"]
    artifact_dir = project.parent / "bin" / args.configuration / plugin.get("framework", "net10.0")

    if not args.skip_build:
        run_build(resolve_dotnet(args.dotnet), project, args.configuration, version)

    dist.mkdir(parents=True, exist_ok=True)
    for old in dist.glob("*.zip"):
        old.unlink()

    copied: list[Path] = []
    for name in plugin["artifacts"]:
        source = artifact_dir / name
        if not source.is_file():
            die(f"找不到构建产物 {source}（先执行 dotnet build，或用 --skip-build 时的正确配置）")
        target = dist / name
        shutil.copy2(source, target)
        if name.lower().endswith(".dll"):
            verify_assembly_version(target, version)
        copied.append(target)

    meta_path = dist / "meta.json"
    write_meta(plugin, version, meta_path, args.changelog, timestamp)
    copied.append(meta_path)

    slug = plugin["name"].lower().replace(" ", "-")
    zip_path = dist / f"{slug}_{version}.zip"
    if not args.no_zip:
        make_zip(zip_path, copied)
        verify_zip(zip_path, {p.name for p in copied}, version)

    info = {
        "name": plugin["name"],
        "guid": plugin["guid"],
        "version": version,
        "targetAbi": plugin["targetAbi"],
        "timestamp": timestamp,
        "zip": zip_path.name if not args.no_zip else "",
        "zipPath": str(zip_path) if not args.no_zip else "",
        "size": zip_path.stat().st_size if not args.no_zip else 0,
        "md5": md5_of(zip_path) if not args.no_zip else "",
        "sha256": sha256_of(zip_path) if not args.no_zip else "",
        "files": [p.name for p in copied],
    }
    (dist / "build-info.json").write_text(json.dumps(info, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    log(f"版本    : {version}")
    log(f"元数据  : {meta_path}")
    if not args.no_zip:
        log(f"插件包  : {zip_path} ({info['size']} bytes, md5={info['md5']})")
    log("完成")

    append_github_output(
        {
            "version": version,
            "zip": info["zip"],
            "zip_path": info["zipPath"],
            "md5": info["md5"],
            "sha256": info["sha256"],
        }
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
