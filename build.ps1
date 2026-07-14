param([switch]$SkipDownload)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
$vendor = Join-Path $dist 'third_party\everything'
$downloads = Join-Path $root '.build'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$windowsBase = Join-Path (Split-Path $csc) 'WPF\WindowsBase.dll'
$presentationCore = Join-Path (Split-Path $csc) 'WPF\PresentationCore.dll'
$systemXaml = Join-Path (Split-Path $csc) 'System.Xaml.dll'

if (-not (Test-Path -LiteralPath $csc)) { throw "C# compiler not found: $csc" }
New-Item -ItemType Directory -Force -Path $vendor, $downloads | Out-Null

function Get-VerifiedFile($url, $path, $sha256) {
    if (Test-Path -LiteralPath $path) {
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
        if ($actual -eq $sha256) { return }
        Remove-Item -LiteralPath $path -Force
    }
    if ($SkipDownload) { throw "Missing dependency: $path" }
    $temporary = "$path.download"
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $temporary
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $temporary).Hash
    if ($actual -ne $sha256) {
        Remove-Item -LiteralPath $temporary -Force
        throw "SHA256 mismatch for $url"
    }
    Move-Item -LiteralPath $temporary -Destination $path
}

$everythingZip = Join-Path $downloads 'Everything-1.4.1.1032.x64.zip'
$esZip = Join-Path $downloads 'ES-1.1.0.30.x64.zip'
Get-VerifiedFile 'https://www.voidtools.com/Everything-1.4.1.1032.x64.zip' $everythingZip '698DF475EC44E638F66F1B6A32D28FEA613CEC78D3B6310E6ABE53431EEB940C'
Get-VerifiedFile 'https://www.voidtools.com/ES-1.1.0.30.x64.zip' $esZip '30147FEADAE528D4BBFB3BCB4597A4C7D9F52A0F9F708EA6577B6028BD8DD268'

$extract = Join-Path $downloads 'extract'
if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $extract 'everything'), (Join-Path $extract 'es') | Out-Null
Expand-Archive -LiteralPath $everythingZip -DestinationPath (Join-Path $extract 'everything')
Expand-Archive -LiteralPath $esZip -DestinationPath (Join-Path $extract 'es')
Copy-Item -LiteralPath (Join-Path $extract 'everything\Everything.exe') -Destination $vendor -Force
Copy-Item -LiteralPath (Join-Path $extract 'es\es.exe') -Destination $vendor -Force
$language = Join-Path $extract 'everything\Everything.lng'
if (Test-Path -LiteralPath $language) { Copy-Item -LiteralPath $language -Destination $vendor -Force }
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.txt') -Destination (Join-Path $vendor 'LICENSE.txt') -Force

$exe = Join-Path $dist 'Matches.exe'
$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Filter '*.cs' | Sort-Object FullName | ForEach-Object FullName
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /out:$exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:$windowsBase /reference:$presentationCore /reference:$systemXaml $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }

$check = Start-Process -FilePath $exe -ArgumentList '--self-test' -Wait -PassThru -WindowStyle Hidden
if ($check.ExitCode -ne 0) { throw 'Self-test failed.' }
Write-Host "Built and checked: $exe"
