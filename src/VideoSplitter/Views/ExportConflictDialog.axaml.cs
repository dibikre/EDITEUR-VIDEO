using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoSplitter.Models;

namespace VideoSplitter.Views;

public partial class ExportConflictDialog : Window
{
    public ExportConflictDialog()
        : this(Array.Empty<string>())
    {
    }

    public ExportConflictDialog(IReadOnlyList<string> conflictingPaths)
    {
        InitializeComponent();

        HeaderTextBlock.Text =
            $"{conflictingPaths.Count} fichier(s) existent déjà dans le dossier de sortie. Que souhaitez-vous faire ?";

        FilesListBox.ItemsSource = conflictingPaths.Select(Path.GetFileName).ToList();
    }

    private void OnOverwriteClick(object? sender, RoutedEventArgs e)
        => Close(ConflictPolicy.Overwrite);

    private void OnAutoRenameClick(object? sender, RoutedEventArgs e)
        => Close(ConflictPolicy.AutoRename);

    private void OnSkipClick(object? sender, RoutedEventArgs e)
        => Close(ConflictPolicy.Skip);

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close(null);
}
