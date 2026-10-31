using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NuGetSift.Desktop.ViewModels;

namespace NuGetSift.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainViewModel { PickFolderAsync = PickFolderAsync };
        DataContext = vm;
    }

    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { CanPickFolder: true } provider) return null;

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 .nupkg 下载目录",
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
