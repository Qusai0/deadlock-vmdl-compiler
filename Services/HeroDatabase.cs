using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class HeroDatabase
{
    private static Dictionary<string, HeroPreset>? _database;

    private static string GetUserDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeadlockVmdlCompiler", "hero_paths.json");

    public static Dictionary<string, HeroPreset> GetDatabase()
    {
        if (_database != null)
            return _database;

        _database = LoadDatabase();
        return _database;
    }

    public static IReadOnlyDictionary<string, HeroPreset> GetVisiblePresets()
    {
        var builtIn = LoadBuiltInDatabase();
        if (builtIn.Count == 0)
            return GetDatabase();

        var current = GetDatabase();
        var visible = new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, defaultPreset) in builtIn)
            visible[key] = current.TryGetValue(key, out var updated) && updated != null
                ? updated : defaultPreset;

        return visible;
    }

    private static Dictionary<string, HeroPreset> LoadDatabase()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            GetUserDatabasePath(),
            Path.Combine(exeDir, "hero_paths.json"),
            Path.Combine(exeDir, "tools", "hero_paths.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "hero_paths.json")
        };

        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                try
                {
                    var json = File.ReadAllText(p);
                    var data = JsonSerializer.Deserialize<Dictionary<string, HeroPreset>>(json);
                    if (data != null)
                    {
                        var dict = new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in data)
                            dict[kv.Key] = kv.Value;
                        CorrectLegacyPresets(dict);
                        return IncludeMissingBuiltInPresets(dict);
                    }
                }
                catch { }
            }
        }

        return LoadBuiltInDatabase();
    }

    private static Dictionary<string, HeroPreset> IncludeMissingBuiltInPresets(
        Dictionary<string, HeroPreset> data)
    {
        // New releases must remain available for auto-detection when an older
        // local database is installed. Keep every user's existing override.
        foreach (var (key, preset) in LoadBuiltInDatabase())
            data.TryAdd(key, preset);
        return data;
    }

    private static Dictionary<string, HeroPreset> LoadBuiltInDatabase()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "DeadlockVmdlCompiler.hero_paths.json";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var data = JsonSerializer.Deserialize<Dictionary<string, HeroPreset>>(json);
                if (data != null)
                {
                    var dict = new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in data)
                        dict[kv.Key] = kv.Value;
                    CorrectLegacyPresets(dict);
                    return dict;
                }
            }
        }
        catch { }

        return new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);
    }

    public static string SaveDatabase(Dictionary<string, HeroPreset> data)
    {
        CorrectLegacyPresets(data);
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        var targetFile = GetUserDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        File.WriteAllText(targetFile, json);

        _database = IncludeMissingBuiltInPresets(
            new Dictionary<string, HeroPreset>(data, StringComparer.OrdinalIgnoreCase));
        return targetFile;
    }

    public static void ReloadDatabase()
    {
        _database = LoadDatabase();
    }

    private static void CorrectLegacyPresets(Dictionary<string, HeroPreset> data)
    {
        if (data.TryGetValue("seven", out var seven) &&
            data.TryGetValue("gigawatt_prisoner", out var gigawatt) &&
            string.Equals(seven.Skel, "models/heroes_wip/frank/frank.vnmskel", StringComparison.OrdinalIgnoreCase) &&
            seven.Graph?.EndsWith("+frank.vnmgraph", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(gigawatt.Skel, "models/heroes_staging/gigawatt_prisoner/gigawatt_prisoner.vnmskel", StringComparison.OrdinalIgnoreCase) &&
            gigawatt.Graph?.EndsWith("+gigawatt.vnmgraph", StringComparison.OrdinalIgnoreCase) == true &&
            gigawatt.UiGraph?.EndsWith("+gigawatt.vnmgraph", StringComparison.OrdinalIgnoreCase) == true)
        {
            data["seven"] = new HeroPreset
            {
                Skel = gigawatt.Skel,
                Graph = gigawatt.Graph,
                UiGraph = gigawatt.UiGraph
            };
        }

        if (data.TryGetValue("familiar", out var familiar) &&
            data.TryGetValue("familiar_wip", out var familiarWip) &&
            string.Equals(familiar.Skel, familiarWip.Skel, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(familiar.Graph, familiarWip.Graph, StringComparison.OrdinalIgnoreCase) &&
            familiarWip.UiGraph?.EndsWith("+familiar.vnmgraph", StringComparison.OrdinalIgnoreCase) == true &&
            familiar.UiGraph?.EndsWith("+frank.vnmgraph", StringComparison.OrdinalIgnoreCase) == true)
        {
            familiar.UiGraph = familiarWip.UiGraph;
        }
    }

    public static (bool Success, string Message, int Count) RestoreOriginalDatabase()
    {
        try
        {
            Dictionary<string, HeroPreset>? data = null;

            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "DeadlockVmdlCompiler.hero_paths.json";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                data = JsonSerializer.Deserialize<Dictionary<string, HeroPreset>>(json);
            }

            if (data == null)
            {
                var candidates = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hero_paths.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "hero_paths.json")
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c))
                    {
                        var json = File.ReadAllText(c);
                        data = JsonSerializer.Deserialize<Dictionary<string, HeroPreset>>(json);
                        if (data != null) break;
                    }
                }
            }

            if (data == null || data.Count == 0)
            {
                return (false, "Could not find valid hero preset database.", 0);
            }

            var dict = new Dictionary<string, HeroPreset>(data, StringComparer.OrdinalIgnoreCase);
            CorrectLegacyPresets(dict);
            _database = dict;
            SaveDatabase(dict);

            return (true, $"Restored {dict.Count} default hero presets.", dict.Count);
        }
        catch (Exception ex)
        {
            return (false, $"Error restoring presets: {ex.Message}", 0);
        }
    }
}
