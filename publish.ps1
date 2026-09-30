$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$publishDir = [IO.Path]::GetFullPath((Join-Path $projectRoot 'publish'))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'obj\publish-staging'))
$stage = [IO.Path]::GetFullPath((Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))))

if (-not $publishDir.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not $stage.StartsWith($stagingRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish paths escaped the project directory.'
}

$published = $false
try {
    dotnet publish (Join-Path $projectRoot 'DeadlockVmdlCompiler.csproj') -c Release -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $stageFiles = @(Get-ChildItem -LiteralPath $stage -Force)
    if ($stageFiles.Count -ne 1 -or $stageFiles[0].Name -ne 'DeadlockVmdlCompiler.exe') {
        throw "Expected one EXE in staging; found: $($stageFiles.Name -join ', ')."
    }

    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
    # Preserve legacy portable settings while keeping publish a single EXE.
    $userDataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DeadlockVmdlCompiler'
    foreach ($name in @('config.json', 'hero_paths.json')) {
        $legacy = Join-Path $publishDir $name
        if (-not (Test-Path -LiteralPath $legacy -PathType Leaf)) { continue }
        $saved = Join-Path $userDataDir $name
        if (-not (Test-Path -LiteralPath $saved)) {
            $null = Get-Content -LiteralPath $legacy -Raw | ConvertFrom-Json
            New-Item -ItemType Directory -Path $userDataDir -Force | Out-Null
            Copy-Item -LiteralPath $legacy -Destination $saved
            if ((Get-FileHash -LiteralPath $legacy -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash) {
                throw "Legacy settings migration failed: $name"
            }
        }
        $legacyBackupDir = [IO.Path]::GetFullPath((Join-Path $stagingRoot ('legacy-settings-' + [Guid]::NewGuid().ToString('N'))))
        if (-not $legacyBackupDir.StartsWith($stagingRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Legacy settings backup escaped the staging directory.'
        }
        New-Item -ItemType Directory -Path $legacyBackupDir -Force | Out-Null
        Move-Item -LiteralPath $legacy -Destination (Join-Path $legacyBackupDir $name)
        Write-Output "Legacy $name preserved at $legacyBackupDir"
    }
    $destination = Join-Path $publishDir 'DeadlockVmdlCompiler.exe'
    $sourceHash = (Get-FileHash -LiteralPath $stageFiles[0].FullName -Algorithm SHA256).Hash
    $copied = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Copy-Item -LiteralPath $stageFiles[0].FullName -Destination $destination -Force
            $copied = $true
            break
        }
        catch {
            if ($attempt -lt 3) { Start-Sleep -Milliseconds 500 }
        }
    }

    if (-not $copied) {
        # Windows can keep a running EXE locked. Stage the replacement beside it,
        # then move the old image out of publish without closing the application.
        $next = [IO.Path]::GetFullPath((Join-Path $publishDir 'DeadlockVmdlCompiler.next.exe'))
        $backupDir = [IO.Path]::GetFullPath((Join-Path $stagingRoot ('running-backup-' + [Guid]::NewGuid().ToString('N'))))
        if (-not $next.StartsWith($publishDir + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not $backupDir.StartsWith($stagingRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Replacement paths escaped the project directory.'
        }
        if (Test-Path -LiteralPath $next) { throw "Temporary publish file already exists: $next" }
        Copy-Item -LiteralPath $stageFiles[0].FullName -Destination $next
        if ((Get-FileHash -LiteralPath $next -Algorithm SHA256).Hash -ne $sourceHash) {
            Remove-Item -LiteralPath $next -Force
            throw 'Temporary replacement differs from the staged build.'
        }

        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        $backup = Join-Path $backupDir 'DeadlockVmdlCompiler.exe'
        try {
            Move-Item -LiteralPath $destination -Destination $backup -ErrorAction Stop
        }
        catch {
            Remove-Item -LiteralPath $next -Force
            throw
        }
        try {
            Move-Item -LiteralPath $next -Destination $destination -ErrorAction Stop
        }
        catch {
            Move-Item -LiteralPath $backup -Destination $destination -ErrorAction Stop
            throw
        }
        Write-Warning "Previous running EXE retained at $backup"
    }

    $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($sourceHash -ne $destinationHash) { throw 'Published EXE differs from the staged build.' }

    $oldSidecars = @(
        'av_libglesv2.dll', 'DeadlockVmdlCompiler.pdb', 'libHarfBuzzSharp.dll',
        'libSkiaSharp.dll', 'libSkiaSharp.pdb', 'spirv-cross.dll'
    )
    foreach ($name in $oldSidecars) {
        $sidecar = [IO.Path]::GetFullPath((Join-Path $publishDir $name))
        if (-not $sidecar.StartsWith($publishDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Sidecar path escaped publish: $sidecar"
        }
        if (Test-Path -LiteralPath $sidecar -PathType Leaf) {
            Remove-Item -LiteralPath $sidecar -Force
        }
    }

    $publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -Force)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'DeadlockVmdlCompiler.exe') {
        throw "Unexpected files in publish: $($publishedFiles.Name -join ', ')."
    }

    $published = $true
    Write-Output "Published $destination ($($publishedFiles[0].Length) bytes, SHA256 $destinationHash)"
}
finally {
    if ($published -and (Test-Path -LiteralPath $stage -PathType Container)) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    elseif (-not $published -and (Test-Path -LiteralPath $stage -PathType Container)) {
        Write-Warning "Staged build preserved at $stage"
    }
}
