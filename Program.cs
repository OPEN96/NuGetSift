using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NuGetSift
{
    /// <summary>
    /// 枚举 BaGet（私有 NuGet V3 源）里的全部包，与 nuget.org（公共源）比对，
    /// 只下载“仅存在于私有源”的包，并生成私有/公共区分报告。
    /// </summary>
    internal class Program
    {
        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 64
        })
        { Timeout = TimeSpan.FromSeconds(60) };

        private const string DefaultConfigJson = @"{
  ""BaGet"": {
    ""BaseUrl"": ""https://your-baget-server/v3/index.json"",
    ""ApiKey"": """",
    ""AuthType"": ""none""
  },
  ""Public"": {
    ""FlatContainer"": ""https://api.nuget.org/v3-flatcontainer""
  },
  ""Output"": {
    ""Dir"": ""downloaded_private_packages"",
    ""ReportMd"": ""report.md"",
    ""ReportCsv"": ""report.csv""
  },
  ""Options"": {
    ""Threads"": 8,
    ""PageSize"": 100,
    ""IncludePrerelease"": true,
    ""VersionLevel"": false,
    ""TreatUnknownAsPrivate"": false,
    ""IdFilter"": """",
    ""EnumerateMode"": ""search"",
    ""V2BaseUrl"": """",
    ""MaxRetries"": 3
  }
}";

        private static async Task<int> Main(string[] args)
        {
            var opts = ParseArgs(args);
            if (opts.Help)
            {
                PrintHelp();
                return 0;
            }

            if (opts.Init)
            {
                File.WriteAllText(opts.ConfigPath, DefaultConfigJson);
                Console.WriteLine($"已生成默认配置文件: {opts.ConfigPath}");
                Console.WriteLine("请编辑 BaGet.BaseUrl / ApiKey / AuthType 后重新运行。");
                return 0;
            }

            if (opts.SelfTest)
            {
                return await SelfTest(opts);
            }

            AppConfig cfg;
            try
            {
                cfg = LoadConfig(opts.ConfigPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"无法加载配置 {opts.ConfigPath}: {ex.Message}");
                Console.Error.WriteLine("可运行: NuGetSift --init 生成默认配置。");
                return 1;
            }

            // 命令行覆盖
            if (!string.IsNullOrEmpty(opts.BagetUrl)) cfg.BaGet.BaseUrl = opts.BagetUrl;
            if (!string.IsNullOrEmpty(opts.OutputDir)) cfg.Output.Dir = opts.OutputDir;

            if (string.IsNullOrWhiteSpace(cfg.BaGet.BaseUrl) ||
                cfg.BaGet.BaseUrl.Contains("your-baget-server"))
            {
                Console.Error.WriteLine("错误: 请在配置中填写真实的 BaGet.BaseUrl（或加 --baget-url）。");
                return 1;
            }

            Console.WriteLine($"=== NuGetSift v1.0.0 ===");
            Console.WriteLine($"私有源(BaGet) : {cfg.BaGet.BaseUrl}");
            Console.WriteLine($"公共源        : {cfg.Public.FlatContainer}");
            Console.WriteLine($"输出目录      : {cfg.Output.Dir}");
            Console.WriteLine($"并发线程      : {cfg.Options.Threads}");
            Console.WriteLine($"比对粒度      : {(cfg.Options.VersionLevel ? "版本级" : "包ID级")}");
            Console.WriteLine();

            try
            {
                await Run(cfg, opts);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"运行失败: {ex.Message}");
                return 1;
            }

            return 0;
        }

        // ------------------------------------------------------------------

        private static async Task Run(AppConfig cfg, CliOptions opts)
        {
            Directory.CreateDirectory(cfg.Output.Dir);

            // 1) 发现 BaGet 服务索引，拿到 Search 与 FlatContainer 地址
            string idxUrl = NormalizeIndexUrl(cfg.BaGet.BaseUrl);
            Console.WriteLine($"读取服务索引: {idxUrl}");
            using var idxDoc = await GetJson(idxUrl, cfg.BaGet, cfg.Options.MaxRetries);
            string searchUrl = null;
            string baseAddr = null;
            foreach (var res in idxDoc.RootElement.GetProperty("resources").EnumerateArray())
            {
                var type = res.GetProperty("@type").GetString() ?? "";
                var id = res.GetProperty("@id").GetString() ?? "";
                if (type.Contains("SearchQueryService") && searchUrl == null) searchUrl = id;
                if (type.Contains("PackageBaseAddress") && baseAddr == null) baseAddr = id;
            }
            if (searchUrl == null)
            {
                searchUrl = idxUrl.Substring(0, idxUrl.LastIndexOf("index.json", StringComparison.OrdinalIgnoreCase))
                            + "search";
            }
            if (baseAddr == null)
            {
                baseAddr = idxUrl.Substring(0, idxUrl.LastIndexOf("index.json", StringComparison.OrdinalIgnoreCase))
                           + "flatcontainer";
            }
            Console.WriteLine($"Search 端点  : {searchUrl}");
            Console.WriteLine($"下载基址      : {baseAddr}");
            Console.WriteLine();

            // 2) 枚举私有源全部包
            List<PackageEntry> packages;
            if (cfg.Options.EnumerateMode.Equals("v2", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(cfg.Options.V2BaseUrl))
            {
                Console.WriteLine($"枚举模式: V2 OData ({cfg.Options.V2BaseUrl})");
                packages = await EnumerateV2(cfg.Options.V2BaseUrl, cfg, opts.Limit);
            }
            else
            {
                Console.WriteLine("枚举模式: V3 Search (分页，按空页/无新增终止)");
                packages = await EnumerateSearch(searchUrl, baseAddr, cfg, opts.Limit);
            }
            Console.WriteLine($"枚举完成: 共 {packages.Count} 个包ID, {packages.Sum(p => p.Versions.Count)} 个版本。");
            Console.WriteLine();

            // 3) 逐个与 nuget.org 比对（并发受限）
            Console.WriteLine("正在与 nuget.org 比对区分私有/公共...");
            var statusMap = new ConcurrentDictionary<string, PublicStatus>();
            int checkedCount = 0;
            await RunBounded(packages, Math.Max(1, cfg.Options.Threads * 2), async p =>
            {
                var st = await CheckPublic(p.Id, cfg);
                statusMap[p.Id] = st;
                int n = Interlocked.Increment(ref checkedCount);
                if (n % 200 == 0) Console.WriteLine($"  已比对 {n}/{packages.Count} ...");
            });

            // 4) 决定下载并下载（并发受限）
            var records = new ConcurrentBag<PackageRecord>();
            int publicCnt = 0, privateCnt = 0, unknownCnt = 0, downloaded = 0, failed = 0;
            var toDownload = new ConcurrentBag<(PackageEntry pkg, List<VersionEntry> vers, PublicStatus st)>();

            foreach (var p in packages)
            {
                var st = statusMap.GetOrAdd(p.Id, PublicStatus.Unknown);
                if (st == PublicStatus.Public)
                {
                    Interlocked.Increment(ref publicCnt);
                    string label = "公共(跳过)";
                    if (cfg.Options.VersionLevel)
                    {
                        // 版本级：仅下载 nuget.org 上没有的版本
                        var pubVers = await GetNugetOrgVersions(p.Id, cfg);
                        var onlyPriv = p.Versions
                            .Where(v => pubVers == null || !pubVers.Contains(v.Version.ToLowerInvariant()))
                            .ToList();
                        if (onlyPriv.Count > 0) { toDownload.Add((p, onlyPriv, st)); label = "公共(仅下载私有独有版本)"; }
                    }
                    records.Add(new PackageRecord(p.Id, label, p.Versions.Count, 0));
                }
                else if (st == PublicStatus.Private)
                {
                    Interlocked.Increment(ref privateCnt);
                    toDownload.Add((p, p.Versions, st));
                }
                else // Unknown
                {
                    Interlocked.Increment(ref unknownCnt);
                    if (cfg.Options.TreatUnknownAsPrivate)
                    {
                        toDownload.Add((p, p.Versions, st));
                        records.Add(new PackageRecord(p.Id, "未知(按私有下载)", p.Versions.Count, 0));
                    }
                    else
                    {
                        records.Add(new PackageRecord(p.Id, "未知(跳过)", p.Versions.Count, 0));
                    }
                }
            }

            if (opts.DryRun)
            {
                Console.WriteLine("[DRY-RUN] 不执行下载。");
            }
            else if (toDownload.Count > 0)
            {
                Console.WriteLine($"开始下载 {toDownload.Count} 个包（仅私有独有）...");
                await RunBounded(toDownload, cfg.Options.Threads, async item =>
                {
                    int ok = 0;
                    foreach (var v in item.vers)
                    {
                        bool success = await Download(item.pkg.Id, v, cfg, baseAddr);
                        if (success) { Interlocked.Increment(ref ok); Interlocked.Increment(ref downloaded); }
                        else Interlocked.Increment(ref failed);
                    }
                    // 更新 record 的下载数
                    var rec = records.FirstOrDefault(r => r.PackageId == item.pkg.Id);
                    if (rec != null) rec.Downloaded = ok;
                });
            }

            // 5) 写报告
            WriteReports(cfg, records, packages.Count, publicCnt, privateCnt, unknownCnt, downloaded, failed, opts.DryRun);

            Console.WriteLine();
            Console.WriteLine("=== 完成 ===");
            Console.WriteLine($"私有源包总数 : {packages.Count}");
            Console.WriteLine($"公共(跳过)   : {publicCnt}");
            Console.WriteLine($"私有独有     : {privateCnt}");
            Console.WriteLine($"未知(跳过)   : {unknownCnt}");
            Console.WriteLine($"已下载 .nupkg : {downloaded}  (失败 {failed})");
            Console.WriteLine($"报告         : {cfg.Output.ReportMd} / {cfg.Output.ReportCsv}");
        }

        // ------------------------------------------------------------------

        // 不信任 totalHits：翻页直到空页或无新增，避免私有源 search 截断导致漏包。
        private static async Task<List<PackageEntry>> EnumerateSearch(
            string searchUrl, string baseAddr, AppConfig cfg, int? limit)
        {
            var dict = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
            int skip = 0;
            int ps = Math.Max(1, cfg.Options.PageSize);
            var pre = cfg.Options.IncludePrerelease.ToString().ToLower(CultureInfo.InvariantCulture);
            Regex filter = null;
            if (!string.IsNullOrWhiteSpace(cfg.Options.IdFilter))
                filter = new Regex(cfg.Options.IdFilter, RegexOptions.IgnoreCase);

            while (true)
            {
                var url = $"{searchUrl}?q=&skip={skip}&take={ps}&prerelease={pre}&semVerLevel=2.0.0";
                using var doc = await GetJson(url, cfg.BaGet, cfg.Options.MaxRetries);
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) break;

                int added = 0;
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString() ?? "";
                    if (filter != null && !filter.IsMatch(id)) continue;
                    if (!dict.TryGetValue(id, out var entry))
                    {
                        var versions = new List<VersionEntry>();
                        if (item.TryGetProperty("versions", out var vArr))
                        {
                            foreach (var v in vArr.EnumerateArray())
                            {
                                var ver = v.GetProperty("version").GetString() ?? "";
                                // search 的 @id 是 registration 叶子(JSON 元数据，内含 packageContent 权威下载地址)，
                                // 并非包文件本身。仅保存为兜底解析源；真正下载用 BuildUrl 或 packageContent。
                                var reg = v.TryGetProperty("@id", out var at) ? (at.GetString() ?? "") : "";
                                versions.Add(new VersionEntry(ver, "", reg));
                            }
                        }
                        if (versions.Count == 0)
                            versions.AddRange(await GetPrivateVersions(id, baseAddr, cfg));
                        entry = new PackageEntry(id, versions);
                        dict[id] = entry;
                        added++;
                    }
                }

                // 本页没有任何新包 => 服务端已无更多数据（或 search 接口上限）=> 终止，防止死循环
                if (added == 0) break;
                skip += ps;
                if (limit.HasValue && dict.Count >= limit.Value) break;
                Console.WriteLine($"  已枚举 {dict.Count} 个包...");
            }
            return dict.Values.ToList();
        }

        // V2 OData 模式：直接读数据库级 Packages()，不受 search 索引上限影响，适合大私有源/检索不全。
        private static async Task<List<PackageEntry>> EnumerateV2(
            string v2Base, AppConfig cfg, int? limit)
        {
            var dict = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
            int skip = 0;
            int top = 200;
            int total = int.MaxValue;
            var atom = XNamespace.Get("http://www.w3.org/2005/Atom");
            var m = XNamespace.Get("http://schemas.microsoft.com/ado/2007/08/dataservices/metadata");
            var d = XNamespace.Get("http://schemas.microsoft.com/ado/2007/08/dataservices");

            while (skip < total)
            {
                var url = $"{v2Base.TrimEnd('/')}/Packages()?$format=atom&$select=Id,Version&$orderby=Id&$skip={skip}&$top={top}&$inlinecount=allpages";
                var xml = await GetText(url, cfg.BaGet, cfg.Options.MaxRetries);
                if (string.IsNullOrWhiteSpace(xml)) break;
                XDocument xdoc;
                try { xdoc = XDocument.Parse(xml); }
                catch { break; }
                var feed = xdoc.Root;
                var countEl = feed?.Element(m + "count");
                if (countEl != null && int.TryParse(countEl.Value, out var c)) total = c;

                var entries = feed?.Elements(atom + "entry") ?? Enumerable.Empty<XElement>();
                int added = 0;
                foreach (var e in entries)
                {
                    var props = e.Element(atom + "content")?.Element(m + "properties");
                    if (props == null) continue;
                    var idEl = props.Element(d + "Id");
                    var verEl = props.Element(d + "Version");
                    if (idEl == null || verEl == null) continue;
                    var id = (string)idEl;
                    var ver = (string)verEl;
                    if (!dict.TryGetValue(id, out var entry))
                    {
                        entry = new PackageEntry(id, new List<VersionEntry>());
                        dict[id] = entry;
                    }
                    if (!entry.Versions.Any(v => v.Version.Equals(ver, StringComparison.OrdinalIgnoreCase)))
                    {
                        entry.Versions.Add(new VersionEntry(ver, ""));
                        added++;
                    }
                }

                if (!entries.Any()) break;
                if (added == 0) break; // 已无新增（含 $skiptoken 边界保护）
                skip += top;
                if (limit.HasValue && dict.Count >= limit.Value) break;
                Console.WriteLine($"  已枚举 {dict.Count} 个包 (共 {total})...");
            }
            return dict.Values.ToList();
        }

        private static async Task<List<VersionEntry>> GetPrivateVersions(string id, string baseAddr, AppConfig cfg)
        {
            var url = $"{baseAddr.TrimEnd('/')}/{id.ToLowerInvariant()}/index.json";
            try
            {
                using var doc = await GetJson(url, cfg.BaGet, cfg.Options.MaxRetries);
                var list = new List<VersionEntry>();
                foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
                    list.Add(new VersionEntry(v.GetString() ?? "", ""));
                return list;
            }
            catch { return new List<VersionEntry>(); }
        }

        private static async Task<PublicStatus> CheckPublic(string id, AppConfig cfg)
        {
            var url = $"{cfg.Public.FlatContainer.TrimEnd('/')}/{id.ToLowerInvariant()}/index.json";
            var st = await GetStatus(url, cfg.Options.MaxRetries);
            if (st == System.Net.HttpStatusCode.OK) return PublicStatus.Public;
            if (st == System.Net.HttpStatusCode.NotFound) return PublicStatus.Private;
            return PublicStatus.Unknown;
        }

        private static async Task<HashSet<string>> GetNugetOrgVersions(string id, AppConfig cfg)
        {
            var url = $"{cfg.Public.FlatContainer.TrimEnd('/')}/{id.ToLowerInvariant()}/index.json";
            try
            {
                using var doc = await GetJson(url, null, cfg.Options.MaxRetries);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
                    set.Add(v.GetString() ?? "");
                return set;
            }
            catch { return null; }
        }

        private static async Task<bool> Download(string id, VersionEntry v, AppConfig cfg, string baseAddr)
        {
            // 候选下载地址：显式 DownloadUrl > 标准基址拼接 > registration.packageContent(最权威兜底)
            var primary = string.IsNullOrEmpty(v.DownloadUrl)
                ? BuildUrl(baseAddr, id, v.Version)
                : v.DownloadUrl;

            byte[] bytes = await GetBytes(primary, cfg.BaGet, cfg.Options.MaxRetries);
            string usedUrl = primary;

            // 自愈：标准拼接失败（路径约定不一致）时，用 registration 叶子的 packageContent 重试
            if (bytes == null && !string.IsNullOrEmpty(v.RegistrationUrl))
            {
                var pc = await GetPackageContentUrl(v.RegistrationUrl, cfg.BaGet, cfg.Options.MaxRetries);
                if (!string.IsNullOrEmpty(pc))
                {
                    bytes = await GetBytes(pc, cfg.BaGet, cfg.Options.MaxRetries);
                    usedUrl = pc;
                }
            }

            if (bytes == null)
            {
                Console.Error.WriteLine($"  [失败] {id} {v.Version} 下载失败 (主地址={primary})");
                return false;
            }

            // 安全校验：.nupkg 本质是 ZIP 文件，必须以 PK\x03\x04 (0x50 0x4B 0x03 0x04) 开头。
            // 若下载到的其实是 JSON/HTML（URL 拼错），直接判定失败并告警，避免写出假文件。
            if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B || bytes[2] != 0x03 || bytes[3] != 0x04)
            {
                var head = bytes.Length >= 64
                    ? System.Text.Encoding.ASCII.GetString(bytes, 0, 64)
                    : (bytes.Length > 0 ? System.Text.Encoding.ASCII.GetString(bytes, 0, bytes.Length) : "(空)");
                Console.Error.WriteLine($"  [警告] {id} {v.Version} 下载内容不是有效的 .nupkg(ZIP)，已跳过。URL={usedUrl}");
                Console.Error.WriteLine($"          文件头: {head.Replace("\n", " ").Replace("\r", " ")}");
                return false;
            }

            var path = Path.Combine(cfg.Output.Dir, $"{id}.{v.Version}.nupkg");
            await File.WriteAllBytesAsync(path, bytes);
            return true;
        }

        // 读取 registration 叶子 JSON，提取最权威的包下载地址 packageContent。
        private static async Task<string> GetPackageContentUrl(string registrationUrl, BaGetConfig cfg, int retries)
        {
            for (int i = 0; i <= retries; i++)
            {
                try
                {
                    using var doc = await GetJson(registrationUrl, cfg, Math.Min(1, retries));
                    if (doc.RootElement.TryGetProperty("packageContent", out var pc))
                        return pc.GetString() ?? "";
                    return "";
                }
                catch (Exception) when (i < retries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                }
            }
            return "";
        }

        // ------------------------------------------------------------------
        // HTTP helpers
        // ------------------------------------------------------------------

        private static string BuildUrl(string baseAddr, string id, string version)
        {
            // baseAddr 应为服务索引 PackageBaseAddress 资源的 @id（不同 BaGet 部署路径不同：
            // 可能是 .../v3/flatcontainer，也可能是 .../v3/package 等）。直接使用，绝不再自行追加
            // /flatcontainer——这正是之前“全部下载失败”的根因（你的源是 /v3/package）。
            var b = baseAddr.TrimEnd('/');
            var lowerId = id.ToLowerInvariant();
            return $"{b}/{lowerId}/{version}/{lowerId}.{version}.nupkg";
        }

        private static void ApplyAuth(HttpRequestMessage req, BaGetConfig cfg)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.ApiKey)) return;
            if (string.Equals(cfg.AuthType, "bearer", StringComparison.OrdinalIgnoreCase))
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            else if (string.Equals(cfg.AuthType, "basic", StringComparison.OrdinalIgnoreCase))
            {
                var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(cfg.ApiKey));
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
            }
        }

        private static async Task<JsonDocument> GetJson(string url, BaGetConfig cfg, int retries)
        {
            for (int i = 0; i <= retries; i++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    ApplyAuth(req, cfg);
                    req.Headers.Add("Accept", "application/json");
                    using var resp = await Http.SendAsync(req);
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        throw new HttpRequestException("404");
                    resp.EnsureSuccessStatusCode();
                    var stream = await resp.Content.ReadAsStreamAsync();
                    return await JsonDocument.ParseAsync(stream);
                }
                catch (Exception) when (i < retries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                }
            }
            throw new Exception($"HTTP 请求失败(已达重试上限): {url}");
        }

        private static async Task<System.Net.HttpStatusCode> GetStatus(string url, int retries)
        {
            for (int i = 0; i <= retries; i++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp = await Http.SendAsync(req);
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        return System.Net.HttpStatusCode.NotFound;
                    if ((int)resp.StatusCode >= 500 ||
                        resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        if (i < retries) { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i))); continue; }
                    }
                    return resp.StatusCode;
                }
                catch (Exception) when (i < retries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                }
            }
            return System.Net.HttpStatusCode.ServiceUnavailable;
        }

        private static async Task<string> GetText(string url, BaGetConfig cfg, int retries)
        {
            for (int i = 0; i <= retries; i++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    ApplyAuth(req, cfg);
                    req.Headers.Add("Accept", "application/atom+xml,application/xml,xml");
                    using var resp = await Http.SendAsync(req);
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return "";
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsStringAsync();
                }
                catch (Exception) when (i < retries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                }
            }
            return "";
        }

        private static async Task<byte[]> GetBytes(string url, BaGetConfig cfg, int retries)
        {
            for (int i = 0; i <= retries; i++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    ApplyAuth(req, cfg);
                    using var resp = await Http.SendAsync(req);
                    if (!resp.IsSuccessStatusCode)
                    {
                        if (i < retries && ((int)resp.StatusCode >= 500 ||
                                            resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests))
                        { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i))); continue; }
                        return null;
                    }
                    return await resp.Content.ReadAsByteArrayAsync();
                }
                catch (Exception) when (i < retries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // 并发工具
        // ------------------------------------------------------------------

        private static async Task RunBounded<T>(
            IEnumerable<T> items, int concurrency, Func<T, Task> work)
        {
            using var sem = new SemaphoreSlim(concurrency);
            var tasks = items.Select(async it =>
            {
                await sem.WaitAsync();
                try { await work(it); }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);
        }

        // ------------------------------------------------------------------
        // 报告
        // ------------------------------------------------------------------

        private static void WriteReports(AppConfig cfg, IEnumerable<PackageRecord> records,
            int total, int pub, int priv, int unk, int downloaded, int failed, bool dry)
        {
            var list = records.OrderBy(r => r.PackageId, StringComparer.OrdinalIgnoreCase).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("# NuGet 私有/公共包区分报告");
            sb.AppendLine();
            sb.AppendLine($"- 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- 比对方式: 包ID是否存在于 nuget.org（{(cfg.Options.VersionLevel ? "版本级" : "包ID级")}）");
            sb.AppendLine($"- 私有源: {cfg.BaGet.BaseUrl}");
            sb.AppendLine($"- 公共源: {cfg.Public.FlatContainer}");
            sb.AppendLine();
            sb.AppendLine("## 汇总");
            sb.AppendLine();
            sb.AppendLine("| 指标 | 数量 |");
            sb.AppendLine("| --- | --- |");
            sb.AppendLine($"| 私有源包总数 | {total} |");
            sb.AppendLine($"| 公共(跳过) | {pub} |");
            sb.AppendLine($"| 私有独有 | {priv} |");
            sb.AppendLine($"| 未知(跳过) | {unk} |");
            sb.AppendLine($"| 已下载 .nupkg | {downloaded} |");
            sb.AppendLine($"| 下载失败 | {failed} |");
            sb.AppendLine();
            sb.AppendLine("## 明细");
            sb.AppendLine();
            sb.AppendLine("| 包ID | 分类 | 版本数 | 已下载 |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var r in list)
                sb.AppendLine($"| {r.PackageId} | {r.Classification} | {r.Versions} | {r.Downloaded} |");
            File.WriteAllText(cfg.Output.ReportMd, sb.ToString());

            var csv = new StringBuilder();
            csv.AppendLine("PackageId,Classification,Versions,Downloaded");
            foreach (var r in list)
                csv.AppendLine($"{r.PackageId},{r.Classification},{r.Versions},{r.Downloaded}");
            File.WriteAllText(cfg.Output.ReportCsv, csv.ToString());
        }

        // ------------------------------------------------------------------
        // 自检（不依赖私有源，仅验证“区分”逻辑）
        // ------------------------------------------------------------------

        private static async Task<int> SelfTest(CliOptions opts)
        {
            var cfg = new AppConfig();
            if (File.Exists(opts.ConfigPath))
            {
                try { cfg = LoadConfig(opts.ConfigPath); } catch { }
            }
            Console.WriteLine("=== 自检: 验证 公共/私有 区分逻辑 ===");
            var samples = new[]
            {
                ("Newtonsoft.Json", PublicStatus.Public),   // 公共
                ("Microsoft.NETCore.App", PublicStatus.Public),
                ("ThisPackageSurelyDoesNotExistZzz12345", PublicStatus.Private), // 私有独有(不存在)
            };
            int ok = 0;
            foreach (var (id, expected) in samples)
            {
                var actual = await CheckPublic(id, cfg);
                bool pass = actual == expected;
                if (pass) ok++;
                Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {id} -> {actual} (期望 {expected})");
            }
            Console.WriteLine($"自检结果: {ok}/{samples.Length} 通过。");
            return ok == samples.Length ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // 配置 & 参数
        // ------------------------------------------------------------------

        private static AppConfig LoadConfig(string path)
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (cfg == null) throw new Exception("配置反序列化失败");
            cfg.BaGet ??= new BaGetConfig();
            cfg.Public ??= new PublicConfig();
            cfg.Output ??= new OutputConfig();
            cfg.Options ??= new OptionsConfig();
            return cfg;
        }

        private static string NormalizeIndexUrl(string baseUrl)
        {
            var u = baseUrl.Trim();
            if (u.Contains("index.json", StringComparison.OrdinalIgnoreCase)) return u;
            return u.TrimEnd('/') + "/v3/index.json";
        }

        private static CliOptions ParseArgs(string[] args)
        {
            var o = new CliOptions { ConfigPath = "config.json" };
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--help": case "-h": o.Help = true; break;
                    case "--init": o.Init = true; break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--selftest": o.SelfTest = true; break;
                    case "--config": o.ConfigPath = args[++i]; break;
                    case "--baget-url": o.BagetUrl = args[++i]; break;
                    case "--output-dir": o.OutputDir = args[++i]; break;
                    case "--limit":
                        if (int.TryParse(args[++i], out var n)) o.Limit = n; break;
                }
            }
            return o;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("NuGetSift - 区分私有/公共 NuGet 源并下载仅私有独有的包");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  NuGetSift --init                生成默认 config.json");
            Console.WriteLine("  NuGetSift --selftest            自检区分逻辑(无需私有源)");
            Console.WriteLine("  NuGetSift                       按 config.json 执行");
            Console.WriteLine("  NuGetSift --dry-run             只枚举+比对，不下载");
            Console.WriteLine("  NuGetSift --limit 50            只处理前 50 个包(测试)");
            Console.WriteLine();
            Console.WriteLine("选项:");
            Console.WriteLine("  --config <path>     配置文件路径 (默认 config.json)");
            Console.WriteLine("  --baget-url <url>   覆盖 BaGet 索引地址");
            Console.WriteLine("  --output-dir <dir>  覆盖下载目录");
            Console.WriteLine();
            Console.WriteLine("判定规则: 包ID 不在 nuget.org 上 => 私有独有 => 下载其全部版本");
            Console.WriteLine();
            Console.WriteLine("枚举不全时: 在 config.json 中将 Options.EnumerateMode 改为 \"v2\"");
            Console.WriteLine("并填写 Options.V2BaseUrl (如 https://你的baget/v2)，走 OData 全量列举。");
        }
    }

    // ----------------------------------------------------------------------
    // 模型
    // ----------------------------------------------------------------------

    internal enum PublicStatus { Public, Private, Unknown }

    internal class PackageEntry
    {
        public string Id;
        public List<VersionEntry> Versions;
        public PackageEntry(string id, List<VersionEntry> versions) { Id = id; Versions = versions; }
    }

    internal class VersionEntry
    {
        public string Version;
        public string DownloadUrl;
        // registration 叶子 JSON 地址（含 packageContent 权威下载地址），作为下载兜底解析源。
        public string RegistrationUrl;
        public VersionEntry(string version, string downloadUrl, string registrationUrl = "")
        { Version = version; DownloadUrl = downloadUrl; RegistrationUrl = registrationUrl; }
    }

    internal class PackageRecord
    {
        public string PackageId;
        public string Classification;
        public int Versions;
        public int Downloaded;
        public PackageRecord(string id, string cls, int vers, int dl)
        { PackageId = id; Classification = cls; Versions = vers; Downloaded = dl; }
    }

    internal class AppConfig
    {
        public BaGetConfig BaGet { get; set; }
        public PublicConfig Public { get; set; }
        public OutputConfig Output { get; set; }
        public OptionsConfig Options { get; set; }
    }

    internal class BaGetConfig
    {
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string AuthType { get; set; } = "none";
    }

    internal class PublicConfig
    {
        public string FlatContainer { get; set; } = "https://api.nuget.org/v3-flatcontainer";
    }

    internal class OutputConfig
    {
        public string Dir { get; set; } = "downloaded_private_packages";
        public string ReportMd { get; set; } = "report.md";
        public string ReportCsv { get; set; } = "report.csv";
    }

    internal class OptionsConfig
    {
        public int Threads { get; set; } = 8;
        public int PageSize { get; set; } = 100;
        public bool IncludePrerelease { get; set; } = true;
        public bool VersionLevel { get; set; } = false;
        public bool TreatUnknownAsPrivate { get; set; } = false;
        public string IdFilter { get; set; } = "";
        public string EnumerateMode { get; set; } = "search";
        public string V2BaseUrl { get; set; } = "";
        public int MaxRetries { get; set; } = 3;
    }

    internal class CliOptions
    {
        public bool Help;
        public bool Init;
        public bool DryRun;
        public bool SelfTest;
        public string ConfigPath = "config.json";
        public string BagetUrl;
        public string OutputDir;
        public int? Limit;
    }
}
