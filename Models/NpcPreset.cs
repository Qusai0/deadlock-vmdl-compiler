namespace DeadlockVmdlCompiler.Models;

public sealed record NpcPreset(
    string Key,
    string DisplayName,
    string SourcePath,
    HeroPreset Preset,
    bool IsNeutral = false);

