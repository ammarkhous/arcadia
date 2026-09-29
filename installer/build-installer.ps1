# Builds dist\Arcadia-Setup-<version>-x64.exe: a self-contained Release publish (no .NET install needed) wrapped by Inno Setup 6,
# plus a SHA-256 checksum file next to it.   .\build-installer.ps1            → 1.0.0
#                                              .\build-installer.ps1 -Suffix rc.1 → 1.0.0-rc.1
param([string]$Suffix = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$prefix = (Select-String -Path "$root\Directory.Build.props" -Pattern '<VersionPrefix>([^<]+)</VersionPrefix>' | Select-Object -First 1).Matches[0].Groups[1].Value
if (-not $prefix) { throw 'VersionPrefix not found in Directory.Build.props' }
$version = if ($Suffix) { "$prefix-$Suffix" } else { $prefix }
$app = Join-Path $root 'dist\app'
if ([IO.Directory]::Exists($app)) { [IO.Directory]::Delete($app, $true) }   # stale publish output only
$publish = @("$root\src\Arcadia.App", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishReadyToRun=true', '-p:DebugType=none', '-o', $app)
if ($Suffix) { $publish += "-p:VersionSuffix=$Suffix" }
dotnet publish @publish
if ($LASTEXITCODE) { throw 'publish failed' }
# release hygiene: no debug symbols, no development settings in the output
$bad = Get-ChildItem $app -Recurse -Include *.pdb, settings.json, secrets.dat, *.db
if ($bad) { throw "unexpected files in the publish output: $($bad.Name -join ', ')" }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
& $iscc "/DAppVersion=$version" "/DAppVersionNumeric=$prefix.0" "/DSourceDir=$app" "$PSScriptRoot\Arcadia.iss"
if ($LASTEXITCODE) { throw 'installer build failed' }
$setup = Get-Item "$root\dist\Arcadia-Setup-$version-x64.exe"
$hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$($setup.FullName).sha256" -Value "$hash  $($setup.Name)" -Encoding ascii
$setup
"SHA-256 $hash"
