namespace DeadlockVmdlCompiler.Models;

public sealed record DeadlockHeroModel(
    string DisplayName,
    string HeroKey,
    string VpkPath,
    string? IconFileName = null)
{
    public string? IconVpkPath => IconFileName == null
        ? null : $"panorama/images/heroes/{IconFileName}.vtex_c";

    public override string ToString() => DisplayName;
}
