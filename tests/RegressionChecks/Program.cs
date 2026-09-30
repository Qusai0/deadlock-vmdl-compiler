using System.Text.RegularExpressions;
using System.Text.Json;
using DeadlockVmdlCompiler.Services;
using DeadlockVmdlCompiler.Models;
using ValveResourceFormat;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static bool IsWriteBlocked(Action write)
{
    try
    {
        write();
        return false;
    }
    catch (IOException)
    {
        return true;
    }
    catch (UnauthorizedAccessException)
    {
        return true;
    }
}

static int Count(string text, string token) => Regex.Matches(text, Regex.Escape(token)).Count;

const string header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc40:version{12fc9d44-453a-4ae4-b4d9-7e2ac0bbd4e0} -->";
var bareModel = header + "\n{\nrootNode =\n{\n_class = \"RootNode\"\nchildren =\n[\n]\n}\n}";
var paths = (Skel: "models/heroes_staging/hornet_v3/hornet.vnmskel",
    Graph: "animgraphs/animgraph2/hero/hero.vnmgraph+vindicta.vnmgraph",
    Ui: "animgraphs/animgraph2/hero/hero_ui.vnmgraph+vindicta.vnmgraph");

var initial = VmdlPipeline.UpgradeVmdlContent(bareModel, paths.Skel, paths.Graph, paths.Ui);
Check(!initial.Changes.Any(c => c.StartsWith("Error:")), "Injection rejected a RootNode without model_archetype.");
Check(Count(initial.UpgradedContent, "_class = \"NmSkeletonList\"") == 1, "Missing or duplicate skeleton list.");
Check(Count(initial.UpgradedContent, "_class = \"AnimGraph2List\"") == 1, "Missing or duplicate graph list.");
Check(initial.UpgradedContent.Contains(paths.Ui), "Missing ui graph path.");

var repeat = VmdlPipeline.UpgradeVmdlContent(initial.UpgradedContent, paths.Skel, paths.Graph, paths.Ui);
Check(repeat.UpgradedContent == initial.UpgradedContent, "Repeated injection is not idempotent.");

var changed = VmdlPipeline.UpgradeVmdlContent(initial.UpgradedContent,
    "new_skeleton.vnmskel", "new_default.vnmgraph", "new_ui.vnmgraph");
Check(changed.UpgradedContent.Contains("new_skeleton.vnmskel") &&
      changed.UpgradedContent.Contains("new_default.vnmgraph") &&
      changed.UpgradedContent.Contains("new_ui.vnmgraph"), "Existing AG2 references were not updated.");
Check(!changed.UpgradedContent.Contains(paths.Graph), "Stale default graph reference remained.");

var uiOnly = VmdlPipeline.UpgradeVmdlContent(bareModel, "", "", paths.Ui,
    addSkel: false, addGraph: false, addUiGraph: true);
Check(uiOnly.UpgradedContent.Contains("_class = \"AnimGraph2\"") &&
      !uiOnly.UpgradedContent.Contains("_class = \"DefaultAnimGraph2\""), "Independent ui graph injection failed.");

var invalid = VmdlPipeline.UpgradeVmdlContent(bareModel, "", paths.Graph, paths.Ui);
Check(invalid.Changes.Any(c => c.StartsWith("Error:")), "Missing skeleton preset was accepted.");

var standaloneModel = header + "\n{ rootNode = { _class = \"RootNode\" children = [ " +
    "{ _class = \"DefaultAnimGraph2\" filename = \"old.vnmgraph\" }, " +
    "{ _class = \"AnimGraph2\" name = \"ui\" filename = \"old_ui.vnmgraph\" }, " +
    "] importer_notes = \"\"\"A note with { and ] characters\"\"\" } }";
var wrapped = VmdlPipeline.UpgradeVmdlContent(standaloneModel, paths.Skel, paths.Graph, paths.Ui);
Check(!wrapped.Changes.Any(c => c.StartsWith("Error:")), "Standalone AG2 nodes could not be wrapped.");
Check(Count(wrapped.UpgradedContent, "_class = \"AnimGraph2List\"") == 1 &&
      Count(wrapped.UpgradedContent, "_class = \"DefaultAnimGraph2\"") == 1 &&
      !wrapped.UpgradedContent.Contains("old.vnmgraph"), "Standalone graph migration failed.");

var commentedModel = header + "\n// rootNode = { children = [ ] }\n" +
    "{ rootNode = { _class = \"RootNode\" children = [ " +
    "{ _class = \"AnimGraph2List\" children = [ " +
    "{ _class = \"DefaultAnimGraph2\" // filename = \"commented.vnmgraph\"\n" +
    "filename = \"old.vnmgraph\" } ] } ] } }";
var commented = VmdlPipeline.UpgradeVmdlContent(commentedModel, paths.Skel, paths.Graph, paths.Ui);
Check(!commented.Changes.Any(c => c.StartsWith("Error:")) &&
      commented.UpgradedContent.Contains("filename = \"" + paths.Graph + "\"") &&
      commented.UpgradedContent.Contains("// filename = \"commented.vnmgraph\""),
    "Commented ModelDoc text was treated as a live field.");

var animationModel = header + """

{ rootNode = { _class = "RootNode" children = [
    { _class = "AnimationList" disabled = true children = [
        { _class = "Folder" children = [
            { _class = "AnimFile" name = "active_legacy" disabled = false source_filename = "models/demo/active.dmx" },
            { _class = "AnimFile" name = "muted_legacy" disabled = true source_filename = "models/demo/muted.dmx" }
        ] }
    ] },
    { _class = "AnimGraph" disabled = false children = [
        { _class = "Folder" disabled = false }
    ] }
] } }
""";
var sanitizedAnimations = Ag2Sanitizer.SanitizeVmdlContent(animationModel).CleanContent;
Check(sanitizedAnimations.Contains("_class = \"AnimFile\" name = \"active_legacy\" disabled = false") &&
      sanitizedAnimations.Contains("_class = \"AnimFile\" name = \"muted_legacy\" disabled = true") &&
      sanitizedAnimations.Contains("_class = \"Folder\" disabled = false"),
    "ModelDoc sanitizer changed disabled flags on children inside an animation node.");
var withAnimations = VmdlPipeline.DisableAnimationNodesForCompilation(sanitizedAnimations, disableAnimationList: false);
Check(Regex.IsMatch(withAnimations, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*false\b") &&
      withAnimations.Contains("_class = \"AnimFile\" name = \"active_legacy\" disabled = false") &&
      withAnimations.Contains("_class = \"AnimFile\" name = \"muted_legacy\" disabled = true"),
    "Unchecking disable animations did not enable the exported AnimationList without changing clips.");
Check(VmdlPipeline.DisableAnimationNodesForCompilation(withAnimations, disableAnimationList: false) == withAnimations,
    "Enabling the AnimationList is not idempotent.");
var withoutAnimations = VmdlPipeline.DisableAnimationNodesForCompilation(withAnimations, disableAnimationList: true);
Check(Regex.IsMatch(withoutAnimations, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*true\b") &&
      withoutAnimations.Contains("_class = \"AnimFile\" name = \"active_legacy\" disabled = false"),
    "Checking disable animations did not disable only the AnimationList.");
Check(VmdlPipeline.DisableAnimationNodesForCompilation(withoutAnimations, disableAnimationList: false) == withAnimations,
    "AnimationList state did not round-trip when toggling the checkbox.");
var animationWithoutFlag = animationModel.Replace("_class = \"AnimationList\" disabled = true",
    "_class = \"AnimationList\"");
var compiledWithoutFlag = VmdlPipeline.DisableAnimationNodesForCompilation(animationWithoutFlag, disableAnimationList: false);
Check(Regex.IsMatch(compiledWithoutFlag, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*false\b"),
    "An AnimationList without an explicit disabled field was not enabled.");

var realAnimationVmdl = Environment.GetEnvironmentVariable("DEADLOCK_TEST_ANIMATION_VMDL");
if (!string.IsNullOrWhiteSpace(realAnimationVmdl))
{
    var originalModel = File.ReadAllText(realAnimationVmdl);
    var enabledModel = VmdlPipeline.DisableAnimationNodesForCompilation(originalModel, disableAnimationList: false);
    var originalClipCount = Regex.Matches(originalModel, @"_class\s*=\s*""AnimFile""").Count;
    Check(originalClipCount > 0 &&
          Regex.IsMatch(enabledModel, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*false\b") &&
          Regex.Matches(enabledModel, @"_class\s*=\s*""AnimFile""").Count == originalClipCount,
        "Real ModelDoc lost animations or kept AnimationList disabled.");
    Console.WriteLine($"Real ModelDoc animation check passed ({originalClipCount} AnimFile nodes).");
}

var root = Path.Combine(Path.GetTempPath(), "deadlock-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var gameDir = Path.Combine(root, "game");
    var modelDir = Path.Combine(gameDir, "models", "heroes_staging", "demo");
    Directory.CreateDirectory(modelDir);

    var deployedModel = Path.Combine(modelDir, "protected.vmdl_c");
    var compilerOutput = Path.Combine(root, "compiler-output.vmdl_c");
    var compiledBytes = new byte[] { 0x11, 0x22, 0x33, 0x44 };
    File.WriteAllBytes(deployedModel, compiledBytes);
    File.WriteAllBytes(compilerOutput, compiledBytes);
    using (CompiledModelProtection.Acquire(deployedModel, compilerOutput))
    {
        Check(File.ReadAllBytes(deployedModel).SequenceEqual(compiledBytes),
            "Protected model could not be read while packaging.");
        var protectedVpk = Path.Combine(root, "protected_pak01_dir.vpk");
        var protectedPack = await VpkBuilder.PackAddonToVpkAsync(gameDir, protectedVpk);
        Check(protectedPack.Success, "VPK packaging could not read a protected model.");
        var protectedBytes = VpkHeroScanner.ExtractFileFromVpk(protectedVpk,
            "models/heroes_staging/demo/protected.vmdl_c");
        Check(protectedBytes != null && protectedBytes.SequenceEqual(compiledBytes),
            "Protected model changed while being packaged.");
        Check(IsWriteBlocked(() =>
        {
            using var stream = new FileStream(deployedModel, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.WriteByte(0xFF);
        }), "FileStream write was allowed while the model was protected.");
        Check(IsWriteBlocked(() => File.WriteAllBytes(deployedModel, new byte[] { 0xFF })),
            "File.WriteAllBytes was allowed while the model was protected.");
        var replacement = Path.Combine(root, "replacement.vmdl_c");
        File.WriteAllBytes(replacement, new byte[] { 0xFF });
        Check(IsWriteBlocked(() => File.Move(replacement, deployedModel, overwrite: true)),
            "Atomic replacement was allowed while the model was protected.");
    }

    using (var writable = new FileStream(deployedModel, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        writable.WriteByte(0x55);

    File.WriteAllBytes(compilerOutput, new byte[] { 0x99, 0x22, 0x33, 0x44 });
    var mismatchRejected = false;
    try
    {
        using var _ = CompiledModelProtection.Acquire(deployedModel, compilerOutput);
    }
    catch (InvalidDataException)
    {
        mismatchRejected = true;
    }
    Check(mismatchRejected, "Protection accepted a compiler output that differs from the deployed model.");

    var original = new byte[] { 0x56, 0x50, 0x4b, 0x21 };
    File.WriteAllBytes(Path.Combine(modelDir, "demo.vmdl_c"), original);
    var vpkPath = Path.Combine(root, "pak01_dir.vpk");
    var packed = await VpkBuilder.PackAddonToVpkAsync(gameDir, vpkPath);
    Check(packed.Success, "VPK packing failed: " + packed.Message);
    var extracted = VpkHeroScanner.ExtractFileFromVpk(vpkPath, "models/heroes_staging/demo/demo.vmdl_c");
    Check(extracted != null && extracted.SequenceEqual(original), "Embedded VPK entry did not round-trip.");

    var vmdl = Path.Combine(root, "sample.vmdl");
    File.WriteAllText(vmdl, bareModel);
    var first = await VmdlPipeline.SanitizeVmdlForModelDocAsync(vmdl);
    Check(first.Success, "First ModelDoc fix failed.");
    File.AppendAllText(vmdl, "\n// second version");
    var second = await VmdlPipeline.SanitizeVmdlForModelDocAsync(vmdl);
    Check(second.Success, "Second ModelDoc fix failed.");
    Check(File.ReadAllText(vmdl + ".bak") == bareModel, "Original backup was overwritten.");
    Check(File.Exists(vmdl + ".bak.1"), "Second backup was not created.");

    Check(AddonCreationService.ValidateName("my_hero_mod") == null, "Valid addon name was rejected.");
    Check(AddonCreationService.ValidateName("../bad") != null &&
          AddonCreationService.ValidateName("_hidden") != null,
        "Unsafe addon name was accepted.");

    var expectedHeroNames = new[]
    {
        "Abrams", "Apollo", "Baba", "Bebop", "Billy", "Calico", "Celeste", "Deadman Danny", "Drifter", "Dynamo",
        "The Doorman", "Graves", "Grey Talon", "Haze", "Holliday", "Infernus", "Ivy",
        "Kelvin", "Lady Geist", "Lash", "McGinnis", "Mina", "Mirage", "Mo & Krill",
        "Nurse Harrow", "Paige", "Paradox", "Pocket", "Rat King", "Rem", "Seven", "Shiv", "Silver", "Sinclair",
        "Solomon", "Venator", "Victor", "Vindicta", "Violet", "Viscous", "Vyper", "Warden", "Wraith", "Yamato"
    };
    var newHeroPresets = new[]
    {
        (HeroKey: "baba", PresetKey: "baba"),
        (HeroKey: "deadman_danny", PresetKey: "deadpack"),
        (HeroKey: "nurse_harrow", PresetKey: "nurse"),
        (HeroKey: "rat_king", PresetKey: "ratking"),
        (HeroKey: "solomon", PresetKey: "chessmaster"),
        (HeroKey: "violet", PresetKey: "artist")
    };
    var catalogHeroes = DeadlockHeroCatalog.GetHeroes();
    Check(catalogHeroes.Select(hero => hero.DisplayName).SequenceEqual(expectedHeroNames),
        "The addon hero catalog does not match the current 44-hero roster.");
    Check(catalogHeroes.Select(hero => hero.HeroKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == expectedHeroNames.Length,
        "The addon hero catalog contains duplicate hero keys.");

    using (var baselineStream = typeof(HeroDatabase).Assembly.GetManifestResourceStream(
               "DeadlockVmdlCompiler.hero_paths.json"))
    {
        Check(baselineStream != null, "The built-in AG2 preset database is missing.");
        var baselinePresets = JsonSerializer.Deserialize<Dictionary<string, HeroPreset>>(baselineStream!);
        Check(baselinePresets is { Count: > 0 }, "The built-in AG2 preset database is empty.");
        var visiblePresets = HeroDatabase.GetVisiblePresets();
        Check(visiblePresets.Count == baselinePresets!.Count &&
              visiblePresets.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                  .SetEquals(baselinePresets.Keys),
            "The preset menu must use the curated built-in key list, even when a local database exists.");
        var visibleHeroKeys = visiblePresets
            .Select(pair => HeroPresetMatcher.FindKnownHero(pair.Key, pair.Value)?.HeroKey)
            .Where(key => key != null)
            .ToList();
        Check(visibleHeroKeys.Count == catalogHeroes.Count &&
              visibleHeroKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalogHeroes.Count,
            "The preset menu contains duplicate heroes or is missing a hero.");
        foreach (var hero in catalogHeroes)
            Check(baselinePresets!.Any(pair =>
                    HeroPresetMatcher.FindKnownHero(pair.Key, pair.Value)?.HeroKey == hero.HeroKey),
                $"No AG2 skeleton preset maps to {hero.DisplayName}.");
        foreach (var (heroKey, presetKey) in newHeroPresets)
        {
            var hero = catalogHeroes.Single(candidate => candidate.HeroKey == heroKey);
            Check(HeroDatabase.GetDatabase().ContainsKey(presetKey),
                $"An older local database hides the new {hero.DisplayName} preset.");
            Check(VmdlPipeline.DetectHeroFromPath("C:/addons/test/" + hero.VpkPath[..^2]) == presetKey,
                $"Exported {hero.DisplayName} does not auto-detect its AG2 preset.");
            Check(VmdlPipeline.DetectHeroFromPath($"C:/addons/test/models/{heroKey}/custom.vmdl") == presetKey,
                $"The {hero.DisplayName} public name does not auto-detect its AG2 preset.");
            Check(HeroPresetMatcher.FindKnownHero("custom_" + presetKey, baselinePresets![presetKey])?.HeroKey == heroKey,
                $"Exact AG2 references under a custom key do not identify {hero.DisplayName}.");
        }
        Check(HeroPresetMatcher.FindKnownHero("seven", baselinePresets!["seven"])?.HeroKey == "seven",
            "Seven still maps to Victor's AG2 skeleton.");
        Check(baselinePresets["familiar_wip"].UiGraph.EndsWith("+familiar.vnmgraph", StringComparison.Ordinal),
            "Rem's UI AG2 graph still points to Frank.");
        Check(HeroPresetMatcher.FindKnownHero("seven", new HeroPreset
        {
            Skel = "models/heroes_wip/frank/frank.vnmskel"
        }) == null, "A mismatched skeleton was given Seven's portrait.");
        Check(HeroPresetMatcher.FindKnownHero("custom_apollo", baselinePresets["fencer"])?.HeroKey == "apollo",
            "An exact AG2 skeleton and graph under a custom key did not identify Apollo.");
        Check(HeroPresetMatcher.FindKnownHero("custom_viscous", baselinePresets["viscous"])?.HeroKey == "viscous",
            "A shared skeleton did not use its graph to distinguish Viscous from Kelvin.");
        foreach (var (folder, key) in new[]
                 {
                     ("familiar", "familiar_wip"), ("gigawatt_prisoner", "seven"),
                     ("hornet_v3", "vindicta"), ("inferno", "infernus"),
                     ("nano_v2", "calico"), ("synth", "pocket"), ("tengu", "ivy")
                 })
            Check(VmdlPipeline.DetectHeroFromPath($"C:/addons/test/models/{folder}/model.vmdl") == key,
                $"Old model folder {folder} does not resolve to the curated preset {key}.");
    }

    var deadlockVpk = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ??
                      Environment.GetEnvironmentVariable("DEADLOCK_TEST_VPK");
    if (!string.IsNullOrWhiteSpace(deadlockVpk))
    {
        var vpkEntries = VpkHeroScanner.ReadVpkDirectory(deadlockVpk);
        var vpkPaths = vpkEntries.Select(entry =>
            $"{entry.Directory}/{entry.FileName}.{entry.Extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var catalogHero in catalogHeroes)
        {
            Check(vpkPaths.Contains(catalogHero.VpkPath),
                $"Missing model in Deadlock VPK: {catalogHero.DisplayName}.");
            if (catalogHero.IconVpkPath != null)
                Check(vpkPaths.Contains(catalogHero.IconVpkPath),
                    $"Missing small portrait in Deadlock VPK: {catalogHero.DisplayName}.");
        }
        var portraits = HeroIconLoader.LoadSmallPortraits(deadlockVpk, catalogHeroes);
        Check(portraits.Count == catalogHeroes.Count(hero => hero.IconVpkPath != null),
            "Not all available small hero portraits could be decoded.");
        Check(portraits.Values.All(png => png.Length > 8 &&
              png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4e && png[3] == 0x47),
            "A hero portrait was not decoded to PNG.");

        var heroBytes = VpkHeroScanner.ExtractFileFromVpk(deadlockVpk,
            "models/heroes_staging/hornet_v3/hornet.vmdl_c");
        Check(heroBytes is { Length: > 0 }, "Could not extract Vindicta model from Deadlock VPK.");
        using var heroResource = new Resource();
        using var stream = new MemoryStream(heroBytes!);
        heroResource.Read(stream);
        var resourceText = heroResource.DataBlock?.ToString() ?? string.Empty;
        Check(resourceText.Contains("m_animGraph2Refs") && resourceText.Contains("m_vecNmSkeletonRefs"),
            "Vindicta compiled model did not expose expected AG2 fields.");
        var heroFile = Path.Combine(root, "hornet.vmdl_c");
        File.WriteAllBytes(heroFile, heroBytes!);
        var hornetPreset = HeroDatabase.GetVisiblePresets()["vindicta"];
        var verificationError = VmdlPipeline.VerifyCompiledAg2References(
            heroFile, hornetPreset.Skel, hornetPreset.Graph, hornetPreset.UiGraph);
        Check(verificationError == null, "Compiled AG2 verification rejected Vindicta: " + verificationError);
        var missingGraph = VmdlPipeline.VerifyCompiledAg2References(
            heroFile, hornetPreset.Skel, "animgraphs/does_not_exist.vnmgraph", hornetPreset.UiGraph);
        Check(missingGraph?.Contains("DefaultAnimGraph2") == true,
            "Compiled AG2 verification accepted a missing graph.");
        foreach (var (heroKey, presetKey) in newHeroPresets)
        {
            var hero = catalogHeroes.Single(candidate => candidate.HeroKey == heroKey);
            var preset = HeroDatabase.GetVisiblePresets()[presetKey];
            Check(vpkPaths.Contains(preset.Skel + "_c") && vpkPaths.Contains(preset.UiGraph + "_c"),
                $"Missing skeleton or UI graph in the VPK for {hero.DisplayName}.");
            var compiledFile = Path.Combine(root, presetKey + ".vmdl_c");
            File.WriteAllBytes(compiledFile, VpkHeroScanner.ExtractFileFromVpk(deadlockVpk, hero.VpkPath)!);
            var newHeroError = VmdlPipeline.VerifyCompiledAg2References(
                compiledFile, preset.Skel, preset.Graph, preset.UiGraph);
            Check(newHeroError == null,
                $"The {hero.DisplayName} preset differs from the game's actual AG2 references: {newHeroError}");
        }
        Console.WriteLine("Deadlock VPK AG2 smoke check passed.");

        if (args.Contains("--new-hero-export"))
        {
            var csdkRoot = Path.Combine(root, "new_hero_csdk12");
            var contentAddons = Path.Combine(csdkRoot, "content", "citadel_addons");
            Directory.CreateDirectory(contentAddons);
            Directory.CreateDirectory(Path.Combine(csdkRoot, "game", "citadel_addons"));
            foreach (var (heroKey, presetKey) in newHeroPresets)
            {
                var hero = catalogHeroes.Single(candidate => candidate.HeroKey == heroKey);
                var addon = await AddonCreationService.CreateAsync(contentAddons, deadlockVpk,
                    hero, presetKey + "_export_test");
                Check(File.Exists(addon.MainVmdlPath) &&
                      Path.GetRelativePath(addon.ContentDirectory, addon.MainVmdlPath).Replace('\\', '/') == hero.VpkPath[..^2],
                    $"The {hero.DisplayName} export lost the main model or its VPK-relative path.");
                Check(Directory.Exists(addon.GameDirectory) &&
                      VmdlScanner.ScanAddons(contentAddons).Any(candidate => candidate.Name == addon.Name),
                    $"The {hero.DisplayName} addon is absent from the CSDK12 or application list.");
                var modelText = File.ReadAllText(addon.MainVmdlPath);
                var meshPaths = Regex.Matches(modelText,
                        @"_class\s*=\s*""RenderMeshFile""[^}]*?\bfilename\s*=\s*""([^""]+\.dmx)""")
                    .Select(match => match.Groups[1].Value).Distinct().ToArray();
                var dmxPaths = Regex.Matches(modelText, @"\bfilename\s*=\s*""([^""]+\.dmx)""")
                    .Select(match => match.Groups[1].Value).Distinct().ToArray();
                Check(meshPaths.Length > 0 && dmxPaths.All(path =>
                        File.Exists(Path.Combine(addon.ContentDirectory, path.Replace('/', Path.DirectorySeparatorChar)))),
                    $"The {hero.DisplayName} export is missing a referenced mesh, animation or cloth DMX.");
                var materialPaths = meshPaths.SelectMany(path =>
                        Regex.Matches(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(
                                Path.Combine(addon.ContentDirectory, path.Replace('/', Path.DirectorySeparatorChar)))),
                            @"[A-Za-z0-9_./-]+\.vmat\b").Select(match => match.Value))
                    .Distinct().ToArray();
                Check(materialPaths.Length > 0 && materialPaths.All(path =>
                        File.Exists(Path.Combine(addon.ContentDirectory, path.Replace('/', Path.DirectorySeparatorChar)))),
                    $"The {hero.DisplayName} export is missing referenced materials.");
                Check(Directory.EnumerateFiles(addon.ContentDirectory, "*.png", SearchOption.AllDirectories).Any(),
                    $"The {hero.DisplayName} export is missing material textures.");
                var preset = HeroDatabase.GetVisiblePresets()[presetKey];
                var injected = VmdlPipeline.UpgradeVmdlContent(modelText, preset.Skel, preset.Graph,
                    preset.UiGraph);
                Check(!injected.Changes.Any(change => change.StartsWith("Error:", StringComparison.Ordinal)) &&
                      injected.UpgradedContent.Contains(preset.Skel, StringComparison.Ordinal) &&
                      injected.UpgradedContent.Contains(preset.Graph, StringComparison.Ordinal) &&
                      injected.UpgradedContent.Contains(preset.UiGraph, StringComparison.Ordinal),
                    $"The {hero.DisplayName} export cannot receive its AG2 nodes for CSWin64.");
                Console.WriteLine($"{hero.DisplayName} addon export passed: {addon.FileCount} files, {addon.ClothFileCount} cloth assets.");
            }
        }

        if (args.Contains("--addon-export"))
        {
            var csdkRoot = Path.Combine(root, "test_csdk12");
            var contentAddons = Path.Combine(csdkRoot, "content", "citadel_addons");
            var gameAddons = Path.Combine(csdkRoot, "game", "citadel_addons");
            Directory.CreateDirectory(contentAddons);
            Directory.CreateDirectory(gameAddons);
            var hero = DeadlockHeroCatalog.GetHeroes().Single(h => h.HeroKey == "wraith");
            var addon = await AddonCreationService.CreateAsync(contentAddons, deadlockVpk,
                hero, "wraith_export_test", onLog: Console.WriteLine);
            Check(File.Exists(addon.MainVmdlPath), "Main hero ModelDoc was not exported.");
            Check(File.Exists(Path.Combine(addon.ContentDirectory,
                "models", "heroes_wip", "wraith", "wraith.vmdl")),
                "Main model lost its VPK-relative path.");
            Check(Directory.Exists(addon.GameDirectory), "CSDK12 game addon directory is missing.");
            var modelDmx = Directory.EnumerateFiles(addon.ContentDirectory, "*model.dmx", SearchOption.AllDirectories)
                .FirstOrDefault();
            Check(modelDmx != null, "Main model mesh DMX was not exported.");
            var dmxText = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(modelDmx!));
            var materialPaths = Regex.Matches(dmxText, @"[A-Za-z0-9_./-]+\.vmat\b")
                .Select(m => m.Value).Distinct().ToArray();
            Check(materialPaths.Length > 0 && materialPaths.All(path =>
                    File.Exists(Path.Combine(addon.ContentDirectory, path.Replace('/', Path.DirectorySeparatorChar)))),
                "A referenced material is missing or lost its VPK-relative path.");
            Check(Directory.EnumerateFiles(addon.ContentDirectory, "*.vmat", SearchOption.AllDirectories).Any(),
                "Material dependencies were not exported.");
            Check(Directory.EnumerateFiles(addon.ContentDirectory, "*.png", SearchOption.AllDirectories).Any(),
                "Material texture dependencies were not exported.");
            Check(Directory.EnumerateFiles(addon.ContentDirectory, "*.dmx", SearchOption.AllDirectories).Any(),
                "Model or animation DMX dependencies were not exported.");
            Check(addon.ClothFileCount >= 2, "Cloth proxy and grid were not exported.");
            Check(VmdlScanner.ScanAddons(contentAddons).Any(a => a.Name == "wraith_export_test"),
                "New addon is absent from the application list.");
            var duplicateRejected = false;
            try
            {
                await AddonCreationService.CreateAsync(contentAddons, deadlockVpk,
                    hero, "wraith_export_test");
            }
            catch (IOException) { duplicateRejected = true; }
            Check(duplicateRejected, "Existing addon was overwritten.");
            Check(!Directory.EnumerateDirectories(contentAddons, ".creating-*").Any() &&
                  !Directory.EnumerateDirectories(gameAddons, ".creating-*").Any(),
                "Temporary addon directories remained after export.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try
            {
                await AddonCreationService.CreateAsync(contentAddons, deadlockVpk,
                    hero, "cancelled_export_test", cancellationToken: cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled &&
                  !Directory.Exists(Path.Combine(contentAddons, "cancelled_export_test")) &&
                  !Directory.Exists(Path.Combine(gameAddons, "cancelled_export_test")) &&
                  !Directory.EnumerateDirectories(contentAddons, ".creating-*").Any() &&
                  !Directory.EnumerateDirectories(gameAddons, ".creating-*").Any(),
                "Cancelled export left an addon or temporary directories.");
            Console.WriteLine($"Wraith addon export passed: {addon.FileCount} files, " +
                              $"{addon.ClothFileCount} cloth assets.");
        }
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}

Console.WriteLine("Regression checks passed.");
