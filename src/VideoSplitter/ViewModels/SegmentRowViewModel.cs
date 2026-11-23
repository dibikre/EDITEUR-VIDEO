using CommunityToolkit.Mvvm.ComponentModel;
using VideoSplitter.Models;

namespace VideoSplitter.ViewModels;

public sealed partial class SegmentRowViewModel : ObservableObject
{
    private readonly Action<Guid, bool>? _onEnabledChangedByUi;
    private readonly Action<Guid, string>? _onNameCommittedByUi;
    private bool _isSyncingFromModel;

    public Guid Id { get; }

    [ObservableProperty]
    private int _index;

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value) && !_isSyncingFromModel)
            {
                _onNameCommittedByUi?.Invoke(Id, value);
            }
        }
    }

    [ObservableProperty]
    private TimeSpan _start;

    [ObservableProperty]
    private TimeSpan _end;

    [ObservableProperty]
    private TimeSpan _duration;

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value) && !_isSyncingFromModel)
            {
                _onEnabledChangedByUi?.Invoke(Id, value);
            }
        }
    }

    [ObservableProperty]
    private string _keyframeBadge = string.Empty;

    [ObservableProperty]
    private string _keyframeTooltip = string.Empty;

    [ObservableProperty]
    private bool _isOnKeyframe;

    public SegmentRowViewModel(
        Guid id,
        Action<Guid, bool>? onEnabledChangedByUi,
        Action<Guid, string>? onNameCommittedByUi)
    {
        Id = id;
        _onEnabledChangedByUi = onEnabledChangedByUi;
        _onNameCommittedByUi = onNameCommittedByUi;
    }

    public void UpdateFrom(int index, Segment segment, string keyframeBadge, string keyframeTooltip, bool isOnKeyframe)
    {
        _isSyncingFromModel = true;
        try
        {
            Index = index;
            Name = segment.Name;
            Start = segment.Start;
            End = segment.End;
            Duration = segment.Duration;
            IsEnabled = segment.Enabled;
            KeyframeBadge = keyframeBadge;
            KeyframeTooltip = keyframeTooltip;
            IsOnKeyframe = isOnKeyframe;
        }
        finally
        {
            _isSyncingFromModel = false;
        }
    }

    public void RestoreNameFromModel(string modelName)
    {
        _isSyncingFromModel = true;
        try
        {
            Name = modelName;
        }
        finally
        {
            _isSyncingFromModel = false;
        }
    }
}
