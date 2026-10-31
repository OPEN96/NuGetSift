namespace NuGetSift.Core;

/// <summary>某个包ID在公共源（nuget.org）上的存在状态。</summary>
public enum PublicStatus
{
    /// <summary>公共源上存在该包ID —— 属于公共/镜像包。</summary>
    Public,

    /// <summary>公共源上没有该包ID（404）—— 私有源独有。</summary>
    Private,

    /// <summary>无法判定（网络错误、超时、5xx 等）。</summary>
    Unknown
}

/// <summary>私有源里的一个包（含其全部版本）。</summary>
public sealed class PackageEntry
{
    public string Id { get; set; }
    public List<VersionEntry> Versions { get; set; }

    public PackageEntry(string id, List<VersionEntry> versions)
    {
        Id = id;
        Versions = versions;
    }

    public override string ToString() => Id;
}

/// <summary>单个版本的条目。</summary>
public sealed class VersionEntry
{
    public string Version { get; set; }

    /// <summary>显式下载地址（一般留空，由 BuildUrl 拼接）。</summary>
    public string DownloadUrl { get; set; }

    /// <summary>registration 叶子 JSON 地址，内含权威的 packageContent，可作下载兜底。</summary>
    public string RegistrationUrl { get; set; }

    public VersionEntry(string version, string downloadUrl = "", string registrationUrl = "")
    {
        Version = version;
        DownloadUrl = downloadUrl;
        RegistrationUrl = registrationUrl;
    }

    public override string ToString() => Version;
}

/// <summary>扫描结果里的一个包：分类结论 + 待下载版本。</summary>
public sealed class ScanItem
{
    public string Id { get; init; } = "";
    public List<VersionEntry> Versions { get; init; } = new();
    public PublicStatus Status { get; init; }
    public string Label { get; init; } = "";
    public List<VersionEntry> ToDownload { get; init; } = new();
    public int DownloadableCount => ToDownload.Count;
}

/// <summary>报告里的一行。</summary>
public sealed class PackageRecord
{
    public string PackageId { get; set; }
    public string Classification { get; set; }
    public int Versions { get; set; }
    public int Downloaded { get; set; }

    public PackageRecord(string packageId, string classification, int versions, int downloaded)
    {
        PackageId = packageId;
        Classification = classification;
        Versions = versions;
        Downloaded = downloaded;
    }
}

// ---------------------------------------------------------------------------
// 配置
// ---------------------------------------------------------------------------

public sealed class AppConfig
{
    public BaGetConfig BaGet { get; set; } = new();
    public PublicConfig Public { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
    public OptionsConfig Options { get; set; } = new();
}

public sealed class BaGetConfig
{
    /// <summary>私有源索引地址，形如 https://host/v3/index.json（也接受只写到 host）。</summary>
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>none | bearer | basic</summary>
    public string AuthType { get; set; } = "none";
}

public sealed class PublicConfig
{
    public string FlatContainer { get; set; } = "https://api.nuget.org/v3-flatcontainer";
}

public sealed class OutputConfig
{
    public string Dir { get; set; } = "downloaded_private_packages";
    public string ReportMd { get; set; } = "report.md";
    public string ReportCsv { get; set; } = "report.csv";
}

public sealed class OptionsConfig
{
    public int Threads { get; set; } = 8;
    public int PageSize { get; set; } = 100;
    public bool IncludePrerelease { get; set; } = true;
    public bool VersionLevel { get; set; } = false;
    public bool TreatUnknownAsPrivate { get; set; } = false;
    public string IdFilter { get; set; } = "";
    /// <summary>search | v2</summary>
    public string EnumerateMode { get; set; } = "search";
    public string V2BaseUrl { get; set; } = "";
    public int MaxRetries { get; set; } = 3;
}
