using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VideoSplitter.Views;

public enum FastCutWarningChoice
{
    Cancel,
    FastAnyway,
    PreciseForOutOfKeyframe,
    AllPrecise
}

public partial class FastCutWarningDialog : Window
{
    public FastCutWarningDialog()
        : this(0, false, true)
    {
    }

    public FastCutWarningDialog(int outOfKeyframeCount, bool keyframesUnavailable, bool allowPerSegmentPrecise = true)
    {
        InitializeComponent();

        if (keyframesUnavailable)
        {
            MessageTextBlock.Text =
                "Les keyframes de cette vidéo ne sont pas disponibles : la précision des coupes en mode Rapide ne peut pas être vérifiée.";
            DetailTextBlock.Text =
                "En mode Rapide, une coupe hors keyframe est décalée vers la keyframe précédente.";
            FastAnywayButton.Content = "Continuer";
            PreciseSubsetButton.IsVisible = false;
            AllPreciseButton.IsVisible = false;
        }
        else if (!allowPerSegmentPrecise)
        {
            MessageTextBlock.Text =
                $"{outOfKeyframeCount} segment(s) commencent hors keyframe : en mode Rapide, la coupe tombera sur le keyframe précédent.";
            DetailTextBlock.Text =
                "Pour une vidéo fusionnée sans perte, tous les segments doivent partager le même format. Vous pouvez basculer tout l'export en mode Précis (ré-encodage exact à l'image).";
            PreciseSubsetButton.IsVisible = false;
            AllPreciseButton.Content = "Passer en mode Précis";
            AllPreciseButton.Classes.Add("primary");
        }
        else
        {
            MessageTextBlock.Text =
                $"{outOfKeyframeCount} segment(s) commencent hors keyframe : en mode Rapide, la coupe tombera sur le keyframe précédent.";
            DetailTextBlock.Text =
                "Vous pouvez basculer uniquement ces segments ou tout l'export en mode Précis (ré-encodage exact à l'image).";
        }
    }

    private void OnFastAnywayClick(object? sender, RoutedEventArgs e)
        => Close(FastCutWarningChoice.FastAnyway);

    private void OnPreciseSubsetClick(object? sender, RoutedEventArgs e)
        => Close(FastCutWarningChoice.PreciseForOutOfKeyframe);

    private void OnAllPreciseClick(object? sender, RoutedEventArgs e)
        => Close(FastCutWarningChoice.AllPrecise);

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close(FastCutWarningChoice.Cancel);
}
