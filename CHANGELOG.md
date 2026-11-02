# 更新日志

本项目所有值得关注的变更都会记录在此。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

> 说明：仓库当前尚未创建 Git 标签。下方 `1.0.0` / `2.0.0` 为按提交历史归纳的功能里程碑，正式标签将在首次发布时补打；届时底部的版本对比链接即可生效。

## [Unreleased]

### 新增

- `scripts/build.sh` / `scripts/build.ps1`：统一的还原、构建、自检、多架构发布与打包入口，本地与 CI 共用。
- `.github/workflows/ci.yml`：三平台（Windows / Ubuntu / macOS）拉取 → 还原 → 构建 → 自检。
- `.github/workflows/release.yml`：`v*` 标签触发的多架构发布与 GitHub Release 上传。
- 完善开源文档：README（环境要求 / 安装 / 基础用法 / 常见问题）、贡献指南、行为准则、Issue 与 PR 模板、CODEOWNERS、Dependabot 配置。

### 变更

- `nuget.config` 固定为 nuget.org 官方源，移除仅本机可用的 `localhost:5000` 源，保证外部开发者还原可复现、消除 NU1900 警告。
- 原有 `.github/workflows/build.yml` 拆分为 `ci.yml` 与 `release.yml`，职责更清晰。

---

## [2.0.0] - 2026-11-01

### 新增

- **Avalonia 桌面客户端**（`src/NuGetSift.Desktop`）：连接配置、测试连接、一键扫描、私有 / 公共分类列表、筛选与搜索、下载进度、运行日志、报告导出、随时停止。
- 解决方案重构为三层：`NuGetSift.Core`（共享引擎）/ `NuGetSift.Cli`（命令行）/ `NuGetSift.Desktop`（图形客户端），CLI 用法保持兼容。
- 引擎全链路支持 `CancellationToken` 与进度回调。
- CI 支持桌面客户端 6 架构、命令行 7 架构的单文件自包含发布。

### 修复

- 报告改为写入下载目录内，并使用 UTF-8 无 BOM 编码。
- `AppConfig` 子对象增加属性初始化器，自检不再因 `config.json` 缺失（CI 场景）而抛 `NullReferenceException`。

---

## [1.0.0] - 2026-10-27

### 新增

- 首个版本：枚举私有 NuGet 源，与 nuget.org 逐一比对，仅下载私有源独有的包。
- V3 Search 健壮翻页（不受 `totalHits` 截断影响）与 V2 OData 全量列举两种枚举模式。
- 自适应 `PackageBaseAddress`、下载失败回退 registration 的 `packageContent`、写盘前 ZIP 文件头校验。
- 并发下载、鉴权（none / bearer / basic）、版本级比对、Markdown / CSV 报告。
- 命令行自检 `--selftest`、试跑 `--dry-run`、配置生成 `--init`。

---

[Unreleased]: https://github.com/OPEN96/NuGetSift/compare/v2.0.0...HEAD
[2.0.0]: https://github.com/OPEN96/NuGetSift/compare/v1.0.0...v2.0.0
[1.0.0]: https://github.com/OPEN96/NuGetSift/releases/tag/v1.0.0
