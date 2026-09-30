using System.ComponentModel;
using Avalonia.Media.Imaging;

namespace DeadlockVmdlCompiler.Models;

public sealed class HeroPresetChoice : INotifyPropertyChanged
{
    private Bitmap? _portrait;

    public HeroPresetChoice(string key, DeadlockHeroModel? hero = null, bool isAuto = false)
    {
        Key = key;
        Hero = hero;
        IsAuto = isAuto;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }
    public DeadlockHeroModel? Hero { get; }
    public bool IsAuto { get; }
    public string DisplayName => IsAuto ? "auto-detect hero paths" : Hero?.DisplayName ?? Key;
    public string Detail => IsAuto ? "from selected model" : Hero != null ? Key : "unknown preset";
    public string FallbackGlyph => IsAuto ? "A" : "?";
    public bool HasPortrait => _portrait != null;
    public bool ShowFallback => _portrait == null;
    public Bitmap? Portrait
    {
        get => _portrait;
        set
        {
            if (ReferenceEquals(_portrait, value)) return;
            _portrait = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Portrait)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPortrait)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowFallback)));
        }
    }
}
