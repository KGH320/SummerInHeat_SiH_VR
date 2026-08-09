param (
    [ValidateSet("OpenVR", "OpenXR")]
    [string]$VrBackend,
    [switch]$MonoOnly,
    [switch]$Il2CppOnly,
    [switch]$BIE5Only,
    [switch]$DebugBuild,
    [switch]$DebugHelper,
    [switch]$SkipUGH,
    [switch]$PhysicsLog,
    [switch]$Deploy,
    [string]$GameDir
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

function Invoke-CheckedCommand {
    param (
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    & $FilePath @ArgumentList
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }
}

function Resolve-Vs2026MsBuildPath {
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $vswherePath = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
        return $null
    }

    $installationPaths = & $vswherePath -latest -products * -version "[18.0,19.0)" -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    $installationPath = @($installationPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($installationPath)) {
        return $null
    }

    $candidate = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        return $null
    }

    foreach ($platform in @("Win32", "x64")) {
        $toolsetProps = Join-Path $installationPath "MSBuild\Microsoft\VC\v180\Platforms\$platform\PlatformToolsets\v145\Toolset.props"
        if (-not (Test-Path -LiteralPath $toolsetProps -PathType Leaf)) {
            return $null
        }
    }

    $versionOutput = & $candidate -nologo -version
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    $versionMatch = [regex]::Match(($versionOutput -join "`n"), '(?m)^\s*(?<version>\d+\.\d+(?:\.\d+){0,2})\s*$')
    if (-not $versionMatch.Success) {
        return $null
    }

    $msbuildVersion = [Version]$versionMatch.Groups["version"].Value
    if ($msbuildVersion.Major -ne 18) {
        return $null
    }

    return $candidate
}

function Assert-RequiredFile {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description not found: $Path"
    }
}

function Assert-RequiredDirectory {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description not found: $Path"
    }
}

function Copy-RequiredFile {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Source,
        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    Assert-RequiredFile -Path $Source -Description "Required source file"
    $destinationDirectory = Split-Path -Parent $Destination
    if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
        New-Item -Path $destinationDirectory -ItemType Directory -Force | Out-Null
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    Assert-RequiredFile -Path $Destination -Description "Copied file"
}

function Copy-RequiredDirectory {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Source,
        [Parameter(Mandatory = $true)]
        [string]$DestinationParent
    )

    Assert-RequiredDirectory -Path $Source -Description "Required source directory"
    New-Item -Path $DestinationParent -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $DestinationParent -Recurse -Force
    $copiedDirectory = Join-Path $DestinationParent (Split-Path -Leaf $Source)
    Assert-RequiredDirectory -Path $copiedDirectory -Description "Copied directory"
}

function Assert-MatchingSha256 {
    param (
        [Parameter(Mandatory = $true)]
        [string]$ReferencePath,
        [Parameter(Mandatory = $true)]
        [string[]]$CandidatePaths,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    Assert-RequiredFile -Path $ReferencePath -Description "$Description reference"
    $referenceHash = (Get-FileHash -LiteralPath $ReferencePath -Algorithm SHA256).Hash
    foreach ($candidatePath in $CandidatePaths) {
        Assert-RequiredFile -Path $candidatePath -Description "$Description candidate"
        $candidateHash = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash
        if ($candidateHash -ne $referenceHash) {
            throw "$Description SHA-256 mismatch: '$candidatePath' is $candidateHash, expected $referenceHash from '$ReferencePath'."
        }
    }

    Write-Host "  Verified $Description SHA-256: $referenceHash"
}

# --- Configuration ---
$YourModName = "UnityVRMod"
$SolutionFile = "src/UnityVRMod.sln"
$UniverseLibSln = "UniverseLib/src/UniverseLib.sln"
$NativeHelperProjectSolution = "UnityGraphicsHelper/UnityGraphicsHelper.sln"
$NativeHelperDllBuildOutputBase = "UnityGraphicsHelper/x64"
$NativeHelperDllName = "UnityGraphicsHelper.dll"
$LibDir = "lib"
$OpenXrHandModelsDir = "OpenXRHandModels"
$OpenXrShadersDir = "OpenXRShaders"
$UniverseLibMonoDllPath = "UniverseLib/Release/UniverseLib.Mono/UniverseLib.Mono.dll"
$UniverseLibIl2CppDllPath = "UniverseLib/Release/UniverseLib.Il2Cpp.Interop/UniverseLib.BIE.IL2CPP.Interop.dll"

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCommand) {
    throw "dotnet SDK not found. Install .NET SDK 8 or newer and retry."
}
$DotNetExe = $dotnetCommand.Source

# --- Determine Build Suffixes ---
$ConfigBuildSuffix = if ($DebugBuild.IsPresent) { "Debug" } else { "Release" }
$OutputFolderSuffix = if ($DebugBuild.IsPresent) { ".Debug" } else { "" }
Write-Host "Selected overall build type: $ConfigBuildSuffix"
if ($PhysicsLog.IsPresent) {
    Write-Host "Physics diagnostics compile flag enabled: PHYSICS_LOG"
}

# --- Base Definitions for Runtimes ---
$MonoBaseDefinition = @{
    TargetRuntimeName = "Mono"
    ConfigNamePattern = "BIE_Unity_Mono_{0}_{1}"
    AssemblyNamePattern = "$($YourModName).BepInEx.Mono_{0}"
    OutputPathPattern = "Release/{0}/$($YourModName).BepInEx.Mono"
    UniverseLibDllPath = $UniverseLibMonoDllPath
}
$Il2CppBaseDefinition = @{
    TargetRuntimeName = "IL2CPP"
    ConfigNamePattern = "BIE_Unity_Cpp_{0}_{1}"
    AssemblyNamePattern = "$($YourModName).BepInEx.IL2CPP_{0}"
    OutputPathPattern = "Release/{0}/$($YourModName).BepInEx.IL2CPP"
    UniverseLibDllPath = $UniverseLibIl2CppDllPath
}
$BIE5MonoBaseDefinition = @{
    TargetRuntimeName = "BIE5-Mono"
    ConfigNamePattern = "BIE5_Unity_Mono_{0}_{1}"
    AssemblyNamePattern = "$($YourModName).BepInEx5.Mono_{0}"
    OutputPathPattern = "Release/{0}/$($YourModName).BepInEx5.Mono"
    UniverseLibDllPath = $UniverseLibMonoDllPath
}

$VrBackendsToProcess = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($VrBackend)) {
    $VrBackendsToProcess.Add($VrBackend)
    Write-Host "Specific VR Backend selected: $VrBackend"
} else {
    Write-Host "No specific VR Backend selected. Processing OpenVR and OpenXR."
    $VrBackendsToProcess.Add("OpenVR")
    $VrBackendsToProcess.Add("OpenXR")
}

$RuntimeDefinitionsToConsider = [System.Collections.Generic.List[System.Collections.Hashtable]]::new()
if ($BIE5Only.IsPresent) {
    $RuntimeDefinitionsToConsider.Add($BIE5MonoBaseDefinition)
} elseif ($MonoOnly.IsPresent) {
    $RuntimeDefinitionsToConsider.Add($MonoBaseDefinition)
    $RuntimeDefinitionsToConsider.Add($BIE5MonoBaseDefinition)
} elseif ($Il2CppOnly.IsPresent) {
    $RuntimeDefinitionsToConsider.Add($Il2CppBaseDefinition)
} else {
    $RuntimeDefinitionsToConsider.Add($MonoBaseDefinition)
    $RuntimeDefinitionsToConsider.Add($Il2CppBaseDefinition)
    $RuntimeDefinitionsToConsider.Add($BIE5MonoBaseDefinition)
}

$targetsToBuildActual = [System.Collections.Generic.List[System.Collections.Hashtable]]::new()
foreach ($currentProcessingVrBackend in $VrBackendsToProcess) {
    foreach ($baseDef in $RuntimeDefinitionsToConsider) {
        $currentTargetDef = $baseDef.Clone()
        $currentTargetDef.VrBackend = $currentProcessingVrBackend
        $currentTargetDef.FullConfigName = $baseDef.ConfigNamePattern -f $currentProcessingVrBackend, $ConfigBuildSuffix
        $currentTargetDef.EffectiveAssemblyName = $baseDef.AssemblyNamePattern -f $currentProcessingVrBackend
        $currentTargetDef.EffectiveOutputPath = ($baseDef.OutputPathPattern -f $currentProcessingVrBackend) + $OutputFolderSuffix
        $targetsToBuildActual.Add($currentTargetDef)
    }
}

if ($targetsToBuildActual.Count -eq 0) {
    throw "No build targets were finalized."
}

# --- Build Native C++ Helper for OpenXR ---
$OpenXrRequested = $VrBackendsToProcess.Contains("OpenXR")
$NativeHelperReferencePath = $null
$LibNativeHelperPath = Join-Path $LibDir $NativeHelperDllName
if ($OpenXrRequested) {
    if ($SkipUGH.IsPresent) {
        Write-Host "Skipping $NativeHelperDllName rebuild; using existing staged helper."
        Assert-RequiredFile -Path $LibNativeHelperPath -Description "Staged native helper"
        $NativeHelperReferencePath = $LibNativeHelperPath
    } else {
        Assert-RequiredFile -Path $NativeHelperProjectSolution -Description "Native helper solution"
        $MsBuildExe = Resolve-Vs2026MsBuildPath
        if ([string]::IsNullOrWhiteSpace($MsBuildExe)) {
            throw "OpenXR builds require Visual Studio 2026/MSBuild 18 with the v145 C++ toolset for Win32 and x64. Install the C++ desktop workload and retry."
        }

        Write-Host "Using Visual Studio 2026 MSBuild: $MsBuildExe"
        $CppBuildConfig = if ($DebugHelper.IsPresent) { "Debug" } else { "Release" }
        Write-Host "Rebuilding $NativeHelperDllName ($CppBuildConfig|x64)..."
        Invoke-CheckedCommand -FilePath $MsBuildExe -ArgumentList @(
            $NativeHelperProjectSolution,
            "/m",
            "/t:Rebuild",
            "/p:Configuration=$CppBuildConfig",
            "/p:Platform=x64"
        ) -Description "Native C++ helper rebuild"

        $NativeHelperReferencePath = Join-Path (Join-Path $NativeHelperDllBuildOutputBase $CppBuildConfig) $NativeHelperDllName
        Assert-RequiredFile -Path $NativeHelperReferencePath -Description "Native helper build output"
        Copy-RequiredFile -Source $NativeHelperReferencePath -Destination $LibNativeHelperPath
        Assert-MatchingSha256 -ReferencePath $NativeHelperReferencePath -CandidatePaths @($LibNativeHelperPath) -Description "native helper staging"
    }
}

# --- Build Required UniverseLib Variants ---
Assert-RequiredFile -Path $UniverseLibSln -Description "UniverseLib solution"
$MonoUniverseLibRequired = @($targetsToBuildActual | Where-Object { $_.UniverseLibDllPath -eq $UniverseLibMonoDllPath }).Count -gt 0
$Il2CppUniverseLibRequired = @($targetsToBuildActual | Where-Object { $_.UniverseLibDllPath -eq $UniverseLibIl2CppDllPath }).Count -gt 0

if ($MonoUniverseLibRequired -and -not (Test-Path -LiteralPath $UniverseLibMonoDllPath -PathType Leaf)) {
    Write-Host "Building UniverseLib Mono..."
    Invoke-CheckedCommand -FilePath $DotNetExe -ArgumentList @("build", $UniverseLibSln, "-c", "Release_Mono") -Description "UniverseLib Mono build"
}
if ($Il2CppUniverseLibRequired -and -not (Test-Path -LiteralPath $UniverseLibIl2CppDllPath -PathType Leaf)) {
    Write-Host "Building UniverseLib IL2CPP interop..."
    Invoke-CheckedCommand -FilePath $DotNetExe -ArgumentList @("build", $UniverseLibSln, "-c", "Release_IL2CPP_Interop_BIE") -Description "UniverseLib IL2CPP build"
}
if ($MonoUniverseLibRequired) {
    Assert-RequiredFile -Path $UniverseLibMonoDllPath -Description "UniverseLib Mono output"
}
if ($Il2CppUniverseLibRequired) {
    Assert-RequiredFile -Path $UniverseLibIl2CppDllPath -Description "UniverseLib IL2CPP output"
}

# --- Build and Package Selected Targets ---
foreach ($currentTargetDef in $targetsToBuildActual) {
    $ConfigToUse = $currentTargetDef.FullConfigName
    $CurrentAssemblyName = $currentTargetDef.EffectiveAssemblyName
    $CurrentOutputPath = $currentTargetDef.EffectiveOutputPath
    $FinalDllName = "$CurrentAssemblyName.dll"

    Write-Host ""
    Write-Host "Building $($currentTargetDef.TargetRuntimeName) ($ConfigToUse)..."
    Write-Host "  Output: $CurrentOutputPath"
    New-Item -Path $CurrentOutputPath -ItemType Directory -Force | Out-Null

    $DotNetBuildArgs = @("build", $SolutionFile, "-c", $ConfigToUse)
    if ($PhysicsLog.IsPresent) {
        $DotNetBuildArgs += "/p:PhysicsLog=true"
    }
    Invoke-CheckedCommand -FilePath $DotNetExe -ArgumentList $DotNetBuildArgs -Description "Managed build '$ConfigToUse'"

    $OutputDllPath = Join-Path $CurrentOutputPath $FinalDllName
    Assert-RequiredFile -Path $OutputDllPath -Description "Managed build output"

    $FinalPluginSubDir = Join-Path $CurrentOutputPath "plugins\$YourModName"
    New-Item -Path $FinalPluginSubDir -ItemType Directory -Force | Out-Null
    Move-Item -LiteralPath $OutputDllPath -Destination (Join-Path $FinalPluginSubDir "$YourModName.dll") -Force

    $universeLibFileName = Split-Path -Leaf $currentTargetDef.UniverseLibDllPath
    Copy-RequiredFile -Source $currentTargetDef.UniverseLibDllPath -Destination (Join-Path $CurrentOutputPath $universeLibFileName)
    Copy-RequiredFile -Source $currentTargetDef.UniverseLibDllPath -Destination (Join-Path $FinalPluginSubDir $universeLibFileName)

    if ($currentTargetDef.VrBackend -eq "OpenVR") {
        $openVrSource = Join-Path $LibDir "openvr_api.dll"
        Copy-RequiredFile -Source $openVrSource -Destination (Join-Path $CurrentOutputPath "openvr_api.dll")
        Copy-RequiredFile -Source $openVrSource -Destination (Join-Path $FinalPluginSubDir "openvr_api.dll")
    } elseif ($currentTargetDef.VrBackend -eq "OpenXR") {
        $openXrLoaderSource = Join-Path $LibDir "openxr_loader.dll"
        Copy-RequiredFile -Source $openXrLoaderSource -Destination (Join-Path $CurrentOutputPath "openxr_loader.dll")
        Copy-RequiredFile -Source $openXrLoaderSource -Destination (Join-Path $FinalPluginSubDir "openxr_loader.dll")
        Copy-RequiredFile -Source $LibNativeHelperPath -Destination (Join-Path $CurrentOutputPath $NativeHelperDllName)
        Copy-RequiredFile -Source $LibNativeHelperPath -Destination (Join-Path $FinalPluginSubDir $NativeHelperDllName)

        Copy-RequiredDirectory -Source (Join-Path $PSScriptRoot $OpenXrHandModelsDir) -DestinationParent $FinalPluginSubDir
        Copy-RequiredDirectory -Source (Join-Path $PSScriptRoot $OpenXrShadersDir) -DestinationParent $FinalPluginSubDir

        Assert-MatchingSha256 -ReferencePath $NativeHelperReferencePath -CandidatePaths @(
            $LibNativeHelperPath,
            (Join-Path $CurrentOutputPath $NativeHelperDllName),
            (Join-Path $FinalPluginSubDir $NativeHelperDllName)
        ) -Description "$ConfigToUse native helper package"
    }

    Write-Host "  Packaged: $FinalPluginSubDir"
    Write-Host "--------------------------------------------------"
}

Write-Host "Build and package completed successfully."

# --- Deploy ---
if ($Deploy.IsPresent) {
    $DeployGameDir = if (-not [string]::IsNullOrWhiteSpace($GameDir)) { $GameDir } else { "D:\RPG\summer" }
    Assert-RequiredDirectory -Path $DeployGameDir -Description "Game directory"

    $DeployVrBackend = if ([string]::IsNullOrWhiteSpace($VrBackend)) { "OpenXR" } else { $VrBackend }
    $DeployRuntimeName = if ($BIE5Only.IsPresent) {
        "BIE5-Mono"
    } elseif ($Il2CppOnly.IsPresent) {
        "IL2CPP"
    } else {
        "Mono"
    }

    $DeployTarget = $targetsToBuildActual |
        Where-Object { $_.VrBackend -eq $DeployVrBackend -and $_.TargetRuntimeName -eq $DeployRuntimeName } |
        Select-Object -First 1
    if (-not $DeployTarget) {
        throw "No built target matches deployment selection '$DeployVrBackend/$DeployRuntimeName'."
    }

    $SourceOutputDir = $DeployTarget.EffectiveOutputPath
    $SourcePluginDir = Join-Path $SourceOutputDir "plugins\$YourModName"
    $DestinationBepInExDir = Join-Path $DeployGameDir "GameData\BepInEx"
    $DestinationPluginDir = Join-Path $DestinationBepInExDir "plugins\$YourModName"
    New-Item -Path $DestinationPluginDir -ItemType Directory -Force | Out-Null

    Copy-RequiredFile -Source (Join-Path $SourcePluginDir "$YourModName.dll") -Destination (Join-Path $DestinationPluginDir "$YourModName.dll")

    $deployUniverseLibName = Split-Path -Leaf $DeployTarget.UniverseLibDllPath
    Copy-RequiredFile -Source (Join-Path $SourceOutputDir $deployUniverseLibName) -Destination (Join-Path $DestinationBepInExDir $deployUniverseLibName)
    Copy-RequiredFile -Source (Join-Path $SourcePluginDir $deployUniverseLibName) -Destination (Join-Path $DestinationPluginDir $deployUniverseLibName)

    if ($DeployVrBackend -eq "OpenXR") {
        foreach ($nativeFileName in @("openxr_loader.dll", $NativeHelperDllName)) {
            Copy-RequiredFile -Source (Join-Path $SourceOutputDir $nativeFileName) -Destination (Join-Path $DestinationBepInExDir $nativeFileName)
            Copy-RequiredFile -Source (Join-Path $SourcePluginDir $nativeFileName) -Destination (Join-Path $DestinationPluginDir $nativeFileName)
        }
        Copy-RequiredDirectory -Source (Join-Path $SourcePluginDir $OpenXrHandModelsDir) -DestinationParent $DestinationPluginDir
        Copy-RequiredDirectory -Source (Join-Path $SourcePluginDir $OpenXrShadersDir) -DestinationParent $DestinationPluginDir

        Assert-MatchingSha256 -ReferencePath $NativeHelperReferencePath -CandidatePaths @(
            (Join-Path $DestinationBepInExDir $NativeHelperDllName),
            (Join-Path $DestinationPluginDir $NativeHelperDllName)
        ) -Description "deployed native helper"
    } else {
        Copy-RequiredFile -Source (Join-Path $SourceOutputDir "openvr_api.dll") -Destination (Join-Path $DestinationBepInExDir "openvr_api.dll")
        Copy-RequiredFile -Source (Join-Path $SourcePluginDir "openvr_api.dll") -Destination (Join-Path $DestinationPluginDir "openvr_api.dll")
    }

    Write-Host "Deployment completed: $SourceOutputDir -> $DestinationBepInExDir"
}
