namespace DeadlockVmdlCompiler.Models;

public sealed class NpcPresetChoice
{
    public NpcPresetChoice(string key, string displayName, string detail,
        HeroPreset? preset = null, bool isAuto = false, string sourcePath = "")
    {
        Key = key;
        DisplayName = displayName;
        Detail = detail;
        Preset = preset;
        IsAuto = isAuto;
        SourcePath = sourcePath;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public string Detail { get; }
    public HeroPreset? Preset { get; }
    public bool IsAuto { get; }
    public string SourcePath { get; }
}
