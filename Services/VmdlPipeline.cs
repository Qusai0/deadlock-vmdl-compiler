using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DeadlockVmdlCompiler.Models;
using ValveResourceFormat;

namespace DeadlockVmdlCompiler.Services;

public static class VmdlPipeline
{
    public const string ModelDoc41Header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc41:version{12fc9d44-453a-4ae4-b4d9-7e2ac0bbd4e0} -->";
    public const string DefaultCsWinDir = @"A:\modding\CSWin64";

    public static bool IsValidCsWinDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        var rc1 = Path.Combine(path, "game", "bin", "win64", "resourcecompiler.exe");
        var rc2 = Path.Combine(path, "bin", "win64", "resourcecompiler.exe");
        return File.Exists(rc1) || File.Exists(rc2);
    }

    public static string? ExtractCitadelAddonsDir(string filepath)
    {
        if (string.IsNullOrWhiteSpace(filepath))
            return null;

        var clean = filepath.Replace('\\', '/');
        var m = Regex.Match(clean, @"^(.*?/content/(citadel_addons|citadel_community_addons|citadel))(/|$)", RegexOptions.IgnoreCase);
        if (m.Success)
            return Path.GetFullPath(m.Groups[1].Value);

        var m2 = Regex.Match(clean, @"^(.*?/citadel_addons)(/|$)", RegexOptions.IgnoreCase);
        if (m2.Success)
            return Path.GetFullPath(m2.Groups[1].Value);

        return null;
    }

    public static string? DetectHeroFromPath(string filepath)
    {
        var db = HeroDatabase.GetDatabase();
        var clean = filepath.Replace('\\', '/').ToLowerInvariant();
        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        // Older game folders and local databases can use names removed from the
        // curated preset menu. Resolve them to the remaining preset first.
        var renamedFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["deadman_danny"] = "deadpack",
            ["familiar"] = "familiar_wip",
            ["ghost"] = "geist",
            ["gigawatt_prisoner"] = "seven",
            ["hornet"] = "vindicta",
            ["inferno"] = "infernus",
            ["lady_geist"] = "geist",
            ["nano"] = "calico",
            ["nurse_harrow"] = "nurse",
            ["rat_king"] = "ratking",
            ["solomon"] = "chessmaster",
            ["synth"] = "pocket",
            ["tengu"] = "ivy",
            ["violet"] = "artist"
        };

        // Check parent folder names from closest upwards
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            var folder = parts[i];
            var renamed = renamedFolders
                .Where(pair => folder.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) ||
                               folder.StartsWith(pair.Key + "_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(pair => pair.Key.Length)
                .FirstOrDefault();
            if (renamed.Value != null && db.ContainsKey(renamed.Value))
                return renamed.Value;
            if (db.ContainsKey(folder))
                return folder;
            var versionedMatch = db.Keys
                .Where(key => folder.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(key => key.Length)
                .FirstOrDefault();
            if (versionedMatch != null)
                return versionedMatch;
        }

        // Check filename stem
        var filename = Path.GetFileNameWithoutExtension(filepath).ToLowerInvariant();
        if (db.ContainsKey(filename))
            return filename;

        return null;
    }

    public static (string Container, string AddonName, string Subpath) ParseCsdkPath(string csdkPath, string? citadelAddonsDir = null)
    {
        var clean = csdkPath.Replace('\\', '/');

        // 1. Standard pattern: .../content/(citadel_addons|citadel_community_addons|citadel)/<addon_name>/<subpath>
        var m = Regex.Match(clean, @"content/(citadel_addons|citadel_community_addons|citadel)/([^/]+)/(.+)$", RegexOptions.IgnoreCase);
        if (m.Success)
            return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);

        // 2. General content subfolder pattern: .../content/<addon_name>/<subpath>
        var m2 = Regex.Match(clean, @"content/([^/]+)/(.+)$", RegexOptions.IgnoreCase);
        if (m2.Success)
            return ("citadel_addons", m2.Groups[1].Value, m2.Groups[2].Value);

        // 3. Relative to configured citadelAddonsDir
        if (!string.IsNullOrWhiteSpace(citadelAddonsDir))
        {
            var cleanAddons = citadelAddonsDir.Replace('\\', '/').TrimEnd('/');
            if (clean.StartsWith(cleanAddons + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = clean[(cleanAddons.Length + 1)..];
                var parts = rel.Split('/', 2);
                if (parts.Length == 2)
                    return ("citadel_addons", parts[0], parts[1]);
                return ("citadel_addons", "addon", parts[0]);
            }
        }

        return ("citadel_addons", "addon", Path.GetFileName(clean));
    }

    public static string ResolveGameAddonDir(string targetVmdlPath, string? citadelAddonsDir, string addonName)
    {
        // 1. Try resolving relative to targetVmdlPath containing /content/
        var normTarget = targetVmdlPath.Replace('\\', '/');
        int contentIdx = normTarget.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
        if (contentIdx >= 0)
        {
            var root = normTarget[..contentIdx];
            var afterContent = normTarget[(contentIdx + "/content/".Length)..];
            var parts = afterContent.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var cand1 = Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), "game", parts[0], parts[1]);
                if (Directory.Exists(cand1)) return cand1;
                var cand2 = Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), "game", parts[1]);
                if (Directory.Exists(cand2)) return cand2;
                return cand1;
            }
        }

        // 2. Try resolving relative to citadelAddonsDir
        if (!string.IsNullOrWhiteSpace(citadelAddonsDir))
        {
            var normCitadel = citadelAddonsDir.Replace('\\', '/');
            int citContentIdx = normCitadel.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
            if (citContentIdx >= 0)
            {
                var root = normCitadel[..citContentIdx];
                var afterContent = normCitadel[(citContentIdx + "/content/".Length)..].Trim('/');
                var cand = Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), "game", afterContent.Replace('/', Path.DirectorySeparatorChar), addonName);
                if (Directory.Exists(cand)) return cand;
                var cand2 = Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), "game", "citadel_addons", addonName);
                if (Directory.Exists(cand2)) return cand2;
                return cand;
            }

            var parent = Directory.GetParent(citadelAddonsDir)?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                var cand = Path.Combine(parent, "game", "citadel_addons", addonName);
                return cand;
            }
        }

        return Path.Combine(Path.GetDirectoryName(targetVmdlPath) ?? string.Empty, "game", addonName);
    }

    public static (string Skel, string Graph, string UiGraph) DeriveDefaultPaths(string vmdlPath)
    {
        var db = HeroDatabase.GetDatabase();
        var hero = DetectHeroFromPath(vmdlPath);

        if (hero != null && db.TryGetValue(hero, out var preset))
        {
            return (preset.Skel, preset.Graph, preset.UiGraph);
        }

        // There is no reliable way to infer compiled Deadlock references from a custom VMDL path.
        return (string.Empty, string.Empty, string.Empty);
    }

    public static (string UpgradedContent, List<string> Changes) UpgradeVmdlContent(
        string content,
        string skelPath,
        string graphPath,
        string? uiGraphPath = null,
        bool addSkel = true,
        bool addGraph = true,
        bool addUiGraph = true,
        bool upgradeHeader = true)
        => ModelDocAg2Editor.Upgrade(content, skelPath, graphPath, uiGraphPath,
            addSkel, addGraph, addUiGraph, upgradeHeader, ModelDoc41Header);

    public record CompileProgress(
        int Step,
        int TotalSteps,
        int Percent,
        string Stage,
        string Detail
    );

    private static bool IsCompilerNoiseLine(string rawLine, out string? cleanedLine)
    {
        cleanedLine = null;
        if (string.IsNullOrWhiteSpace(rawLine))
            return true;

        var line = rawLine.Trim();

        // 1. Missing material references & illegal resource loaders (CSWin64 does not host Deadlock materials)
        if (line.Contains("missing material", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("referencing missing material", StringComparison.OrdinalIgnoreCase) ||
            (line.Contains("Trying to load an illegal resource name", StringComparison.OrdinalIgnoreCase) && line.Contains(".vmat", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 2. Generic warning headers produced when materials are missing
        if (line.Contains("Compile WARNINGS", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("These may represent problems, but will not cause the compile to fail", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Look for \"RESOURCE COMPILE WARNING:\"", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. Dashed or equal sign horizontal separator lines
        if (line.Length >= 5 && line.All(c => c == '-' || c == '='))
        {
            return true;
        }

        // 4. "RESOURCE COMPILE WARNING:" specifically for .vmat
        if (line.Contains("RESOURCE COMPILE WARNING:", StringComparison.OrdinalIgnoreCase) &&
            line.Contains(".vmat", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 5. If summary line has "WARNING: 1 compiled, 0 failed...", strip the "WARNING: " prefix
        if (line.Contains("compiled,", StringComparison.OrdinalIgnoreCase) && line.Contains("failed,", StringComparison.OrdinalIgnoreCase))
        {
            if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
            {
                line = line.Substring("WARNING:".Length).Trim();
            }
            cleanedLine = line;
            return false;
        }

        cleanedLine = line;
        return false;
    }

    public static async Task<(bool Success, string Message)> CompileViaCsWinAndDeployAsync(
        string csdk12VmdlPath,
        string upgradedVmdlContent,
        string? cswinDir = null,
        string? citadelAddonsDir = null,
        bool disableAnimationList = true,
        IProgress<CompileProgress>? progress = null,
        Action<string>? onLog = null,
        string? expectedSkelPath = null,
        string? expectedGraphPath = null,
        string? expectedUiGraphPath = null,
        Action<string>? beforeDeploy = null,
        Action<string, string>? afterDeploy = null)
    {
        var cfg = ConfigManager.LoadConfig();
        var useCsWinDir = !string.IsNullOrWhiteSpace(cswinDir) ? cswinDir : (!string.IsNullOrWhiteSpace(cfg.CsWinDir) ? cfg.CsWinDir : DefaultCsWinDir);
        var useCitadelDir = !string.IsNullOrWhiteSpace(citadelAddonsDir) ? citadelAddonsDir : cfg.CitadelAddonsDir;

        var rcExe = Path.Combine(useCsWinDir, "game", "bin", "win64", "resourcecompiler.exe");
        var csWinGameDir = Path.Combine(useCsWinDir, "game", "csgo");

        if (!File.Exists(rcExe))
        {
            var altRcExe = Path.Combine(useCsWinDir, "bin", "win64", "resourcecompiler.exe");
            if (File.Exists(altRcExe))
            {
                rcExe = altRcExe;
            }
            else
            {
                return (false, $"CSWin64 resourcecompiler.exe not found in: {useCsWinDir}");
            }
        }

        var (container, addonName, subpath) = ParseCsdkPath(csdk12VmdlPath, useCitadelDir);

        var csWinVmdlPath = Path.Combine(useCsWinDir, "content", "csgo_addons", addonName, subpath);
        var csWinVmdlDir = Path.GetDirectoryName(csWinVmdlPath)!;
        var csWinCompiledVmdlc = Path.Combine(useCsWinDir, "game", "csgo_addons", addonName, subpath + "_c");
        var csdkVmdlDir = Path.GetDirectoryName(csdk12VmdlPath);
        Directory.CreateDirectory(csWinVmdlDir);

        // 1. Sync mesh/model files (.dmx, .fbx, .smd, .obj, .vmat, .png, .vanim) to CSWin64 so resourcecompiler finds them
        if (!string.IsNullOrEmpty(csdkVmdlDir) && Directory.Exists(csdkVmdlDir))
        {
            progress?.Report(new CompileProgress(2, 5, 25, "[2/5] syncing assets", "scanning model assets..."));
            onLog?.Invoke("[sync] scanning for model assets (.dmx, .fbx, .smd, .vmat, .png, .vanim)...");

            var allowedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".dmx", ".fbx", ".smd", ".obj", ".vmat", ".png", ".vanim"
            };

            var filesToCopy = Directory.EnumerateFiles(csdkVmdlDir, "*.*", SearchOption.AllDirectories)
                .Where(f => allowedExts.Contains(Path.GetExtension(f)))
                .ToList();

            int copied = 0;
            foreach (var srcFile in filesToCopy)
            {
                var relFile = Path.GetRelativePath(csdkVmdlDir, srcFile);
                var dstFile = Path.Combine(csWinVmdlDir, relFile);
                Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                if (!File.Exists(dstFile) || File.GetLastWriteTimeUtc(srcFile) > File.GetLastWriteTimeUtc(dstFile))
                {
                    try
                    {
                        File.Copy(srcFile, dstFile, overwrite: true);
                        copied++;
                        progress?.Report(new CompileProgress(
                            2,
                            5,
                            25 + (int)(20.0 * copied / Math.Max(1, filesToCopy.Count)),
                            "[2/5] syncing assets",
                            relFile
                        ));
                        onLog?.Invoke($"[sync] copied: {relFile}");
                    }
                    catch { }
                }
            }
            onLog?.Invoke($"[sync] synchronized {copied} updated asset(s) to cswin64");
        }

        // 2. Apply the checkbox state to the temporary CSWin64 ModelDoc.
        progress?.Report(new CompileProgress(3, 5, 50, "[3/5] preparing modeldoc", "temporary definition..."));
        var csWinContent = DisableAnimationNodesForCompilation(upgradedVmdlContent, disableAnimationList);

        await File.WriteAllTextAsync(csWinVmdlPath, csWinContent);
        onLog?.Invoke($"[prepare] AnimationList {(disableAnimationList ? "disabled" : "enabled")} for CSWin64 compilation");
        onLog?.Invoke("[prepare] wrote temporary modeldoc definition to cswin64 addon");

        // A successful compiler exit must not be mistaken for an old output from a previous run.
        if (File.Exists(csWinCompiledVmdlc))
            File.Delete(csWinCompiledVmdlc);

        var psi = new ProcessStartInfo
        {
            FileName = rcExe,
            Arguments = $"-f -i \"{csWinVmdlPath}\" -game \"{csWinGameDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        progress?.Report(new CompileProgress(4, 5, 60, "[4/5] compiling model", "resourcecompiler.exe"));
        onLog?.Invoke($"[compiler] starting: resourcecompiler.exe -f -i \"{Path.GetFileName(csWinVmdlPath)}\"");

        var outputLines = new List<string>();
        var errorLines = new List<string>();
        var rawLines = new List<string>();

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                rawLines.Add(e.Data);
                if (!IsCompilerNoiseLine(e.Data, out var cleaned))
                {
                    outputLines.Add(cleaned!);
                    progress?.Report(new CompileProgress(4, 5, 75, "[4/5] compiling model", cleaned!));
                    onLog?.Invoke($"[cswin64] {cleaned!}");
                }
            }
        };
        proc.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                rawLines.Add(e.Data);
                if (!IsCompilerNoiseLine(e.Data, out var cleaned))
                {
                    errorLines.Add(cleaned!);
                    progress?.Report(new CompileProgress(4, 5, 75, "[4/5] compiling model", cleaned!));
                    onLog?.Invoke($"[cswin64 err] {cleaned!}");
                }
            }
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var msg = errorLines.Count > 0 
                ? string.Join("\n", errorLines) 
                : (outputLines.Count > 0 ? string.Join("\n", outputLines) : string.Join("\n", rawLines));
            return (false, $"CSWin64 Compiler error (code {proc.ExitCode}): {msg.Trim()}");
        }

        if (!File.Exists(csWinCompiledVmdlc))
        {
            return (false, $"Compiler finished but .vmdl_c was not created at: {csWinCompiledVmdlc}");
        }

        var verificationError = VerifyCompiledAg2References(csWinCompiledVmdlc,
            expectedSkelPath, expectedGraphPath, expectedUiGraphPath);
        if (verificationError != null)
            return (false, verificationError);

        progress?.Report(new CompileProgress(5, 5, 90, "[5/5] deploying model", Path.GetFileName(csWinCompiledVmdlc)));

        string csdk12GameVmdlc;
        var cleanPath = csdk12VmdlPath.Replace('\\', '/');

        if (cleanPath.Contains("/content/", StringComparison.OrdinalIgnoreCase))
        {
            var idx = cleanPath.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
            var root = cleanPath[..idx];
            csdk12GameVmdlc = Path.Combine(root, "game", container, addonName, subpath + "_c");
        }
        else if (!string.IsNullOrWhiteSpace(useCitadelDir) && useCitadelDir.Replace('\\', '/').Contains("/content/", StringComparison.OrdinalIgnoreCase))
        {
            var cleanCitadel = useCitadelDir.Replace('\\', '/');
            var idx = cleanCitadel.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
            var root = cleanCitadel[..idx];
            csdk12GameVmdlc = Path.Combine(root, "game", container, addonName, subpath + "_c");
        }
        else
        {
            csdk12GameVmdlc = Path.ChangeExtension(csdk12VmdlPath, ".vmdl_c");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(csdk12GameVmdlc)!);
        beforeDeploy?.Invoke(csdk12GameVmdlc);
        File.Copy(csWinCompiledVmdlc, csdk12GameVmdlc, overwrite: true);
        afterDeploy?.Invoke(csdk12GameVmdlc, csWinCompiledVmdlc);

        var vmdlcSize = new FileInfo(csdk12GameVmdlc).Length;
        onLog?.Invoke($"[deploy] deployed .vmdl_c ({vmdlcSize / 1024:N0} KB) to: {csdk12GameVmdlc}");

        return (true, $"Compiled via CSWin64 & deployed .vmdl_c to: {csdk12GameVmdlc}");
    }

    public static string? VerifyCompiledAg2References(
        string compiledPath, string? expectedSkelPath, string? expectedGraphPath, string? expectedUiGraphPath)
    {
        if (expectedSkelPath == null && expectedGraphPath == null && expectedUiGraphPath == null)
            return null;
        if ((expectedSkelPath != null && string.IsNullOrWhiteSpace(expectedSkelPath)) ||
            (expectedGraphPath != null && string.IsNullOrWhiteSpace(expectedGraphPath)) ||
            (expectedUiGraphPath != null && string.IsNullOrWhiteSpace(expectedUiGraphPath)))
            return "Cannot verify AG2 references because a selected hero preset path is empty.";

        try
        {
            using var resource = new Resource();
            resource.Read(compiledPath);
            var data = (resource.DataBlock?.ToString() ?? string.Empty)
                .Replace('\\', '/')
                .Replace("\\u002B", "+", StringComparison.OrdinalIgnoreCase);
            var missing = new List<string>();

            if (expectedSkelPath != null &&
                (!data.Contains("m_vecNmSkeletonRefs", StringComparison.OrdinalIgnoreCase) ||
                 !data.Contains(expectedSkelPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                missing.Add($"NmSkeletonReference ({expectedSkelPath})");

            if (expectedGraphPath != null &&
                (!data.Contains("m_animGraph2Refs", StringComparison.OrdinalIgnoreCase) ||
                 !data.Contains(expectedGraphPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                missing.Add($"DefaultAnimGraph2 ({expectedGraphPath})");

            if (expectedUiGraphPath != null)
            {
                var uiFound = Regex.Matches(data, @"\{[\s\S]*?\}")
                    .Any(item => Regex.IsMatch(item.Value, @"\bm_sIdentifier\s*=\s*""ui""", RegexOptions.IgnoreCase) &&
                                 item.Value.Contains(expectedUiGraphPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                if (!uiFound) missing.Add($"ui AnimGraph2 ({expectedUiGraphPath})");
            }

            return missing.Count == 0 ? null :
                $"Compiled model is missing AG2 references: {string.Join(", ", missing)}. The model was not deployed.";
        }
        catch (Exception ex)
        {
            return $"Could not verify AG2 references in compiled model: {ex.Message}. The model was not deployed.";
        }
    }

    private static string CreateUniqueBackup(string sourcePath)
    {
        var basePath = sourcePath + ".bak";
        for (var number = 0; ; number++)
        {
            var destination = number == 0 ? basePath : basePath + "." + number;
            try
            {
                File.Copy(sourcePath, destination, overwrite: false);
                return destination;
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Preserve every previous backup, including the original pre-sanitize source.
            }
        }
    }

    public static async Task<(bool Success, string Message)> ProcessVmdlFileAsync(
        string filepath,
        string? skelPath = null,
        string? graphPath = null,
        string? uiGraphPath = null,
        bool createBackup = true,
        bool addSkel = true,
        bool addGraph = true,
        bool addUiGraph = true,
        bool upgradeHeader = true,
        bool compileCsWin = true,
        bool revertVmdl = true,
        string? cswinDir = null,
        string? citadelAddonsDir = null,
        bool disableAnimationList = true,
        IProgress<CompileProgress>? progress = null,
        Action<string>? onLog = null,
        Action<string>? beforeDeploy = null,
        Action<string, string>? afterDeploy = null)
    {
        filepath = Path.GetFullPath(filepath);
        if (!File.Exists(filepath))
            return (false, $"File not found: {filepath}");

        progress?.Report(new CompileProgress(1, 5, 10, "[1/5] preparing source", Path.GetFileName(filepath)));

        var (defSkel, defGraph, defUiGraph) = DeriveDefaultPaths(filepath);
        var useSkel = !string.IsNullOrWhiteSpace(skelPath) ? skelPath : defSkel;
        var useGraph = !string.IsNullOrWhiteSpace(graphPath) ? graphPath : defGraph;
        var useUiGraph = !string.IsNullOrWhiteSpace(uiGraphPath) ? uiGraphPath : defUiGraph;

        var origContent = await File.ReadAllTextAsync(filepath);

        var (upgradedContent, changes) = UpgradeVmdlContent(
            origContent,
            skelPath: useSkel,
            graphPath: useGraph,
            uiGraphPath: useUiGraph,
            addSkel: addSkel,
            addGraph: addGraph,
            addUiGraph: addUiGraph,
            upgradeHeader: upgradeHeader
        );

        var upgradeError = changes.FirstOrDefault(change => change.StartsWith("Error:", StringComparison.Ordinal));
        if (upgradeError != null)
            return (false, upgradeError);

        if (changes.Count > 0)
        {
            onLog?.Invoke($"[ag2] upgraded syntax: {string.Join(", ", changes)}");
        }

        if (createBackup)
        {
            var bakFile = CreateUniqueBackup(filepath);
            onLog?.Invoke($"[backup] created backup: {Path.GetFileName(bakFile)}");
        }

        var stepLogs = new List<string>();

        if (compileCsWin)
        {
            var (compSuccess, compMsg) = await CompileViaCsWinAndDeployAsync(
                filepath,
                upgradedContent,
                cswinDir: cswinDir,
                citadelAddonsDir: citadelAddonsDir,
                disableAnimationList: disableAnimationList,
                progress: progress,
                onLog: onLog,
                expectedSkelPath: addSkel ? useSkel : null,
                expectedGraphPath: addGraph ? useGraph : null,
                expectedUiGraphPath: addUiGraph ? useUiGraph : null,
                beforeDeploy: beforeDeploy,
                afterDeploy: afterDeploy
            );

            if (!compSuccess)
                return (false, $"CSWin64 Compilation Failed: {compMsg}");

            stepLogs.Add(compMsg);
        }

        progress?.Report(new CompileProgress(5, 5, 95, "[5/5] finalizing", revertVmdl ? "leaving source unchanged" : "saving vmdl"));

        if (revertVmdl)
        {
            stepLogs.Add("Left CSDK12 VMDL unchanged (ModelDoc compatible)");
            onLog?.Invoke("[revert] working .vmdl was not modified");
        }
        else
        {
            await File.WriteAllTextAsync(filepath, upgradedContent);
            stepLogs.Add($"Saved upgraded VMDL ({string.Join(", ", changes)})");
            onLog?.Invoke($"[save] saved upgraded .vmdl with ag2 node injections");
        }

        progress?.Report(new CompileProgress(5, 5, 100, "[5/5] complete", "model compiled and deployed successfully"));
        onLog?.Invoke($"[success] compilation finished successfully for {Path.GetFileName(filepath)}!");

        return (true, string.Join(" | ", stepLogs));
    }

    public static async Task<(bool Success, string Message, int FilesCopied)> ExportToCsWinAddonAsync(
        string filepath,
        string? skelPath = null,
        string? graphPath = null,
        string? uiGraphPath = null,
        bool addSkel = true,
        bool addGraph = true,
        bool addUiGraph = true,
        string? cswinDir = null,
        string? citadelAddonsDir = null)
    {
        filepath = Path.GetFullPath(filepath);
        if (!File.Exists(filepath))
            return (false, $"File not found: {filepath}", 0);

        var cfg = ConfigManager.LoadConfig();
        var useCsWinDir = !string.IsNullOrWhiteSpace(cswinDir) ? cswinDir : (!string.IsNullOrWhiteSpace(cfg.CsWinDir) ? cfg.CsWinDir : DefaultCsWinDir);
        var useCitadelDir = !string.IsNullOrWhiteSpace(citadelAddonsDir) ? citadelAddonsDir : cfg.CitadelAddonsDir;

        if (!IsValidCsWinDir(useCsWinDir))
            return (false, $"CSWin64 resourcecompiler.exe was not found in: {useCsWinDir}", 0);

        var (container, addonName, subpath) = ParseCsdkPath(filepath, useCitadelDir);

        var (defSkel, defGraph, defUiGraph) = DeriveDefaultPaths(filepath);
        var useSkel = !string.IsNullOrWhiteSpace(skelPath) ? skelPath : defSkel;
        var useGraph = !string.IsNullOrWhiteSpace(graphPath) ? graphPath : defGraph;
        var useUiGraph = !string.IsNullOrWhiteSpace(uiGraphPath) ? uiGraphPath : defUiGraph;

        var origContent = await File.ReadAllTextAsync(filepath);

        var (upgradedContent, changes) = UpgradeVmdlContent(
            origContent,
            skelPath: useSkel,
            graphPath: useGraph,
            uiGraphPath: useUiGraph,
            addSkel: addSkel,
            addGraph: addGraph,
            addUiGraph: addUiGraph,
            upgradeHeader: true
        );

        var upgradeError = changes.FirstOrDefault(change => change.StartsWith("Error:", StringComparison.Ordinal));
        if (upgradeError != null)
            return (false, upgradeError, 0);

        int filesCopied = 0;
        var srcModelDir = Path.GetDirectoryName(filepath) ?? string.Empty;
        var contentAddonDir = Path.Combine(useCsWinDir, "content", "csgo_addons", addonName);
        var gameAddonDir = Path.Combine(useCsWinDir, "game", "csgo_addons", addonName);
        var destModelDir = Path.Combine(contentAddonDir, Path.GetDirectoryName(subpath) ?? string.Empty);

        Directory.CreateDirectory(contentAddonDir);
        Directory.CreateDirectory(gameAddonDir);
        Directory.CreateDirectory(destModelDir);

        // Auto-register addon in CSWin64 Workshop Tools via ServerConfig.vdf
        var serverConfigPath = Path.Combine(gameAddonDir, "ServerConfig.vdf");
        if (!File.Exists(serverConfigPath))
        {
            var serverConfigContent = "\"ServerConfig\"\n{\n\t\"bot_quota\"\t\t\"10\"\n\t\"bot_difficulty\"\t\t\"2\"\n\t\"bot_chatter\"\t\t\"normal\"\n\t\"bot_join_team\"\t\t\"any\"\n\t\"bot_defer_to_human_items\"\t\t\"true\"\n\t\"bot_defer_to_human_goals\"\t\t\"true\"\n\t\"bot_join_after_player\"\t\t\"true\"\n\t\"bot_allow_rogues\"\t\t\"true\"\n\t\"bot_allow_pistols\"\t\t\"true\"\n\t\"bot_allow_shotguns\"\t\t\"true\"\n\t\"bot_allow_sub_machine_guns\"\t\t\"true\"\n\t\"bot_allow_machine_guns\"\t\t\"true\"\n\t\"bot_allow_rifles\"\t\t\"true\"\n\t\"bot_allow_snipers\"\t\t\"true\"\n\t\"bot_allow_grenades\"\t\t\"true\"\n\t\"bot_controllable\"\t\t\"true\"\n}\n";
            await File.WriteAllTextAsync(serverConfigPath, serverConfigContent);
        }

        // Copy ONLY .vmdl and 3D mesh files (.dmx, .smd, .fbx, .obj) - no materials or textures
        var meshExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dmx", ".smd", ".fbx", ".obj" };

        if (Directory.Exists(srcModelDir))
        {
            var allFiles = Directory.GetFiles(srcModelDir, "*.*", SearchOption.AllDirectories)
                .Where(f => meshExts.Contains(Path.GetExtension(f)) ||
                            string.Equals(f, filepath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var srcFile in allFiles)
            {
                var relFile = Path.GetRelativePath(srcModelDir, srcFile);
                var destFile = Path.Combine(destModelDir, relFile);

                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                // If it is the main .vmdl, write the upgraded version with AG2 nodes
                if (string.Equals(srcFile, filepath, StringComparison.OrdinalIgnoreCase))
                {
                    await File.WriteAllTextAsync(destFile, upgradedContent);
                }
                else
                {
                    File.Copy(srcFile, destFile, overwrite: true);
                }
                filesCopied++;
            }
        }
        else
        {
            var destVmdl = Path.Combine(useCsWinDir, "content", "csgo_addons", addonName, subpath);
            Directory.CreateDirectory(Path.GetDirectoryName(destVmdl)!);
            await File.WriteAllTextAsync(destVmdl, upgradedContent);
            filesCopied++;
        }

        var destVmdlPath = Path.Combine(useCsWinDir, "content", "csgo_addons", addonName, subpath);
        return (true, $"Exported model & {filesCopied} asset(s) to CSWin64 addon: {destVmdlPath}", filesCopied);
    }

    public static string DisableAnimationNodesForCompilation(string content, bool disableAnimationList = true)
    {
        // Decompiled CSDK12 sources may already contain disabled = true. The checkbox
        // must override that source state in both directions for the CSWin64 copy.
        content = ModelDocAg2Editor.SetNodeDisabled(content, "AnimationList", disableAnimationList);
        content = DisableNodeByClass(content, "EmptyAnimGraph");
        content = DisableNodeByClass(content, "AnimGraph");
        return content;
    }

    private static string DisableNodeByClass(string content, string className) =>
        ModelDocAg2Editor.SetNodeDisabled(content, className, disabled: true);

    public static string RemoveModelDocNode(string content, string className)
    {
        while (true)
        {
            var match = Regex.Match(content, @"_class\s*=\s*""" + Regex.Escape(className) + @"""", RegexOptions.IgnoreCase);
            if (!match.Success) break;

            int classIdx = match.Index;

            int openBrace = -1;
            for (int i = classIdx - 1; i >= 0; i--)
            {
                if (content[i] == '{')
                {
                    openBrace = i;
                    break;
                }
                if (content[i] == '}')
                    break;
            }

            if (openBrace == -1) break;

            int depth = 0;
            int closeBrace = -1;
            for (int i = openBrace; i < content.Length; i++)
            {
                if (content[i] == '{') depth++;
                else if (content[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBrace = i;
                        break;
                    }
                }
            }

            if (closeBrace == -1) break;

            int endIdx = closeBrace + 1;
            while (endIdx < content.Length && (content[endIdx] == ' ' || content[endIdx] == '\t'))
                endIdx++;
            if (endIdx < content.Length && content[endIdx] == ',')
                endIdx++;
            while (endIdx < content.Length && (content[endIdx] == '\r' || content[endIdx] == '\n'))
                endIdx++;

            int startIdx = openBrace;
            while (startIdx > 0 && (content[startIdx - 1] == ' ' || content[startIdx - 1] == '\t'))
                startIdx--;

            content = content.Remove(startIdx, endIdx - startIdx);
        }

        return content;
    }

    public static async Task<(bool Success, string Message)> SanitizeVmdlForModelDocAsync(
        string vmdlPath,
        bool createBackup = true,
        bool disableAnimationList = true)
    {
        vmdlPath = Path.GetFullPath(vmdlPath);
        if (!File.Exists(vmdlPath))
            return (false, $"File not found: {vmdlPath}");

        var content = await File.ReadAllTextAsync(vmdlPath);

        if (createBackup)
        {
            CreateUniqueBackup(vmdlPath);
        }

        var changes = new List<string>();

        // 1. Remove NmSkeletonList block if present
        if (content.Contains("NmSkeletonList"))
        {
            content = RemoveModelDocNode(content, "NmSkeletonList");
            changes.Add("Stripped NmSkeletonList");
        }

        // 2. Remove AnimGraph2List block if present
        if (content.Contains("AnimGraph2List"))
        {
            content = RemoveModelDocNode(content, "AnimGraph2List");
            changes.Add("Stripped AnimGraph2List");
        }

        // 3. Remove standalone DefaultAnimGraph2 or AnimGraph2 if present outside list
        if (content.Contains("DefaultAnimGraph2") || content.Contains("AnimGraph2"))
        {
            content = RemoveModelDocNode(content, "DefaultAnimGraph2");
            content = RemoveModelDocNode(content, "AnimGraph2");
            changes.Add("Stripped standalone AnimGraph2 nodes");
        }

        // 4. Ensure AnimationList is disabled = true (without deleting animations) if requested
        if (disableAnimationList)
        {
            var disabledContent = DisableNodeByClass(content, "AnimationList");
            if (disabledContent != content)
            {
                content = disabledContent;
                changes.Add("Set disabled = true on AnimationList");
            }
        }

        var disabledAnimGraphs = DisableNodeByClass(DisableNodeByClass(content, "EmptyAnimGraph"), "AnimGraph");
        if (disabledAnimGraphs != content)
        {
            content = disabledAnimGraphs;
            changes.Add("Disabled anim graph nodes");
        }

        await File.WriteAllTextAsync(vmdlPath, content);

        var msg = changes.Count > 0
            ? $"ModelDoc Fix Applied: {string.Join(", ", changes)}"
            : "VMDL was already clean and ModelDoc compatible";

        return (true, msg);
    }
}
