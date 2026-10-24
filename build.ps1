param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$bundledDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $bundledDotnet) { $bundledDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.packages'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $projectRoot
try {
    & $dotnetExe build CursePlus.slnx -c Release -m:1 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação.' }
    & $dotnetExe 'tests\CursePlus.Tests\bin\Release\net10.0\CursePlus.Tests.dll'
    if ($LASTEXITCODE -ne 0) { throw 'Falha nos testes.' }
    if ($Publish) {
        & $dotnetExe publish 'src\CursePlus.Desktop\CursePlus.Desktop.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o 'dist\CursePlus' --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o aplicativo portátil.' }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $projectRoot 'dist\CursePlus\LEIA-ME.md') -Force
        Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $projectRoot 'dist\CursePlus\THIRD-PARTY-NOTICES.md') -Force
        Copy-Item -LiteralPath (Join-Path $projectRoot 'VALIDATION.md') -Destination (Join-Path $projectRoot 'dist\CursePlus\VALIDATION.md') -Force
        $licensesRoot = Join-Path $projectRoot 'dist\CursePlus\licenses'
        New-Item -ItemType Directory -Force -Path $licensesRoot | Out-Null
        $packagesRoot = Join-Path $projectRoot '.packages'
        foreach ($packageName in @('flaui.core', 'flaui.uia3', 'interop.uiautomationclient', 'microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64', 'system.drawing.common', 'system.management', 'system.security.permissions', 'system.diagnostics.performancecounter', 'microsoft.win32.registry')) {
            $packageRoot = Join-Path $packagesRoot $packageName
            if (Test-Path -LiteralPath $packageRoot) {
                $packageVersion = Get-ChildItem -LiteralPath $packageRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
                foreach ($notice in (Get-ChildItem -LiteralPath $packageVersion.FullName -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' })) {
                    Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licensesRoot ($packageName + '-' + $notice.Name)) -Force
                }
            }
        }
        Compress-Archive -LiteralPath (Join-Path $projectRoot 'dist\CursePlus') -DestinationPath (Join-Path $projectRoot 'dist\CursePlus-win-x64.zip') -Force
    }
} finally { Pop-Location }
