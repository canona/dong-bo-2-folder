# FolderSync - build ban phat hanh tren may Windows (PowerShell)
# Chay tu thu muc goc cua solution:
#   powershell -ExecutionPolicy Bypass -File .\packaging\build-release.ps1
#   tuy chon: -IsccPath "D:\Tools\InnoSetup6\ISCC.exe"   (neu Inno Setup cai o noi khac thuong le)
#             -PackagesDir "D:\nuget-packages"            (o C: day: luu goi NuGet / runtime sang o khac)
# Ket qua trong thu muc dist\ :
#   dist\FolderSync-Setup-<phien ban>.exe  - bo cai (neu tim thay Inno Setup 6)
#   dist\FolderSync-portable.zip           - ban portable (co portable.flag, giai nen la chay, de vao USB)
#   dist\installer\                        - ban publish dung lam dau vao cho bo cai
# Ca 2 ban deu self-contained: kem .NET runtime + SQLite native, may dich KHONG can cai .NET.
param(
    [string]$IsccPath = "",
    [string]$PackagesDir = ""
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

if ($PackagesDir) {
    # Goi runtime win-x64 (~200 MB) mac dinh tai ve %USERPROFILE%\.nuget (o C:) - doi sang o khac khi C: chat
    New-Item -ItemType Directory -Force $PackagesDir | Out-Null
    $env:NUGET_PACKAGES = $PackagesDir
    Write-Host "Goi NuGet luu tai: $PackagesDir"
}

# Sinh file icon .ico tu ban base64 (repo chi luu .b64 vi cong day code chi nhan file text)
$icoB64 = Join-Path $root 'src\FolderSync.App\Assets\foldersync.ico.b64'
$ico = Join-Path $root 'src\FolderSync.App\Assets\foldersync.ico'
if (Test-Path $icoB64) {
    [IO.File]::WriteAllBytes($ico, [Convert]::FromBase64String((Get-Content $icoB64 -Raw)))
    Write-Host "Da sinh icon: $ico"
}

# Self-contained + single file: kem runtime; thu vien native (e_sqlite3.dll) tu giai nen khi chay
dotnet publish (Join-Path $root 'src\FolderSync.App\FolderSync.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None `
    -o (Join-Path $dist 'installer')
if ($LASTEXITCODE -ne 0) { throw "dotnet publish that bai (ma $LASTEXITCODE)" }

# Ban portable = cung mot ban publish + co file portable.flag (state nam canh .exe)
Copy-Item (Join-Path $dist 'installer') (Join-Path $dist 'portable') -Recurse
New-Item -ItemType File -Path (Join-Path $dist 'portable\portable.flag') -Force | Out-Null
# Thu lai vai lan: antivirus (Windows Defender) hay giu file .exe vua chep de quet -> "file dang bi dung"
for ($try = 1; $try -le 5; $try++) {
    try {
        Compress-Archive -Path (Join-Path $dist 'portable\*') -DestinationPath (Join-Path $dist 'FolderSync-portable.zip') -Force
        break
    } catch {
        if ($try -eq 5) { throw }
        Write-Host "File dang bi khoa (co the antivirus dang quet) - thu lai sau 3 giay ($try/5)..."
        Start-Sleep -Seconds 3
    }
}

# Bo cai .exe bang Inno Setup 6 (neu co)
# @(...) bao ngoai: chi 1 ket qua thi Where-Object tra ve CHUOI, $candidates[0] se la ky tu dau ("D")
$candidates = @(@(
    $IsccPath,
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) })
if ($candidates) {
    & $candidates[0] /Q (Join-Path $PSScriptRoot 'FolderSync.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup bien dich that bai (ma $LASTEXITCODE)" }
    Write-Host "Xong. Bo cai: $(Get-ChildItem $dist -Filter 'FolderSync-Setup-*.exe' | Select-Object -ExpandProperty FullName)"
} else {
    Write-Host "Khong tim thay Inno Setup 6 (ISCC.exe) - bo qua buoc tao bo cai."
    Write-Host "Cai Inno Setup 6 roi chay lai, hoac truyen -IsccPath <duong dan ISCC.exe>."
}
Write-Host "Portable: dist\FolderSync-portable.zip"
