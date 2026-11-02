#!/usr/bin/env bash
# ==============================================================================
# NuGetSift 构建 / 测试 / 发布 统一脚本（POSIX bash）
#
# 同时供本地开发与时序 CI 调用，确保「本地能跑，CI 也能跑」。
#
# 用法:
#   ./scripts/build.sh restore            # 还原依赖
#   ./scripts/build.sh build              # 构建解决方案 (Release)
#   ./scripts/build.sh test               # 运行自检 (公共/私有 区分逻辑)
#   ./scripts/build.sh ci                 # restore + build + test
#   ./scripts/build.sh publish <version>  # 发布全部 RID 到 ./publish
#   ./scripts/build.sh package <version>  # 将 ./publish 打包为 zip
#   ./scripts/build.sh all <version>      # publish + package
# ==============================================================================
set -euo pipefail

# ---- 路径定位 ----------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT_DIR"

# ---- 常量 --------------------------------------------------------------------
SOLUTION="NuGetSift.slnx"
CONFIGURATION="${CONFIGURATION:-Release}"
CLI_PROJECT="src/NuGetSift.Cli/NuGetSift.Cli.csproj"
DESKTOP_PROJECT="src/NuGetSift.Desktop/NuGetSift.Desktop.csproj"

# 桌面客户端 6 架构（linux-arm 32 位缺少原生图形依赖，不发布桌面版）
DESKTOP_RIDS="win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64"
# 命令行 7 架构（含 linux-arm 32 位）
CLI_RIDS="win-x64 win-arm64 linux-x64 linux-arm64 linux-arm osx-x64 osx-arm64"

# ---- 日志 --------------------------------------------------------------------
log() { printf '\033[1;34m[build]\033[0m %s\n' "$*"; }
err() { printf '\033[1;31m[error]\033[0m %s\n' "$*" >&2; }

# ---- 命令 --------------------------------------------------------------------
cmd_restore() {
  log "restore $SOLUTION"
  dotnet restore "$SOLUTION"
}

cmd_build() {
  log "build $SOLUTION ($CONFIGURATION)"
  dotnet build "$SOLUTION" -c "$CONFIGURATION" --no-restore
}

cmd_test() {
  log "self-test (公共/私有 区分逻辑)"
  dotnet run --project "$CLI_PROJECT" -c "$CONFIGURATION" --no-build -- --selftest
}

cmd_ci() {
  cmd_restore
  cmd_build
  cmd_test
}

publish_one() {
  local project="$1" rid="$2" out="$3"
  log "publish $project -> $rid"
  dotnet publish "$project" -c "$CONFIGURATION" -r "$rid" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishTrimmed=false \
    --self-contained true \
    -o "$out"
}

cmd_publish() {
  local version="${1:?用法: publish <version>}"
  rm -rf ./publish
  mkdir -p ./publish
  local rid
  for rid in $DESKTOP_RIDS; do
    publish_one "$DESKTOP_PROJECT" "$rid" "./publish/desktop-$rid"
  done
  for rid in $CLI_RIDS; do
    publish_one "$CLI_PROJECT" "$rid" "./publish/cli-$rid"
  done
}

cmd_package() {
  local version="${1:?用法: package <version>}"
  log "打包产物 -> NuGetSift-$version-*.zip"
  local uname_s; uname_s="$(uname -s 2>/dev/null || echo unknown)"
  local is_windows=false
  if [[ "${OS:-}" == "Windows_NT" || "$uname_s" == MINGW* || "$uname_s" == MSYS* || "$uname_s" == CYGWIN* ]]; then
    is_windows=true
  fi

  # Windows / macOS / Linux 统一用 PowerShell 打包（GitHub runner 与多数开发机均自带），
  # 避免依赖 zip 命令在不同平台上的差异；pwsh 优先，回退 powershell。
  local ps="pwsh"; command -v pwsh >/dev/null 2>&1 || ps="powershell"
  if $is_windows && ! command -v "$ps" >/dev/null 2>&1; then
    err "未找到 PowerShell，无法打包"; return 1
  fi
  if command -v "$ps" >/dev/null 2>&1; then
    VERSION="$version" "$ps" -NoProfile -Command '
      Get-ChildItem ./publish -Directory | ForEach-Object {
        $dest = "./NuGetSift-$env:VERSION-$($_.Name).zip"
        Compress-Archive -Path "$($_.FullName)/*" -DestinationPath $dest -Force
      }'
  else
    command -v zip >/dev/null 2>&1 || { err "未找到 zip / PowerShell，无法打包"; return 1; }
    for d in ./publish/*/; do
      local name; name="$(basename "$d")"
      log "zip NuGetSift-$version-$name.zip"
      ( cd "$d" && zip -r "$ROOT_DIR/NuGetSift-$version-$name.zip" . )
    done
  fi
  log "已生成:"; ls -1 ./*.zip
}

cmd_all() {
  local version="${1:?用法: all <version>}"
  cmd_publish "$version"
  cmd_package "$version"
}

# ---- 分发 --------------------------------------------------------------------
main() {
  local cmd="${1:-ci}"; shift || true
  case "$cmd" in
    restore|build|test|ci|publish|package|all) "cmd_$cmd" "$@" ;;
    *) err "未知命令: $cmd"; echo "可用: restore build test ci publish package all"; exit 1 ;;
  esac
}

main "$@"
