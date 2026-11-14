<#
.SYNOPSIS
    Publishes the Natter CLI as a self-contained single-file executable per runtime.
.EXAMPLE
    ./scripts/publish.ps1 -RuntimeIdentifiers win-x64,linux-x64,osx-arm64
#>
param(
    [string[]]$RuntimeIdentifiers = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"),
    [string]$Configuration = "Release",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $root "artifacts/cli"
}

foreach ($rid in $RuntimeIdentifiers) {
    Write-Host "==> publishing Natter CLI for $rid ($Configuration)"
    dotnet publish (Join-Path $root "src/Natter.Cli/Natter.Cli.csproj") `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=embedded `
        -o (Join-Path $OutputRoot $rid)

    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host ""
Write-Host "Published to $OutputRoot"
