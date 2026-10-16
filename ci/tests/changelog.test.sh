#!/usr/bin/env bash
# 更新说明提取逻辑测试（对应 ci/changelog.py，供发版流水线使用）。
#
# 规则：
#   发版（Release）必须有更新说明：CHANGELOG.md 缺少对应版本小节时，--require 必须以非零退出
#   代码块里的示例标题不参与解析；版本号 1~4 段等价（0.4 == 0.4.0.0）
#
# 用法：bash ci/tests/changelog.test.sh

set -uo pipefail
cd "$(dirname "$0")/../.."
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
FAILED=0

python3() { python "$@"; }

check() {
  local name="$1" expected="$2" actual="$3"
  if [[ "$expected" == "$actual" ]]; then
    echo "  [PASS] $name"
  else
    echo "  [FAIL] $name"
    echo "         期望: $expected"
    echo "         实际: $actual"
    FAILED=$((FAILED + 1))
  fi
}

cat > "$WORK/CHANGELOG.md" <<'EOF'
# 更新日志

## 格式

```markdown
## [9.9.9.9] - 2000-01-01
### 变更摘要
代码块里的示例标题不应被解析
```

## [0.4.0.0] - 2026-09-27
### 变更摘要
一句话摘要：发版说明自动化

### 新增
- 条目一
- 条目二

## [0.3.0.0] - 2026-09-20
- 没有摘要小节时，取第一条列表项
- 第二条
EOF

echo "=== 用例 1：--list 列出真实版本（忽略代码块里的示例） ==="
check "list" "0.4.0.0 0.3.0.0" "$(python3 ci/changelog.py --file "$WORK/CHANGELOG.md" --list | tr -d '\r' | tr '\n' ' ' | sed 's/ $//')"

echo "=== 用例 2：完整说明 ==="
full="$(python3 ci/changelog.py --file "$WORK/CHANGELOG.md" --version 0.4.0.0 --format full)"
check "含摘要" "yes" "$(grep -q '一句话摘要：发版说明自动化' <<< "$full" && echo yes || echo no)"
check "含条目" "yes" "$(grep -q '条目二' <<< "$full" && echo yes || echo no)"
check "不含下一版本内容" "no" "$(grep -q '第二条' <<< "$full" && echo yes || echo no)"
check "不含标题行" "no" "$(grep -q '^## ' <<< "$full" && echo yes || echo no)"

echo "=== 用例 3：单行摘要 ==="
check "取变更摘要" "一句话摘要：发版说明自动化" "$(python3 ci/changelog.py --file "$WORK/CHANGELOG.md" --version 0.4.0.0 --format summary)"
check "回退取第一条列表项" "没有摘要小节时，取第一条列表项" "$(python3 ci/changelog.py --file "$WORK/CHANGELOG.md" --version 0.3.0.0 --format summary)"

echo "=== 用例 4：版本号 1~4 段等价 ==="
check "0.4 等价 0.4.0.0" "一句话摘要：发版说明自动化" "$(python3 ci/changelog.py --file "$WORK/CHANGELOG.md" --version 0.4 --format summary)"
check "v 前缀与规范化" "0.4.0.0" "$(python3 ci/changelog.py --file "$WORK/CHANGELOG.md" --version v0.4 --print-version)"

echo "=== 用例 5：缺失版本 ==="
python3 ci/changelog.py --file "$WORK/CHANGELOG.md" --version 1.2.3.4 --require >/dev/null 2>"$WORK/err"
check "require 退出码 3" "3" "$?"
check "提示里带已记录版本" "yes" "$(grep -q '0.4.0.0' "$WORK/err" && echo yes || echo no)"
python3 ci/changelog.py --file "$WORK/CHANGELOG.md" --version 1.2.3.4 >"$WORK/out" 2>/dev/null
check "不 require 时退出码 0" "0" "$?"
check "不 require 时输出为空" "" "$(cat "$WORK/out")"

echo "=== 用例 6：真实 CHANGELOG.md 能解析出当前版本 ==="
current="$(python3 -c "import json,pathlib;print(json.loads(pathlib.Path('plugin.json').read_text(encoding='utf-8'))['version'])")"
check "当前版本有说明" "yes" "$(python3 ci/changelog.py --version "$current" --require >/dev/null 2>&1 && echo yes || echo no)"

echo
if [[ "$FAILED" -eq 0 ]]; then
  echo "全部用例通过 ✅"
else
  echo "失败用例 $FAILED 个 ❌"
fi
exit $(( FAILED > 0 ? 1 : 0 ))
