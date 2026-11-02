# ==============================================================================
# NuGetSift 构建 / 测试 / 发布 统一脚本（Windows PowerShell）
#
# 与 scripts/build.sh 功能对齐，供 Windows PowerShell 开发者使用。
# GitHub Actions 各平台统一通过 build.sh 调用，本文件主要服务本地 Windows 开发。
#
# 用法:
#   .\scripts\build.ps1 ci                 # 还原 + 构建 + 自检
#   .\scripts\build.ps1 publish <version>  # 发布全部 RID 到 ./publish
#   .\scripts\build.ps1 package <version>  # 打包 ./publish 为 zip
#   .\scripts\build.ps1 all <version>      # publish + package
# ==============================================================================

[CmdletBinding()]
param(
    [Parameter(Position = 0)] [string] $Command = "ci",
    [Parameter(Position = 1)] [string] $Version = ""
)

$ErrorActionPreference = "Stop"
$Solution        = "NuGetSift.slnx"
$Configuration   = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { "Release" }
$CliProject      = "src/NuGetSift.Cli/NuGetSift.Cli.csproj"
$DesktopProject  = "src/NuGetSift.Desktop/NuGetSift.Desktop.csproj"
$DesktopRids     = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
$CliRids         = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-arm", "osx-x64", "osx-arm64")

function Log($msg) { Write-Host "[build] $msg" -ForegroundColor Cyan }

function Invoke-Restore { Log "restore $Solution"; dotnet restore $Solution }
function Invoke-Build   { Log "build $Solution ($Configuration)"; dotnet build $Solution -c $Configuration --no-restore }
function Invoke-Test    { Log "self-test (公共/私有 区分逻辑)"; dotnet run --project $CliProject -c $Configuration --no-build -- --selftest }

function Invoke-Ci {
    Invoke-Restore
    Invoke-Build
    Invoke-Test
}

function Publish-One($project, $rid, $out) {
    Log "publish $project -> $rid"
    dotnet publish $project -c $Configuration -r $rid `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        --self-contained true `
        -o $out
}

function Invoke-Publish($version) {
    if (-not $version) { throw "用法: publish <version>" }
    if (Test-Path ./publish) { Remove-Item -Recurse -Force ./publish }
    New-Item -ItemType Directory -Force -Path ./publish | Out-Null
    foreach ($rid in $DesktopRids) { Publish-One $DesktopProject $rid "./publish/desktop-$rid" }
    foreach ($rid in $CliRids)    { Publish-One $CliProject    $rid "./publish/cli-$rid" }
}

function Invoke-Package($version) {
    if (-not $version) { throw "用法: package <version>" }
    Log "打包产物 -> NuGetSift-$version-*.zip"
    Get-ChildItem ./publish -Directory | ForEach-Object {
        $name = "NuGetSift-$version-$($_.Name).zip"
        Log "zip $name"
        Compress-Archive -Path "$($_.FullName)/*" -DestinationPath "./$name" -Force
    }
    Get-ChildItem ./*.zip | Select-Object Name, Length
}

function Invoke-All($version) {
    Invoke-Publish $version
    Invoke-Package $version
}

switch ($Command) {
    "restore" { Invoke-Restore }
    "build"   { Invoke-Build }
    "test"    { Invoke-Test }
    "ci"      { Invoke-Ci }
    "publish" { Invoke-Publish $Version }
    "package" { Invoke-Package $Version }
    "all"     { Invoke-All $Version }
    default   { Write-Error "未知命令: $Command"; exit 1 }
}
