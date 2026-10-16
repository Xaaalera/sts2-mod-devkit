<#
.SYNOPSIS
Build, prepare, launch, and control an owned StS2 development session.
.EXAMPLE
./scripts/sdk.ps1 -Action init -GameDirectory 'D:/Games/Slay the Spire 2' -ProbeDirectory 'D:/Code/sts2-reload-probe'
.EXAMPLE
./scripts/sdk.ps1 -Action build
./scripts/sdk.ps1 -Action prepare
./scripts/sdk.ps1 -Action launch
./scripts/sdk.ps1 -Action reload
./scripts/sdk.ps1 -Action invoke -Command probe
#>
[CmdletBinding()]
param(
    [ValidateSet('init','build','prepare','copy-config','launch','status','reload','unload','invoke','package-player')]
    [string]$Action = 'status',
    [string]$GameDirectory,
    [string]$ProbeDirectory,
    [string]$SettingsFile,
    [string]$PluginId = 'reload-probe',
    [string]$Command = 'probe',
    [hashtable]$Arguments = @{}
)

$ErrorActionPreference = 'Stop'
$sdkRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stateDirectory = Join-Path $sdkRoot '.local'
$configurationPath = Join-Path $stateDirectory 'config.json'
$utf8 = [Text.UTF8Encoding]::new($false)

function Save-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), $utf8)
}

function Get-OwnedTestProcesses {
    $ownedRoot = [IO.Path]::GetFullPath($stateDirectory) + [IO.Path]::DirectorySeparatorChar
    return @(Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ([IO.Path]::GetFullPath($_.Path)).StartsWith($ownedRoot, [StringComparison]::OrdinalIgnoreCase)
    })
}

function Build-Projects($Configuration) {
    $previousGameDirectory = $env:STS2_GAME_DIR
    try {
        $env:STS2_GAME_DIR = $Configuration.gameDirectory
        foreach ($project in @('src/Runtime/Sts2.Devkit.Runtime.csproj','src/GameHost/Sts2.Devkit.GameHost.csproj','src/Bootstrap/Sts2Devkit.csproj')) {
            dotnet build (Join-Path $sdkRoot $project) --nologo --verbosity quiet
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
        }
        $feedDirectory = Join-Path $stateDirectory 'package-feed'
        dotnet pack (Join-Path $sdkRoot 'src/Contracts/Sts2.Devkit.Contracts.csproj') --output $feedDirectory --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Contract package creation failed' }
        $extensionProject = Join-Path $Configuration.probeDirectory 'src/Extension/reload-probe.csproj'
        dotnet restore $extensionProject --source $feedDirectory --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Extension restore failed' }
        dotnet build $extensionProject --no-restore --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Extension build failed' }
        dotnet build (Join-Path $Configuration.probeDirectory 'src/Player/ReloadProbe.csproj') --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Player adapter build failed' }
    } finally { $env:STS2_GAME_DIR = $previousGameDirectory }
}

function Prepare-Game($Configuration) {
    $ownedDirectory = Join-Path $stateDirectory 'game'
    if (-not (Test-Path -LiteralPath "$ownedDirectory/SlayTheSpire2.exe")) {
        New-Item -ItemType Directory -Path $ownedDirectory -Force | Out-Null
        robocopy $Configuration.gameDirectory $ownedDirectory /E /XD mods controller_config MonsterActionPredictor_Broken /XF mods.zip /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -gt 7) { throw 'Isolated game copy failed' }
    }
    if (Get-OwnedTestProcesses) { throw 'Prepare requires the owned test game to be closed; the owner Steam game may remain running' }
    $release = Get-Content -LiteralPath "$ownedDirectory/release_info.json" -Raw | ConvertFrom-Json
    if ($release.version -ne 'v0.107.1') { throw 'This first slice targets game v0.107.1' }
    $libraryCache = Join-Path $stateDirectory 'runtime-libraries'
    New-Item -ItemType Directory -Path $libraryCache -Force | Out-Null
    $libraries = @(
        @{Repository='Alchyr/BaseLib-StS2';Tag='v3.4.7';Asset='BaseLib.3.4.7.zip';Id='BaseLib';Hash='D28C649A3AEA9EF4869050A06BFBADC4863016BCBD4C1740ACC4CD07809E4BD8'},
        @{Repository='BAKAOLC/STS2-RitsuLib';Tag='v0.6.7';Asset='STS2.RitsuLib.Compat.0.107.1.0.6.7.github.zip';Id='STS2-RitsuLib';Hash='0DBCE8E962F639C4AD1D151ED05E367A9BB25BAC23FFFF60DBA18B0E05D4C568'}
    )
    foreach ($library in $libraries) {
        $archivePath = Join-Path $libraryCache $library.Asset
        if (-not (Test-Path -LiteralPath $archivePath)) {
            gh release download $library.Tag --repo $library.Repository --pattern $library.Asset --dir $libraryCache
            if ($LASTEXITCODE -ne 0) { throw 'Runtime library download failed' }
        }
        if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $library.Hash) { throw 'Runtime library identity changed' }
        Expand-Archive -LiteralPath $archivePath -DestinationPath "$ownedDirectory/mods/$($library.Id)" -Force
    }
    $modDirectory = "$ownedDirectory/mods/Sts2Devkit"
    New-Item -ItemType Directory -Path $modDirectory -Force | Out-Null
    Copy-Item -LiteralPath "$sdkRoot/src/Bootstrap/bin/Debug/net9.0/Sts2Devkit.dll","$sdkRoot/src/GameHost/bin/Debug/net9.0/Sts2.Devkit.GameHost.dll","$sdkRoot/src/Runtime/bin/Debug/net9.0/Sts2.Devkit.Runtime.dll","$sdkRoot/src/Contracts/bin/Debug/net9.0/HotMod.Contracts.dll","$sdkRoot/Sts2Devkit.json" -Destination $modDirectory
    Copy-Item -LiteralPath "$sdkRoot/upstream/spirectl/LICENSE","$sdkRoot/upstream/spirectl/NOTICE" -Destination $modDirectory
    $pluginDirectory = Join-Path $stateDirectory 'plugins/reload-probe'
    New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
    Copy-Item -LiteralPath "$($Configuration.probeDirectory)/src/Extension/bin/Debug/net9.0/reload-probe.dll" -Destination $pluginDirectory
    $profileName = 'Sts2Devkit/owned-v107'
    $override = '[application]' + [char]10 + 'config/use_custom_user_dir=true' + [char]10 + 'config/custom_user_dir_name="' + $profileName + '"' + [char]10
    [IO.File]::WriteAllText("$ownedDirectory/override.cfg", $override, $utf8)
    $userDirectory = Join-Path $env:APPDATA $profileName
    New-Item -ItemType Directory -Path $userDirectory -Force | Out-Null
    $accountDirectory = Join-Path $userDirectory 'default/1'
    New-Item -ItemType Directory -Path $accountDirectory -Force | Out-Null
    $settingsPath = Join-Path $accountDirectory 'settings.save'
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        Save-Json $settingsPath @{schema_version=5;mod_settings=@{mods_enabled=$true;mod_list=@()};skip_intro_logo=$true;fullscreen=$false;volume_master=0;window_size=@{X=1280;Y=720};target_display=0}
    }
    Save-Json (Join-Path $stateDirectory 'prepared.json') @{gameDirectory=$ownedDirectory;userDirectory=$userDirectory}
}

function Copy-AccountConfiguration {
    if (Get-OwnedTestProcesses) { throw 'Close the owned test game before replacing its settings' }
    $source = $SettingsFile
    if (-not $source) {
        $candidates = @(Get-ChildItem -Path (Join-Path $env:APPDATA 'SlayTheSpire2/steam/*/settings.save') -File)
        if ($candidates.Count -ne 1) { throw 'Specify the main account settings.save with -SettingsFile' }
        $source = $candidates[0].FullName
    }
    $source = (Resolve-Path -LiteralPath $source).Path
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $prepared = Get-Content -LiteralPath (Join-Path $stateDirectory 'prepared.json') -Raw | ConvertFrom-Json
    $target = Join-Path $prepared.userDirectory 'default/1/settings.save'
    $settings = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
    $settings.fullscreen = $false
    $settings.skip_intro_logo = $true
    $settings.window_size = @{X=1280;Y=720}
    $settings.target_display = 0
    $settings.mod_settings = @{mods_enabled=$true;mod_list=@()}
    Save-Json $target $settings
    $sourcePreferences = Join-Path (Split-Path $source) 'profile1/saves/prefs.save'
    if (Test-Path -LiteralPath $sourcePreferences) {
        $targetPreferences = Join-Path $prepared.userDirectory 'default/1/profile1/saves/prefs.save'
        New-Item -ItemType Directory -Path (Split-Path $targetPreferences) -Force | Out-Null
        Copy-Item -LiteralPath $sourcePreferences -Destination $targetPreferences -Force
    }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Source configuration changed during copy' }
    $receipt = @{source=$source;sourceSha256=$sourceHash;target=$target;overrides=@('fullscreen=false','skip_intro_logo=true','window_size=1280x720','target_display=0','local mod consent and loadout')}
    Save-Json (Join-Path $stateDirectory 'config-copy.json') $receipt
    return $receipt
}

function Read-OwnedSession {
    $session = Get-Content -LiteralPath (Join-Path $stateDirectory 'session.json') -Raw | ConvertFrom-Json
    $process = Get-Process -Id $session.pid -ErrorAction Stop
    if ($process.Path -ne $session.executable -or $process.StartTime.ToUniversalTime().Ticks -ne ([datetime]$session.startedAtUtc).ToUniversalTime().Ticks) {
        throw 'Owned process identity mismatch'
    }
    if (-not ([IO.Path]::GetFullPath($process.Path)).StartsWith([IO.Path]::GetFullPath("$stateDirectory/game/"), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Process is outside the owned game directory'
    }
    return $session
}

function Send-Request($Session, [string]$RequestAction) {
    $requestPath = Join-Path $Session.controlDirectory 'request.json'
    if ((Test-Path -LiteralPath $requestPath) -or (Test-Path -LiteralPath (Join-Path $Session.controlDirectory 'processing.json'))) {
        throw 'An operation is still pending; do not replay it'
    }
    $requestId = [guid]::NewGuid().ToString()
    $request = @{requestId=$requestId;session=$Session.session;action=$RequestAction;pluginId=$PluginId;command=$Command;arguments=$Arguments}
    $temporary = "$requestPath.$requestId.tmp"
    Save-Json $temporary $request
    [IO.File]::Move($temporary, $requestPath)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 20) {
        $responsePath = Join-Path $Session.controlDirectory 'response.json'
        if (Test-Path -LiteralPath $responsePath) {
            $response = Get-Content -LiteralPath $responsePath -Raw | ConvertFrom-Json
            if ($response.requestId -eq $requestId -and $response.session -eq $Session.session) { return $response }
        }
        Start-Sleep -Milliseconds 25
    }
    throw 'Operation outcome is unknown after timeout; inspect session files before continuing'
}

function Start-Game {
    if (Get-OwnedTestProcesses) { throw 'An owned test game already exists; reuse its session' }
    $prepared = Get-Content -LiteralPath (Join-Path $stateDirectory 'prepared.json') -Raw | ConvertFrom-Json
    $sessionId = [guid]::NewGuid().ToString()
    $control = Join-Path $stateDirectory "sessions/$sessionId"
    New-Item -ItemType Directory -Path $control -Force | Out-Null
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Join-Path $prepared.gameDirectory 'SlayTheSpire2.exe'
    $startInfo.WorkingDirectory = $prepared.gameDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.Arguments = '--windowed --resolution 1280x720 --force-steam off --log-file "' + (Join-Path $control 'game.log') + '"'
    $startInfo.EnvironmentVariables['STS2_DEVKIT_CONTROL'] = $control
    $startInfo.EnvironmentVariables['STS2_DEVKIT_SESSION'] = $sessionId
    $startInfo.EnvironmentVariables['STS2_DEVKIT_PLUGINS'] = Join-Path $stateDirectory 'plugins'
    $startInfo.EnvironmentVariables['STS2_DEVKIT_USER_DIR'] = $prepared.userDirectory
    $previousEnvironment = @{}
    $taskEnvironment = @('STS2_DEVKIT_CONTROL','STS2_DEVKIT_SESSION','STS2_DEVKIT_PLUGINS','STS2_DEVKIT_USER_DIR')
    try {
        foreach ($name in $taskEnvironment) {
            $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, $startInfo.EnvironmentVariables[$name])
        }
        $process = Start-Process -FilePath $startInfo.FileName -ArgumentList $startInfo.Arguments -WorkingDirectory $startInfo.WorkingDirectory -RedirectStandardOutput (Join-Path $control 'stdout.log') -RedirectStandardError (Join-Path $control 'stderr.log') -PassThru
    } finally {
        foreach ($name in $taskEnvironment) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name]) }
    }
    $session = @{session=$sessionId;pid=$process.Id;startedAtUtc=$process.StartTime.ToUniversalTime().ToString('o');executable=$startInfo.FileName;controlDirectory=$control}
    Save-Json (Join-Path $stateDirectory 'session.json') $session
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 45) {
        if (Test-Path -LiteralPath (Join-Path $control 'status.json')) { return Get-Content -LiteralPath (Join-Path $control 'status.json') -Raw | ConvertFrom-Json }
        if ($process.HasExited) { throw 'Owned game exited during startup; inspect its log' }
        Start-Sleep -Milliseconds 100
    }
    throw 'SDK startup was not confirmed; inspect the retained owned session without launching another game'
}

try {
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    $lifecycleLease = $null
    if ($Action -in @('init','prepare','copy-config','launch')) {
        $lifecycleLease = [IO.File]::Open((Join-Path $stateDirectory 'lifecycle.lock'),
            [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    if ($Action -eq 'init') {
        $gamePath = (Resolve-Path -LiteralPath $GameDirectory).Path
        $probePath = (Resolve-Path -LiteralPath $ProbeDirectory).Path
        if (-not (Test-Path -LiteralPath "$gamePath/data_sts2_windows_x86_64/sts2.dll")) { throw 'Game assembly is missing' }
        Save-Json $configurationPath @{gameDirectory=$gamePath;probeDirectory=$probePath}
        @{configured=$true;configuration=$configurationPath} | ConvertTo-Json
        return
    }
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    $controlResponse = $null
    switch ($Action) {
        'build' { Build-Projects $configuration }
        'prepare' { Prepare-Game $configuration }
        'copy-config' { Copy-AccountConfiguration | ConvertTo-Json -Depth 6 }
        'launch' { Start-Game | ConvertTo-Json -Depth 20 }
        'status' { $controlResponse = Send-Request (Read-OwnedSession) 'status' }
        'reload' {
            $session = Read-OwnedSession
            $source = Join-Path $configuration.probeDirectory 'src/Extension/bin/Debug/net9.0/reload-probe.dll'
            $destination = Join-Path $stateDirectory "plugins/$PluginId/$PluginId.dll"
            if ($PluginId -ne 'reload-probe') { throw 'This build/deploy recipe only owns reload-probe; other plugins require their own artifact recipe' }
            Copy-Item -LiteralPath $source -Destination "$destination.next" -Force
            Move-Item -LiteralPath "$destination.next" -Destination $destination -Force
            $controlResponse = Send-Request $session 'reload'
        }
        'invoke' { $controlResponse = Send-Request (Read-OwnedSession) 'invoke' }
        'unload' { $controlResponse = Send-Request (Read-OwnedSession) 'unload' }
        'package-player' {
            $packageDirectory = Join-Path $stateDirectory 'player-package/ReloadProbe'
            New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
            Copy-Item -LiteralPath "$($configuration.probeDirectory)/src/Player/bin/Debug/net9.0/ReloadProbe.dll","$($configuration.probeDirectory)/ReloadProbe.json" -Destination $packageDirectory
            @{package=$packageDirectory;runtimeDependencies=@('BaseLib 3.4.7','RitsuLib 0.6.7 / API 0.107.1')} | ConvertTo-Json
        }
    }
    if ($null -ne $controlResponse) {
        $controlResponse | ConvertTo-Json -Depth 20
        if ($controlResponse.result.error) { exit 1 }
    }
} catch {
    $diagnosticPath = Join-Path $stateDirectory 'cli-errors.jsonl'
    [IO.File]::AppendAllText($diagnosticPath, (@{time=[datetime]::UtcNow.ToString('o');diagnostic=$_.ToString()} | ConvertTo-Json -Compress) + [Environment]::NewLine, $utf8)
    [xml]$catalog = Get-Content -LiteralPath (Join-Path $sdkRoot 'src/Runtime/Errors.resx') -Raw
    $message = ($catalog.root.data | Where-Object { $_.name -eq 'INTERNAL_FAILURE' }).value
    @{error=@{code=500;status='INTERNAL';message=$message;details=@(@{'@type'='ErrorInfo';reason='INTERNAL_FAILURE';domain='sts2.devkit.cli';metadata=@{diagnostics=$diagnosticPath}})}} | ConvertTo-Json -Depth 6
    exit 1
} finally {
    if ($null -ne $lifecycleLease) { $lifecycleLease.Dispose() }
}
