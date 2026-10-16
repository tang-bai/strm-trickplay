#!/usr/bin/env bash
# 版本号解析逻辑测试。
#
# 本文件与 .github/workflows/build.yml 里 "解析版本号" 步骤保持一致，
# 修改 workflow 中的解析逻辑时请同步更新这里，然后执行：
#   bash ci/tests/version-resolution.test.sh
python3() { python "$@"; }   # Windows/Git Bash 兼容；Linux 上原样可用

set -uo pipefail
cd "$(dirname "$0")/../.."

GITHUB_OUTPUT="$(mktemp)"
trap 'rm -f "$GITHUB_OUTPUT"' EXIT

resolve() {
  rm -f "$GITHUB_OUTPUT"; : > "$GITHUB_OUTPUT"
  local manual="${INPUT_VERSION:-}" publish="${INPUT_PUBLISH:-false}" raw release
  if [[ -n "$manual" ]]; then
    raw="$manual"; release="$publish"
  elif [[ "${GITHUB_REF}" == refs/tags/v* ]]; then
    raw="${GITHUB_REF#refs/tags/v}"; release="true"
  else
    local base
    base="$(python3 -c "import json,pathlib;print(json.loads(pathlib.Path('plugin.json').read_text(encoding='utf-8'))['version'])")"
    raw="$(echo "$base" | cut -d. -f1-3).${GITHUB_RUN_NUMBER}"; release="false"
  fi
  IFS='.' read -r a b c d <<< "$raw"
  echo "${a:-0}.${b:-0}.${c:-0}.${d:-0}|$release"
}

check() {
  local desc="$1" expected="$2" actual
  actual="$(resolve)"
  if [[ "$actual" == "$expected" ]]; then
    echo "  [PASS] $desc -> $actual"
  else
    echo "  [FAIL] $desc -> 期望 $expected，实际 $actual"
    FAILED=1
  fi
}

FAILED=0
export GITHUB_RUN_NUMBER=42
export GITHUB_REF=refs/heads/main INPUT_VERSION= INPUT_PUBLISH=
check "分支 push 自动 dev 版本" "0.1.0.42|false"

export GITHUB_REF=refs/tags/v0.2.0.0
check "tag v0.2.0.0" "0.2.0.0|true"

export GITHUB_REF=refs/tags/v0.3
check "tag v0.3（三段补零）" "0.3.0.0|true"

export GITHUB_REF=refs/heads/main INPUT_VERSION=0.4.0.0 INPUT_PUBLISH=true
check "手动指定 + 发布" "0.4.0.0|true"

export INPUT_VERSION=0.4.5 INPUT_PUBLISH=false
check "手动指定三段、不发布" "0.4.5.0|false"

if [[ "$FAILED" == "0" ]]; then echo "全部通过"; else echo "存在失败用例"; exit 1; fi
