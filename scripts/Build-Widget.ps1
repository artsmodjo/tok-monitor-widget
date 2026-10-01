$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\Artsmodjo.Widget\Artsmodjo.Widget.csproj'
$localSdk = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$publish = Join-Path $root 'artifacts\publish'
$zip = Join-Path $root 'artifacts\Widget-win-x64.zip'
& $dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish --nologo /m:1 /p:UseSharedCompilation=false /p:DebugType=None /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $publish 'Artsmodjo.Widget.exe'))) { throw 'Executable missing.' }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal -Force
Get-Item -LiteralPath $zip | Select-Object FullName,Length
