using Avalonia.Media;
using NuGetSift.Core;

namespace NuGetSift.Desktop.ViewModels;

/// <summary>列表中的一行（一个私有源里的包）。</summary>
public sealed class PackageRow : ObservableObject
{
    private static readonly IBrush PrivateBrush = new SolidColorBrush(Color.Parse("#F5A524"));
    private static readonly IBrush PublicBrush = new SolidColorBrush(Color.Parse("#7A869A"));
    private static readonly IBrush UnknownBrush = new SolidColorBrush(Color.Parse("#A78BFA"));

    private int _downloaded;

    public PackageRow(ScanItem item)
    {
        Id = item.Id;
        VersionCount = item.Versions.Count;
        Status = item.Status;
        Label = item.Label;
        Pending = item.ToDownload.Count;
        TotalVersions = string.Join(", ", item.Versions.Select(v => v.Version).Take(4)) +
                        (item.Versions.Count > 4 ? " …" : "");
    }

    public string Id { get; }
    public int VersionCount { get; }
    public PublicStatus Status { get; }
    public string Label { get; }
    public int Pending { get; }
    public string TotalVersions { get; }

    public int Downloaded
    {
        get => _downloaded;
        set
        {
            if (SetProperty(ref _downloaded, value)) OnPropertyChanged(nameof(DownloadText));
        }
    }

    public string DownloadText => Downloaded > 0 ? $"已下载 {Downloaded}" : "—";

    public IBrush StatusBrush => Status switch
    {
        PublicStatus.Private => PrivateBrush,
        PublicStatus.Public => PublicBrush,
        _ => UnknownBrush
    };

    public string StatusText => Status switch
    {
        PublicStatus.Private => "私有独有",
        PublicStatus.Public => "公共",
        _ => "未知"
    };

    public bool IsPrivate => Status == PublicStatus.Private;
    public bool IsPublic => Status == PublicStatus.Public;
    public bool IsUnknown => Status == PublicStatus.Unknown;
}
