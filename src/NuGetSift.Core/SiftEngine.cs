namespace NuGetSift.Core;

public enum SiftStage
{
    Preparing,
    Enumerating,
    Classifying,
    Downloading,
    Reporting,
    Done
}

/// <summary>进度回调载荷。</summary>
public sealed record SiftProgress(SiftStage Stage, int Done, int Total, string Message)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Done * 100.0 / Total, 0, 100);
}

public sealed class ScanResult
{
    public required SourceEndpoints Endpoints { get; init; }
    public List<ScanItem> Items { get; init; } = new();
    public int Total => Items.Count;
    public int PublicCount { get; init; }
    public int PrivateCount { get; init; }
    public int UnknownCount { get; init; }
    public int DownloadablePackages => Items.Count(i => i.ToDownload.Count > 0);
    public int DownloadableVersions => Items.Sum(i => i.ToDownload.Count);
}

public sealed class DownloadResult
{
    public int Downloaded { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = new();

    /// <summary>每个包ID 实际成功下载的版本数，用于写报告。</summary>
    public Dictionary<string, int> DownloadedByPackage { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>把"枚举 → 比对 → 下载 → 报告"串起来的引擎，CLI 与桌面客户端共用。</summary>
public sealed class SiftEngine
{
    public SourceClient Client { get; } = new();

    /// <summary>测试私有源连通性并返回解析出的端点。</summary>
    public Task<SourceEndpoints> TestConnectionAsync(AppConfig cfg, CancellationToken ct)
        => Client.DiscoverAsync(cfg, ct);

    /// <summary>枚举私有源全部包，并与公共源逐一比对分类（不下载）。</summary>
    public async Task<ScanResult> ScanAsync(
        AppConfig cfg, int? limit,
        IProgress<SiftProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new SiftProgress(SiftStage.Preparing, 0, 0, "读取服务索引..."));

        var ep = await Client.DiscoverAsync(cfg, ct).ConfigureAwait(false);
        progress?.Report(new SiftProgress(SiftStage.Preparing, 0, 0,
            $"Search={ep.SearchUrl}  下载基址={ep.PackageBaseAddress}"));

        List<PackageEntry> packages;
        var useV2 = cfg.Options.EnumerateMode.Equals("v2", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(cfg.Options.V2BaseUrl);

        if (useV2)
        {
            progress?.Report(new SiftProgress(SiftStage.Enumerating, 0, 0, $"枚举模式：V2 OData ({cfg.Options.V2BaseUrl})"));
            packages = await Client.EnumerateV2Async(
                cfg.Options.V2BaseUrl, cfg, limit,
                n => progress?.Report(new SiftProgress(SiftStage.Enumerating, n, 0, $"已枚举 {n} 个包...")),
                ct).ConfigureAwait(false);
        }
        else
        {
            progress?.Report(new SiftProgress(SiftStage.Enumerating, 0, 0, "枚举模式：V3 Search（健壮翻页）"));
            packages = await Client.EnumerateSearchAsync(
                ep, cfg, limit,
                n => progress?.Report(new SiftProgress(SiftStage.Enumerating, n, 0, $"已枚举 {n} 个包...")),
                ct).ConfigureAwait(false);
        }

        progress?.Report(new SiftProgress(SiftStage.Enumerating, packages.Count, packages.Count,
            $"枚举完成：{packages.Count} 个包ID"));

        // --- 比对 ---
        progress?.Report(new SiftProgress(SiftStage.Classifying, 0, packages.Count,
            "正在与公共源比对..."));

        var statusMap = new System.Collections.Concurrent.ConcurrentDictionary<string, PublicStatus>(
            StringComparer.OrdinalIgnoreCase);
        var checkedCount = 0;

        await RunBoundedAsync(packages, Math.Max(1, cfg.Options.Threads * 2), async p =>
        {
            var st = await Client.CheckPublicAsync(p.Id, cfg, ct).ConfigureAwait(false);
            statusMap[p.Id] = st;
            var n = Interlocked.Increment(ref checkedCount);
            if (n % 25 == 0 || n == packages.Count)
                progress?.Report(new SiftProgress(SiftStage.Classifying, n, packages.Count,
                    $"已比对 {n}/{packages.Count}"));
        }, ct).ConfigureAwait(false);

        // --- 归类 ---
        var items = new List<ScanItem>(packages.Count);
        int pub = 0, priv = 0, unk = 0;

        foreach (var p in packages)
        {
            var st = statusMap.TryGetValue(p.Id, out var s) ? s : PublicStatus.Unknown;

            switch (st)
            {
                case PublicStatus.Public:
                {
                    pub++;
                    var toDownload = new List<VersionEntry>();
                    var label = "公共(跳过)";

                    if (cfg.Options.VersionLevel)
                    {
                        var pubVers = await Client.GetPublicVersionsAsync(p.Id, cfg, ct).ConfigureAwait(false);
                        toDownload = p.Versions
                            .Where(v => pubVers == null ||
                                        !pubVers.Contains(v.Version))
                            .ToList();
                        if (toDownload.Count > 0) label = "公共(仅私有版本)";
                    }

                    items.Add(new ScanItem
                    {
                        Id = p.Id, Versions = p.Versions, Status = st,
                        Label = label, ToDownload = toDownload
                    });
                    break;
                }

                case PublicStatus.Private:
                {
                    priv++;
                    items.Add(new ScanItem
                    {
                        Id = p.Id, Versions = p.Versions, Status = st,
                        Label = "私有独有", ToDownload = p.Versions.ToList()
                    });
                    break;
                }

                default:
                {
                    unk++;
                    var treat = cfg.Options.TreatUnknownAsPrivate;
                    items.Add(new ScanItem
                    {
                        Id = p.Id, Versions = p.Versions, Status = st,
                        Label = treat ? "未知(按私有)" : "未知(跳过)",
                        ToDownload = treat ? p.Versions.ToList() : new List<VersionEntry>()
                    });
                    break;
                }
            }
        }

        progress?.Report(new SiftProgress(SiftStage.Done, packages.Count, packages.Count,
            $"扫描完成：私有独有 {priv}，公共 {pub}，未知 {unk}"));

        return new ScanResult
        {
            Endpoints = ep,
            Items = items,
            PublicCount = pub,
            PrivateCount = priv,
            UnknownCount = unk
        };
    }

    /// <summary>下载扫描结果中标记为"应下载"的全部版本。</summary>
    public async Task<DownloadResult> DownloadAsync(
        AppConfig cfg, ScanResult scan,
        IProgress<SiftProgress>? progress, CancellationToken ct)
    {
        var jobs = scan.Items
            .Where(i => i.ToDownload.Count > 0)
            .Select(i => (Item: i, Versions: i.ToDownload.ToList()))
            .ToList();

        var totalVersions = jobs.Sum(j => j.Versions.Count);
        var result = new DownloadResult();
        var done = 0;
        var okTotal = 0;
        var failTotal = 0;

        progress?.Report(new SiftProgress(SiftStage.Downloading, 0, totalVersions,
            $"开始下载 {jobs.Count} 个包 / {totalVersions} 个版本..."));

        Directory.CreateDirectory(cfg.Output.Dir);

        await RunBoundedAsync(jobs, Math.Max(1, cfg.Options.Threads), async job =>
        {
            var okCount = 0;
            foreach (var v in job.Versions)
            {
                ct.ThrowIfCancellationRequested();
                var (ok, msg) = await Client
                    .DownloadAsync(job.Item.Id, v, cfg, scan.Endpoints.PackageBaseAddress, ct)
                    .ConfigureAwait(false);

                if (ok)
                {
                    okCount++;
                    Interlocked.Increment(ref okTotal);
                }
                else
                {
                    Interlocked.Increment(ref failTotal);
                    lock (result.Errors)
                        result.Errors.Add($"{job.Item.Id} {v.Version}: {msg}");
                }

                var n = Interlocked.Increment(ref done);
                if (n % 5 == 0 || n == totalVersions)
                    progress?.Report(new SiftProgress(SiftStage.Downloading, n, totalVersions,
                        $"{job.Item.Id} {v.Version}"));
            }

            if (okCount > 0)
            {
                lock (result.DownloadedByPackage)
                    result.DownloadedByPackage[job.Item.Id] = okCount;
            }
        }, ct).ConfigureAwait(false);

        result.Downloaded = okTotal;
        result.Failed = failTotal;

        progress?.Report(new SiftProgress(SiftStage.Done, totalVersions, totalVersions,
            $"下载完成：成功 {result.Downloaded}，失败 {result.Failed}"));

        return result;
    }

    internal static async Task RunBoundedAsync<T>(
        IEnumerable<T> items, int concurrency, Func<T, Task> work, CancellationToken ct)
    {
        using var sem = new SemaphoreSlim(concurrency);
        var tasks = items.Select(async it =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try { await work(it).ConfigureAwait(false); }
            finally { sem.Release(); }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
