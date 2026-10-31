using System.Text.Json;

namespace NuGetSift.Core;

/// <summary>config.json 的读写。</summary>
public static class ConfigStore
{
    public const string DefaultFileName = "config.json";

    public const string DefaultJson = """
{
  "BaGet": {
    "BaseUrl": "https://your-baget-server/v3/index.json",
    "ApiKey": "",
    "AuthType": "none"
  },
  "Public": {
    "FlatContainer": "https://api.nuget.org/v3-flatcontainer"
  },
  "Output": {
    "Dir": "downloaded_private_packages",
    "ReportMd": "report.md",
    "ReportCsv": "report.csv"
  },
  "Options": {
    "Threads": 8,
    "PageSize": 100,
    "IncludePrerelease": true,
    "VersionLevel": false,
    "TreatUnknownAsPrivate": false,
    "IdFilter": "",
    "EnumerateMode": "search",
    "V2BaseUrl": "",
    "MaxRetries": 3
  }
}
""";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, ReadOptions);
        return cfg ?? throw new InvalidOperationException("配置反序列化失败：内容不是有效的 NuGetSift 配置。");
    }

    public static AppConfig LoadOrDefault(string path)
    {
        if (!File.Exists(path)) return new AppConfig();
        try { return Load(path); }
        catch { return new AppConfig(); }
    }

    public static void Save(string path, AppConfig cfg)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, WriteOptions));
    }

    /// <summary>把用户输入的地址归一化成 V3 索引地址。</summary>
    public static string NormalizeIndexUrl(string baseUrl)
    {
        var u = (baseUrl ?? "").Trim();
        if (u.Length == 0) return u;
        if (u.Contains("index.json", StringComparison.OrdinalIgnoreCase)) return u;
        return u.TrimEnd('/') + "/v3/index.json";
    }
}
