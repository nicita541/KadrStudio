using KadrStudio.ViewModels;
using CoreTrackKind = KadrStudio.Core.Domain.TrackKind;

namespace KadrStudio.Models;

/// <summary>Legacy WPF projection of a core track. The immutable Track ID and flags must survive the migration boundary.</summary>
public sealed class EditorTrack : ObservableObject
{
    private string _name = string.Empty;
    private bool _isMuted;
    private bool _isLocked;
    private bool _isVisible = true;

    public Guid Id { get; set; } = Guid.NewGuid();
    public CoreTrackKind Kind { get; set; }
    public int Index { get; set; }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public bool IsMuted { get => _isMuted; set => SetProperty(ref _isMuted, value); }
    public bool IsLocked { get => _isLocked; set => SetProperty(ref _isLocked, value); }
    public bool IsVisible { get => _isVisible; set => SetProperty(ref _isVisible, value); }
}
