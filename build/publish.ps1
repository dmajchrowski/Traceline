<#
.SYNOPSIS
  Builds Traceline into the dist folder: the web UI, the unit tests, then a self-contained Traceline.exe
  (no .NET install needed on the target machine) with LICENSE.txt and THIRD-PARTY-NOTICES.txt next to it.
  With -Installer it also builds the setup program.

.DESCRIPTION
  The version comes from Directory.Build.props unless -Version is given.

.PARAMETER Installer
  Also builds dist\Traceline-Setup-<version>.exe with Inno Setup 6 (winget install JRSoftware.InnoSetup).

.PARAMETER OtherSystems
  Also builds dist\linux-x64 and dist\osx-arm64: the same program without the Windows window, tray icon and
  picture export. These builds compile but have not been run on those systems.

.EXAMPLE
  .\build\publish.ps1
.EXAMPLE
  .\build\publish.ps1 -Installer
#>
param(
    [string]$Version = "",
    [string]$Output = (Join-Path $PSScriptRoot "..\dist"),
    [switch]$Installer,
    [switch]$OtherSystems
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version }

$mit = @"
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@ -replace "`r`n", "`n"
# For bundled packages that declare the Apache License without shipping its text.
$apache = ([IO.File]::ReadAllText((Join-Path $PSScriptRoot "licenses\Apache-2.0.txt")) -replace "`r`n", "`n").Trim()

# The exe bundles the .NET runtime and NuGet packages such as MailKit, and the web UI bundles React. Their license and
# notice texts ship next to the exe, taken from the exact versions this build restored. Call this right after the
# "dotnet publish" for $Rid: it reads the file that publish leaves behind.
function Write-ThirdPartyNotices([string]$Rid, [string]$Dir) {
    $assets = Get-Content (Join-Path $root "src\Traceline.App\obj\project.assets.json") -Raw | ConvertFrom-Json
    $packages = @($assets.packageFolders.PSObject.Properties.Name)[0]
    $sources = @()
    foreach ($fw in $assets.project.frameworks.PSObject.Properties) {
        # Only the runtimes the app uses (.NET, ASP.NET Core and, on Windows, Windows Forms).
        $refs = @($fw.Value.frameworkReferences.PSObject.Properties.Name)
        foreach ($d in $fw.Value.downloadDependencies) {
            if ($d.name -notlike "*.Runtime.$Rid") { continue }
            $base = $d.name.Substring(0, $d.name.Length - ".Runtime.$Rid".Length)
            if (-not ($refs | Where-Object { $_ -eq $base -or $_ -like "$base.*" })) { continue }
            $v = $d.version -replace '^\[([^,\]]+).*$', '$1' # "[10.0.14, 10.0.14]"
            $sources += @{ Name = "$($d.name) $v"; Dir = Join-Path $packages "$($d.name.ToLowerInvariant())/$v" }
        }
    }
    $shipped = ($assets.targets.PSObject.Properties | Where-Object Name -like "*/$Rid" | Select-Object -First 1).Value
    if (-not $shipped) { throw "project.assets.json has no target for $Rid; run this right after publishing for it." }
    foreach ($lib in $assets.libraries.PSObject.Properties | Sort-Object Name) {
        $t = $shipped.($lib.Name)
        if ($lib.Value.type -ne 'package' -or -not ($t.runtime -or $t.native -or $t.runtimeTargets)) { continue } # build-only
        $sources += @{ Name = $lib.Name -replace '/', ' '; Dir = Join-Path $packages $lib.Value.path }
    }
    # The web UI: the packages web\package.json depends on, and what those depend on. Build tools are not shipped.
    $modules = Join-Path $root "web\node_modules"
    $queue = [Collections.Generic.Queue[string]]::new()
    $seen = @{}
    foreach ($n in (Get-Content (Join-Path $root "web\package.json") -Raw | ConvertFrom-Json).dependencies.PSObject.Properties.Name) { $queue.Enqueue($n) }
    while ($queue.Count -gt 0) {
        $name = $queue.Dequeue()
        if ($seen[$name]) { continue }
        $seen[$name] = $true
        $meta = Get-Content (Join-Path $modules "$name\package.json") -Raw | ConvertFrom-Json
        $sources += @{ Name = "$name $($meta.version) (npm package in the web UI)"; Dir = Join-Path $modules $name; Npm = "$($meta.license)" }
        foreach ($set in @($meta.dependencies, $meta.peerDependencies)) {
            if ($set) { foreach ($n in $set.PSObject.Properties.Name) { $queue.Enqueue($n) } }
        }
    }

    $texts = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal) # text -> files that contain it
    foreach ($s in $sources) {
        $files = @(Get-ChildItem $s.Dir -File | Where-Object Name -match '^(license|licence|notice|third-?party-?notices)' | Sort-Object Name)
        foreach ($f in $files) {
            $text = ([IO.File]::ReadAllText($f.FullName) -replace "`r`n", "`n").Trim()
            $texts[$text] = @($texts[$text] | Where-Object { $_ }) + "$($s.Name) - $($f.Name)"
        }
        if ($files | Where-Object Name -match '^licen[sc]e') { continue }
        if ($s.Npm) {
            Write-Warning "$($s.Name): no license file and license '$($s.Npm)' - check its terms by hand."
            $text = "Licensed under $($s.Npm)."
            $texts[$text] = @($texts[$text] | Where-Object { $_ }) + "$($s.Name) - license declared in the package ($($s.Npm))"
            continue
        }
        # No license file in the package (MailKit, MimeKit, Microsoft.Extensions.*): use the license its .nuspec declares.
        $meta = ([xml](Get-Content (Get-ChildItem $s.Dir -Filter *.nuspec | Select-Object -First 1).FullName)).package.metadata
        $expression = if ($meta.license.type -eq 'expression') { $meta.license.'#text' } else { '' }
        $copyright = "Copyright (c) " + ($meta.copyright -replace "^(Copyright\s*)?($([char]0xA9)|\(c\))?\s*", '')
        $text = if ($expression -eq 'MIT') { "MIT License`n`n$copyright`n`n$mit".Trim() }
                elseif ($expression -eq 'Apache-2.0') { "$copyright`n`nLicensed under the Apache License, Version 2.0:`n`n$apache" }   # the SQLite packages
                else { Write-Warning "$($s.Name): no license file and license '$expression' - check its terms by hand."; "$copyright`n`nLicensed under $expression ($($meta.projectUrl))." }
        $texts[$text] = @($texts[$text] | Where-Object { $_ }) + "$($s.Name) - license declared in the package ($expression)"
    }
    $notices = [Text.StringBuilder]::new()
    [void]$notices.AppendLine("Traceline includes the following third-party software. Their license and notice texts follow.")
    foreach ($t in $texts.GetEnumerator()) {
        [void]$notices.AppendLine("`n$('=' * 78)`n$($t.Value -join "`n")`n$('=' * 78)`n`n$($t.Key)")
    }
    [IO.File]::WriteAllText((Join-Path $Dir "THIRD-PARTY-NOTICES.txt"), ($notices.ToString() -replace "`r?`n", "`r`n"))
    Copy-Item (Join-Path $root "LICENSE") (Join-Path $Dir "LICENSE.txt")
}

# 1. Web UI -> src\Traceline.App\wwwroot
Push-Location (Join-Path $root "web")
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "The web UI did not build" }
}
finally { Pop-Location }

# 2. Unit tests
dotnet test (Join-Path $root "tests\Traceline.Core.Tests") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }

# 3. The app: one exe plus the wwwroot folder
$app = Join-Path $Output "app"
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish (Join-Path $root "src\Traceline.App") -c Release -r win-x64 --self-contained true -o $app --nologo `
    "-p:Version=$Version" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
Write-ThirdPartyNotices "win-x64" $app
$exe = Join-Path $app "Traceline.exe"
"{0} ({1:N1} MB), version {2}" -f $exe, ((Get-Item $exe).Length / 1MB), $Version

# 4. Optional builds for other systems
if ($OtherSystems) {
    foreach ($rid in "linux-x64", "osx-arm64") {
        $dir = Join-Path $Output $rid
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        dotnet publish (Join-Path $root "src\Traceline.App") -c Release -r $rid --self-contained true -o $dir --nologo `
            "-p:Version=$Version" -p:Portable=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
        if ($LASTEXITCODE -ne 0) { throw "Publish for $rid failed" }
        Write-ThirdPartyNotices $rid $dir
        Write-Host "Published Traceline $Version for $rid to $dir"
    }
}

# 5. Optional setup program
if ($Installer) {
    $iscc = @((Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup)." }
    $out = (Resolve-Path $Output).Path
    & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$(Join-Path $out 'app')" "/O$out" (Join-Path $root "installer\Traceline.iss")
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    $setup = Join-Path $out "Traceline-Setup-$Version.exe"
    "{0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)
}
