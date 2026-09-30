using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DeadlockVmdlCompiler.Models;
using DeadlockVmdlCompiler.Services;
using DeadlockVmdlCompiler.Views;

namespace DeadlockVmdlCompiler;

public partial class MainWindow : Window
{
    private AppConfig _config = new();
    private List<DiscoveredAddon> _discoveredAddons = new();
    private bool _isProcessing;
    private bool _isInitializing = true;
    private bool _isUpdatingSelection = false;
    private List<HeroPresetChoice> _presetChoices = new();
    private Dictionary<string, Bitmap> _presetPortraits = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _portraitLoadGate = new(1, 1);
    private string? _portraitSource;
    private int _logLineCount = 0;
    private readonly System.Text.StringBuilder _logBuffer = new();
    private sealed record ProtectedModelState(
        CompiledModelProtection Guard, string? Skel, string? Graph, string? UiGraph);

    private readonly Dictionary<string, ProtectedModelState> _protectedModels =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly IBrush BrushOk = new SolidColorBrush(Color.FromRgb(0xDF, 0xE2, 0xE6));
    private static readonly IBrush BrushWarn = new SolidColorBrush(Color.FromRgb(0xBA, 0xBE, 0xC4));
    private static readonly IBrush BrushErr = new SolidColorBrush(Color.FromRgb(0xDF, 0x70, 0x70));
    private static readonly IBrush BrushMuted = new SolidColorBrush(Color.FromRgb(0x82, 0x86, 0x8E));

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            ReleaseAllModelProtections();
            foreach (var portrait in _presetPortraits.Values)
                portrait.Dispose();
        };
    }

    private void PopulateHeroPresets()
    {
        var choices = HeroDatabase.GetVisiblePresets()
            .Select(pair => new HeroPresetChoice(pair.Key,
                HeroPresetMatcher.FindKnownHero(pair.Key, pair.Value)))
            .OrderBy(choice => choice.Hero == null ? 1 : 0)
            .ThenBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        choices.Insert(0, new HeroPresetChoice(string.Empty, isAuto: true));
        foreach (var choice in choices)
        {
            if (choice.Hero != null && _presetPortraits.TryGetValue(choice.Hero.HeroKey, out var portrait))
                choice.Portrait = portrait;
        }
        _presetChoices = choices;
        CmbHeroPreset.ItemsSource = choices;
        CmbHeroPreset.SelectedIndex = 0;
    }

    private async Task LoadPresetPortraitsAsync(string vpkPath)
    {
        if (!File.Exists(vpkPath)) return;
        await _portraitLoadGate.WaitAsync();
        try
        {
            if (_portraitSource?.Equals(vpkPath, StringComparison.OrdinalIgnoreCase) == true &&
                _presetPortraits.Count == DeadlockHeroCatalog.GetHeroes().Count(hero => hero.IconVpkPath != null))
                return;

            var pngs = await Task.Run(() => HeroIconLoader.LoadSmallPortraits(
                vpkPath, DeadlockHeroCatalog.GetHeroes()));
            if (!IsVisible) return;

            var portraits = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            foreach (var (heroKey, png) in pngs)
            {
                try
                {
                    using var input = new MemoryStream(png);
                    portraits[heroKey] = new Bitmap(input);
                }
                catch (Exception ex)
                {
                    Log($"[ag2 presets] could not display portrait for {heroKey}: {ex.Message}");
                }
            }
            foreach (var choice in _presetChoices)
                choice.Portrait = choice.Hero != null &&
                                  portraits.TryGetValue(choice.Hero.HeroKey, out var portrait)
                    ? portrait : null;
            foreach (var oldPortrait in _presetPortraits.Values)
                oldPortrait.Dispose();
            _presetPortraits = portraits;
            _portraitSource = vpkPath;
            if (portraits.Count != DeadlockHeroCatalog.GetHeroes().Count)
                Log($"[ag2 presets] loaded {portraits.Count} of 38 hero portraits from VPK.");
        }
        catch (Exception ex)
        {
            Log($"[ag2 presets] hero portraits unavailable: {ex.Message}");
        }
        finally
        {
            _portraitLoadGate.Release();
        }
    }

    private void UpdateProtectionStatus()
    {
        TxtMakeVpkBtn.Text = _protectedModels.Count == 0
            ? "make vpk..."
            : $"make vpk... ({_protectedModels.Count} protected)";
    }

    private void ReleaseProtectionForOutput(string deployedPath)
    {
        foreach (var sourcePath in _protectedModels
                     .Where(pair => string.Equals(pair.Value.Guard.ModelPath, deployedPath, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key).ToList())
        {
            _protectedModels[sourcePath].Guard.Dispose();
            _protectedModels.Remove(sourcePath);
            Log($"[protect] released: {Path.GetFileName(deployedPath)}");
        }
        UpdateProtectionStatus();
    }

    private void ReleaseProtectionForSource(string sourcePath)
    {
        if (_protectedModels.Remove(sourcePath, out var protection))
        {
            protection.Guard.Dispose();
            Log($"[protect] released: {Path.GetFileName(protection.Guard.ModelPath)}");
            UpdateProtectionStatus();
        }
    }

    private void ReleaseProtectionsForAddon(string gameAddonDir)
    {
        var fullAddonDir = Path.GetFullPath(gameAddonDir);
        foreach (var pair in _protectedModels.ToList())
        {
            if (IsModelInAddon(pair.Value.Guard.ModelPath, fullAddonDir))
                ReleaseProtectionForSource(pair.Key);
        }
    }

    private void ReleaseAllModelProtections()
    {
        foreach (var protection in _protectedModels.Values)
            protection.Guard.Dispose();
        _protectedModels.Clear();
    }

    private async Task OfferProtectionReleaseAfterFailedPackAsync(string targetPath, string? citadelDir)
    {
        var (_, addonName, _) = VmdlPipeline.ParseCsdkPath(targetPath, citadelDir);
        var gameAddonDir = VmdlPipeline.ResolveGameAddonDir(targetPath, citadelDir, addonName);
        if (!_protectedModels.Values.Any(protection =>
                IsModelInAddon(protection.Guard.ModelPath, gameAddonDir)))
            return;

        var keepProtected = await DialogService.ShowConfirmAsync(this,
            "VPK was not created",
            "Keep the compiled model protected while you retry packaging?\n\nChoose No to release the protection.");
        if (!keepProtected)
            ReleaseProtectionsForAddon(gameAddonDir);
    }

    private static bool IsModelInAddon(string modelPath, string gameAddonDir)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(gameAddonDir), modelPath);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private string? VerifyModelsInVpk(string vpkPath, string gameAddonDir, string selectedCompiledPath)
    {
        var modelPaths = _protectedModels.Values
            .Select(state => state.Guard.ModelPath)
            .Where(path => IsModelInAddon(path, gameAddonDir))
            .Append(selectedCompiledPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var modelPath in modelPaths)
        {
            var relativePath = Path.GetRelativePath(gameAddonDir, modelPath).Replace('\\', '/');
            var packedBytes = VpkHeroScanner.ExtractFileFromVpk(vpkPath, relativePath);
            if (packedBytes is null)
                return $"The VPK does not contain the compiled model: {relativePath}";

            using var deployed = File.OpenRead(modelPath);
            if (packedBytes.LongLength != deployed.Length ||
                !SHA256.HashData(packedBytes).AsSpan().SequenceEqual(SHA256.HashData(deployed)))
                return $"The VPK contains a different version of the compiled model: {relativePath}";
        }

        return null;
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            _isInitializing = true;
            _isUpdatingSelection = true;
            DmxModelLoader.DebugLogger = Log;

            _config = ConfigManager.LoadConfig();

            // Populate presets
            PopulateHeroPresets();

            // Apply config to UI
            TxtCsWinPath.Text = _config.CsWinDir ?? string.Empty;
            TxtCitadelPath.Text = _config.CitadelAddonsDir ?? string.Empty;

            ChkRevert.IsChecked = _config.ChkRevert;
            ChkSkel.IsChecked = _config.ChkSkel;
            ChkGraph.IsChecked = _config.ChkGraph;
            ChkUiGraph.IsChecked = _config.ChkUiGraph;
            ChkDisableAnimList.IsChecked = _config.ChkDisableAnimList;

            ChkRevert.IsCheckedChanged += (_, _) => SaveConfig();
            ChkSkel.IsCheckedChanged += (_, _) => SaveConfig();
            ChkGraph.IsCheckedChanged += (_, _) => SaveConfig();
            ChkUiGraph.IsCheckedChanged += (_, _) => SaveConfig();
            ChkDisableAnimList.IsCheckedChanged += (_, _) => SaveConfig();

            // Environment validation on startup
            ValidateEnvironmentOnStartup();

            var deadlockInstall = DeadlockLocator.DetectDeadlockInstallation();
            if (deadlockInstall.IsValid)
                _ = LoadPresetPortraitsAsync(deadlockInstall.Pak01VpkPath);

            RescanModels(logOutput: false);

            var resolved = GetResolvedTargetPath();
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                UpdateHeroDetailsFromPath(resolved);
                _ = Init3DSceneAsync(resolved);
            }

            Log("environment verified. ready.");

            // First-time setup wizard
            var csValid = VmdlPipeline.IsValidCsWinDir(TxtCsWinPath.Text?.Trim());
            var citValid = !string.IsNullOrWhiteSpace(TxtCitadelPath.Text?.Trim()) && Directory.Exists(TxtCitadelPath.Text?.Trim());

            if (!csValid || !citValid)
            {
                await PromptFirstTimeSetupAsync(!csValid, !citValid);
            }
        }
        catch (Exception ex)
        {
            Log($"[init error] {ex.Message}");
        }
        finally
        {
            _isUpdatingSelection = false;
            _isInitializing = false;
        }
    }

    private void BtnToggleAdvanced_Click(object? sender, RoutedEventArgs e)
    {
        PanelAdvancedContent.IsVisible = !PanelAdvancedContent.IsVisible;
        IconAdvancedChevron.Data = PanelAdvancedContent.IsVisible
            ? Geometry.Parse("M7.41 15.41L12 10.83L16.59 15.41L18 14L12 8L6 14L7.41 15.41Z")
            : Geometry.Parse("M7.41 8.59L12 13.17L16.59 8.59L18 10L12 16L6 10L7.41 8.59Z");
    }

    private async Task PromptFirstTimeSetupAsync(bool needCsWin, bool needCitadel)
    {
        await DialogService.ShowInfoAsync(
            this,
            "initial setup",
            "welcome! please configure your cswin64 compiler and citadel addons directory to get started."
        );

        if (needCsWin)
        {
            var csFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "select cswin64 directory (containing resourcecompiler.exe)"
            });

            if (csFolders.Count > 0)
            {
                TxtCsWinPath.Text = csFolders[0].Path.LocalPath;
                Log($"[setup] cswin64 path configured: {csFolders[0].Path.LocalPath}");
                SaveConfig();
            }
        }

        if (needCitadel)
        {
            var citFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "select citadel addons directory (content/citadel_addons)"
            });

            if (citFolders.Count > 0)
            {
                TxtCitadelPath.Text = citFolders[0].Path.LocalPath;
                Log($"[setup] citadel addons path configured: {citFolders[0].Path.LocalPath}");
                SaveConfig();
                RescanModels();
            }
        }

        CheckEnvironmentStatus();
    }

    private void BtnClearLog_Click(object? sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        _logLineCount = 0;
        TxtLog.Text = string.Empty;
    }

    private async void BtnCopyLog_Click(object? sender, RoutedEventArgs e)
    {
        var text = TxtLog.Text;
        if (!string.IsNullOrEmpty(text))
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(text);
                Log("[log] console text copied to clipboard");
            }
        }
    }

    private void Log(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var line = $"[{timestamp}] {message}";
            _logBuffer.AppendLine(line);
            _logLineCount++;

            // Truncate buffer if exceedingly large (keep latest ~150k chars) to preserve UI responsiveness
            if (_logBuffer.Length > 200_000)
            {
                var content = _logBuffer.ToString();
                var splitIdx = content.IndexOf('\n', 50_000);
                if (splitIdx > 0)
                {
                    _logBuffer.Clear();
                    _logBuffer.Append(content.Substring(splitIdx + 1));
                }
            }

            TxtLog.Text = _logBuffer.ToString();

            if (ChkAutoScroll?.IsChecked == true)
            {
                ScrollLog?.ScrollToEnd();
            }
        });
    }

    // -----------------------------------------------------------------
    // 3D VIEWPORT LOGIC
    // -----------------------------------------------------------------
    private async Task Init3DSceneAsync(string? targetPath = null)
    {
        try
        {
            var path = targetPath ?? GetResolvedTargetPath();
            var citadelDir = TxtCitadelPath.Text?.Trim();

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                LblMeshName.Text = "mesh: loading preview...";
                var mesh = await DmxModelLoader.LoadModelFromVmdlAsync(path, citadelDir);
                ModelViewport.CurrentMesh = mesh;

                if (mesh != null && mesh.Vertices.Count > 0)
                {
                    LblMeshName.Text = $"mesh: {mesh.MeshName} ({mesh.Vertices.Count:N0} verts, {mesh.Indices.Count / 3:N0} tris)";
                }
                else
                {
                    LblMeshName.Text = $"mesh: {Path.GetFileName(path)} (render mesh not found)";
                }
            }
            else
            {
                ModelViewport.CurrentMesh = null;
                LblMeshName.Text = "mesh: no model selected";
            }
        }
        catch { }
    }

    // -----------------------------------------------------------------
    // ENVIRONMENT & MODEL DETECTION LOGIC
    // -----------------------------------------------------------------
    private void CheckEnvironmentStatus()
    {
        try
        {
            var csWinDir = TxtCsWinPath.Text?.Trim() ?? string.Empty;
            var isValidCsWin = VmdlPipeline.IsValidCsWinDir(csWinDir);

            if (isValidCsWin)
            {
                LblCsWinStatus.Text = "ready (compiler found)";
                LblCsWinStatus.Foreground = BrushOk;
                if (BorderCsWinCheck != null)
                {
                    BorderCsWinCheck.IsVisible = true;
                    BorderCsWinCheck.Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x3E, 0x2B));
                    BorderCsWinCheck.BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                    IconCsWinStatus.Data = Geometry.Parse("M9 16.2L4.8 12L3.4 13.4L9 19L21 7L19.6 5.6L9 16.2Z");
                    IconCsWinStatus.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                }
            }
            else
            {
                LblCsWinStatus.Text = "compiler missing";
                LblCsWinStatus.Foreground = BrushErr;
                if (BorderCsWinCheck != null)
                {
                    BorderCsWinCheck.IsVisible = true;
                    BorderCsWinCheck.Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x1A, 0x1A));
                    BorderCsWinCheck.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    IconCsWinStatus.Data = Geometry.Parse("M19 6.41L17.59 5L12 10.59L6.41 5L5 6.41L10.59 12L5 17.59L6.41 19L12 13.41L17.59 19L19 17.59L13.41 12L19 6.41Z");
                    IconCsWinStatus.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                }
            }

            var citadelDir = TxtCitadelPath.Text?.Trim() ?? string.Empty;
            var isValidCitadel = !string.IsNullOrWhiteSpace(citadelDir) && Directory.Exists(citadelDir);

            if (isValidCitadel)
            {
                LblCitadelStatus.Text = "connected to addons folder";
                LblCitadelStatus.Foreground = BrushOk;
            }
            else
            {
                LblCitadelStatus.Text = "addons folder not found";
                LblCitadelStatus.Foreground = BrushErr;
            }
        }
        catch { }
    }

    private void ValidateEnvironmentOnStartup()
    {
        var citadelDir = TxtCitadelPath.Text?.Trim();
        var csWinDir = TxtCsWinPath.Text?.Trim();

        bool citadelValid = !string.IsNullOrWhiteSpace(citadelDir) && Directory.Exists(citadelDir);
        bool csWinValid = VmdlPipeline.IsValidCsWinDir(csWinDir);

        if (!citadelValid || !csWinValid)
        {
            var info = DeadlockLocator.DetectDeadlockInstallation();
            if (info.IsValid && !citadelValid)
            {
                var candAddons = Path.Combine(info.GameRootPath, "content", "citadel_addons");
                if (Directory.Exists(candAddons))
                {
                    TxtCitadelPath.Text = candAddons;
                    Log($"[auto-detect] citadel addons directory: {candAddons}");
                }
            }
        }

        CheckEnvironmentStatus();
    }

    private void RescanModels(bool logOutput = true)
    {
        try
        {
            var citadelDir = TxtCitadelPath.Text?.Trim();
            if (string.IsNullOrWhiteSpace(citadelDir) || !Directory.Exists(citadelDir))
            {
                LblDiscoveredCount.Text = "addons folder not configured";
                LblDiscoveredCount.Foreground = BrushWarn;
                CmbDiscovered.ItemsSource = null;
                CmbTargetVmdl.ItemsSource = null;
                return;
            }

            var previousAddonName = (CmbDiscovered.SelectedItem as DiscoveredAddon)?.Name;

            _discoveredAddons = VmdlScanner.ScanAddons(citadelDir);

            var displayList = new List<DiscoveredAddon>();
            var placeholder = new DiscoveredAddon
            {
                Name = $"(select addon: {_discoveredAddons.Count} available)",
                FullPath = string.Empty,
                HeroModels = new List<DiscoveredModel>(),
                IsPlaceholder = true,
                Display = $"(select addon: {_discoveredAddons.Count} available)"
            };
            displayList.Add(placeholder);
            displayList.AddRange(_discoveredAddons);

            CmbDiscovered.ItemsSource = displayList;

            int totalModels = _discoveredAddons.Sum(a => a.HeroModels.Count);
            LblDiscoveredCount.Text = $"{_discoveredAddons.Count} addon(s) available";
            LblDiscoveredCount.Foreground = BrushMuted;

            if (logOutput)
            {
                Log($"discovered {_discoveredAddons.Count} addon(s) in: {citadelDir}");
            }

            // Restore selection
            if (!string.IsNullOrEmpty(previousAddonName))
            {
                var match = _discoveredAddons.FirstOrDefault(a => a.Name.Equals(previousAddonName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    CmbDiscovered.SelectedItem = match;
                    return;
                }
            }

            CmbDiscovered.SelectedIndex = _discoveredAddons.Count > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Log($"[scan error] {ex.Message}");
        }
    }

    private string? GetResolvedTargetPath()
    {
        if (CmbTargetVmdl.SelectedItem is DiscoveredModel selectedModel)
        {
            return selectedModel.FullPath;
        }

        return null;
    }

    private void UpdateHeroDetailsFromPath(string vmdlPath)
    {
        try
        {
            var heroName = VmdlPipeline.DetectHeroFromPath(vmdlPath);
            var db = HeroDatabase.GetDatabase();
            if (string.IsNullOrEmpty(heroName) || !db.TryGetValue(heroName, out var preset))
            {
                CmbHeroPreset.SelectedIndex = 0;

                TxtSkel.Text = string.Empty;
                TxtGraph.Text = string.Empty;
                TxtUiGraph.Text = string.Empty;
                return;
            }

            TxtSkel.Text = preset.Skel ?? string.Empty;
            TxtGraph.Text = preset.Graph ?? string.Empty;
            TxtUiGraph.Text = preset.UiGraph ?? string.Empty;

            var match = _presetChoices.FirstOrDefault(choice =>
                choice.Key.Equals(heroName, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                var hero = HeroPresetMatcher.FindKnownHero(heroName, preset);
                if (hero != null)
                    match = _presetChoices.FirstOrDefault(choice =>
                        choice.Hero?.HeroKey.Equals(hero.HeroKey, StringComparison.OrdinalIgnoreCase) == true);
            }
            if (match != null && !ReferenceEquals(CmbHeroPreset.SelectedItem, match))
                CmbHeroPreset.SelectedItem = match;
        }
        catch { }
    }

    private void SaveConfig()
    {
        if (_isInitializing) return;

        _config.CsWinDir = TxtCsWinPath.Text?.Trim() ?? string.Empty;
        _config.CitadelAddonsDir = TxtCitadelPath.Text?.Trim() ?? string.Empty;
        _config.LastTargetPath = GetResolvedTargetPath() ?? string.Empty;
        _config.ChkRevert = ChkRevert.IsChecked == true;
        _config.ChkSkel = ChkSkel.IsChecked == true;
        _config.ChkGraph = ChkGraph.IsChecked == true;
        _config.ChkUiGraph = ChkUiGraph.IsChecked == true;
        _config.ChkDisableAnimList = ChkDisableAnimList.IsChecked == true;

        if (!ConfigManager.SaveConfig(_config))
        {
            Log("[config error] configuration could not be saved.");
        }
    }

    // -----------------------------------------------------------------
    // EVENT HANDLERS
    // -----------------------------------------------------------------
    private void TxtCsWinPath_TextChanged(object? sender, TextChangedEventArgs e)
    {
        CheckEnvironmentStatus();
        SaveConfig();
    }

    private void TxtCitadelPath_TextChanged(object? sender, TextChangedEventArgs e)
    {
        CheckEnvironmentStatus();
        SaveConfig();
        if (!_isInitializing) RescanModels(logOutput: false);
    }

    private void CmbDiscovered_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;

        try
        {
            _isUpdatingSelection = true;
            if (CmbDiscovered.SelectedItem is DiscoveredAddon addon && addon.HeroModels.Count > 0)
            {
                CmbTargetVmdl.ItemsSource = addon.HeroModels;
                CmbTargetVmdl.SelectedIndex = 0;
            }
            else
            {
                CmbTargetVmdl.ItemsSource = null;
            }
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        var path = GetResolvedTargetPath();
        if (!string.IsNullOrEmpty(path))
        {
            UpdateHeroDetailsFromPath(path);
            _ = Init3DSceneAsync(path);
        }
    }

    private void CmbTargetVmdl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;

        var path = GetResolvedTargetPath();
        if (!string.IsNullOrEmpty(path))
        {
            UpdateHeroDetailsFromPath(path);
            _ = Init3DSceneAsync(path);
            Log($"selected model: {Path.GetFileName(path)}");
        }
        SaveConfig();
    }

    private void CmbHeroPreset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CmbHeroPreset.SelectedItem is HeroPresetChoice choice)
        {
            if (choice.IsAuto)
            {
                var path = GetResolvedTargetPath();
                if (!string.IsNullOrEmpty(path)) UpdateHeroDetailsFromPath(path);
            }
            else
            {
                var db = HeroDatabase.GetVisiblePresets();
                if (db.TryGetValue(choice.Key, out var preset))
                {
                    TxtSkel.Text = preset.Skel ?? string.Empty;
                    TxtGraph.Text = preset.Graph ?? string.Empty;
                    TxtUiGraph.Text = preset.UiGraph ?? string.Empty;
                }
            }
        }
    }

    private async void BrowseCsWin_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "select cswin64 directory (containing resourcecompiler.exe)"
        });

        if (folders.Count > 0)
        {
            TxtCsWinPath.Text = folders[0].Path.LocalPath;
            Log($"[setup] cswin64 path configured: {folders[0].Path.LocalPath}");
        }
    }

    private async void BrowseCitadel_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "select citadel addons directory (content/citadel_addons)"
        });

        if (folders.Count > 0)
        {
            TxtCitadelPath.Text = folders[0].Path.LocalPath;
            Log($"[setup] citadel addons path configured: {folders[0].Path.LocalPath}");
            RescanModels();
        }
    }

    private async void BrowseFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "select target .vmdl file",
            FileTypeFilter = new[]
            {
                new FilePickerFileType("valve modeldoc (*.vmdl)") { Patterns = new[] { "*.vmdl" } },
                new FilePickerFileType("all files (*.*)") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            var fullPath = files[0].Path.LocalPath;
            var model = new DiscoveredModel
            {
                FullPath = fullPath,
                Filename = Path.GetFileName(fullPath),
                Display = Path.GetFileName(fullPath),
                Addon = "manual"
            };
            CmbTargetVmdl.ItemsSource = new List<DiscoveredModel> { model };
            CmbTargetVmdl.SelectedIndex = 0;
            UpdateHeroDetailsFromPath(fullPath);
            _ = Init3DSceneAsync(fullPath);
            Log($"manually loaded model: {Path.GetFileName(fullPath)}");
        }
    }

    private void Rescan_Click(object? sender, RoutedEventArgs e)
    {
        RescanModels();
    }

    private async void BtnSanitizeModelDoc_Click(object? sender, RoutedEventArgs e)
    {
        var targetPath = GetResolvedTargetPath();
        if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
        {
            Log("[fix modeldoc] please select a target .vmdl file first.");
            await DialogService.ShowErrorAsync(this, "selection required", "please select a target .vmdl model file first.");
            return;
        }

        try
        {
            var (success, msg) = await VmdlPipeline.SanitizeVmdlForModelDocAsync(
                targetPath,
                disableAnimationList: ChkDisableAnimList.IsChecked == true
            );
            Log(msg);
            if (success)
            {
                await DialogService.ShowInfoAsync(this, "modeldoc fixed", "modeldoc syntax cleaned successfully.");
            }
            else
            {
                await DialogService.ShowErrorAsync(this, "modeldoc fix failed", msg);
            }
        }
        catch (Exception ex)
        {
            Log($"[fix modeldoc error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "fix modeldoc error", ex.Message);
        }
    }

    private async Task<bool> MakeVpkAsync(
        bool suppressSuccessDialog = false,
        string? targetPathOverride = null,
        string? citadelDirOverride = null)
    {
        var targetPath = targetPathOverride ?? GetResolvedTargetPath();
        var citadelDir = citadelDirOverride ?? TxtCitadelPath.Text?.Trim();

        if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(citadelDir))
        {
            Log("[make vpk] please select an addon and target model first.");
            await DialogService.ShowErrorAsync(this, "selection required", "please select an addon and target model first.");
            return false;
        }

        try
        {
            var (container, addonName, subpath) = VmdlPipeline.ParseCsdkPath(targetPath, citadelDir);
            var gameAddonDir = VmdlPipeline.ResolveGameAddonDir(targetPath, citadelDir, addonName);

            if (!Directory.Exists(gameAddonDir))
            {
                await DialogService.ShowErrorAsync(this, "compiled addon missing", $"compiled game addon directory does not exist:\n{gameAddonDir}\n\nCompile the addon before creating its VPK.");
                return false;
            }

            var compiledPath = Path.Combine(gameAddonDir, subpath + "_c");
            if (!File.Exists(compiledPath))
            {
                await DialogService.ShowErrorAsync(this, "compiled model missing", $"compiled model does not exist:\n{compiledPath}\n\nCompile the selected model before creating its VPK.");
                return false;
            }

            _protectedModels.TryGetValue(targetPath, out var protectedState);
            var expectedSkel = protectedState is null
                ? (ChkSkel.IsChecked == true ? TxtSkel.Text?.Trim() ?? string.Empty : null)
                : protectedState.Skel;
            var expectedGraph = protectedState is null
                ? (ChkGraph.IsChecked == true ? TxtGraph.Text?.Trim() ?? string.Empty : null)
                : protectedState.Graph;
            var expectedUiGraph = protectedState is null
                ? (ChkUiGraph.IsChecked == true ? TxtUiGraph.Text?.Trim() ?? string.Empty : null)
                : protectedState.UiGraph;
            var verificationError = VmdlPipeline.VerifyCompiledAg2References(
                compiledPath,
                expectedSkel, expectedGraph, expectedUiGraph);
            if (verificationError != null)
            {
                await DialogService.ShowErrorAsync(this, "compiled model missing AG2 references", verificationError);
                return false;
            }

            // Let user choose destination path & filename
            var suggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(gameAddonDir);
            var saveFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "save addon vpk file",
                DefaultExtension = "vpk",
                SuggestedFileName = "pak01_dir.vpk",
                SuggestedStartLocation = suggestedStartLocation,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("valve pack file (*.vpk)") { Patterns = new[] { "*.vpk" } }
                }
            });

            if (saveFile == null)
            {
                Log("[make vpk] packaging cancelled by user.");
                ReleaseProtectionsForAddon(gameAddonDir);
                return false;
            }

            var outputVpk = saveFile.Path.LocalPath;

            var res = await VpkBuilder.PackAddonToVpkAsync(gameAddonDir, outputVpk);
            if (res.Success)
            {
                var packedModelError = VerifyModelsInVpk(outputVpk, gameAddonDir, compiledPath);
                if (packedModelError != null)
                {
                    Log($"[make vpk error] {packedModelError}");
                    await DialogService.ShowErrorAsync(this, "VPK verification failed", packedModelError);
                    return false;
                }

                ReleaseProtectionsForAddon(gameAddonDir);
                Log($"[make vpk] addon packaged successfully: {res.OutputVpkPath} ({res.FileCount} files, {res.TotalBytes / 1024 / 1024:N1} mb)");
                if (!suppressSuccessDialog)
                {
                    await DialogService.ShowInfoAsync(this, "vpk created", "addon packaged into vpk successfully.");
                }
                return true;
            }
            else
            {
                Log($"[make vpk error] {res.Message}");
                await DialogService.ShowErrorAsync(this, "vpk packaging failed", res.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            Log($"[make vpk error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "vpk packaging error", ex.Message);
            return false;
        }
    }

    private async void BtnMakeVpk_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        var targetPath = GetResolvedTargetPath();
        try
        {
            _isProcessing = true;
            var packed = await MakeVpkAsync(suppressSuccessDialog: false);
            if (!packed && !string.IsNullOrWhiteSpace(targetPath))
                await OfferProtectionReleaseAfterFailedPackAsync(targetPath, TxtCitadelPath.Text?.Trim());
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async void BtnAddAddon_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        var contentAddonsDir = TxtCitadelPath.Text?.Trim();
        if (string.IsNullOrWhiteSpace(contentAddonsDir) || !Directory.Exists(contentAddonsDir))
        {
            await DialogService.ShowErrorAsync(this, "CSDK12 folder required",
                "Select CSDK12's content/citadel_addons folder before creating an addon.");
            return;
        }

        var deadlockInfo = DeadlockLocator.DetectDeadlockInstallation();
        string? vpkPath = deadlockInfo.IsValid ? deadlockInfo.Pak01VpkPath : null;
        if (vpkPath == null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "select Deadlock pak01_dir.vpk",
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Deadlock VPK (*.vpk)")
                    {
                        Patterns = new[] { "pak01_dir.vpk", "*.vpk" }
                    }
                }
            });
            if (files.Count == 0) return;
            vpkPath = files[0].Path.LocalPath;
        }

        _ = LoadPresetPortraitsAsync(vpkPath);

        try
        {
            _isProcessing = true;
            var dialog = new AddAddonWindow(contentAddonsDir, vpkPath, Log);
            await dialog.ShowDialog(this);
            if (dialog.Result is not { } result) return;

            Log($"[add addon] created {result.Name}: {result.FileCount} files, " +
                $"{result.ClothFileCount} cloth assets; model: {result.MainVmdlPath}");
            RescanModels();
            var addon = _discoveredAddons.FirstOrDefault(a =>
                a.Name.Equals(result.Name, StringComparison.OrdinalIgnoreCase));
            if (addon != null)
            {
                CmbDiscovered.SelectedItem = addon;
                var model = addon.HeroModels.FirstOrDefault(m =>
                    m.FullPath.Equals(result.MainVmdlPath, StringComparison.OrdinalIgnoreCase));
                if (model != null) CmbTargetVmdl.SelectedItem = model;
            }
            SaveConfig();
        }
        catch (Exception ex)
        {
            Log($"[add addon error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "Addon creation failed", ex.Message);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async void BtnExportCsWin_Click(object? sender, RoutedEventArgs e)
    {
        var targetPath = GetResolvedTargetPath();
        var csWinDir = TxtCsWinPath.Text?.Trim();
        var citadelDir = TxtCitadelPath.Text?.Trim();

        if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(csWinDir))
        {
            Log("[export] cswin64 directory or target model not configured.");
            await DialogService.ShowErrorAsync(this, "configuration required", "cswin64 directory or target model is not configured.");
            return;
        }

        if (!VmdlPipeline.IsValidCsWinDir(csWinDir))
        {
            Log("[export] cswin64 directory is invalid or resourcecompiler.exe is missing.");
            await DialogService.ShowErrorAsync(this, "compiler missing", "cswin64 directory is invalid or missing resourcecompiler.exe.");
            return;
        }

        try
        {
            var (success, msg, filesCopied) = await VmdlPipeline.ExportToCsWinAddonAsync(
                targetPath,
                skelPath: TxtSkel.Text?.Trim(),
                graphPath: TxtGraph.Text?.Trim(),
                uiGraphPath: TxtUiGraph.Text?.Trim(),
                addSkel: ChkSkel.IsChecked == true,
                addGraph: ChkGraph.IsChecked == true,
                addUiGraph: ChkUiGraph.IsChecked == true,
                cswinDir: csWinDir,
                citadelAddonsDir: citadelDir
            );

            Log(msg);
            if (success)
            {
                await DialogService.ShowInfoAsync(this, "export complete", "exported to cswin64 successfully.");
            }
            else
            {
                await DialogService.ShowErrorAsync(this, "export failed", msg);
            }
        }
        catch (Exception ex)
        {
            Log($"[export error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "export error", ex.Message);
        }
    }

    private async void BtnCompile_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;

        var targetPath = GetResolvedTargetPath();
        var csWinDir = TxtCsWinPath.Text?.Trim();
        var citadelDir = TxtCitadelPath.Text?.Trim();

        if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
        {
            Log("[compile error] target .vmdl file does not exist.");
            await DialogService.ShowErrorAsync(this, "target missing", "target .vmdl file does not exist. please select a valid model.");
            return;
        }

        if (!VmdlPipeline.IsValidCsWinDir(csWinDir))
        {
            Log("[compile error] cswin64 compiler path is invalid or missing resourcecompiler.exe.");
            await DialogService.ShowErrorAsync(this, "compiler missing", "cswin64 compiler path is invalid or missing resourcecompiler.exe.");
            return;
        }

        var requestedSkel = TxtSkel.Text?.Trim();
        var requestedGraph = TxtGraph.Text?.Trim();
        var requestedUiGraph = TxtUiGraph.Text?.Trim();
        var addSkel = ChkSkel.IsChecked == true;
        var addGraph = ChkGraph.IsChecked == true;
        var addUiGraph = ChkUiGraph.IsChecked == true;
        var (defaultSkel, defaultGraph, defaultUiGraph) = VmdlPipeline.DeriveDefaultPaths(targetPath);
        var expectedSkel = addSkel ? (string.IsNullOrWhiteSpace(requestedSkel) ? defaultSkel : requestedSkel) : null;
        var expectedGraph = addGraph ? (string.IsNullOrWhiteSpace(requestedGraph) ? defaultGraph : requestedGraph) : null;
        var expectedUiGraph = addUiGraph ? (string.IsNullOrWhiteSpace(requestedUiGraph) ? defaultUiGraph : requestedUiGraph) : null;

        try
        {
            _isProcessing = true;
            BtnCompile.IsEnabled = false;
            TxtCompileBtn.Text = "compiling...";

            // Show real-time compilation progress bar under compile button
            PanelCompileProgress.IsVisible = true;
            PrgCompile.Value = 10;
            LblCompilePercent.Text = "10%";
            LblCompileStage.Text = "[1/5] preparing source";
            LblCompileDetail.Text = Path.GetFileName(targetPath);

            Log($"[compile] starting compilation for: {Path.GetFileName(targetPath)}");

            var progress = new Progress<VmdlPipeline.CompileProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    PrgCompile.Value = p.Percent;
                    LblCompilePercent.Text = $"{p.Percent}%";
                    LblCompileStage.Text = p.Stage;
                    LblCompileDetail.Text = p.Detail;
                });
            });

            var (success, msg) = await VmdlPipeline.ProcessVmdlFileAsync(
                targetPath,
                skelPath: requestedSkel,
                graphPath: requestedGraph,
                uiGraphPath: requestedUiGraph,
                createBackup: true,
                addSkel: addSkel,
                addGraph: addGraph,
                addUiGraph: addUiGraph,
                upgradeHeader: true,
                compileCsWin: true,
                revertVmdl: ChkRevert.IsChecked == true,
                cswinDir: csWinDir,
                citadelAddonsDir: citadelDir,
                disableAnimationList: ChkDisableAnimList.IsChecked == true,
                progress: progress,
                onLog: Log,
                beforeDeploy: ReleaseProtectionForOutput,
                afterDeploy: (deployedPath, compilerOutputPath) =>
                {
                    var protection = CompiledModelProtection.Acquire(deployedPath, compilerOutputPath);
                    _protectedModels[targetPath] = new ProtectedModelState(
                        protection, expectedSkel, expectedGraph, expectedUiGraph);
                    UpdateProtectionStatus();
                    Log($"[protect] CSDK12 cannot overwrite {Path.GetFileName(deployedPath)} until packaging or refusal.");
                }
            );

            if (success)
            {
                PrgCompile.Value = 100;
                LblCompilePercent.Text = "100%";
                LblCompileStage.Text = "[5/5] complete";
                LblCompileDetail.Text = "deployed successfully";

                Log($"[compile success] {msg}");

                var packVpk = await DialogService.ShowConfirmAsync(
                    this,
                    "compilation successful",
                    "model compiled and deployed successfully!\n\nwould you like to package the addon into a .vpk archive now?"
                );

                if (packVpk)
                {
                    var packed = await MakeVpkAsync(
                        suppressSuccessDialog: false,
                        targetPathOverride: targetPath,
                        citadelDirOverride: citadelDir);
                    if (!packed)
                        await OfferProtectionReleaseAfterFailedPackAsync(targetPath, citadelDir);
                }
                else
                {
                    ReleaseProtectionForSource(targetPath);
                }
            }
            else
            {
                LblCompileStage.Text = "[error] failed";
                LblCompileDetail.Text = msg;

                Log($"[compile error] {msg}");
                await DialogService.ShowErrorAsync(this, "compilation failed", $"compilation failed:\n\n{msg}");
            }
        }
        catch (Exception ex)
        {
            LblCompileStage.Text = "[error] exception";
            LblCompileDetail.Text = ex.Message;

            Log($"[compile exception] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "compile exception", ex.Message);
        }
        finally
        {
            _isProcessing = false;
            BtnCompile.IsEnabled = true;
            TxtCompileBtn.Text = "compile model";
        }
    }

    private async void BtnLaunchGame_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var targetPath = GetResolvedTargetPath();
            var citadelDir = TxtCitadelPath.Text?.Trim();
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(citadelDir))
            {
                Log("[launch error] please select an addon and target model first.");
                await DialogService.ShowErrorAsync(this, "selection required", "please select an addon and target model first.");
                return;
            }

            var (_, addonName, _) = VmdlPipeline.ParseCsdkPath(targetPath, citadelDir);

            var deadlockInfo = DeadlockLocator.DetectDeadlockInstallation();
            if (!deadlockInfo.IsValid || !File.Exists(deadlockInfo.DeadlockExePath))
            {
                Log("[launch error] could not locate deadlock.exe automatically.");
                await DialogService.ShowErrorAsync(this, "deadlock not found", "could not locate deadlock.exe automatically.\nplease ensure deadlock is installed in your steam library.");
                return;
            }

            // Check if the actual game instance is already running
            var runningGame = Process.GetProcessesByName("deadlock")
                .FirstOrDefault(p =>
                {
                    try
                    {
                        return string.Equals(p.MainModule?.FileName, deadlockInfo.DeadlockExePath, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });

            if (runningGame != null)
            {
                var launchAnother = await DialogService.ShowConfirmAsync(
                    this,
                    "deadlock already running",
                    "an instance of deadlock game is already running.\n\nwould you like to launch another instance anyway?"
                );
                if (!launchAnother) return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = deadlockInfo.DeadlockExePath,
                WorkingDirectory = Path.GetDirectoryName(deadlockInfo.DeadlockExePath)!,
                UseShellExecute = true
            };
            psi.ArgumentList.Add("-addon");
            psi.ArgumentList.Add(addonName);
            psi.ArgumentList.Add("-allowmultiple");

            Process.Start(psi);
            Log($"[launch] started deadlock: {deadlockInfo.DeadlockExePath} -addon {addonName} -allowmultiple");
        }
        catch (Exception ex)
        {
            Log($"[launch error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "launch failed", $"failed to launch deadlock:\n\n{ex.Message}");
        }
    }
}
