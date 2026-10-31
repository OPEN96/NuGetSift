using NuGetSift.Core;

namespace NuGetSift.Cli;

/// <summary>
/// NuGetSift 命令行入口：枚举私有 NuGet 源，与公共源比对，只下载私有独有的包。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var opts = ParseArgs(args);

        if (opts.Help) { PrintHelp(); return 0; }

        if (opts.Init)
        {
            if (File.Exists(opts.ConfigPath))
            {
                Console.WriteLine($"配置文件已存在，未覆盖：{opts.ConfigPath}");
            }
            else
            {
                File.WriteAllText(opts.ConfigPath, ConfigStore.DefaultJson);
                Console.WriteLine($"已生成默认配置文件：{opts.ConfigPath}");
            }
            Console.WriteLine("请编辑 BaGet.BaseUrl / ApiKey / AuthType 后重新运行。");
            return 0;
        }

        if (opts.SelfTest) return await SelfTestAsync(opts).ConfigureAwait(false);

        AppConfig cfg;
        try
        {
            cfg = ConfigStore.Load(opts.ConfigPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"无法加载配置 {opts.ConfigPath}：{ex.Message}");
            Console.Error.WriteLine("可运行 `NuGetSift --init` 生成默认配置。");
            return 1;
        }

        if (!string.IsNullOrEmpty(opts.BagetUrl)) cfg.BaGet.BaseUrl = opts.BagetUrl;
        if (!string.IsNullOrEmpty(opts.OutputDir)) cfg.Output.Dir = opts.OutputDir;

        if (string.IsNullOrWhiteSpace(cfg.BaGet.BaseUrl) ||
            cfg.BaGet.BaseUrl.Contains("your-baget-server", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("错误：请在配置中填写真实的 BaGet.BaseUrl（或使用 --baget-url）。");
            return 1;
        }

        Console.WriteLine("=== NuGetSift v2.0.0 ===");
        Console.WriteLine($"私有源    : {cfg.BaGet.BaseUrl}");
        Console.WriteLine($"公共源    : {cfg.Public.FlatContainer}");
        Console.WriteLine($"输出目录  : {cfg.Output.Dir}");
        Console.WriteLine($"并发线程  : {cfg.Options.Threads}");
        Console.WriteLine($"比对粒度  : {(cfg.Options.VersionLevel ? "版本级" : "包ID级")}");
        Console.WriteLine();

        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var engine = new SiftEngine();
            var progress = new Progress<SiftProgress>(p =>
            {
                if (p.Message.Length > 0) Console.WriteLine($"  {p.Message}");
            });

            // 1) 枚举 + 比对
            var scan = await engine.ScanAsync(cfg, opts.Limit, progress, cts.Token).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine($"扫描完成：共 {scan.Total} 个包，私有独有 {scan.PrivateCount}，" +
                              $"公共 {scan.PublicCount}，未知 {scan.UnknownCount}");
            Console.WriteLine($"可下载：{scan.DownloadablePackages} 个包 / {scan.DownloadableVersions} 个版本");
            Console.WriteLine();

            // 2) 下载
            DownloadResult? download = null;
            if (opts.DryRun)
            {
                Console.WriteLine("[DRY-RUN] 不执行下载。");
            }
            else if (scan.DownloadableVersions > 0)
            {
                download = await engine.DownloadAsync(cfg, scan, progress, cts.Token).ConfigureAwait(false);
                if (download.Errors.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"--- 失败明细（{download.Errors.Count} 条）---");
                    foreach (var err in download.Errors.Take(50))
                        Console.Error.WriteLine($"  [失败] {err}");
                    if (download.Errors.Count > 50)
                        Console.Error.WriteLine($"  ... 其余 {download.Errors.Count - 50} 条已省略");
                }
            }
            else
            {
                Console.WriteLine("没有需要下载的包（私有独有版本数为 0）。");
            }

            // 3) 报告
            var records = ReportWriter.ToRecords(scan, download);
            var (md, csv) = ReportWriter.Write(cfg, records, new ReportWriter.Summary
            {
                Total = scan.Total,
                Public = scan.PublicCount,
                Private = scan.PrivateCount,
                Unknown = scan.UnknownCount,
                Downloaded = download?.Downloaded ?? 0,
                Failed = download?.Failed ?? 0,
                DryRun = opts.DryRun
            });

            Console.WriteLine();
            Console.WriteLine("=== 完成 ===");
            Console.WriteLine($"私有源包总数   : {scan.Total}");
            Console.WriteLine($"私有独有       : {scan.PrivateCount}");
            Console.WriteLine($"公共（跳过）   : {scan.PublicCount}");
            Console.WriteLine($"未知（跳过）   : {scan.UnknownCount}");
            Console.WriteLine($"已下载 .nupkg  : {download?.Downloaded ?? 0}  (失败 {download?.Failed ?? 0})");
            Console.WriteLine($"报告           : {md} / {csv}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("已取消。");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"运行失败：{ex.Message}");
            return 1;
        }
    }

    // ------------------------------------------------------------------

    private static async Task<int> SelfTestAsync(CliOptions opts)
    {
        var cfg = ConfigStore.LoadOrDefault(opts.ConfigPath);

        Console.WriteLine("=== 自检：验证 公共/私有 区分逻辑 ===");
        var samples = new (string Id, PublicStatus Expected)[]
        {
            ("Newtonsoft.Json", PublicStatus.Public),
            ("Microsoft.NETCore.App", PublicStatus.Public),
            ("ThisPackageSurelyDoesNotExistZzz12345", PublicStatus.Private)
        };

        var engine = new SiftEngine();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ok = 0;
        foreach (var (id, expected) in samples)
        {
            var actual = await engine.Client.CheckPublicAsync(id, cfg, cts.Token).ConfigureAwait(false);
            var pass = actual == expected;
            if (pass) ok++;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {id} -> {actual}（期望 {expected}）");
        }

        Console.WriteLine($"自检结果：{ok}/{samples.Length} 通过。");
        return ok == samples.Length ? 0 : 1;
    }

    private static CliOptions ParseArgs(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--help" or "-h": o.Help = true; break;
                case "--init": o.Init = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--selftest": o.SelfTest = true; break;
                case "--config":
                    if (i + 1 < args.Length) o.ConfigPath = args[++i];
                    break;
                case "--baget-url":
                    if (i + 1 < args.Length) o.BagetUrl = args[++i];
                    break;
                case "--output-dir":
                    if (i + 1 < args.Length) o.OutputDir = args[++i];
                    break;
                case "--limit":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var n)) o.Limit = n;
                    break;
            }
        }
        return o;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("NuGetSift - 区分私有/公共 NuGet 源，只下载私有源独有的包");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  NuGetSift --init                生成默认 config.json");
        Console.WriteLine("  NuGetSift --selftest            自检区分逻辑（无需私有源）");
        Console.WriteLine("  NuGetSift                       按 config.json 执行");
        Console.WriteLine("  NuGetSift --dry-run             只枚举+比对，不下载");
        Console.WriteLine("  NuGetSift --limit 50            只处理前 50 个包（测试用）");
        Console.WriteLine();
        Console.WriteLine("选项：");
        Console.WriteLine("  --config <path>     配置文件路径（默认 config.json）");
        Console.WriteLine("  --baget-url <url>   覆盖私有源索引地址");
        Console.WriteLine("  --output-dir <dir>  覆盖下载目录");
        Console.WriteLine();
        Console.WriteLine("判定规则：包ID 在公共源上不存在（404）=> 私有独有 => 下载其全部版本");
        Console.WriteLine("枚举不全时：在 config.json 中把 Options.EnumerateMode 改为 \"v2\"，");
        Console.WriteLine("并填写 Options.V2BaseUrl（如 http://你的主机:5000/v2），走 OData 全量列举。");
        Console.WriteLine();
        Console.WriteLine("桌面客户端：运行 NuGetSift.Desktop，图形化完成同样流程。");
    }

    private sealed class CliOptions
    {
        public bool Help;
        public bool Init;
        public bool DryRun;
        public bool SelfTest;
        public string ConfigPath = ConfigStore.DefaultFileName;
        public string? BagetUrl;
        public string? OutputDir;
        public int? Limit;
    }
}
