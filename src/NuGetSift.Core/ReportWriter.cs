using System.Text;

namespace NuGetSift.Core;

/// <summary>生成 Markdown / CSV 区分报告。</summary>
public static class ReportWriter
{
    public sealed class Summary
    {
        public int Total { get; set; }
        public int Public { get; set; }
        public int Private { get; set; }
        public int Unknown { get; set; }
        public int Downloaded { get; set; }
        public int Failed { get; set; }
        public bool DryRun { get; set; }
    }

    public static (string MdPath, string CsvPath) Write(
        AppConfig cfg, IEnumerable<PackageRecord> records, Summary summary)
    {
        var list = records
            .OrderBy(r => r.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var md = new StringBuilder();
        md.AppendLine("# NuGet 私有 / 公共包区分报告");
        md.AppendLine();
        md.AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        md.AppendLine($"- 比对方式：包ID 是否存在于公共源（{(cfg.Options.VersionLevel ? "版本级" : "包ID级")}）");
        md.AppendLine($"- 私有源：{cfg.BaGet.BaseUrl}");
        md.AppendLine($"- 公共源：{cfg.Public.FlatContainer}");
        md.AppendLine($"- 枚举模式：{(string.Equals(cfg.Options.EnumerateMode, "v2", StringComparison.OrdinalIgnoreCase) ? "V2 OData" : "V3 Search")}");
        if (summary.DryRun) md.AppendLine("- 本次为 **DRY-RUN**，未执行下载");
        md.AppendLine();
        md.AppendLine("## 汇总");
        md.AppendLine();
        md.AppendLine("| 指标 | 数量 |");
        md.AppendLine("| --- | ---: |");
        md.AppendLine($"| 私有源包总数 | {summary.Total} |");
        md.AppendLine($"| 私有独有 | {summary.Private} |");
        md.AppendLine($"| 公共（跳过） | {summary.Public} |");
        md.AppendLine($"| 未知（跳过） | {summary.Unknown} |");
        md.AppendLine($"| 已下载 .nupkg | {summary.Downloaded} |");
        md.AppendLine($"| 下载失败 | {summary.Failed} |");
        md.AppendLine();
        md.AppendLine("## 明细");
        md.AppendLine();
        md.AppendLine("| 包ID | 分类 | 版本数 | 已下载 |");
        md.AppendLine("| --- | --- | ---: | ---: |");
        foreach (var r in list)
            md.AppendLine($"| {r.PackageId} | {r.Classification} | {r.Versions} | {r.Downloaded} |");

        var csv = new StringBuilder();
        csv.AppendLine("PackageId,Classification,Versions,Downloaded");
        foreach (var r in list)
            csv.AppendLine($"{Escape(r.PackageId)},{Escape(r.Classification)},{r.Versions},{r.Downloaded}");

        File.WriteAllText(cfg.Output.ReportMd, md.ToString(), Utf8NoBom);
        File.WriteAllText(cfg.Output.ReportCsv, csv.ToString(), Utf8NoBom);
        return (cfg.Output.ReportMd, cfg.Output.ReportCsv);
    }

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string Escape(string s)
        => s.Contains(',') || s.Contains('"')
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;

    /// <summary>把扫描结果转换为报告记录（下载前）。</summary>
    public static List<PackageRecord> ToRecords(ScanResult scan, DownloadResult? download = null)
        => scan.Items.Select(i => new PackageRecord(
                i.Id, i.Label, i.Versions.Count,
                download != null && download.DownloadedByPackage.TryGetValue(i.Id, out var n) ? n : 0))
            .ToList();
}
