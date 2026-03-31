<#
.SYNOPSIS
	Publishes the NuGet package to nuget.org.

.DESCRIPTION
	Packs the project in Release configuration and pushes the resulting
	.nupkg and .snupkg to nuget.org using the API key stored in nuget-key.txt.

.EXAMPLE
	.\Publish.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$keyFile = Join-Path $PSScriptRoot 'nuget-key.txt'
if (-not (Test-Path $keyFile)) {
	Write-Error "nuget-key.txt not found. Create it in the repo root with your NuGet API key."
	exit 1
}

$apiKey = (Get-Content $keyFile -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($apiKey)) {
	Write-Error "nuget-key.txt is empty."
	exit 1
}

$projectDir = Join-Path $PSScriptRoot 'PanoramicData.NugetManagement'

Write-Host "Packing..." -ForegroundColor Cyan
dotnet pack $projectDir --configuration Release --output ./artifacts
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$packages = Get-ChildItem ./artifacts -Filter '*.nupkg'
foreach ($pkg in $packages) {
	Write-Host "Pushing $($pkg.Name)..." -ForegroundColor Cyan
	dotnet nuget push $pkg.FullName --api-key $apiKey --source https://api.nuget.org/v3/index.json --skip-duplicate
	if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Done." -ForegroundColor Green
