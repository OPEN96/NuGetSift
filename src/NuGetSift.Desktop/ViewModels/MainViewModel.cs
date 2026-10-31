using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Threading;
using NuGetSift.Core;

namespace NuGetSift.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SiftEngine _engine = new();
    private readonly List<PackageRow> _allRows = new();
    private CancellationTokenSource? _cts;
    private ScanResult? _scan;

    public MainViewModel()
    {
        ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

        TestCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy && SourceUrl.Length > 0);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy && _scan is { DownloadableVersions: > 0 });
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        SaveCommand = new RelayCommand(SaveConfig);
        BrowseCommand = new AsyncRelayCommand(BrowseOutputAsync, () => !IsBusy);
        OpenOutputCommand = new RelayCommand(OpenOutputDir);
        ClearLogCommand = new RelayCommand(() => Logs.Clear());

        FilterAllCommand = new RelayCommand(() => FilterIndex = 0);
        FilterPrivateCommand = new RelayCommand(() => FilterIndex = 1);
        FilterPublicCommand = new RelayCommand(() => FilterIndex = 2);
        FilterUnknownCommand = new RelayCommand(() => FilterIndex = 3);

        LoadConfig();
        AddLog($"就绪。配置文件：{ConfigPath}");
    }

    // ------------------------------------------------------------------
    // 配置绑定
    // ------------------------------------------------------------------

    public string ConfigPath { get; }

    private string _sourceUrl = "";
    public string SourceUrl
    {
        get => _sourceUrl;
        set { if (SetProperty(ref _sourceUrl, value)) RefreshCommands(); }
    }

    private string _apiKey = "";
    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }

    private int _authTypeIndex;
    public int AuthTypeIndex { get => _authTypeIndex; set => SetProperty(ref _authTypeIndex, value); }

    private string _publicFeed = "https://api.nuget.org/v3-flatcontainer";
    public string PublicFeed { get => _publicFeed; set => SetProperty(ref _publicFeed, value); }

    private string _outputDir = "";
    public string OutputDir { get => _outputDir; set => SetProperty(ref _outputDir, value); }

    private int _enumerateModeIndex;
    public int EnumerateModeIndex
    {
        get => _enumerateModeIndex;
        set
        {
            if (SetProperty(ref _enumerateModeIndex, value)) OnPropertyChanged(nameof(IsV2Mode));
        }
    }

    public bool IsV2Mode => EnumerateModeIndex == 1;

    private string _v2Url = "";
    public string V2Url { get => _v2Url; set => SetProperty(ref _v2Url, value); }

    private decimal _threads = 8;
    public decimal Threads { get => _threads; set => SetProperty(ref _threads, value); }

    private string _idFilter = "";
    public string IdFilter { get => _idFilter; set => SetProperty(ref _idFilter, value); }

    private bool _includePrerelease = true;
    public bool IncludePrerelease { get => _includePrerelease; set => SetProperty(ref _includePrerelease, value); }

    private bool _versionLevel;
    public bool VersionLevel { get => _versionLevel; set => SetProperty(ref _versionLevel, value); }

    private bool _treatUnknown;
    public bool TreatUnknown { get => _treatUnknown; set => SetProperty(ref _treatUnknown, value); }

    private bool _dryRun;
    public bool DryRun { get => _dryRun; set => SetProperty(ref _dryRun, value); }

    // ------------------------------------------------------------------
    // 状态 / 统计
    // ------------------------------------------------------------------

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            RefreshCommands();
        }
    }

    public bool IsIdle => !IsBusy;

    private string _statusText = "填写私有源地址后点击「测试连接」，再「开始扫描」。";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; set => SetProperty(ref _progressValue, value); }

    private bool _isProgressIndeterminate;
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        set => SetProperty(ref _isProgressIndeterminate, value);
    }

    private int _totalCount;
    public int TotalCount { get => _totalCount; set => SetProperty(ref _totalCount, value); }

    private int _privateCount;
    public int PrivateCount { get => _privateCount; set => SetProperty(ref _privateCount, value); }

    private int _publicCount;
    public int PublicCount { get => _publicCount; set => SetProperty(ref _publicCount, value); }

    private int _unknownCount;
    public int UnknownCount { get => _unknownCount; set => SetProperty(ref _unknownCount, value); }

    private int _pendingVersions;
    public int PendingVersions { get => _pendingVersions; set => SetProperty(ref _pendingVersions, value); }

    private int _downloadedCount;
    public int DownloadedCount { get => _downloadedCount; set => SetProperty(ref _downloadedCount, value); }

    private string _endpointText = "";
    public string EndpointText { get => _endpointText; set => SetProperty(ref _endpointText, value); }

    // ------------------------------------------------------------------
    // 列表与筛选
    // ------------------------------------------------------------------

    public ObservableCollection<PackageRow> VisibleRows { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    private int _filterIndex;
    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (!SetProperty(ref _filterIndex, value)) return;
            OnPropertyChanged(nameof(IsFilterAll));
            OnPropertyChanged(nameof(IsFilterPrivate));
            OnPropertyChanged(nameof(IsFilterPublic));
            OnPropertyChanged(nameof(IsFilterUnknown));
            ApplyFilters();
        }
    }

    public bool IsFilterAll => FilterIndex == 0;
    public bool IsFilterPrivate => FilterIndex == 1;
    public bool IsFilterPublic => FilterIndex == 2;
    public bool IsFilterUnknown => FilterIndex == 3;

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilters(); }
    }

    private string _visibleCountText = "0 个";
    public string VisibleCountText { get => _visibleCountText; set => SetProperty(ref _visibleCountText, value); }

    private bool _hasResults;
    public bool HasResults
    {
        get => _hasResults;
        set
        {
            if (SetProperty(ref _hasResults, value)) OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    public bool ShowEmpty => !HasResults;

    // ------------------------------------------------------------------
    // 命令
    // ------------------------------------------------------------------

    public AsyncRelayCommand TestCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SaveCommand { get; }
    public AsyncRelayCommand BrowseCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public RelayCommand FilterAllCommand { get; }
    public RelayCommand FilterPrivateCommand { get; }
    public RelayCommand FilterPublicCommand { get; }
    public RelayCommand FilterUnknownCommand { get; }

    /// <summary>由窗口注入：弹出文件夹选择对话框。</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    private void RefreshCommands()
    {
        TestCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        BrowseCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------
    // 配置读写
    // ------------------------------------------------------------------

    private void LoadConfig()
    {
        var cfg = File.Exists(ConfigPath) ? ConfigStore.LoadOrDefault(ConfigPath) : new AppConfig();

        if (!File.Exists(ConfigPath))
        {
            cfg.BaGet.BaseUrl = "http://localhost:5000/v3/index.json";
            cfg.Output.Dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NuGetSift", "packages");
        }

        SourceUrl = cfg.BaGet.BaseUrl;
        ApiKey = cfg.BaGet.ApiKey;
        AuthTypeIndex = cfg.BaGet.AuthType.ToLowerInvariant() switch
        {
            "bearer" => 1,
            "basic" => 2,
            _ => 0
        };
        PublicFeed = cfg.Public.FlatContainer;
        OutputDir = ResolvePath(cfg.Output.Dir);
        EnumerateModeIndex = string.Equals(cfg.Options.EnumerateMode, "v2", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        V2Url = string.IsNullOrWhiteSpace(cfg.Options.V2BaseUrl)
            ? DeriveV2Url(cfg.BaGet.BaseUrl)
            : cfg.Options.V2BaseUrl;
        Threads = Math.Clamp(cfg.Options.Threads, 1, 64);
        IdFilter = cfg.Options.IdFilter;
        IncludePrerelease = cfg.Options.IncludePrerelease;
        VersionLevel = cfg.Options.VersionLevel;
        TreatUnknown = cfg.Options.TreatUnknownAsPrivate;
    }

    private AppConfig BuildConfig()
    {
        return new AppConfig
        {
            BaGet = new BaGetConfig
            {
                BaseUrl = SourceUrl.Trim(),
                ApiKey = ApiKey.Trim(),
                AuthType = AuthTypeIndex switch { 1 => "bearer", 2 => "basic", _ => "none" }
            },
            Public = new PublicConfig { FlatContainer = PublicFeed.Trim() },
            Output = new OutputConfig
            {
                Dir = OutputDir,
                ReportMd = Path.Combine(OutputDir, "report.md"),
                ReportCsv = Path.Combine(OutputDir, "report.csv")
            },
            Options = new OptionsConfig
            {
                Threads = (int)Math.Clamp(Threads, 1, 64),
                IncludePrerelease = IncludePrerelease,
                VersionLevel = VersionLevel,
                TreatUnknownAsPrivate = TreatUnknown,
                EnumerateMode = EnumerateModeIndex == 1 ? "v2" : "search",
                V2BaseUrl = V2Url.Trim(),
                IdFilter = IdFilter.Trim()
            }
        };
    }

    private void SaveConfig()
    {
        try
        {
            ConfigStore.Save(ConfigPath, BuildConfig());
            AddLog($"配置已保存：{ConfigPath}");
            StatusText = "配置已保存。";
        }
        catch (Exception ex)
        {
            AddLog($"保存配置失败：{ex.Message}");
            StatusText = "保存配置失败：" + ex.Message;
        }
    }

    private static string ResolvePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return AppContext.BaseDirectory;
        return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p));
    }

    private static string DeriveV2Url(string indexUrl)
    {
        if (string.IsNullOrWhiteSpace(indexUrl)) return "";
        var u = indexUrl.Trim().TrimEnd('/');
        var i = u.IndexOf("/v3/index.json", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return u[..i] + "/v2";
        var j = u.IndexOf("/index.json", StringComparison.OrdinalIgnoreCase);
        if (j >= 0) return u[..j] + "/v2";
        return u + "/v2";
    }

    // ------------------------------------------------------------------
    // 操作
    // ------------------------------------------------------------------

    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        StatusText = "正在读取服务索引...";
        IsProgressIndeterminate = true;
        try
        {
            var ep = await _engine.TestConnectionAsync(BuildConfig(), CancellationToken.None);
            EndpointText = $"Search: {ep.SearchUrl}    |    下载基址: {ep.PackageBaseAddress}";
            StatusText = "连接成功，服务索引已解析。";
            AddLog($"连接成功。下载基址 {ep.PackageBaseAddress}");
        }
        catch (Exception ex)
        {
            EndpointText = "";
            StatusText = "连接失败：" + ex.Message;
            AddLog("连接失败：" + ex.Message);
        }
        finally
        {
            IsProgressIndeterminate = false;
            IsBusy = false;
        }
    }

    private async Task ScanAsync()
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = true;
        _allRows.Clear();
        VisibleRows.Clear();
        HasResults = false;
        DownloadedCount = 0;
        TotalCount = PrivateCount = PublicCount = UnknownCount = PendingVersions = 0;
        StatusText = "开始扫描...";
        AddLog("==== 开始扫描 ====");

        try
        {
            var cfg = BuildConfig();
            var progress = new Progress<SiftProgress>(p => Dispatcher.UIThread.Post(() => ApplyProgress(p)));

            _scan = await _engine.ScanAsync(cfg, null, progress, _cts.Token);

            foreach (var item in _scan.Items)
                _allRows.Add(new PackageRow(item));

            TotalCount = _scan.Total;
            PrivateCount = _scan.PrivateCount;
            PublicCount = _scan.PublicCount;
            UnknownCount = _scan.UnknownCount;
            PendingVersions = _scan.DownloadableVersions;
            EndpointText = $"Search: {_scan.Endpoints.SearchUrl}    |    下载基址: {_scan.Endpoints.PackageBaseAddress}";
            HasResults = _allRows.Count > 0;

            ApplyFilters();

            StatusText = $"扫描完成：共 {_scan.Total} 个包，私有独有 {_scan.PrivateCount} 个，" +
                         $"可下载 {_scan.DownloadableVersions} 个版本。";
            AddLog(StatusText);

            // 自动落一份报告
            WriteReportSafely(cfg, download: null);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止。";
            AddLog("扫描已取消。");
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败：" + ex.Message;
            AddLog("扫描失败：" + ex.Message);
        }
        finally
        {
            IsProgressIndeterminate = false;
            IsBusy = false;
            ProgressValue = 0;
            RefreshCommands();
        }
    }

    private async Task DownloadAsync()
    {
        if (_scan == null) return;

        if (DryRun)
        {
            StatusText = "已勾选「仅扫描不下载」，跳过下载。";
            AddLog("DRY-RUN：跳过下载。");
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        StatusText = "开始下载...";
        AddLog("==== 开始下载 ====");

        try
        {
            var cfg = BuildConfig();
            var progress = new Progress<SiftProgress>(p => Dispatcher.UIThread.Post(() => ApplyProgress(p)));
            var result = await _engine.DownloadAsync(cfg, _scan, progress, _cts.Token);

            DownloadedCount = result.Downloaded;
            foreach (var row in _allRows)
            {
                if (result.DownloadedByPackage.TryGetValue(row.Id, out var n))
                    row.Downloaded = n;
            }

            StatusText = $"下载完成：成功 {result.Downloaded} 个版本，失败 {result.Failed} 个。";
            AddLog(StatusText);

            foreach (var err in result.Errors.Take(20))
                AddLog("  [失败] " + err);
            if (result.Errors.Count > 20)
                AddLog($"  ... 其余 {result.Errors.Count - 20} 条失败已省略");

            WriteReportSafely(cfg, result);
        }
        catch (OperationCanceledException)
        {
            StatusText = "下载已停止。";
            AddLog("下载已取消。");
        }
        catch (Exception ex)
        {
            StatusText = "下载失败：" + ex.Message;
            AddLog("下载失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
            RefreshCommands();
        }
    }

    private void WriteReportSafely(AppConfig cfg, DownloadResult? download)
    {
        try
        {
            if (_scan == null) return;
            var records = ReportWriter.ToRecords(_scan, download);
            var (md, csv) = ReportWriter.Write(cfg, records, new ReportWriter.Summary
            {
                Total = _scan.Total,
                Public = _scan.PublicCount,
                Private = _scan.PrivateCount,
                Unknown = _scan.UnknownCount,
                Downloaded = download?.Downloaded ?? 0,
                Failed = download?.Failed ?? 0,
                DryRun = DryRun
            });
            AddLog($"报告已生成：{md}");
            AddLog($"             {csv}");
        }
        catch (Exception ex)
        {
            AddLog("生成报告失败：" + ex.Message);
        }
    }

    private void Stop()
    {
        try
        {
            _cts?.Cancel();
            StatusText = "正在停止...";
        }
        catch { /* ignore */ }
    }

    private async Task BrowseOutputAsync()
    {
        if (PickFolderAsync == null) return;
        var picked = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(picked)) OutputDir = picked!;
    }

    private void OpenOutputDir()
    {
        try
        {
            var dir = ResolvePath(OutputDir);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog("打开目录失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // 进度 / 筛选 / 日志
    // ------------------------------------------------------------------

    private void ApplyProgress(SiftProgress p)
    {
        if (p.Message.Length > 0) StatusText = p.Message;

        if (p.Total > 0)
        {
            IsProgressIndeterminate = false;
            ProgressValue = p.Percent;
        }
        else if (p.Stage is SiftStage.Enumerating or SiftStage.Preparing)
        {
            IsProgressIndeterminate = true;
        }
    }

    private void ApplyFilters()
    {
        IEnumerable<PackageRow> query = _allRows;

        query = FilterIndex switch
        {
            1 => query.Where(r => r.Status == PublicStatus.Private),
            2 => query.Where(r => r.Status == PublicStatus.Public),
            3 => query.Where(r => r.Status == PublicStatus.Unknown),
            _ => query
        };

        var search = SearchText.Trim();
        if (search.Length > 0)
            query = query.Where(r => r.Id.Contains(search, StringComparison.OrdinalIgnoreCase));

        VisibleRows.Clear();
        foreach (var row in query
                     .OrderBy(r => r.Status switch
                     {
                         PublicStatus.Private => 0,
                         PublicStatus.Unknown => 1,
                         _ => 2
                     })
                     .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
            VisibleRows.Add(row);

        VisibleCountText = $"{VisibleRows.Count} 个 / 共 {_allRows.Count} 个";
    }

    private void AddLog(string message)
    {
        void Append()
        {
            Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (Logs.Count > 500) Logs.RemoveAt(0);
        }

        if (Dispatcher.UIThread.CheckAccess()) Append();
        else Dispatcher.UIThread.Post(Append);
    }
}
