[CmdletBinding()]
param(
    [ValidateSet('Portable', 'Small', 'Both')]
    [string]$Mode = 'Portable'
)

$ErrorActionPreference = 'Stop'
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectFile = Join-Path $ProjectDir 'Interstellar.csproj'
$DistDir = Join-Path $ProjectDir 'dist'

if (-not (Test-Path -LiteralPath $ProjectFile)) {
    throw "Could not find Interstellar.csproj at: $ProjectFile"
}

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$dotnetVersion = (& $dotnetCommand.Source --version).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to run dotnet --version. Install the .NET 8 SDK and try again.'
}

if (-not $dotnetVersion.StartsWith('8.')) {
    Write-Warning "This project targets .NET 8. Detected SDK $dotnetVersion; an installed .NET 8 SDK is recommended."
}

Write-Host "Building Interstellar with .NET SDK $dotnetVersion" -ForegroundColor Cyan
Write-Host "Project: $ProjectFile" -ForegroundColor DarkGray

function Publish-Interstellar {
    param(
        [Parameter(Mandatory = $true)][string]$Flavor,
        [Parameter(Mandatory = $true)][bool]$SelfContained
    )

    $outputDirectory = Join-Path $DistDir $Flavor.ToLowerInvariant()
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

    $selfContainedArgument = if ($SelfContained) { 'true' } else { 'false' }
    $publishArguments = @(
        'publish', $ProjectFile,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', $selfContainedArgument,
        '--output', $outputDirectory,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:PublishTrimmed=false',
        '-p:PublishReadyToRun=false',
        '-p:UseAppHost=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )

    Write-Host "`nPublishing $Flavor build..." -ForegroundColor Cyan
    & $dotnetCommand.Source @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for the $Flavor build (exit code $LASTEXITCODE)."
    }

    $executable = Join-Path $outputDirectory 'Interstellar.exe'
    if (-not (Test-Path -LiteralPath $executable)) {
        throw "Publish completed but Interstellar.exe was not found at: $executable"
    }

    $sizeMB = [Math]::Round((Get-Item -LiteralPath $executable).Length / 1MB, 1)
    Write-Host "Created: $executable ($sizeMB MB)" -ForegroundColor Green

    $runtimeNote = if ($SelfContained) {
        'This build includes the .NET runtime. Microsoft Edge WebView2 Runtime is still required.'
    } else {
        'This build requires the .NET 8 Desktop Runtime and Microsoft Edge WebView2 Runtime.'
    }
    @(
        'Interstellar Windows x64 release',
        "Build type: $Flavor",
        "Executable: Interstellar.exe",
        $runtimeNote,
        'Configuration is stored under the current Windows user profile in %AppData%\OverlayAI.'
    ) | Set-Content -LiteralPath (Join-Path $outputDirectory 'README.txt') -Encoding UTF8
}

New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

switch ($Mode) {
    'Portable' { Publish-Interstellar -Flavor 'Portable' -SelfContained $true }
    'Small'    { Publish-Interstellar -Flavor 'Small' -SelfContained $false }
    'Both' {
        Publish-Interstellar -Flavor 'Small' -SelfContained $false
        Publish-Interstellar -Flavor 'Portable' -SelfContained $true
    }
}

Write-Host "`nFinished. Look in: $DistDir" -ForegroundColor Green
