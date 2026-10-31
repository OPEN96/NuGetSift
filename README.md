# NuGetSift

> 枚举私有 NuGet 源（Bagetter / BaGet / Nexus / Artifactory 等兼容 NuGet V3 的源），与 nuget.org 逐一比对，**只下载「仅存在于私有源」的包**，并生成私有 / 公共区分报告。
>
> 提供 **桌面客户端**（Avalonia，跨平台图形界面）与 **命令行工具** 两种形态。

![界面截图](docs/screenshot.png)

---

## 两种形态

| 形态 | 项目 | 说明 |
| --- | --- | --- |
| 🖥️ 桌面客户端 | `src/NuGetSift.Desktop` | 图形界面：填地址 → 测试连接 → 扫描 → 筛选查看 → 一键下载，实时进度与运行日志 |
| ⌨️ 命令行 | `src/NuGetSift.Cli` | 适合脚本 / CI / 服务器环境，一条命令完成全流程 |

两者共用同一套引擎 `src/NuGetSift.Core`，行为与判定规则完全一致。

---

## 特性（Features）

- **自动区分私有 / 公共**：包 ID 在公共源（nuget.org）上不存在（404）→ 判定「私有独有」→ 下载其全部版本。
- 支持任意兼容 NuGet V3 的私有源：Bagetter、BaGet、Nexus、Artifactory 等。
- **两种枚举模式**：
  - **V3 Search**（默认）：健壮翻页，翻到空页 / 本页无新增即止，**不受 `totalHits` 截断影响**（不会只拿到 100 个）。
  - **V2 OData**：直连 `Packages()` 全量列举，适合超大私有源 / search 检索不全的场景。
- **自适应包文件基址**：直接使用服务索引里的 `PackageBaseAddress`（Bagetter 为 `/v3/package`，标准 BaGet 为 `/v3/flatcontainer`），不硬编码路径。
- **下载兜底自愈**：标准路径失败时，自动回退读取 registration 叶子里的 `packageContent`（最权威的下载地址）。
- **写盘前安全校验**：`.nupkg` 本质是 ZIP，必须以 `PK\x03\x04` 开头；下到 JSON / HTML 直接判失败，**绝不写出假包**。
- **版本级比对**（可选）：公共包也下载「nuget.org 上没有的那几个版本」。
- 并发下载、可取消、生成 Markdown / CSV 区分报告。

---

## 目录结构（Project Structure）

```text
NuGetSift/
├── NuGetSift.slnx                     # 解决方案
├── src/
│   ├── NuGetSift.Core/                # 共享引擎（枚举 / 比对 / 下载 / 报告）
│   │   ├── SourceClient.cs            #   源访问：服务发现、枚举、比对、下载
│   │   ├── SiftEngine.cs              #   流程编排（扫描 / 下载，带进度与取消）
│   │   ├── ConfigStore.cs             #   config.json 读写
│   │   ├── ReportWriter.cs            #   Markdown / CSV 报告
│   │   ├── Http.cs                    #   带重试与鉴权的 HTTP 封装
│   │   └── Models.cs                  #   模型与配置类型
│   ├── NuGetSift.Cli/                 # 命令行入口
│   └── NuGetSift.Desktop/             # 桌面客户端（Avalonia）
│       ├── MainWindow.axaml           #   主界面
│       ├── App.axaml                  #   暗色主题与控件样式
│       └── ViewModels/                #   MVVM 视图模型
├── docs/screenshot.png                # 界面截图
├── config.json.example                # 配置模板（复制为 config.json 后编辑）
├── nuget.config                       # 本地 NuGet 源配置
├── LICENSE                            # MIT
└── .github/workflows/build.yml        # CI：三平台构建 + 七架构发布
```

---

## 快速开始（Quick Start）

### 1. 搭建私有 NuGet 源（Bagetter）

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
- 服务索引地址：`http://localhost:5000/v3/index.json`。
- 运维：`docker stop nuget-server` / `docker start nuget-server` / `docker logs -f nuget-server`。

### 2. 推送私有包

```bash
dotnet nuget push -s http://localhost:5000/v3/index.json *.nupkg --allow-insecure-connections
```

- `--allow-insecure-connections`：目标是 `http://`（非 HTTPS）源，需此参数；已配 HTTPS 可省略。
- 若服务器开启 ApiKey 校验，追加 `-k <你的ApiKey>`。

### 3. 用桌面客户端（推荐）

```bash
git clone <本仓库> && cd NuGetSift
dotnet run --project src/NuGetSift.Desktop -c Release
```

界面操作顺序：

1. 左侧填「私有源索引地址」（如 `http://192.168.1.12:5000/v3/index.json`），需要鉴权时选 Bearer / Basic 并填令牌。
2. 选「下载目录」（默认为 `我的文档\NuGetSift\packages`）。
3. 点 **测试连接** 确认能读到服务索引。
4. 点 **开始扫描**：自动枚举并逐一与公共源比对，列表按「私有独有 → 未知 → 公共」排序。
5. 点 **下载私有包**：只下载私有源独有的版本，底部实时显示进度；完成后 `report.md` / `report.csv` 生成在下载目录。

### 4. 用命令行

```bash
# 生成默认配置（或手动复制 config.json.example 为 config.json）
dotnet run --project src/NuGetSift.Cli -- --init

# 编辑 config.json，把 BaGet.BaseUrl 改成你的私有源索引地址
dotnet run --project src/NuGetSift.Cli -- --selftest          # 自检区分逻辑（只需连 nuget.org）
dotnet run --project src/NuGetSift.Cli -- --dry-run           # 只枚举 + 比对，不下载
dotnet run --project src/NuGetSift.Cli                        # 正式执行
```

常用参数：

| 参数 | 说明 |
| --- | --- |
| `--init` | 生成默认 `config.json` |
| `--selftest` | 自检：验证公共 / 私有区分逻辑 |
| `--dry-run` | 只扫描不下载 |
| `--limit <n>` | 只处理前 n 个包（按页截断，测试用） |
| `--baget-url <url>` | 覆盖私有源索引地址 |
| `--output-dir <dir>` | 覆盖下载目录 |
| `--config <path>` | 指定配置文件路径 |

---

## 配置说明（Configuration）

`config.json` 字段：

| 字段 | 说明 |
| --- | --- |
| `BaGet.BaseUrl` | 私有源服务索引地址（V3 `index.json`） |
| `BaGet.ApiKey` | 鉴权令牌；无需鉴权时留空 |
| `BaGet.AuthType` | `none` / `bearer`（PAT）/ `basic`（`user:pass`） |
| `Public.FlatContainer` | 公共源 flatcontainer 基址（默认 nuget.org） |
| `Output.Dir` | 下载目录 |
| `Output.ReportMd` / `ReportCsv` | 报告输出路径（客户端默认写在下载目录内） |
| `Options.Threads` | 下载并发数（默认 8） |
| `Options.PageSize` | search 分页大小（默认 100） |
| `Options.IncludePrerelease` | 是否包含预发行版本 |
| `Options.VersionLevel` | 版本级比对：公共包也下载 nuget.org 上没有的版本 |
| `Options.EnumerateMode` | `search`（默认）或 `v2`（OData 全量列举） |
| `Options.V2BaseUrl` | `v2` 模式下的 OData 基址，如 `http://host:5000/v2` |
| `Options.IdFilter` | 按包 ID 过滤的正则（可选，留空 = 全部） |
| `Options.TreatUnknownAsPrivate` | 比对失败（网络异常等）时是否按私有处理 |
| `Options.MaxRetries` | HTTP 重试次数 |

> 桌面客户端会把配置保存到**程序所在目录**的 `config.json`；命令行默认读取当前目录的 `config.json`。该文件已被 `.gitignore` 忽略，避免泄露真实地址与令牌。

---

## 工作原理（How it works）

1. 读取私有源服务索引，取得 `SearchQueryService` 与 `PackageBaseAddress` 两个端点。
2. 枚举私有源全部包及其版本（V3 Search 健壮翻页，或 V2 OData 全量）。
3. 逐包向公共源查询 `flatcontainer/{id}/index.json`：
   - **404** → 私有独有 → 下载其全部版本；
   - **200** → 公共 / 镜像 → 跳过（开启版本级比对时，仅下载 nuget.org 上缺失的版本）。
4. 下载用 `PackageBaseAddress` 基址按标准规则拼 URL；失败则回退 `packageContent`；写盘前校验 ZIP 文件头。
5. 并发下载并生成 Markdown / CSV 报告。

---

## 构建与发布（Build & Publish）

```bash
# 构建全部
dotnet build NuGetSift.slnx -c Release

# 发布桌面客户端（Windows 单文件、自包含）
dotnet publish src/NuGetSift.Desktop -c Release -r win-x64 \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --self-contained true -o dist/desktop

# 发布命令行（单文件、自包含）
dotnet publish src/NuGetSift.Cli -c Release -r win-x64 \
  -p:PublishSingleFile=true --self-contained true -o dist/cli
```

### 持续集成 / 自动发布（GitHub Actions）

`.github/workflows/build.yml`：

- **Push / PR** 到 `main` / `master`：在 **Windows / Ubuntu / macOS** 三平台执行 `restore → build → --selftest`。
- **打 `v*` 标签**（如 `git tag v1.0.0 && git push origin v1.0.0`）：自动发布单文件自包含程序
  - 桌面客户端：`win-x64 / win-arm64 / linux-x64 / linux-arm64 / osx-x64 / osx-arm64`（六架构）
  - 命令行：上述六架构 + `linux-arm`（32 位 ARM，共七架构）
  - 产物命名：`NuGetSift-<tag>-desktop-<rid>.zip` / `NuGetSift-<tag>-cli-<rid>.zip`，由 `softprops/action-gh-release@v2` 上传到 Release。

```bash
git tag v1.0.0
git push origin v1.0.0
```

---

## 许可证（License）

[MIT](LICENSE) © NuGetSift Authors
