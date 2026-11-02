# 贡献指南

感谢你愿意为 NuGetSift 贡献代码、文档或想法！本文说明如何搭好开发环境、跑通验证、以及提交变更的约定。

## 目录

- [行为准则](#行为准则)
- [从哪里开始](#从哪里开始)
- [开发环境](#开发环境)
- [构建与测试](#构建与测试)
- [项目结构](#项目结构)
- [代码规范](#代码规范)
- [提交与 PR 流程](#提交与-pr-流程)
- [commit message 约定](#commit-message-约定)
- [发布流程](#发布流程)
- [安全与配置注意事项](#安全与配置注意事项)

---

## 行为准则

参与本项目即表示同意遵守 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。请保持友善、尊重、就事论事。

## 从哪里开始

- 想报 Bug 或提需求：先搜一下 [Issues](https://github.com/OPEN96/NuGetSift/issues) 是否已有重复，再用对应模板新建。
- 想动手改代码：建议先开一个 Issue 说明思路，避免重复劳动或方向分歧；小修正（错别字、明显 bug）可以直接提 PR。
- 适合入手的改动：文档补全、边界情况处理、新的私有源兼容、界面文案与易用性优化。

## 开发环境

| 项目 | 要求 |
| --- | --- |
| SDK | .NET 10 SDK（目标框架 `net10.0`） |
| IDE（任选） | Visual Studio 2022 / Rider / VS Code + C# Dev Kit |
| 版本控制 | Git |
| Shell | Windows 建议 Git Bash 或 PowerShell；Linux / macOS 用 bash |
| 依赖还原 | 需要访问 `https://api.nuget.org`（`nuget.config` 已固定官方源） |

克隆并初始化：

```bash
git clone https://github.com/OPEN96/NuGetSift.git
cd NuGetSift
cp config.json.example config.json       # 本地配置，已被 .gitignore 忽略
```

> 桌面客户端基于 **Avalonia 11.3.20**，首次构建会自动从 nuget.org 还原依赖，无需额外安装 UI 框架。

## 构建与测试

所有命令统一通过 `scripts/build.sh`（Windows 可用 `scripts/build.ps1`）：

```bash
bash ./scripts/build.sh ci        # 还原 + 构建 + 自检（提交前必跑）
bash ./scripts/build.sh test      # 只跑自检
bash ./scripts/build.sh build     # 只构建
```

`ci` 会验证 3 个自检用例（公共包、公共包、不存在的包），结果应为 `3/3 通过`。这些用例会实际访问 `https://api.nuget.org`，**需要联网**。

手动功能验证（可选，需自备私有源）：

```bash
dotnet run --project src/NuGetSift.Cli -- --baget-url "http://<你的源>/v3/index.json" --dry-run
```

或用 Docker 起本地测试源，步骤见 [README · 安装](README.md#安装)。

**提交前检查清单**

- [ ] `bash ./scripts/build.sh ci` 通过
- [ ] 新增 / 修改的配置字段同步更新 `config.json.example` 与 `README.md`
- [ ] 未提交 `config.json`、令牌、真实内网地址或任何密钥
- [ ] 新增公开 API 或命令行参数已补充文档
- [ ] 界面改动在深色主题下检查过可读性

## 项目结构

| 项目 | 职责 |
| --- | --- |
| `src/NuGetSift.Core` | 共享引擎：服务发现、枚举、比对、下载、报告。**不依赖任何 UI**，是逻辑改动的主要落点 |
| `src/NuGetSift.Cli` | 命令行薄壳：参数解析 + 控制台输出 |
| `src/NuGetSift.Desktop` | Avalonia 桌面客户端：`*.axaml` 界面 + `ViewModels/` MVVM |

分层原则：**业务逻辑放 Core，展示逻辑放 Cli / Desktop**。不要让 Core 引用任何界面项目，也不要在 Desktop 里重写引擎逻辑。

## 代码规范

- 遵循常规 C# 命名与 `.editorconfig` 约定：`PascalCase` 类型与公开成员、`camelCase` 局部变量、私有字段 `_camelCase`。
- 启用 `Nullable`，尽量避免用 `!` 抑制空警告；对外入参做校验。
- 异步方法以 `Async` 结尾，并透传 `CancellationToken`；I/O 操作使用 `ConfigureAwait(false)`（Core 层）。
- 网络请求统一走 `Core/Http.cs`，以便复用重试与鉴权逻辑；不要在各处散落 `new HttpClient()`。
- 下载写盘前必须保留 ZIP 文件头校验，**不要移除**——这是防止写出 JSON / HTML 假包的关键。
- 界面文案与注释使用简体中文，保持与现有文档一致；代码标识符使用英文。
- 不引入需要联网才能还原的新依赖，除非在 PR 中说明理由并评估对离线构建的影响。

## 提交与 PR 流程

1. Fork 仓库并基于 `main` 建分支：`git checkout -b feat/my-change` 或 `fix/my-bug`。
2. 小步提交，每条提交聚焦一件事。
3. 运行 `bash ./scripts/build.sh ci` 确认通过。
4. `git push origin <分支>`，在 GitHub 上发起 PR 到 `main`，并按 [PR 模板](.github/PULL_REQUEST_TEMPLATE.md) 填写：
   - 改了什么、为什么改；
   - 如何验证（含自检 / 实跑结果）；
   - 关联 Issue（`Closes #123`）；
   - 是否影响配置、命令参数或界面（若有，说明文档是否已同步）。
5. CI（[`ci.yml`](.github/workflows/ci.yml)）会在 Windows / Ubuntu / macOS 三平台自动跑构建与自检，需全部通过。
6. 维护者 review 后合并。请保持讨论聚焦于代码本身。

## commit message 约定

采用 [Conventional Commits](https://www.conventionalcommits.org/) 风格，形如 `<type>: <简述>`：

| type | 用途 |
| --- | --- |
| `feat` | 新功能 |
| `fix` | Bug 修复 |
| `docs` | 文档改动 |
| `refactor` | 重构（无行为变化） |
| `ci` | 工作流 / 构建脚本 |
| `chore` | 依赖、模板、杂项 |

示例：

```text
fix(ci): 修正项目路径，移除多余的子目录前缀
feat(desktop): 列表默认按私有独有优先排序
docs: 补充常见问题与贡献指南
```

## 发布流程

发布由维护者执行，推送 `v*` 标签后 [`release.yml`](.github/workflows/release.yml) 自动完成验证、多架构发布、打包与上传 Release：

```bash
git tag v2.0.0
git push origin v2.0.0
```

也可以在 Actions 页面手动运行 Release 工作流并填写版本号。发布前请确认 [CHANGELOG.md](CHANGELOG.md) 已更新到对应版本。

## 安全与配置注意事项

- `config.json` 被 `.gitignore` 忽略，**绝不提交**真实内网地址、API Key 或 PAT。
- 报告 issue 时请脱敏配置与日志（隐藏主机、令牌、内部包 ID）。
- 发现安全漏洞**不要公开提 Issue**，请按 [SECURITY.md](SECURITY.md) 私密报告。

---

再次感谢你的贡献！如有疑问，欢迎在 Issue 中提问。
