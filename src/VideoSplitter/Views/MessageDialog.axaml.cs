using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VideoSplitter.Views;

public partial class MessageDialog : Window
{
    public MessageDialog()
        : this("VideoSplitter", string.Empty, isConfirmation: false)
    {
    }

    public MessageDialog(string title, string message, bool isConfirmation)
    {
        InitializeComponent();

        Title = title;
        MessageTextBlock.Text = message;

        YesButton.IsVisible = isConfirmation;
        NoButton.IsVisible = isConfirmation;
        OkButton.IsVisible = !isConfirmation;
    }

    private void OnYesClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNoClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(true);
}
