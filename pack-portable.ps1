# ====================================================================
# QuickLook One-Click Build & Packaging Script
# ====================================================================

$MSBuildPath = "D:\software\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$BaseDir = $PSScriptRoot

if (-not (Test-Path $MSBuildPath)) {
    Write-Error "Cannot find MSBuild.exe. Current configured path: $MSBuildPath"
    exit
}

# Ensure running in project root
Set-Location $BaseDir

Write-Host "=================== Start Build and Pack Process ===================" -ForegroundColor Yellow

# 1. Build 64-bit Native DLL
Write-Host ">>> Step 1/7: Compiling 64-bit Native DLL..." -ForegroundColor Cyan
& $MSBuildPath "$BaseDir\QuickLook.Native\QuickLook.Native64\QuickLook.Native64.vcxproj" -p:Configuration=Debug -p:Platform=x64 -p:SolutionDir="$BaseDir\" -v:minimal
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to compile Native64!"; exit }

# 2. Build 32-bit Native DLL
Write-Host ">>> Step 2/7: Compiling 32-bit Native DLL..." -ForegroundColor Cyan
& $MSBuildPath "$BaseDir\QuickLook.Native\QuickLook.Native32\QuickLook.Native32.vcxproj" -p:Configuration=Debug -p:Platform=Win32 -p:SolutionDir="$BaseDir\" -v:minimal
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to compile Native32!"; exit }

# 3. Build C# Main Program
Write-Host ">>> Step 3/7: Compiling C# Main Program..." -ForegroundColor Cyan
& $MSBuildPath "$BaseDir\QuickLook\QuickLook.csproj" -t:Build -p:Configuration=Debug -p:Platform="Any CPU" -p:SolutionDir="$BaseDir\" -p:OutputPath="$BaseDir\Build\Debug\" -v:minimal
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to compile QuickLook main project!"; exit }

# 4. Build VideoViewer Plugin
Write-Host ">>> Step 4/7: Compiling VideoViewer Plugin..." -ForegroundColor Cyan
& $MSBuildPath "$BaseDir\QuickLook.Plugin\QuickLook.Plugin.VideoViewer\QuickLook.Plugin.VideoViewer.csproj" -t:Build -p:Configuration=Debug -p:Platform="Any CPU" -p:SolutionDir="$BaseDir\" -p:OutputPath="$BaseDir\Build\Debug\QuickLook.Plugin\QuickLook.Plugin.VideoViewer\" -v:minimal
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to compile VideoViewer plugin!"; exit }

# 5. Sync binary dependencies with robocopy
Write-Host ">>> Step 5/7: Synchronizing compiled files to Package directory..." -ForegroundColor Cyan
robocopy "$BaseDir\Build\Debug" "$BaseDir\Build\Package" *.* /e /njh /njs /ndl /nfl /nc /ns /np /xf *.pdb *.obj *.ipdb *.iobj *.exp *.lib *.ilk *.xml
# robocopy exit code <= 7 is successful sync
if ($LASTEXITCODE -gt 7) { Write-Error "Robocopy execution failed!"; exit }

# 6. Generate portable lock file
Write-Host ">>> Step 6/7: Writing portable lock file (portable.lock)..." -ForegroundColor Cyan
"This file makes QuickLook portable." | Out-File -FilePath "$BaseDir\Build\Package\portable.lock" -Encoding utf8

# 7. Zip package archive
Write-Host ">>> Step 7/7: Compressing into portable ZIP file..." -ForegroundColor Cyan
Set-Location "$BaseDir\Scripts"
& powershell -ExecutionPolicy Bypass -File .\pack-zip.ps1

Write-Host "=================== Packaging Completed Successfully ===================" -ForegroundColor Green
Write-Host ">>> Portable ZIP package generated in Build\ folder." -ForegroundColor Green
Set-Location $BaseDir
