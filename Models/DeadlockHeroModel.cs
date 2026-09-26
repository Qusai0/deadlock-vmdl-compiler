namespace DeadlockVmdlCompiler.Models;

public sealed record DeadlockHeroModel(
    string DisplayName,
    string HeroKey,
    string VpkPath)
{
    public override string ToString() => DisplayName;
}
