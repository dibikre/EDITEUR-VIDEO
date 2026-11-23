using System.Windows;

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
    public FastCutWarningChoice Choice { get; private set; } = FastCutWarningChoice.Cancel;

    public FastCutWarningDialog(int outOfKeyframeCount, bool keyframesUnavailable)
    {
        InitializeComponent();

        if (keyframesUnavailable)
        {
            MessageTextBlock.Text =
                "Les keyframes de cette vidéo ne sont pas disponibles : la précision des coupes en mode Rapide ne peut pas être vérifiée.";
            DetailTextBlock.Text =
                "En mode Rapide, une coupe hors keyframe est décalée vers la keyframe précédente.";
            FastAnywayButton.Content = "Continuer";
            PreciseSubsetButton.Visibility = Visibility.Collapsed;
            AllPreciseButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            MessageTextBlock.Text =
                $"{outOfKeyframeCount} segment(s) commencent hors keyframe : en mode Rapide, la coupe tombera sur le keyframe précédent.";
            DetailTextBlock.Text =
                "Vous pouvez basculer uniquement ces segments ou tout l'export en mode Précis (ré-encodage exact à l'image).";
        }
    }

    private void OnFastAnywayClick(object sender, RoutedEventArgs e)
    {
        Choice = FastCutWarningChoice.FastAnyway;
        DialogResult = true;
    }

    private void OnPreciseSubsetClick(object sender, RoutedEventArgs e)
    {
        Choice = FastCutWarningChoice.PreciseForOutOfKeyframe;
        DialogResult = true;
    }

    private void OnAllPreciseClick(object sender, RoutedEventArgs e)
    {
        Choice = FastCutWarningChoice.AllPrecise;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Choice = FastCutWarningChoice.Cancel;
        DialogResult = false;
    }
}
