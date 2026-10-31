using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NuGetSift.Core;

/// <summary>服务索引里解析出的端点。</summary>
public sealed record SourceEndpoints(string IndexUrl, string SearchUrl, string PackageBaseAddress);

/// <summary>NuGet 源访问：服务发现、枚举、公共比对、包下载。</summary>
public sealed class SourceClient
{
    // ------------------------------------------------------------------
    // 服务索引
    // ------------------------------------------------------------------

    /// <summary>读取 V3 服务索引，解析 SearchQueryService 与 PackageBaseAddress。</summary>
    public async Task<SourceEndpoints> DiscoverAsync(AppConfig cfg, CancellationToken ct)
    {
        var idxUrl = ConfigStore.NormalizeIndexUrl(cfg.BaGet.BaseUrl);
        if (string.IsNullOrWhiteSpace(idxUrl))
            throw new InvalidOperationException("私有源地址为空，请先填写（例如 http://192.168.1.12:5000/v3/index.json）。");

        using var doc = await Http.GetJsonAsync(idxUrl, cfg.BaGet, cfg.Options.MaxRetries, ct)
            .ConfigureAwait(false);

        string? search = null, baseAddr = null;
        if (doc.RootElement.TryGetProperty("resources", out var resources))
        {
            foreach (var res in resources.EnumerateArray())
            {
                var type = res.TryGetProperty("@type", out var t) ? t.GetString() ?? "" : "";
                var id = res.TryGetProperty("@id", out var i) ? i.GetString() ?? "" : "";
                if (search == null && type.Contains("SearchQueryService", StringComparison.OrdinalIgnoreCase))
                    search = id;
                if (baseAddr == null && type.Contains("PackageBaseAddress", StringComparison.OrdinalIgnoreCase))
                    baseAddr = id;
            }
        }

        var prefix = idxUrl[..idxUrl.LastIndexOf("index.json", StringComparison.OrdinalIgnoreCase)];
        search ??= prefix + "search";
        baseAddr ??= prefix + "flatcontainer";

        return new SourceEndpoints(idxUrl, search, baseAddr);
    }

    // ------------------------------------------------------------------
    // 枚举
    // ------------------------------------------------------------------

    /// <summary>
    /// V3 Search 分页枚举。不信任 totalHits：一直翻页直到空页或本页无新增包，
    /// 避免私有源 search 索引截断（常见只回 100 条）导致漏包。
    /// </summary>
    public async Task<List<PackageEntry>> EnumerateSearchAsync(
        SourceEndpoints ep, AppConfig cfg, int? limit,
        Action<int>? onProgress, CancellationToken ct)
    {
        var dict = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
        var pageSize = Math.Max(1, cfg.Options.PageSize);
        var pre = cfg.Options.IncludePrerelease.ToString().ToLowerInvariant();
        Regex? filter = null;
        if (!string.IsNullOrWhiteSpace(cfg.Options.IdFilter))
            filter = new Regex(cfg.Options.IdFilter, RegexOptions.IgnoreCase);

        var skip = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{ep.SearchUrl}?q=&skip={skip}&take={pageSize}&prerelease={pre}&semVerLevel=2.0.0";
            using var doc = await Http.GetJsonAsync(url, cfg.BaGet, cfg.Options.MaxRetries, ct)
                .ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("data", out var data)) break;
            if (data.GetArrayLength() == 0) break;

            var added = 0;
            foreach (var item in data.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (id.Length == 0) continue;
                if (filter != null && !filter.IsMatch(id)) continue;
                if (dict.ContainsKey(id)) continue;

                var versions = new List<VersionEntry>();
                if (item.TryGetProperty("versions", out var vArr))
                {
                    foreach (var v in vArr.EnumerateArray())
                    {
                        var ver = v.TryGetProperty("version", out var vv) ? vv.GetString() ?? "" : "";
                        if (ver.Length == 0) continue;
                        // search 的 @id 指向 registration 叶子（JSON 元数据，内含 packageContent 权威地址），
                        // 不是包文件本身；仅存作下载兜底。
                        var reg = v.TryGetProperty("@id", out var at) ? at.GetString() ?? "" : "";
                        versions.Add(new VersionEntry(ver, "", reg));
                    }
                }

                if (versions.Count == 0)
                    versions.AddRange(await GetVersionsAsync(ep.PackageBaseAddress, id, cfg, ct).ConfigureAwait(false));

                dict[id] = new PackageEntry(id, versions);
                added++;
            }

            if (added == 0) break;           // 服务端已无更多数据 / search 上限，防死循环
            if (limit is > 0 && dict.Count >= limit.Value) break;

            skip += pageSize;
            onProgress?.Invoke(dict.Count);
        }

        return dict.Values.ToList();
    }

    /// <summary>
    /// V2 OData 枚举：直接读数据库级 Packages()，不受 search 索引上限影响。
    /// 适用于大私有源或 search 检索不全的场景。
    /// </summary>
    public async Task<List<PackageEntry>> EnumerateV2Async(
        string v2Base, AppConfig cfg, int? limit,
        Action<int>? onProgress, CancellationToken ct)
    {
        var dict = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
        const int top = 200;
        var skip = 0;
        var total = int.MaxValue;

        var atom = XNamespace.Get("http://www.w3.org/2005/Atom");
        var m = XNamespace.Get("http://schemas.microsoft.com/ado/2007/08/dataservices/metadata");
        var d = XNamespace.Get("http://schemas.microsoft.com/ado/2007/08/dataservices");

        while (skip < total)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{v2Base.TrimEnd('/')}/Packages()?$format=atom&$select=Id,Version" +
                      $"&$orderby=Id&$skip={skip}&$top={top}&$inlinecount=allpages";

            var xml = await Http.GetTextAsync(url, cfg.BaGet, cfg.Options.MaxRetries, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(xml)) break;

            XDocument xdoc;
            try { xdoc = XDocument.Parse(xml); }
            catch { break; }

            var feed = xdoc.Root;
            var countEl = feed?.Element(m + "count");
            if (countEl != null && int.TryParse(countEl.Value, out var c)) total = c;

            var entries = feed?.Elements(atom + "entry").ToList() ?? new List<XElement>();
            if (entries.Count == 0) break;

            var added = 0;
            foreach (var e in entries)
            {
                var props = e.Element(atom + "content")?.Element(m + "properties");
                if (props == null) continue;

                var id = (string?)props.Element(d + "Id");
                var ver = (string?)props.Element(d + "Version");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ver)) continue;

                if (!dict.TryGetValue(id, out var entry))
                {
                    entry = new PackageEntry(id, new List<VersionEntry>());
                    dict[id] = entry;
                }

                if (!entry.Versions.Any(v => v.Version.Equals(ver, StringComparison.OrdinalIgnoreCase)))
                {
                    entry.Versions.Add(new VersionEntry(ver));
                    added++;
                }
            }

            if (added == 0) break;
            if (limit is > 0 && dict.Count >= limit.Value) break;

            skip += top;
            onProgress?.Invoke(dict.Count);
        }

        return dict.Values.ToList();
    }

    /// <summary>用 PackageBaseAddress 的 flatcontainer 索引补全某个包的全部版本。</summary>
    public async Task<List<VersionEntry>> GetVersionsAsync(
        string packageBaseAddress, string id, AppConfig cfg, CancellationToken ct)
    {
        var url = $"{packageBaseAddress.TrimEnd('/')}/{id.ToLowerInvariant()}/index.json";
        try
        {
            using var doc = await Http.GetJsonAsync(url, cfg.BaGet, cfg.Options.MaxRetries, ct)
                .ConfigureAwait(false);
            var list = new List<VersionEntry>();
            if (doc.RootElement.TryGetProperty("versions", out var arr))
            {
                foreach (var v in arr.EnumerateArray())
                {
                    var ver = v.GetString();
                    if (!string.IsNullOrWhiteSpace(ver)) list.Add(new VersionEntry(ver));
                }
            }
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch { return new List<VersionEntry>(); }
    }

    // ------------------------------------------------------------------
    // 公共 / 私有 判定
    // ------------------------------------------------------------------

    /// <summary>包ID 在公共源上返回 200 => 公共；404 => 私有独有；其它 => 未知。</summary>
    public async Task<PublicStatus> CheckPublicAsync(string id, AppConfig cfg, CancellationToken ct)
    {
        var url = $"{cfg.Public.FlatContainer.TrimEnd('/')}/{id.ToLowerInvariant()}/index.json";
        var status = await Http.GetStatusAsync(url, cfg.Options.MaxRetries, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.OK) return PublicStatus.Public;
        if (status == HttpStatusCode.NotFound) return PublicStatus.Private;
        return PublicStatus.Unknown;
    }

    /// <summary>取该包在公共源上已有的版本集合（版本级比对用）；失败返回 null。</summary>
    public async Task<HashSet<string>?> GetPublicVersionsAsync(string id, AppConfig cfg, CancellationToken ct)
    {
        var url = $"{cfg.Public.FlatContainer.TrimEnd('/')}/{id.ToLowerInvariant()}/index.json";
        try
        {
            using var doc = await Http.GetJsonAsync(url, null, cfg.Options.MaxRetries, ct)
                .ConfigureAwait(false);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("versions", out var arr))
            {
                foreach (var v in arr.EnumerateArray())
                {
                    var ver = v.GetString();
                    if (!string.IsNullOrWhiteSpace(ver)) set.Add(ver);
                }
            }
            return set;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ------------------------------------------------------------------
    // 下载
    // ------------------------------------------------------------------

    /// <summary>按标准规则拼接 .nupkg 下载地址。</summary>
    public static string BuildUrl(string packageBaseAddress, string id, string version)
    {
        // 直接使用服务索引 PackageBaseAddress 的 @id（不同部署路径不同：/v3/flatcontainer、/v3/package 等），
        // 绝不自行追加 /flatcontainer。
        var b = packageBaseAddress.TrimEnd('/');
        var lowerId = id.ToLowerInvariant();
        return $"{b}/{lowerId}/{version}/{lowerId}.{version}.nupkg";
    }

    /// <summary>
    /// 下载单个版本到输出目录。返回 (是否成功, 说明)。
    /// 主地址失败时用 registration.packageContent 自愈；写盘前校验 ZIP 文件头，绝不落假文件。
    /// </summary>
    public async Task<(bool Ok, string Message)> DownloadAsync(
        string id, VersionEntry v, AppConfig cfg, string packageBaseAddress, CancellationToken ct)
    {
        var primary = string.IsNullOrWhiteSpace(v.DownloadUrl)
            ? BuildUrl(packageBaseAddress, id, v.Version)
            : v.DownloadUrl;

        var bytes = await Http.GetBytesAsync(primary, cfg.BaGet, cfg.Options.MaxRetries, ct).ConfigureAwait(false);
        var usedUrl = primary;

        if (bytes == null && !string.IsNullOrWhiteSpace(v.RegistrationUrl))
        {
            var pc = await GetPackageContentUrlAsync(v.RegistrationUrl, cfg, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(pc))
            {
                bytes = await Http.GetBytesAsync(pc, cfg.BaGet, cfg.Options.MaxRetries, ct).ConfigureAwait(false);
                usedUrl = pc;
            }
        }

        if (bytes == null)
            return (false, $"下载失败（主地址 {primary}）");

        // .nupkg 本质是 ZIP，必须以 PK\x03\x04 开头；否则说明下到的是 JSON/HTML（URL 拼错）。
        if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B || bytes[2] != 0x03 || bytes[3] != 0x04)
        {
            var head = bytes.Length == 0
                ? "(空)"
                : System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(64, bytes.Length))
                    .Replace("\n", " ").Replace("\r", " ");
            return (false, $"内容不是有效的 .nupkg（ZIP），已跳过。URL={usedUrl} 头部={head}");
        }

        Directory.CreateDirectory(cfg.Output.Dir);
        var path = Path.Combine(cfg.Output.Dir, $"{id}.{v.Version}.nupkg");
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        return (true, path);
    }

    private async Task<string> GetPackageContentUrlAsync(string registrationUrl, AppConfig cfg, CancellationToken ct)
    {
        try
        {
            using var doc = await Http.GetJsonAsync(registrationUrl, cfg.BaGet, cfg.Options.MaxRetries, ct)
                .ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("packageContent", out var pc)
                ? pc.GetString() ?? ""
                : "";
        }
        catch (OperationCanceledException) { throw; }
        catch { return ""; }
    }
}
