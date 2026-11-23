using System.IO;
using System.Windows;
using VideoSplitter.Models;

namespace VideoSplitter.Views;

public partial class ExportConflictDialog : Window
{
    public ConflictPolicy? SelectedPolicy { get; private set; }

    public ExportConflictDialog(IReadOnlyList<string> conflictingPaths)
    {
        InitializeComponent();

        HeaderTextBlock.Text =
            $"{conflictingPaths.Count} fichier(s) existent déjà dans le dossier de sortie. Que souhaitez-vous faire ?";

        FilesListBox.ItemsSource = conflictingPaths.Select(Path.GetFileName).ToList();
    }

    private void OnOverwriteClick(object sender, RoutedEventArgs e)
    {
        SelectedPolicy = ConflictPolicy.Overwrite;
        DialogResult = true;
    }

    private void OnAutoRenameClick(object sender, RoutedEventArgs e)
    {
        SelectedPolicy = ConflictPolicy.AutoRename;
        DialogResult = true;
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        SelectedPolicy = ConflictPolicy.Skip;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        SelectedPolicy = null;
        DialogResult = false;
    }
}
