param(
	[string[]]$RuntimeIdentifiers = @(),
	[string]$Configuration = "Release",
	[switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$project = Join-Path $repoRoot "Gif320Sharp/Gif320Sharp.csproj"
$outputRoot = Join-Path $repoRoot "artifacts/native-aot"

if ($RuntimeIdentifiers.Count -eq 0) {
	$arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
		"Arm64" { "arm64"; break }
		"X64" { "x64"; break }
		default { throw "Unsupported architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
	}

	if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
		$RuntimeIdentifiers = @("win-$arch")
	}
	elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
		$RuntimeIdentifiers = @("linux-$arch")
	}
	elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
		$RuntimeIdentifiers = @("osx-$arch")
	}
	else {
		throw "Unsupported OS for Native AOT publish."
	}
}

foreach ($rid in $RuntimeIdentifiers) {
	$output = Join-Path $outputRoot $rid
	$args = @(
		"publish",
		$project,
		"-c",
		$Configuration,
		"-r",
		$rid,
		"-p:PublishAot=true",
		"-p:SelfContained=true",
		"-p:StripSymbols=true",
		"-o",
		$output
	)

	if ($NoRestore) {
		$args += "--no-restore"
	}

	Write-Host "Publishing gif320sharp Native AOT for $rid..."
	& dotnet @args
	if ($LASTEXITCODE -ne 0) {
		throw "Native AOT publish failed for $rid. Install the RID's native compiler toolchain and runtime packs, or build that RID on its native OS runner."
	}
}
