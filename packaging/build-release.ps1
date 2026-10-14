# FolderSync - build ban phat hanh tren may Windows (PowerShell)
# Chay tu thu muc goc cua solution:  .\packaging\build-release.ps1
# Ket qua trong thu muc dist\ :
#   dist\portable\  - ban portable (co san portable.flag, giai nen la chay, de vao USB)
#   dist\FolderSync-portable.zip
#   dist\installer\ - dau vao cho Inno Setup (mo packaging\FolderSync.iss de bien dich)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

# Self-contained + single file: may dich khong can cai .NET rieng.
dotnet publish (Join-Path $root 'src\FolderSync.App\FolderSync.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $dist 'installer')

# Ban portable = cung mot ban publish + co file portable.flag (state nam canh .exe)
Copy-Item (Join-Path $dist 'installer') (Join-Path $dist 'portable') -Recurse
New-Item -ItemType File -Path (Join-Path $dist 'portable\portable.flag') -Force | Out-Null
Compress-Archive -Path (Join-Path $dist 'portable\*') -DestinationPath (Join-Path $dist 'FolderSync-portable.zip')

Write-Host "Xong. Portable: dist\FolderSync-portable.zip | Installer input: dist\installer\"
Write-Host "De tao bo cai .exe: mo packaging\FolderSync.iss bang Inno Setup 6 va bam Compile."
