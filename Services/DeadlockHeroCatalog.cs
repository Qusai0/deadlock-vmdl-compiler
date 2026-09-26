using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class DeadlockHeroCatalog
{
    private static readonly IReadOnlyList<DeadlockHeroModel> Heroes = Array.AsReadOnly(new DeadlockHeroModel[]
    {
        new("Abrams", "abrams", "models/heroes_wip/abrams/abrams.vmdl_c"),
        new("Bebop", "bebop", "models/heroes_staging/bebop/bebop.vmdl_c"),
        new("Bookworm", "bookworm", "models/heroes_wip/bookworm/bookworm.vmdl_c"),
        new("Calico", "calico", "models/heroes_staging/nano/nano_v2/nano.vmdl_c"),
        new("Doorman", "doorman", "models/heroes_wip/doorman_v2/doorman.vmdl_c"),
        new("Dynamo", "dynamo", "models/heroes_wip/dynamo/dynamo.vmdl_c"),
        new("Fencer", "fencer", "models/heroes_wip/fencer/fencer.vmdl_c"),
        new("Grey Talon", "grey_talon", "models/heroes_staging/archer/archer.vmdl_c"),
        new("Haze", "haze", "models/heroes_staging/haze/haze.vmdl_c"),
        new("Holliday", "holliday", "models/heroes_staging/astro/astro.vmdl_c"),
        new("Infernus", "infernus", "models/heroes_wip/inferno/inferno.vmdl_c"),
        new("Ivy", "ivy", "models/heroes_staging/tengu/tengu_v2/tengu.vmdl_c"),
        new("Kelvin", "kelvin", "models/heroes_staging/kelvin_v2/kelvin.vmdl_c"),
        new("Lady Geist", "lady_geist", "models/heroes_wip/geist/geist.vmdl_c"),
        new("Lash", "lash", "models/heroes_wip/lash/lash.vmdl_c"),
        new("McGinnis", "mcginnis", "models/heroes_wip/mcginnis/mcginnis.vmdl_c"),
        new("Mirage", "mirage", "models/heroes_staging/mirage_v2/mirage.vmdl_c"),
        new("Mo & Krill", "mo_krill", "models/heroes_staging/digger/digger.vmdl_c"),
        new("Paradox", "paradox", "models/heroes_staging/chrono/chrono.vmdl_c"),
        new("Pocket", "pocket", "models/heroes_wip/pocket/pocket.vmdl_c"),
        new("Priest", "priest", "models/heroes_wip/priest/priest.vmdl_c"),
        new("Punkgoat", "punkgoat", "models/heroes_wip/punkgoat/punkgoat.vmdl_c"),
        new("Seven", "seven", "models/heroes_wip/frank/frank.vmdl_c"),
        new("Shiv", "shiv", "models/heroes_staging/shiv/shiv.vmdl_c"),
        new("Vindicta", "vindicta", "models/heroes_staging/hornet_v3/hornet.vmdl_c"),
        new("Viper", "viper", "models/heroes_staging/viper/viper.vmdl_c"),
        new("Viscous", "viscous", "models/heroes_staging/viscous/viscous.vmdl_c"),
        new("Warden", "warden", "models/heroes_staging/warden/warden.vmdl_c"),
        new("Werewolf", "werewolf", "models/heroes_wip/werewolf/werewolf.vmdl_c"),
        new("Wraith", "wraith", "models/heroes_wip/wraith/wraith.vmdl_c"),
        new("Wrecker", "wrecker", "models/heroes_staging/wrecker/wrecker.vmdl_c"),
        new("Yamato", "yamato", "models/heroes_staging/yamato_v2/yamato.vmdl_c")
    });

    public static IReadOnlyList<DeadlockHeroModel> GetHeroes() => Heroes;
}
