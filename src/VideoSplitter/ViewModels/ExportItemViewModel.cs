using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoSplitter.ViewModels;

public enum ExportItemStatus
{
    EnAttente,
    EnCours,
    Termine,
    Erreur,
    Annule,
    Ignore
}

public sealed partial class ExportItemViewModel : ObservableObject
{
    public Guid SegmentId { get; }
    public string SegmentName { get; }
    public string OutputFileName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private ExportItemStatus _status = ExportItemStatus.EnAttente;

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private string _message = "En attente";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTechnicalDetails))]
    private string? _technicalDetails;

    public bool HasError => Status == ExportItemStatus.Erreur;
    public bool HasTechnicalDetails => !string.IsNullOrWhiteSpace(TechnicalDetails);

    public string StatusLabel => Status switch
    {
        ExportItemStatus.EnAttente => "En attente",
        ExportItemStatus.EnCours => "En cours",
        ExportItemStatus.Termine => "Terminé",
        ExportItemStatus.Erreur => "Erreur",
        ExportItemStatus.Annule => "Annulé",
        ExportItemStatus.Ignore => "Ignoré",
        _ => string.Empty
    };

    public ExportItemViewModel(Guid segmentId, string segmentName, string outputFileName)
    {
        SegmentId = segmentId;
        SegmentName = segmentName;
        OutputFileName = outputFileName;
    }
}
