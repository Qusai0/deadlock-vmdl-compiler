using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeadlockVmdlCompiler.Models;
using DeadlockVmdlCompiler.Services;

namespace DeadlockVmdlCompiler.Views;

public partial class AddAddonWindow : Window
{
    private readonly string _contentAddonsDirectory;
    private readonly string _pak01VpkPath;
    private readonly Action<string> _onLog;
    private CancellationTokenSource? _cancellation;
    private bool _isExporting;

    public AddonCreationResult? Result { get; private set; }

    public AddAddonWindow() : this(string.Empty, string.Empty, _ => { }) { }

    public AddAddonWindow(string contentAddonsDirectory, string pak01VpkPath, Action<string> onLog)
    {
        InitializeComponent();
        _contentAddonsDirectory = contentAddonsDirectory;
        _pak01VpkPath = pak01VpkPath;
        _onLog = onLog;
        CmbHero.ItemsSource = DeadlockHeroCatalog.GetHeroes();
        CmbHero.SelectedIndex = 0;
        Opened += (_, _) => TxtAddonName.Focus();
        Closing += (_, e) =>
        {
            if (_isExporting)
            {
                e.Cancel = true;
                _cancellation?.Cancel();
                TxtProgress.Text = "Cancelling after the current export step...";
            }
        };
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_isExporting)
        {
            _cancellation?.Cancel();
            BtnCancel.IsEnabled = false;
            TxtProgress.Text = "Cancelling after the current export step...";
        }
        else Close();
    }

    private async void BtnCreate_Click(object? sender, RoutedEventArgs e)
    {
        if (_isExporting) return;
        var name = TxtAddonName.Text?.Trim() ?? string.Empty;
        var error = AddonCreationService.ValidateName(name);
        var hero = CmbHero.SelectedItem as DeadlockHeroModel;
        if (hero is null)
            error = "Choose a character.";
        if (error != null)
        {
            TxtError.Text = error;
            TxtError.IsVisible = true;
            if (CmbHero.SelectedItem is null) CmbHero.Focus();
            else TxtAddonName.Focus();
            return;
        }

        _isExporting = true;
        _cancellation = new CancellationTokenSource();
        BtnCreate.IsEnabled = false;
        CmbHero.IsEnabled = false;
        TxtAddonName.IsEnabled = false;
        TxtError.IsVisible = false;
        PanelProgress.IsVisible = true;
        TxtProgress.Text = $"Exporting {hero!.DisplayName}...";

        try
        {
            var progress = new Progress<DecompileProgress>(p =>
                TxtProgress.Text = $"{p.ExtractedCount} files: {p.CurrentFile}");
            Result = await AddonCreationService.CreateAsync(
                _contentAddonsDirectory, _pak01VpkPath, hero!, name,
                progress, _onLog, _cancellation.Token);
            _isExporting = false;
            Close();
        }
        catch (OperationCanceledException)
        {
            _onLog("[add addon] export cancelled; temporary files removed.");
            _isExporting = false;
            Close();
        }
        catch (Exception ex)
        {
            _onLog($"[add addon error] {ex.Message}");
            TxtError.Text = ex.Message;
            TxtError.IsVisible = true;
            PanelProgress.IsVisible = false;
        }
        finally
        {
            _isExporting = false;
            _cancellation?.Dispose();
            _cancellation = null;
            BtnCreate.IsEnabled = true;
            BtnCancel.IsEnabled = true;
            CmbHero.IsEnabled = true;
            TxtAddonName.IsEnabled = true;
        }
    }
}
