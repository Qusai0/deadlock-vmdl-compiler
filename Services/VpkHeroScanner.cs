using System.Text;

namespace DeadlockVmdlCompiler.Services;

public class VpkEntry
{
    public string Extension { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public ushort PreloadBytes { get; set; }
    public ushort ArchiveIndex { get; set; }
    public uint EntryOffset { get; set; }
    public uint EntryLength { get; set; }
    public byte[]? PreloadData { get; set; }
    public long EmbeddedDataOffset { get; set; }
}

public static class VpkHeroScanner
{
    public static async Task<(bool Success, string Message, Dictionary<string, HeroPreset> Presets)> ScanVpkForHeroesAsync(
        string targetPath,
        IProgress<(int Current, int Total, string CurrentModel)>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return (false, "Target path is empty.", new());

        var resolvedPath = ResolveVpkPath(targetPath);

        var results = new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);

        return await Task.Run(() =>
        {
            try
            {
                if (File.Exists(resolvedPath) && resolvedPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                {
                    var entries = ReadVpkDirectory(resolvedPath);

                    var targetEntries = entries.Where(e =>
                        e.Extension.Equals("vmdl_c", StringComparison.OrdinalIgnoreCase) &&
                        (e.Directory.StartsWith("models/heroes", StringComparison.OrdinalIgnoreCase) ||
                         e.Directory.StartsWith("models/characters", StringComparison.OrdinalIgnoreCase))
                    ).ToList();

                    if (targetEntries.Count == 0)
                    {
                        return (false, $"No hero models found in: {Path.GetFileName(resolvedPath)}", results);
                    }

                    var vpkBaseDir = Path.GetDirectoryName(resolvedPath) ?? string.Empty;
                    var vpkBaseName = Path.GetFileNameWithoutExtension(resolvedPath);
                    if (vpkBaseName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
                    {
                        vpkBaseName = vpkBaseName[..^4];
                    }

                    int total = targetEntries.Count;
                    int current = 0;

                    foreach (var entry in targetEntries)
                    {
                        current++;
                        progress?.Report((current, total, entry.FileName));

                        try
                        {
                            var byteData = ExtractVpkEntryBytes(entry, resolvedPath, vpkBaseDir, vpkBaseName);
                            if (byteData == null || byteData.Length == 0) continue;

                            using var res = new Resource();
                            using var ms = new MemoryStream(byteData);
                            res.Read(ms);

                            var (hasAg2, preset, heroKey) = ExtractAg2FromResource(res, entry.Directory, entry.FileName);
                            if (hasAg2 && preset != null && !string.IsNullOrEmpty(heroKey))
                            {
                                results[heroKey] = preset;
                            }
                        }
                        catch { }
                    }
                }
                else if (Directory.Exists(resolvedPath))
                {
                    var allVmdlcFiles = Directory.GetFiles(resolvedPath, "*.vmdl_c", SearchOption.AllDirectories)
                        .Where(f => f.Replace('\\', '/').Contains("/heroes", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    int total = allVmdlcFiles.Count;
                    int current = 0;

                    foreach (var filePath in allVmdlcFiles)
                    {
                        current++;
                        var fn = Path.GetFileNameWithoutExtension(filePath);
                        progress?.Report((current, total, fn));

                        try
                        {
                            using var res = new Resource();
                            res.Read(filePath);

                            var relPath = Path.GetRelativePath(resolvedPath, filePath).Replace('\\', '/');
                            var dirName = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? string.Empty;
                            var fileName = Path.GetFileNameWithoutExtension(filePath);
                            if (fileName.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
                                fileName = Path.GetFileNameWithoutExtension(fileName);

                            var (hasAg2, preset, heroKey) = ExtractAg2FromResource(res, dirName, fileName);
                            if (hasAg2 && preset != null && !string.IsNullOrEmpty(heroKey))
                            {
                                results[heroKey] = preset;
                            }
                        }
                        catch { }
                    }
                }
                else
                {
                    return (false, $"File or folder not found: {resolvedPath}", results);
                }

                if (results.Count == 0)
                {
                    return (false, "Scan completed, but no models with AnimGraph2 nodes were found in the selected VPK.", results);
                }

                var merged = new Dictionary<string, HeroPreset>(HeroDatabase.GetDatabase(), StringComparer.OrdinalIgnoreCase);
                foreach (var kv in results)
                {
                    merged[kv.Key] = kv.Value;
                }

                HeroDatabase.SaveDatabase(merged);

                return (true, $"Updated {merged.Count} hero preset(s).", merged);
            }
            catch (Exception ex)
            {
                return (false, $"Error scanning VPK: {ex.Message}", results);
            }
        });
    }

    public static async Task<(bool Success, string Message, List<NpcPreset> Presets)> ScanVpkForNpcsAsync(
        string targetPath,
        IProgress<(int Current, int Total, string CurrentModel)>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return (false, "Target path is empty.", new());

        var resolvedPath = ResolveVpkPath(targetPath);
        return await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(resolvedPath) ||
                    !resolvedPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                    return (false, $"File or folder not found: {resolvedPath}", new List<NpcPreset>());

                var entries = ReadVpkDirectory(resolvedPath)
                    .Where(entry => entry.Extension.Equals("vmdl_c", StringComparison.OrdinalIgnoreCase) &&
                    entry.Directory.StartsWith("models/npc/", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (entries.Count == 0)
                    return (false, "No NPC models were found under models/npc in the selected VPK.", new List<NpcPreset>());

                var vpkBaseDir = Path.GetDirectoryName(resolvedPath) ?? string.Empty;
                var vpkBaseName = Path.GetFileNameWithoutExtension(resolvedPath);
                if (vpkBaseName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
                    vpkBaseName = vpkBaseName[..^4];

                var results = new Dictionary<string, NpcPreset>(StringComparer.OrdinalIgnoreCase);
                var total = entries.Count;
                var current = 0;
                foreach (var entry in entries)
                {
                    current++;
                    var modelPath = $"{entry.Directory}/{entry.FileName}.{entry.Extension}";
                    progress?.Report((current, total, modelPath));

                    try
                    {
                        var bytes = ExtractVpkEntryBytes(entry, resolvedPath, vpkBaseDir, vpkBaseName);
                        if (bytes == null || bytes.Length == 0) continue;

                        using var resource = new Resource();
                        using var stream = new MemoryStream(bytes);
                        resource.Read(stream);

                        var (hasAg2, preset, _) = ExtractAg2FromResource(
                            resource, entry.Directory, entry.FileName);
                        if (!hasAg2 || preset == null) continue;

                        var display = modelPath.StartsWith("models/npc/", StringComparison.OrdinalIgnoreCase)
                            ? modelPath["models/npc/".Length..]
                            : modelPath;
                        var key = modelPath[..^("_c".Length)];
                        results[modelPath] = new NpcPreset(
                            key,
                            display,
                            modelPath,
                            preset,
                            preset.NamedGraphs.ContainsKey("Neutrals"));
                    }
                    catch { }
                }

                var list = results.Values
                    .OrderBy(preset => preset.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return list.Count == 0
                    ? (false, "NPC scan completed, but no models with AnimGraph2 nodes were found.", list)
                    : (true, $"Found {list.Count} NPC preset(s) under models/npc.", list);
            }
            catch (Exception ex)
            {
                return (false, $"Error scanning NPC presets: {ex.Message}", new List<NpcPreset>());
            }
        });
    }

    private static string ResolveVpkPath(string targetPath)
    {
        var resolvedPath = Path.GetFullPath(targetPath);
        if (!Directory.Exists(resolvedPath)) return resolvedPath;

        var candidates = new[]
        {
            Path.Combine(resolvedPath, "pak01_dir.vpk"),
            Path.Combine(resolvedPath, "game", "citadel", "pak01_dir.vpk"),
            Path.Combine(resolvedPath, "citadel", "pak01_dir.vpk")
        };
        return candidates.FirstOrDefault(File.Exists) ?? resolvedPath;
    }

    private static (bool HasAg2, HeroPreset? Preset, string HeroKey) ExtractAg2FromResource(
        Resource resource,
        string dirName,
        string fileName)
    {
        try
        {
            if (resource.DataBlock == null)
                return (false, null, string.Empty);

            var text = resource.DataBlock.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return (false, null, string.Empty);

            if (!text.Contains("m_animGraph2Refs", StringComparison.OrdinalIgnoreCase) ||
                !text.Contains("m_vecNmSkeletonRefs", StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, string.Empty);
            }

            string skel = string.Empty;
            string graph = string.Empty;
            string uiGraph = string.Empty;
            var namedGraphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var skelMatch = Regex.Match(text, @"m_vecNmSkeletonRefs\s*=\s*\[[\s\S]*?(?:resource:)?\s*""([^""]+\.vnmskel)""", RegexOptions.IgnoreCase);
            if (skelMatch.Success)
            {
                skel = skelMatch.Groups[1].Value.Trim();
            }

            var ag2SectionMatch = Regex.Match(text, @"m_animGraph2Refs\s*=\s*\[([\s\S]*?)\]\s*(?:\r?\n|$)", RegexOptions.IgnoreCase);
            if (ag2SectionMatch.Success)
            {
                var ag2Text = ag2SectionMatch.Groups[1].Value;
                var itemMatches = Regex.Matches(ag2Text, @"\{[\s\S]*?\}");
                foreach (Match item in itemMatches)
                {
                    var itemStr = item.Value;
                    var idMatch = Regex.Match(itemStr, @"m_sIdentifier\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
                    var graphMatch = Regex.Match(itemStr, @"m_hGraph\s*=\s*(?:resource:)?\s*""([^""]+\.vnmgraph)""", RegexOptions.IgnoreCase);

                    if (graphMatch.Success)
                    {
                        var id = idMatch.Success ? idMatch.Groups[1].Value.Trim() : string.Empty;
                        var gPath = graphMatch.Groups[1].Value.Trim();

                        if (string.IsNullOrEmpty(id) || id.Equals("default", StringComparison.OrdinalIgnoreCase))
                        {
                            graph = gPath;
                        }
                        else if (id.Equals("ui", StringComparison.OrdinalIgnoreCase))
                        {
                            uiGraph = gPath;
                        }
                        else
                        {
                            namedGraphs[id] = gPath;
                        }
                    }
                }
            }

            if (skel.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                skel = skel["resource:".Length..].Trim().Trim('"');
            if (graph.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                graph = graph["resource:".Length..].Trim().Trim('"');
            if (uiGraph.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                uiGraph = uiGraph["resource:".Length..].Trim().Trim('"');

            if (string.IsNullOrWhiteSpace(skel) ||
                (string.IsNullOrWhiteSpace(graph) && namedGraphs.Count == 0))
                return (false, null, string.Empty);

            var heroKey = fileName.ToLowerInvariant();
            if (heroKey.EndsWith(".vmdl")) heroKey = heroKey[..^5];
            if (heroKey.EndsWith("_body")) heroKey = heroKey[..^5];
            if (heroKey.EndsWith("_model")) heroKey = heroKey[..^6];
            if (heroKey.EndsWith("_base")) heroKey = heroKey[..^5];

            var preset = new HeroPreset
            {
                Skel = skel,
                Graph = graph,
                UiGraph = uiGraph,
                NamedGraphs = namedGraphs
            };

            return (true, preset, heroKey);
        }
        catch
        {
            return (false, null, string.Empty);
        }
    }

    public static List<VpkEntry> ReadVpkDirectory(string vpkPath)
    {
        var entries = new List<VpkEntry>();
        using var fs = new FileStream(vpkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        uint signature = reader.ReadUInt32();
        if (signature != 0x55aa1234)
            throw new InvalidDataException($"Invalid VPK signature: 0x{signature:X8}");

        uint version = reader.ReadUInt32();
        uint treeSize = reader.ReadUInt32();

        long headerSize = version switch
        {
            1 => 12,
            2 => 28,
            _ => throw new InvalidDataException($"Unsupported VPK version: {version}")
        };

        if (fs.Length < headerSize || treeSize > fs.Length - headerSize)
            throw new InvalidDataException("VPK tree extends beyond the directory file.");

        if (version == 2)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
        }

        long treeEnd = checked(headerSize + treeSize);

        while (true)
        {
            string ext = ReadNullTerminatedString(reader, treeEnd);
            if (string.IsNullOrEmpty(ext)) break;

            while (true)
            {
                string dir = ReadNullTerminatedString(reader, treeEnd);
                if (string.IsNullOrEmpty(dir)) break;

                while (true)
                {
                    string filename = ReadNullTerminatedString(reader, treeEnd);
                    if (string.IsNullOrEmpty(filename)) break;

                    EnsureTreeBytesAvailable(reader, treeEnd, sizeof(uint) + sizeof(ushort) * 2 + sizeof(uint) * 2 + sizeof(ushort));

                    reader.ReadUInt32(); // CRC32, not checked here
                    var entry = new VpkEntry
                    {
                        Extension = ext,
                        Directory = dir == " " ? string.Empty : dir,
                        FileName = filename,
                        PreloadBytes = reader.ReadUInt16(),
                        ArchiveIndex = reader.ReadUInt16(),
                        EntryOffset = reader.ReadUInt32(),
                        EntryLength = reader.ReadUInt32(),
                        EmbeddedDataOffset = treeEnd
                    };

                    ushort terminator = reader.ReadUInt16();
                    if (terminator != 0xffff)
                        throw new InvalidDataException("Invalid VPK entry terminator.");

                    if (entry.PreloadBytes > 0)
                    {
                        EnsureTreeBytesAvailable(reader, treeEnd, entry.PreloadBytes);
                        entry.PreloadData = reader.ReadBytes(entry.PreloadBytes);
                    }

                    entries.Add(entry);
                }
            }
        }

        if (reader.BaseStream.Position != treeEnd)
            throw new InvalidDataException("VPK tree size does not match its parsed contents.");

        return entries;
    }

    public static byte[]? ExtractVpkEntryBytes(VpkEntry entry, string dirVpkPath, string vpkBaseDir, string vpkBaseName)
    {
        if (entry.EntryLength == 0 && entry.PreloadBytes > 0)
            return entry.PreloadData;

        ulong totalLength = (ulong)entry.PreloadBytes + entry.EntryLength;
        if (totalLength > int.MaxValue)
            throw new InvalidDataException("VPK entry is too large to extract.");

        int totalLen = (int)totalLength;
        var buffer = new byte[totalLen];

        if (entry.PreloadBytes > 0 && entry.PreloadData != null)
        {
            Buffer.BlockCopy(entry.PreloadData, 0, buffer, 0, entry.PreloadBytes);
        }

        if (entry.EntryLength > 0)
        {
            string archivePath;
            if (entry.ArchiveIndex == 0x7fff)
            {
                archivePath = dirVpkPath;
            }
            else
            {
                archivePath = Path.Combine(vpkBaseDir, $"{vpkBaseName}_{entry.ArchiveIndex:D3}.vpk");
            }

            if (!File.Exists(archivePath)) return null;

            using var afs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long dataOffset = entry.ArchiveIndex == 0x7fff
                ? checked(entry.EmbeddedDataOffset + entry.EntryOffset)
                : entry.EntryOffset;
            if (dataOffset < 0 || dataOffset > afs.Length || entry.EntryLength > afs.Length - dataOffset)
                throw new InvalidDataException("VPK entry data extends beyond its archive file.");

            afs.Seek(dataOffset, SeekOrigin.Begin);
            afs.ReadExactly(buffer, entry.PreloadBytes, (int)entry.EntryLength);
        }

        return buffer;
    }

    public static byte[]? ExtractFileFromVpk(string vpkPath, string internalPath)
    {
        try
        {
            if (!File.Exists(vpkPath)) return null;
            var entries = ReadVpkDirectory(vpkPath);

            var cleanTarget = internalPath.Replace('\\', '/').TrimStart('/');
            var targetDir = Path.GetDirectoryName(cleanTarget)?.Replace('\\', '/') ?? string.Empty;
            var targetExt = Path.GetExtension(cleanTarget).TrimStart('.');
            var targetName = Path.GetFileNameWithoutExtension(cleanTarget);

            var match = entries.FirstOrDefault(e =>
                e.Extension.Equals(targetExt, StringComparison.OrdinalIgnoreCase) &&
                e.FileName.Equals(targetName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(targetDir) || e.Directory.Equals(targetDir, StringComparison.OrdinalIgnoreCase)));

            if (match != null)
            {
                var vpkBaseDir = Path.GetDirectoryName(vpkPath) ?? string.Empty;
                var vpkBaseName = Path.GetFileNameWithoutExtension(vpkPath);
                if (vpkBaseName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
                {
                    vpkBaseName = vpkBaseName[..^4];
                }

                return ExtractVpkEntryBytes(match, vpkPath, vpkBaseDir, vpkBaseName);
            }
        }
        catch { }

        return null;
    }

    private static string ReadNullTerminatedString(BinaryReader reader, long limit)
    {
        var bytes = new List<byte>();
        while (true)
        {
            if (reader.BaseStream.Position >= limit)
                throw new InvalidDataException("VPK tree contains an unterminated string.");
            byte b = reader.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        // VpkBuilder writes UTF-8 names; ASCII game archives read the same either way.
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void EnsureTreeBytesAvailable(BinaryReader reader, long treeEnd, long byteCount)
    {
        if (byteCount < 0 || reader.BaseStream.Position > treeEnd - byteCount)
            throw new InvalidDataException("VPK entry extends beyond the declared tree size.");
    }
}
