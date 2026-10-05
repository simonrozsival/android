#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
RUNTIME="${1:?Supply the matching modified dotnet/runtime repository}"
CROSSGEN2="${2:?Supply its runnable host crossgen2 executable}"
OUTPUT="${3:?Supply an absolute artifact output directory}"

if [[ "$OUTPUT" != /* ]]; then
	echo "The artifact output directory must be absolute." >&2
	exit 1
fi
if [[ ! -f "$RUNTIME/artifacts/bin/microsoft.netcore.app.runtime.android-arm64/Release/data/RuntimeList.xml" || ! -x "$CROSSGEN2" ]]; then
	echo "The complete matching Android runtime pack and executable crossgen2 are required." >&2
	exit 1
fi

export DOTNET_CLI_USE_MSBUILD_SERVER=0
mkdir -p "$OUTPUT"
cd "$ROOT"

build_variant ()
{
	local app="$1" variant="$2" elf="$3" diagnostic="$4"
	local source="$ROOT/tests/Performance/ReadyToRunEnvelope/$app"
	local project="ReadyToRunEnvelope.csproj"
	local package="com.github.copilot.r2renvelope.cc24e062.simple"
	if [[ "$app" == "Maui" ]]; then
		project="ReadyToRunEnvelope.Maui.csproj"
		package="com.github.copilot.r2renvelope.cc24e062.maui"
	fi
	local name="$app-$variant"
	local command=(
		"$ROOT/dotnet-local.sh" build "$source/$project"
		-c Release -t:SignAndroidPackage -nr:false -v minimal
		-p:_CLRLocalRuntimePath="$RUNTIME"
		-p:_DotNetRuntimeConfiguration=Release
		-p:_AndroidReadyToRunCrossgen2="$CROSSGEN2"
		-p:_AndroidReadyToRunElf="$elf"
		-p:_AndroidReadyToRunEnvelopeDiagnostic="$diagnostic"
		-p:AndroidZipAlignment=16
		-p:BaseIntermediateOutputPath="obj/$variant/"
		-p:BaseOutputPath="bin/$variant/"
		-bl:"$OUTPUT/$name.binlog"
	)
	printf '%q ' "${command[@]}" > "$OUTPUT/$name.command"
	printf '\n' >> "$OUTPUT/$name.command"
	if ! "${command[@]}" > "$OUTPUT/$name.log" 2>&1; then
		echo "Build $name failed; see $OUTPUT/$name.log" >&2
		tail -40 "$OUTPUT/$name.log" >&2
		return 1
	fi
	local apk="$source/bin/$variant/Release/net11.0-android/android-arm64/$package-Signed.apk"
	if [[ ! -f "$apk" ]]; then
		echo "The build did not produce the expected signed APK: $apk" >&2
		exit 1
	fi
	cp "$apk" "$OUTPUT/$name.apk"
	shasum -a 256 "$OUTPUT/$name.apk" > "$OUTPUT/$name.apk.sha256"
	echo "$OUTPUT/$name.apk"
}

build_variant Simple pe false false
build_variant Simple elf true false
build_variant Simple elf-diagnostic true true
build_variant Maui pe false false
build_variant Maui elf true false
