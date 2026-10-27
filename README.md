# NuGetSift

> 枚举私有 NuGet 源（Bagetter / BaGet 等兼容 NuGet V3 的源），与 nuget.org 比对，**只下载「仅存在于私有源」的包**，并生成私有 / 公共区分报告。

---

## 特性（Features）

- 支持任意兼容 NuGet V3 的私有源：Bagetter、BaGet、Nexus、Artifactory 等。
- 自动区分「私有独有包」与「公共 / 镜像包」（基于 nuget.org 返回 404 判定）。
- 两种枚举模式：
  - **V3 Search**（默认）：健壮翻页，翻到空页 / 无新增即止，不受 `totalHits` 截断影响。
  - **V2 OData**（`EnumerateMode: v2`）：直连 `Packages()` 全量列举，适合大私有源 / 检索不全场景。
- **自适应包文件基址约定**：直接使用服务索引中的 `PackageBaseAddress` 基址（Bagetter 为 `/v3/package`，标准 BaGet 为 `/v3/flatcontainer`，不硬编码）。
- **下载兜底**：标准路径失败时，自动回退读取每个包 registration 叶子中的 `packageContent`（最权威下载地址）。
- **安全校验**：下载前校验文件头（`.nupkg` 必须是 ZIP，以 `PK` 开头），杜绝写出 JSON 假文件。
- 生成 Markdown / CSV 区分报告。

---

## 环境要求（Requirements）

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

---

## 目录结构（Project Structure）

```text
NuGetSift/
├── NuGetSift.csproj   # 项目文件（net10.0 控制台）
├── Program.cs                 # 工具主逻辑（仅用 BCL，无需联网还原依赖）
├── config.json.example        # 配置模板（复制为 config.json 后编辑，已被 .gitignore 忽略）
├── nuget.config               # 本地 NuGet 源配置（含私有源 allowInsecureConnections）
├── LICENSE                    # MIT 许可证
├── README.md                  # 本文件
└── .gitignore
```

---

## 快速开始（Quick Start）

### 1. 搭建私有 NuGet 源（Bagetter）

Bagetter 是一个开源 NuGet 服务器（基于 BaGet 增强，包文件基址为 `/v3/package/`）。一行命令即可启动：

```bash
docker run --name nuget-server -p 5000:8080 bagetter/bagetter
```

> **Docker 镜像加速**（可选，避免拉取超时）：在 Docker daemon 配置中加入
>
> ```json
> { "registry-mirrors": ["https://docker.1ms.run"] }
> ```
>
> Windows / macOS：Docker Desktop → Settings → Docker Engine，粘贴后 Apply & Restart。  
> Linux：写入 `/etc/docker/daemon.json` 后 `sudo systemctl restart docker`。

- `-p 5000:8080`：宿主机 **5000** 端口映射到容器内 **8080** 端口。
- 启动后访问地址：`http://localhost:5000/v3/index.json`（服务索引）。
- 默认允许匿名推送，无需额外配置。
- 运维：`docker stop nuget-server` / `docker start nuget-server` / `docker logs -f nuget-server`。

### 2. 推送私有包到服务器

在存放 `.nupkg` 的目录下执行：

```bash
dotnet nuget push -s http://localhost:5000/v3/index.json *.nupkg --allow-insecure-connections
```

- `-s` 指定私有源索引地址。
- `--allow-insecure-connections`：推送目标是 `http://`（非 HTTPS）本地源，需此参数允许不安全连接；若已配置 HTTPS 可省略。
- 若服务器开启 ApiKey 校验，追加 `-k <你的ApiKey>`。

### 3. 配置并运行本工具

```bash
# 进入项目目录
cd NuGetSift

# 生成默认配置（或手动复制 config.json.example 为 config.json）
dotnet run -- --init

# 编辑 config.json，把 BaGet.BaseUrl 改成你的私有源索引地址
# 例如："http://localhost:5000/v3/index.json"
```

常用运行方式：

```bash
dotnet run -- --selftest                              # 自检：验证 公共/私有 区分逻辑（仅需连 nuget.org）
dotnet run -- --dry-run --limit 50                    # 试跑：只枚举+比对，不下载
dotnet run                                           # 正式执行：下载所有“私有独有”的包
```

**输出**：

- 下载的 `.nupkg` → `downloaded_private_packages/`
- 区分报告 → `report.md` / `report.csv`（含每个包的 私有 / 公共 分类与下载结果）

---

## 配置说明（Configuration）

`config.json` 字段：

| 字段                              | 说明                                           |
| ------------------------------- | -------------------------------------------- |
| `BaGet.BaseUrl`                 | 私有源服务索引地址（V3 `index.json`）                   |
| `BaGet.ApiKey`                  | 鉴权令牌；无需鉴权时留空                                 |
| `BaGet.AuthType`                | `none` / `bearer`（PAT）/ `basic`（`user:pass`） |
| `Public.FlatContainer`          | 公共源 flatcontainer 基址（默认 nuget.org）           |
| `Output.Dir`                    | 下载目录                                         |
| `Options.Threads`               | 下载并发数                                        |
| `Options.PageSize`              | search 分页大小                                  |
| `Options.IncludePrerelease`     | 是否包含预发行版本                                    |
| `Options.VersionLevel`          | 版本级比对（仅下载 nuget.org 上没有的版本）                  |
| `Options.EnumerateMode llLLL`   | `search`（默认）或 `v2`（OData 全量）                 |
| `Options.V2BaseUrl`             | `v2` 模式下的 OData 基址，如 `https://你的源/v2`        |
| `Options.IdFilter`              | 按包 ID 前缀过滤的正则（可选）                            |
| `Options.TreatUnknownAsPrivate` | 比对失败时是否按私有处理                                 |

---

## 工作原理（How it works）

1. 读取私有源服务索引，取得 Search / PackageBaseAddress 端点。
2. 枚举私有源全部包（含每个版本）。
3. 对每个包 ID 查询 nuget.org 的 flatcontainer 索引：
   - **404（不存在）** → 判定「私有独有」→ 下载其全部版本。
   - **200（存在）** → 判定「公共 / 镜像」→ 跳过（可选版本级仅下载私有独有版本）。
4. 下载时直接使用服务索引的 `PackageBaseAddress` 基址拼 URL；若失败则回退读取 registration 叶子的 `packageContent`；写入前校验 ZIP 文件头。
5. 并发下载并生成区分报告。

---

## 构建与发布（Build & Publish）

```bash
# 构建
dotnet build -c Release

# 发布为单文件可执行程序（Windows 示例）
dotnet publish -c Release -r win-x64 --self-contained false -o dist
```

发布后 `dist/NuGetSift.exe` 可直接运行，无需安装 .NET 运行时（若 `--self-contained true` 则连运行时一并打包）。

### 持续集成 / 自动发布（GitHub Actions）

仓库已内置 `.github/workflows/build.yml`，实现：

- **Push / PR** 到 `main`/`master`：在 **Windows / Linux / macOS** 三平台自动 `restore → build → --selftest`，确保代码始终可编译、区分逻辑正常。
- **打 `v*` 标签推送**（如 `git tag v1.0.0 && git push origin v1.0.0`）：自动发布 **win / linux / osx** 在 **x64、arm64 与 linux-arm（32 位）** 共七个架构的**单文件可执行程序**（`PublishSingleFile=true`，自包含运行时），产物按 `NuGetSift-<版本号>-<平台>.zip` 命名（如 `NuGetSift-v1.0.0-win-arm64.zip`），并创建 GitHub Release、打包上传、自动生成发布说明。

本地打标签发布示例：

```bash
git tag v1.0.0
git push origin v1.0.0        # 触发 release 任务，自动产出跨平台可执行文件
```

> 说明：构建使用 `actions/setup-dotnet@v4` 安装 **.NET 10 SDK**；发布任务依赖 `softprops/action-gh-release@v2` 创建 Release，已通过 `permissions: contents: write` 授权。

---

## 许可证（License）

[MIT](LICENSE) © NuGetSift Authors
