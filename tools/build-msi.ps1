param(
    [string]$DotNet = 'dotnet.exe',
    [string]$Wix = 'wix.exe',
    [string]$LocalNuGetSource
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src\MONY\MONY.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$work = Join-Path $root ('.work\build-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'publish'
$artifacts = Join-Path $work 'artifacts'
$name = "MONY-$version-x64.msi"

if ([IO.Path]::IsPathRooted($DotNet)) {
    $env:DOTNET_ROOT = Split-Path $DotNet
}

New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $pawnIoInstaller = Join-Path $root 'installer\vendor\PawnIO_setup.exe'
    if (!(Test-Path -LiteralPath $pawnIoInstaller -PathType Leaf)) {
        $pawnIoInstaller = Join-Path $work 'PawnIO_setup.exe'
        $pawnIoRelease = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
        Invoke-WebRequest -UseBasicParsing -Uri $pawnIoRelease -OutFile $pawnIoInstaller
    }
    $pawnIoHash = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
    if ((Get-FileHash -LiteralPath $pawnIoInstaller -Algorithm SHA256).Hash -ne $pawnIoHash) {
        throw 'The PawnIO installer does not match the official 2.2.0 release.'
    }

    $publishArgs = @('publish', $project, '-c', 'Release', '-p:Platform=x64', '-p:PublishSingleFile=true',
        '-p:IncludeAllContentForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=embedded', '--artifacts-path', $artifacts, '-o', $payload, '--nologo')
    if ($LocalNuGetSource) {
        $publishArgs += "-p:RestoreAdditionalProjectSources=$LocalNuGetSource"
    }
    & $DotNet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

    $assetsPath = Join-Path $artifacts 'obj\MONY\project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $packagePaths = @($assets.libraries.PSObject.Properties | ForEach-Object { $_.Value.path })
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        foreach ($dependency in $framework.Value.downloadDependencies) {
            $packageVersion = ($dependency.version.Trim('[', ']') -split ',')[0].Trim()
            $packagePaths += ('{0}/{1}' -f $dependency.name.ToLowerInvariant(), $packageVersion)
        }
    }
    $noticeText = [Text.StringBuilder]::new()
    $hardwareNotice = Join-Path $root 'installer\licenses\HardwareSensors.txt'
    [void]$noticeText.AppendLine([IO.File]::ReadAllText($hardwareNotice))
    [void]$noticeText.AppendLine([IO.File]::ReadAllText((Join-Path $root 'installer\licenses\PawnIO.txt')))
    $seen = [Collections.Generic.HashSet[string]]::new()
    foreach ($relativePath in ($packagePaths | Sort-Object -Unique)) {
        if ($relativePath -notmatch '^(microsoft\.(windowsappsdk[./]|web\.webview2/|windows\.ai\.machinelearning/|(netcore|aspnetcore|windowsdesktop)\.app\.runtime\.)|system\.(numerics\.tensors|management|io\.ports|io\.filesystem\.accesscontrol|threading\.accesscontrol)/|hidsharp/)') {
            continue
        }
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $package = Join-Path $folder $relativePath
            if (!(Test-Path -LiteralPath $package)) { continue }
            foreach ($file in (Get-ChildItem -LiteralPath $package -File | Where-Object Name -Match '^(licen[cs]e(\.txt)?|(?:third[- ]?party[- ]?)?notices?\.txt)$')) {
                $hash = (Get-FileHash -LiteralPath $file.FullName).Hash
                if ($seen.Add($hash)) {
                    [void]$noticeText.AppendLine("=== $relativePath / $($file.Name) ===")
                    [void]$noticeText.AppendLine([IO.File]::ReadAllText($file.FullName))
                }
            }
            break
        }
    }
    if ($seen.Count -eq 0) { throw 'No dependency license notices found.' }
    [IO.File]::WriteAllText((Join-Path $payload 'ThirdPartyNotices.txt'), $noticeText.ToString())

    $msi = Join-Path $work $name
    & $Wix build (Join-Path $root 'installer\Package.wxs') -arch x64 -d "ProjectRoot=$root" `
        -d "AppVersion=$version" -d "PayloadDir=$payload" -d "PawnIoInstaller=$pawnIoInstaller" -intermediateFolder (Join-Path $work 'wix') `
        -pdbtype none -o $msi
    if ($LASTEXITCODE -ne 0) { throw "WiX build failed ($LASTEXITCODE)" }
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    Copy-Item -LiteralPath $msi -Destination (Join-Path $dist $name) -Force
    Write-Output (Join-Path $dist $name)
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
