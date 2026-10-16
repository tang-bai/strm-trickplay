#!/usr/bin/env python3
"""从 CHANGELOG.md 提取指定版本的更新说明。

GitHub Release 说明、Jellyfin 插件目录里的 changelog、构建产物 meta.json
三处共用这一份内容，避免各写各的、也避免发布"没有说明"的版本。

用法：
  python3 ci/changelog.py --list                          # 列出所有已记录版本
  python3 ci/changelog.py --version 0.4.0.0               # 该版本的完整说明
  python3 ci/changelog.py --version 0.4.0.0 --format summary   # 单行摘要（给 manifest/meta.json）
  python3 ci/changelog.py --version 0.4.0.0 --require     # 缺失时以退出码 3 失败（发版用）
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_FILE = ROOT / "CHANGELOG.md"

# ## [0.4.0.0] - 2026-09-27   /   ## 0.4.0.0   /   ## v0.4.0.0 - 2026-09-27
HEADING = re.compile(r"^##\s+\[?v?(\d+(?:\.\d+){0,3})\]?\s*(?:[-—–]\s*(.*?))?\s*$")
SUMMARY_HEADING = re.compile(r"^###\s+(?:变更摘要|摘要|summary)\s*$", re.IGNORECASE)
ANY_HEADING = re.compile(r"^#{1,6}\s+")
LIST_ITEM = re.compile(r"^\s*[-*+]\s+(.+?)\s*$")
FENCE = re.compile(r"^\s*```")

MAX_SUMMARY = 300


def normalize_version(raw: str) -> str:
    """把 1~4 段版本号补齐成 4 段，便于比较。"""
    parts = [p for p in str(raw).strip().lstrip("vV").split(".") if p != ""]
    while len(parts) < 4:
        parts.append("0")
    return ".".join(parts[:4])


def _die(message: str, code: int = 1) -> "None":
    print(f"[changelog] {message}", file=sys.stderr)
    raise SystemExit(code)


def iter_sections(text: str):
    """产出 (版本号, 正文)；代码块里的示例标题会被忽略。"""
    version = None
    body: list[str] = []
    in_fence = False

    for line in text.splitlines():
        if FENCE.match(line):
            in_fence = not in_fence
            if version is not None:
                body.append(line)
            continue

        match = None if in_fence else HEADING.match(line)
        if match:
            if version is not None:
                yield version, "\n".join(body).strip()
            version = normalize_version(match.group(1))
            body = []
            continue

        if version is not None:
            body.append(line)

    if version is not None:
        yield version, "\n".join(body).strip()


def list_versions(path: Path = DEFAULT_FILE) -> list[str]:
    if not path.is_file():
        return []
    return [version for version, _ in iter_sections(path.read_text(encoding="utf-8"))]


def extract(version: str, path: Path = DEFAULT_FILE) -> str | None:
    """取某个版本的说明正文（不含标题行）。"""
    if not path.is_file():
        return None
    wanted = normalize_version(version)
    for found, body in iter_sections(path.read_text(encoding="utf-8")):
        if found == wanted:
            return body
    return None


def summarize(body: str) -> str:
    """生成单行摘要：优先 '### 变更摘要' 段，其次第一条列表项，最后取首行。"""
    if not body:
        return ""

    lines = body.splitlines()
    collected: list[str] = []
    inside = False
    for line in lines:
        if SUMMARY_HEADING.match(line):
            inside = True
            continue
        if inside:
            if ANY_HEADING.match(line):
                break
            if line.strip():
                collected.append(line.strip())
    if collected:
        text = " ".join(collected)
    else:
        text = ""
        for line in lines:
            item = LIST_ITEM.match(line)
            if item:
                text = item.group(1)
                break
        if not text:
            for line in lines:
                stripped = line.strip()
                if stripped and not ANY_HEADING.match(stripped):
                    text = stripped
                    break

    text = re.sub(r"\s+", " ", text).strip()
    text = re.sub(r"[*`]", "", text).strip()
    if len(text) > MAX_SUMMARY:
        text = text[: MAX_SUMMARY - 1].rstrip() + "…"
    return text


def main() -> int:
    # Windows 上 Python 文本模式会把 \n 写成 \r\n，统一成 LF 方便 shell 消费
    try:
        sys.stdout.reconfigure(newline="\n")
        sys.stderr.reconfigure(newline="\n")
    except (AttributeError, ValueError):
        pass

    parser = argparse.ArgumentParser(description="从 CHANGELOG.md 提取版本更新说明")
    parser.add_argument("--version", default="", help="版本号（1~4 段）")
    parser.add_argument("--file", default=str(DEFAULT_FILE), help="更新日志文件")
    parser.add_argument("--format", choices=["full", "summary"], default="full", help="输出格式")
    parser.add_argument("--require", action="store_true", help="缺失该版本时以退出码 3 失败（发版流水线用）")
    parser.add_argument("--list", action="store_true", help="列出所有已记录版本")
    parser.add_argument("--print-version", action="store_true", help="只输出规范化后的版本号")
    args = parser.parse_args()

    path = Path(args.file)

    if args.list:
        for version in list_versions(path):
            print(version)
        return 0

    if not args.version:
        _die("必须通过 --version 指定版本号（或使用 --list）", 2)

    version = normalize_version(args.version)

    if args.print_version:
        print(version)
        return 0

    body = extract(version, path)

    if body is None:
        known = ", ".join(list_versions(path)) or "（空）"
        message = (
            f"{path.name} 里找不到版本 {version} 的更新说明。\n"
            f"已记录的版本: {known}\n"
            "请在 CHANGELOG.md 里补一节，形如：\n"
            f"  ## [{version}] - YYYY-MM-DD\n"
            "  ### 变更摘要\n"
            "  - 一句话说明这个版本改了什么\n"
            "  ### 新增\n"
            "  - ..."
        )
        if args.require:
            _die(message, 3)
        print(message, file=sys.stderr)
        return 0

    print(summarize(body) if args.format == "summary" else body)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
