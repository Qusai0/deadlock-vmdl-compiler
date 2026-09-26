using System.Text.RegularExpressions;
using DeadlockVmdlCompiler.Services;
using ValveResourceFormat;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
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

var root = Path.Combine(Path.GetTempPath(), "deadlock-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var gameDir = Path.Combine(root, "game");
    var modelDir = Path.Combine(gameDir, "models", "heroes_staging", "demo");
    Directory.CreateDirectory(modelDir);
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

    var deadlockVpk = Environment.GetEnvironmentVariable("DEADLOCK_TEST_VPK");
    if (!string.IsNullOrWhiteSpace(deadlockVpk))
    {
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
        var hornetPreset = HeroDatabase.GetDatabase()["hornet"];
        var verificationError = VmdlPipeline.VerifyCompiledAg2References(
            heroFile, hornetPreset.Skel, hornetPreset.Graph, hornetPreset.UiGraph);
        Check(verificationError == null, "Compiled AG2 verification rejected Vindicta: " + verificationError);
        var missingGraph = VmdlPipeline.VerifyCompiledAg2References(
            heroFile, hornetPreset.Skel, "animgraphs/does_not_exist.vnmgraph", hornetPreset.UiGraph);
        Check(missingGraph?.Contains("DefaultAnimGraph2") == true,
            "Compiled AG2 verification accepted a missing graph.");
        Console.WriteLine("Deadlock VPK AG2 smoke check passed.");
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}

Console.WriteLine("Regression checks passed.");
