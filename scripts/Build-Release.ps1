param(
    [string]$Version = '0.9.5',
    [string]$OutputRoot,
    [string]$Publisher = 'CN=Snap Workspace Development',
    [string]$PfxPath,
    [string]$PfxPassword,
    [switch]$RequireMsix
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\release-$Version"
}
$outputFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
$repoFullPath = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
if (-not $outputFullPath.StartsWith($repoFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must stay inside the repository.'
}
if (Test-Path -LiteralPath $outputFullPath) {
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}
New-Item -ItemType Directory -Path $outputFullPath -Force | Out-Null

$dotnet = if (Test-Path -LiteralPath 'D:\software\dotnet-sdk-10.0.302\dotnet.exe') {
    'D:\software\dotnet-sdk-10.0.302\dotnet.exe'
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}
$project = Join-Path $repoRoot 'SnapWorkspace.App\SnapWorkspace.App.csproj'
$portableDir = Join-Path $outputFullPath 'portable-framework-dependent'
$standaloneDir = Join-Path $outputFullPath 'portable-self-contained'
$msixPublishDir = Join-Path $outputFullPath 'msix-publish'
$versionParts = ($Version -split '\.') + @('0','0','0','0')
$fourPartVersion = $versionParts[0..3] -join '.'

& $dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:Version=$Version -p:FileVersion=$fourPartVersion -p:AssemblyVersion=$fourPartVersion `
    -p:DebugType=None -p:DebugSymbols=false -o $portableDir
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent publish failed.' }

& $dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:FileVersion=$fourPartVersion -p:AssemblyVersion=$fourPartVersion `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $standaloneDir
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

foreach ($directory in @($portableDir, $standaloneDir)) {
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $directory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $directory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'CONTRIBUTING.md') -Destination $directory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'SECURITY.md') -Destination $directory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $directory -Recurse
    $releaseNotes = Join-Path $repoRoot "docs\RELEASE_NOTES_$Version.md"
    if (Test-Path -LiteralPath $releaseNotes) {
        Copy-Item -LiteralPath $releaseNotes -Destination $directory
    }
}

$portableZip = Join-Path $outputFullPath "SnapWorkspace-$Version-win-x64-portable.zip"
$standaloneZip = Join-Path $outputFullPath "SnapWorkspace-$Version-win-x64-self-contained.zip"
Compress-Archive -Path (Join-Path $portableDir '*') -DestinationPath $portableZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $standaloneDir '*') -DestinationPath $standaloneZip -CompressionLevel Optimal

& $dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:FileVersion=$fourPartVersion -p:AssemblyVersion=$fourPartVersion `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $msixPublishDir
if ($LASTEXITCODE -ne 0) { throw 'MSIX staging publish failed.' }

$windowsKits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppx = if (Test-Path -LiteralPath $windowsKits) {
    Get-ChildItem -LiteralPath $windowsKits -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -Match '\\x64\\makeappx\.exe$' |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
$signTool = if (Test-Path -LiteralPath $windowsKits) {
    Get-ChildItem -LiteralPath $windowsKits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -Match '\\x64\\signtool\.exe$' |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

$msixPath = Join-Path $outputFullPath "SnapWorkspace-$Version-win-x64.msix"
if ($makeAppx) {
    $staging = Join-Path $outputFullPath 'msix-staging'
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Copy-Item -Path (Join-Path $msixPublishDir '*') -Destination $staging -Recurse
    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\msix\Assets') -Destination $staging -Recurse
    $packageVersion = $fourPartVersion
    $manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'packaging\msix\AppxManifest.xml.template') -Raw
    $manifest = $manifest.Replace('__PUBLISHER__', $Publisher)
    $manifest = $manifest.Replace('__VERSION__', $packageVersion)
    Set-Content -LiteralPath (Join-Path $staging 'AppxManifest.xml') -Value $manifest -Encoding utf8NoBOM
    & $makeAppx pack /o /h SHA256 /d $staging /p $msixPath
    if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed.' }

    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        if (-not $signTool) { throw 'SignTool was not found in the Windows SDK.' }
        if (-not (Test-Path -LiteralPath $PfxPath)) { throw "PFX not found: $PfxPath" }
        $signArguments = @('sign', '/fd', 'SHA256', '/a', '/f', $PfxPath)
        if (-not [string]::IsNullOrEmpty($PfxPassword)) { $signArguments += @('/p', $PfxPassword) }
        $signArguments += $msixPath
        & $signTool @signArguments
        if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }
    } else {
        Write-Warning 'MSIX was created unsigned. It is for CI validation only until a trusted signing certificate is configured.'
    }
} elseif ($RequireMsix) {
    throw 'MakeAppx was not found. Install the Windows SDK or run on windows-latest.'
} else {
    Write-Warning 'Windows SDK not found; portable packages were built and MSIX creation was skipped.'
}

foreach ($temporaryDirectory in @($portableDir, $standaloneDir, $msixPublishDir, (Join-Path $outputFullPath 'msix-staging'))) {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

$releaseFiles = Get-ChildItem -LiteralPath $outputFullPath -File |
    Where-Object Extension -In '.zip','.msix'
$checksums = foreach ($file in $releaseFiles) {
    $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
    "$($hash.Hash)  $($file.Name)"
}
$checksums | Set-Content -LiteralPath (Join-Path $outputFullPath 'SHA256SUMS.txt') -Encoding ascii

Write-Output "Release artifacts: $outputFullPath"
Get-ChildItem -LiteralPath $outputFullPath -File | Select-Object Name,Length
