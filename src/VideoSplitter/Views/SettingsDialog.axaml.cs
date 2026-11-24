using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoSplitter.ViewModels;

namespace VideoSplitter.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel? _viewModel;

    public SettingsDialog()
    {
        InitializeComponent();
    }

    public SettingsDialog(SettingsViewModel viewModel, bool isExporting)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        if (isExporting)
        {
            ExportModeSectionBorder.IsEnabled = false;
            OutputFolderSectionBorder.IsEnabled = false;
            FileNamePatternSectionBorder.IsEnabled = false;
            ExportLockHintText.IsVisible = true;
        }

        Closed += OnDialogClosed;
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        Closed -= OnDialogClosed;
        _viewModel?.SaveNow();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _viewModel?.SaveNow();
        Close();
    }
}
