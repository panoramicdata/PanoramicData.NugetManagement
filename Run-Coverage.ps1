# Runs the unit tests with code coverage and reports the headline figures.
#
# The test project is a Microsoft.Testing.Platform application
# (UseMicrosoftTestingPlatformRunner), so coverage is collected by running it directly rather than
# through `dotnet test`. That is deliberate: it works whether or not the machine's `dotnet test`
# integration does, and it is the same invocation CI uses.

param(
	# Fails the run when line coverage is below this percentage. Off by default: coverage is
	# informational while the estate climbs towards a level worth enforcing.
	[double]$MinimumLineCoverage = 0
)

$ErrorActionPreference = 'Stop'

$testProject = Join-Path $PSScriptRoot 'PanoramicData.NugetManagement.Test'
$outputDirectory = Join-Path $testProject 'bin/Debug/net10.0/TestResults'
$coverageFile = Join-Path $outputDirectory 'coverage.cobertura.xml'

dotnet build (Join-Path $testProject 'PanoramicData.NugetManagement.Test.csproj') --configuration Debug --nologo
if ($LASTEXITCODE -ne 0) {
	Write-Error 'Build failed.'
	exit 1
}

$testExecutable = Join-Path $testProject 'bin/Debug/net10.0/PanoramicData.NugetManagement.Test.exe'

& $testExecutable `
	--coverage `
	--coverage-settings (Join-Path $PSScriptRoot 'coverage.config') `
	--coverage-output-format cobertura `
	--coverage-output coverage.cobertura.xml
$testExitCode = $LASTEXITCODE

if (-not (Test-Path $coverageFile)) {
	Write-Error "No coverage file was produced at $coverageFile."
	exit 1
}

[xml]$report = Get-Content $coverageFile
$lineRate = [double]$report.coverage.'line-rate' * 100
$branchRate = [double]$report.coverage.'branch-rate' * 100

Write-Host ''
Write-Host ('Line coverage:   {0:N1}%' -f $lineRate)
Write-Host ('Branch coverage: {0:N1}%' -f $branchRate)
Write-Host ''

foreach ($package in $report.coverage.packages.package) {
	Write-Host ('  {0,-50} {1,6:N1}%' -f $package.name, ([double]$package.'line-rate' * 100))
}

Write-Host ''
Write-Host "Report: $coverageFile"

if ($MinimumLineCoverage -gt 0 -and $lineRate -lt $MinimumLineCoverage) {
	Write-Error ('Line coverage {0:N1}% is below the required {1:N1}%.' -f $lineRate, $MinimumLineCoverage)
	exit 1
}

exit $testExitCode
