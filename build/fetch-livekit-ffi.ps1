<#
.SYNOPSIS
  Downloads the LiveKit Rust FFI native library for one runtime and copies it next to a published Relay build.

.DESCRIPTION
  Livekit.Rtc.Dotnet does not ship its native core, so every published Relay build that runs the voice modes (radio,
  record, dispatch) needs livekit_ffi.dll / liblivekit_ffi.so / liblivekit_ffi.dylib beside the executable. The version
  must match the Livekit.Rtc.Dotnet package (see Resgrid.Audio.Voice.csproj) and the Dockerfile's LIVEKIT_FFI_VERSION.
  Runs on Windows, Linux and macOS runners (pwsh).

.EXAMPLE
  ./build/fetch-livekit-ffi.ps1 -Rid win-x64 -Destination ./out/desktop-win-x64
#>
param(
	[Parameter(Mandatory)] [string] $Rid,
	[Parameter(Mandatory)] [string] $Destination,
	[string] $FfiVersion = 'livekit-ffi/v0.12.65'
)

$ErrorActionPreference = 'Stop'

$map = @{
	'win-x64'     = @('ffi-windows-x86_64', 'livekit_ffi.dll')
	'win-arm64'   = @('ffi-windows-arm64',  'livekit_ffi.dll')
	'linux-x64'   = @('ffi-linux-x86_64',   'liblivekit_ffi.so')
	'linux-arm64' = @('ffi-linux-arm64',    'liblivekit_ffi.so')
	'osx-x64'     = @('ffi-macos-x86_64',   'liblivekit_ffi.dylib')
	'osx-arm64'   = @('ffi-macos-arm64',    'liblivekit_ffi.dylib')
}

if (-not $map.ContainsKey($Rid)) {
	throw "No LiveKit FFI build is mapped for runtime '$Rid'. Known: $($map.Keys -join ', ')"
}

$asset, $lib = $map[$Rid]
$encoded = $FfiVersion.Replace('/', '%2F')
$url = "https://github.com/livekit/rust-sdks/releases/download/$encoded/$asset.zip"
$work = Join-Path ([IO.Path]::GetTempPath()) "livekit-ffi-$Rid-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
	$zip = Join-Path $work "$asset.zip"
	Write-Host "Downloading $url"
	Invoke-WebRequest -Uri $url -OutFile $zip
	if (-not (Test-Path $zip) -or (Get-Item $zip).Length -eq 0) {
		throw "LiveKit FFI download failed or produced an empty archive: $url"
	}

	Expand-Archive -Path $zip -DestinationPath (Join-Path $work 'x') -Force
	$found = Get-ChildItem -Path (Join-Path $work 'x') -Recurse -Filter $lib | Select-Object -First 1
	if (-not $found) {
		throw "'$lib' was not found in $asset.zip ($url)"
	}

	New-Item -ItemType Directory -Force -Path $Destination | Out-Null
	Copy-Item $found.FullName (Join-Path $Destination $lib) -Force
	Write-Host "Copied $lib ($([math]::Round($found.Length / 1MB, 1)) MB) to $Destination"
}
finally {
	Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
