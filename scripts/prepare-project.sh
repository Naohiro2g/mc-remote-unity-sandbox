#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || ! -x "$1" ]]; then
  echo '使い方: bash scripts/prepare-project.sh /Unity/Editor/Unity' >&2
  exit 2
fi

unity_editor="$1"
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
project_dir="$repo_dir/unity"
log_dir="$repo_dir/.local/logs"
mkdir -p "$log_dir"

# Assets / Packages / ProjectSettingsを追跡する正式なProjectを直接使う。
# 編集元のコピーや、利用者が編集したシーンの再生成は行わない。
if [[ ! -f "$project_dir/ProjectSettings/ProjectVersion.txt" || ! -f "$project_dir/Assets/LocalSandboxDemo/Scenes/LocalSandboxDemo.unity" ]]; then
  echo 'Unity Projectの設定またはデモシーンがありません。cloneした内容を確認してください。' >&2
  exit 1
fi

if ! "$unity_editor" -batchmode -nographics -quit -projectPath "$project_dir" \
  -executeMethod Code2Create.UnitySandbox.LocalDemo.DemoEditor.Validate -logFile "$log_dir/prepare.log"; then
  tail -n 50 "$log_dir/prepare.log" >&2
  exit 1
fi
if ! grep -q 'LOCAL_SANDBOX_VALIDATION_PASS' "$log_dir/prepare.log"; then
  echo "検証の成功を確認できません: $log_dir/prepare.log" >&2
  exit 1
fi

echo 'Unity Hubで次のプロジェクトを追加して開いてください:'
echo "$project_dir"
echo 'LocalSandboxDemoシーンを開き、Playで開始します。'
