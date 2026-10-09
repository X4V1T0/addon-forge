#!/usr/bin/env pwsh
# Publishes AddonForge.Web into ./dist for the Dockerfile to package.
# -r linux-musl-x64 matters even though this isn't self-contained: it makes
# `dotnet publish` fetch the musl-linked SQLite native library that the
# Alpine-based runtime image needs (glibc builds won't load there).
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$distPath = Join-Path $root "dist"

if (Test-Path $distPath) {
    Remove-Item $distPath -Recurse -Force
}

dotnet publish (Join-Path $root "src/AddonForge.Web") `
    -c $Configuration `
    -r linux-musl-x64 `
    --self-contained false `
    -o $distPath

Write-Host "Published to $distPath. Run 'docker build .' to package the addon image, or bump config.yaml's version first."
