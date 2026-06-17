param (
    [ValidateSet("OpenVR", "OpenXR")]
    [string]$VrBackend = "OpenXR",

    [switch]$BIE5Only,

    [string]$GameDir = "E:\Games\Summer"
)

$ErrorActionPreference = "Stop"

# --- 游戏目录配置 ---
# 在此处配置默认游戏根目录。命令行 -GameDir 参数可覆盖此值。
$ConfiguredGameDir = "D:\RPG\summer"

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    $GameDir = $ConfiguredGameDir
}

# --- 路径计算 ---
$ProjectRoot = $PSScriptRoot
$RuntimeName = if ($BIE5Only) { "BepInEx5.Mono" } else { "BepInEx.Mono" }
$SourceDir = Join-Path $ProjectRoot "Release\$VrBackend\UnityVRMod.$RuntimeName\plugins\UnityVRMod"
$SourceDll = Join-Path $SourceDir "UnityVRMod.dll"

$DestDir = Join-Path $GameDir "GameData\BepInEx\plugins\UnityVRMod"

# --- 验证源文件 ---
if (-not (Test-Path $SourceDll)) {
    Write-Error "源 DLL 不存在: $SourceDll"
    Write-Host "请先执行构建: ./build.ps1 -VrBackend $VrBackend$(if ($BIE5Only) { ' -BIE5Only' })"
    exit 1
}

# --- 验证游戏目录 ---
if (-not (Test-Path $GameDir)) {
    Write-Error "游戏目录不存在: $GameDir"
    Write-Host "可通过 -GameDir 参数指定游戏目录"
    exit 1
}

$GameExe = Join-Path $GameDir "GameData\SummerInHeat.exe"
if (-not (Test-Path $GameExe)) {
    Write-Warning "未找到 SummerInHeat.exe，请确认游戏目录是否正确: $GameDir"
}

# --- 创建目标目录 ---
if (-not (Test-Path $DestDir)) {
    New-Item -Path $DestDir -ItemType Directory -Force | Out-Null
    Write-Host "创建目标目录: $DestDir"
}

# --- 复制 DLL ---
Copy-Item -Path $SourceDll -Destination $DestDir -Force
Write-Host "部署完成: $SourceDll -> $DestDir"
