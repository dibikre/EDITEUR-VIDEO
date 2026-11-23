using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoSplitter.ViewModels;

namespace VideoSplitter.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
    }

    public SettingsDialog(SettingsViewModel viewModel, bool isExporting)
    {
        InitializeComponent();
        DataContext = viewModel;

        if (isExporting)
        {
            ExportModeSectionBorder.IsEnabled = false;
            OutputFolderSectionBorder.IsEnabled = false;
            FileNamePatternSectionBorder.IsEnabled = false;
            ExportLockHintText.IsVisible = true;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
