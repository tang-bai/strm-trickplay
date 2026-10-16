#!/usr/bin/env bash
# 发版判定逻辑测试（与 .github/workflows/build.yml 的 "解析版本号" 步骤保持一致）。
#
# 规则：
#   tag vX        -> 版本 X，发版
#   手动指定版本   -> 按 publish 开关决定是否发版
#   普通 push     -> plugin.json 的版本若"从未发布过"（远端没有 v<版本> tag）则自动发版，
#                    否则退化为开发构建 <前3段>.<run_number>，避免刷屏 Releases
#
# 用法：bash ci/tests/release-detection.test.sh

set -uo pipefail
cd "$(dirname "$0")/../.."
ROOT="$(pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
FAILED=0

python3() { python "$@"; }

# 与 workflow 相同的解析逻辑
resolve() {
  local manual="${INPUT_VERSION:-}" publish="${INPUT_PUBLISH:-false}" raw release base dev
  if [[ -n "$manual" ]]; then
    raw="$manual"; release="$publish"
  elif [[ "${GITHUB_REF}" == refs/tags/v* ]]; then
    raw="${GITHUB_REF#refs/tags/v}"; release="true"
  else
    base="$(python3 -c "import json,pathlib;print(json.loads(pathlib.Path('plugin.json').read_text(encoding='utf-8'))['version'])")"
    IFS='.' read -r v1 v2 v3 v4 <<< "$base"
    base="${v1:-0}.${v2:-0}.${v3:-0}.${v4:-0}"
    dev="$(echo "$base" | cut -d. -f1-3).${GITHUB_RUN_NUMBER}"
    if [[ "${GITHUB_EVENT_NAME}" == "pull_request" ]]; then
      raw="$dev"; release="false"
    elif git ls-remote --exit-code --tags origin "refs/tags/v${base}" >/dev/null 2>&1; then
      raw="$dev"; release="false"
    else
      raw="$base"; release="true"
    fi
  fi
  IFS='.' read -r a b c d <<< "$raw"
  echo "${a:-0}.${b:-0}.${c:-0}.${d:-0}|$release"
}

# 造一个带 origin（本地裸仓库）的临时工作区
setup_repo() {
  rm -rf "$WORK/repo" "$WORK/remote.git"
  git init -q --bare "$WORK/remote.git"
  git init -q "$WORK/repo"
  cd "$WORK/repo"
  git remote add origin "$WORK/remote.git"
  printf '{"version":"%s"}' "$1" > plugin.json
  git add -A
  git -c user.email=t@t -c user.name=t commit -q -m init
  git push -q origin HEAD:main
}

check() {
  local desc="$1" expected="$2" actual="$3"
  if [[ "$actual" == "$expected" ]]; then
    echo "  [PASS] $desc -> $actual"
  else
    echo "  [FAIL] $desc -> 期望 $expected，实际 $actual"
    FAILED=1
  fi
}

export GITHUB_RUN_NUMBER=7
export INPUT_VERSION= INPUT_PUBLISH=

echo "--- 1) 普通 push，plugin.json 是全新版本（远端无 tag） ---"
setup_repo 0.3.0.0
GITHUB_EVENT_NAME=push GITHUB_REF=refs/heads/main
check "自动发版" "0.3.0.0|true" "$(resolve)"

echo "--- 2) 同一个版本再次 push（远端已有 tag）→ 开发构建 ---"
cd "$WORK/repo"
git push -q origin HEAD:refs/tags/v0.3.0.0
GITHUB_EVENT_NAME=push GITHUB_REF=refs/heads/main
check "退化为开发构建" "0.3.0.7|false" "$(resolve)"

echo "--- 3) 版本升到 0.3.1.0 后再 push → 再次自动发版 ---"
setup_repo 0.3.1.0
GITHUB_EVENT_NAME=push GITHUB_REF=refs/heads/main
check "自动发版" "0.3.1.0|true" "$(resolve)"

echo "--- 4) Pull Request 永不发版 ---"
GITHUB_EVENT_NAME=pull_request GITHUB_REF=refs/pull/9/merge
check "PR 只做开发构建" "0.3.1.7|false" "$(resolve)"

echo "--- 5) 推 tag（手动指定版本） ---"
GITHUB_EVENT_NAME=push GITHUB_REF=refs/tags/v0.4
check "tag 发版并补零" "0.4.0.0|true" "$(resolve)"

echo "--- 6) 手动 dispatch：指定版本 + 发布 ---"
GITHUB_EVENT_NAME=workflow_dispatch GITHUB_REF=refs/heads/main
INPUT_VERSION=0.5.0.0 INPUT_PUBLISH=true
check "手动发版" "0.5.0.0|true" "$(resolve)"

echo "--- 7) 手动 dispatch：指定版本但不发布 ---"
INPUT_VERSION=0.5.1 INPUT_PUBLISH=false
check "只构建" "0.5.1.0|false" "$(resolve)"

cd "$ROOT"
if [[ "$FAILED" == "0" ]]; then echo "全部通过"; else echo "存在失败用例"; exit 1; fi
