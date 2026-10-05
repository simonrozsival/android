# Local Android ELF composite ReadyToRun experiment

This is an opt-in, arm64-only experiment for [#10802](https://github.com/dotnet/android/issues/10802),
not a supported deployment option. Normal PE, noncomposite, and ReadyToRun-disabled builds
are unchanged.

Use a matching local dotnet/runtime build with ELF-enabled crossgen2. The compiler must
support `--obj-format:elf` and emit
both `lib<assembly>.r2r.so.o` and its `.o.ld` RVA linker script. The object is linked
with the Android NDK arm64 API wrapper, without relaxation, ICF, or section GC.
The final ELF shared library must have a zero-based load segment, executable code,
SONAME `lib<assembly>.r2r.so`, and an exported `RTR_HEADER`. The forwarding component
assemblies remain PE and name the final shared library, not the intermediate object.
Both maximum and common linker page sizes are 16 KiB. This aligns load segments
and the end of RELRO; setting maximum page size alone is insufficient with older
NDK linkers. The Android 64-bit native host and generated app libraries use the
same policy. The pair script requests 16-KiB APK alignment explicitly.
The compiler infers the final owner from the object filename: `.so.o` drops only
the final `.o`, while an ordinary `.o` extension is replaced by `.so`. No explicit
composite-owner command-line argument is required.
Native owners without requested symbols are stripped using the existing native
linker policy (remove debug and unneeded symbols) before publication. The linked
unstripped image remains in `R2R/` for diagnostics; the published owner is in
`R2R/stripped/`. `AndroidIncludeDebugSymbols=true` or `StripSymbols=false` retains
the unstripped published image. The strip step is incremental and does not relink.

The Android host loads native images through its normal APK/extracted-native-library
search paths, validates their mapped header and section ranges, and retains each
handle for the process lifetime. Its runtime contract returns the ELF load bias and
maximum `PT_LOAD` virtual end. CoreCLR serializes composite loading and caches
native images by owner name; the Android callback does not maintain a second cache.
Failures are explicitly logged; the runtime does not
silently fall back to JIT when a platform-native composite cannot be opened.
The existing uncompressed assembly-store `malloc`/`memcpy` workaround and compressed
assembly handling are deliberately unchanged.

## Building the pair

Prepare and build this Android worktree, then use its local workload:

```sh
make prepare CONFIGURATION=Release
make all CONFIGURATION=Release
```

Set `RUNTIME` to the modified runtime repository and `CROSSGEN2` to its runnable
host crossgen2 executable. `_CLRLocalRuntimePath` replaces the runtime pack with
`$RUNTIME/artifacts/bin/microsoft.netcore.app.runtime.android-arm64/Release`.
Both variants must use this same runtime, compiler, and Android host. Use separate
intermediate and output directories so one envelope cannot reuse the other's
incremental compilation outputs:

```sh
export DOTNET_CLI_USE_MSBUILD_SERVER=0
./dotnet-local.sh build tests/Performance/ReadyToRunEnvelope/Simple/ReadyToRunEnvelope.csproj \
  -c Release -t:SignAndroidPackage \
  -p:_CLRLocalRuntimePath="$RUNTIME" \
  -p:_AndroidReadyToRunCrossgen2="$CROSSGEN2" \
  -p:_AndroidReadyToRunElf=false \
  -p:BaseIntermediateOutputPath=obj/pe/ -p:BaseOutputPath=bin/pe/

./dotnet-local.sh build tests/Performance/ReadyToRunEnvelope/Simple/ReadyToRunEnvelope.csproj \
  -c Release -t:SignAndroidPackage \
  -p:_CLRLocalRuntimePath="$RUNTIME" \
  -p:_AndroidReadyToRunCrossgen2="$CROSSGEN2" \
  -p:_AndroidReadyToRunElf=true \
  -p:BaseIntermediateOutputPath=obj/elf/ -p:BaseOutputPath=bin/elf/
```

The private `_AndroidReadyToRunElf` switch is blank/off by default. It requires
CoreCLR, composite ReadyToRun, `android-arm64`, and explicit matching local runtime
and compiler paths. Separately compiled composite exclusions are not supported
by this experiment and fail explicitly. `_AndroidReadyToRunCrossgen2` overrides
the compiler for **both** PE and ELF; `_AndroidReadyToRunCrossgen2Host` can supply
a matching dotnet host when invoking a managed compiler DLL instead of an executable.

The simple app uses package `com.github.copilot.r2renvelope.cc24e062.simple` and
activity `com.github.copilot.r2renvelope.MainActivity` for both variants. It logs
`R2REnvelopeStartup: READY` and calls `ReportFullyDrawn` through the same startup
path. `MainActivity.CreateStartupText` is a non-inlined method suitable for
separate R2R-entrypoint diagnostics.
The simple app uses a no-action-bar theme and applies system-bar/display-cutout
insets to its shared root layout in both envelopes. This keeps startup text and
the diagnostic finish button usable with target-35+ enforced edge-to-edge layout.

`tests/Performance/ReadyToRunEnvelope/build-pair.sh` builds the simple PE and ELF
envelopes plus the ELF diagnostic APK, recording exact commands, binlogs,
text logs, and APK hashes in a caller-supplied absolute artifact directory:

```sh
bash tests/Performance/ReadyToRunEnvelope/build-pair.sh "$RUNTIME" "$CROSSGEN2" "$ARTIFACTS"
python3 tests/Performance/ReadyToRunEnvelope/verify-pair.py \
  tests/Performance/ReadyToRunEnvelope/Simple "$ARTIFACTS"
```

Build a separate diagnostic APK with `_AndroidReadyToRunEnvelopeDiagnostic=true`
and a third intermediate/output directory. This keeps Release, trimming, R2R,
compression, and compiler settings unchanged, but enables the runtime diagnostic
component, a debuggable manifest, and startup EventPipe collection. The output is
`/data/data/com.github.copilot.r2renvelope.cc24e062.simple/files/r2r.nettrace`.
It uses provider `Microsoft-Windows-DotNETRuntime`, keyword `0x2000000000`, level 4.
Extract event 159 (`R2RGetEntryPoint`) for `MainActivity.CreateStartupText` and match
its nonzero `EntryPoint` to the ELF RX mapping. `run-as` can read this app's maps
and trace in the diagnostic build. Stop/flush tracing before reading the complete
trace; force-stopping the process is not a guaranteed flush.
The diagnostic-only "Finish diagnostic trace" button exits through
`System.Environment.Exit(0)` so the runtime can shut down tracing normally.
Before readiness it also runs non-inlined compacting-GC, generic-identity, and
throw/catch checks and requires the explicit log
`DIAGNOSTIC CHECKS PASSED GC=1024 GENERIC=ELF EH=17`. Correlate those methods'
event-159 entrypoints with their retained native symbols/runtime-function RVAs
in the diagnostic ELF. These methods and their call sites are excluded from
timing builds by `R2R_DIAGNOSTIC`.
Never use the diagnostic APK for performance timing.

## Validation and measurements

```sh
./dotnet-local.sh test \
  src/Microsoft.Android.Build.Tasks/Tests/Microsoft.Android.Build.Tasks.Tests/Microsoft.Android.Build.Tasks.Tests.csproj \
  -c Release --filter FullyQualifiedName~ReadyToRunElfTargetsTests
```

Inspect the ELF with the NDK's `llvm-readelf`: require `ET_DYN`/AArch64, a readable
zero-based load segment, an executable `PT_LOAD`, matching SONAME, and exported
`RTR_HEADER`. Check component PE headers for `PLATFORM_NATIVE_IMAGE` and the final
owner basename. Verify the APK contains the owner in `lib/arm64-v8a/` and the
components in the assembly store, without an object file or fake composite DLL.
Native image mapping alone is not proof of execution: correlate R2R entrypoint
events with executable ranges of the loaded ELF in a separate diagnostic run.
The pair verifier also requires identical trimmed compiler input DLLs and
CoreCLR/JIT/native framework libraries, and checks the actual APK's native loading
mode matches the PE control. Extracted mode permits compressed APK entries; APK-backed
mode requires an uncompressed, page-aligned owner. The generated resource designer's current
COFF timestamp and single random MVID GUID are normalized; every other byte,
including all IL, resources, and other metadata, must match.
Packaging may strip non-loadable ELF symbols;
the exported header and load layout must still match the linker output.
The verifier rejects packaged `.symtab`/`.strtab` and requires identical program
headers, allocated-section bytes/RVAs, and `PT_LOAD` payloads before/after stripping.
Only ELF-header fields describing the removed/moved section table are normalized.
It also audits **every** native library in both APKs: load alignment of at least
16 KiB, congruent load offsets/virtual addresses, a 16-KiB-aligned RELRO end,
no writable/executable load segment, and no text relocations. Every uncompressed
native ZIP entry must begin at a 16-KiB-aligned file offset; compressed entries
are permitted only with native extraction enabled. Independently run the Android
build-tools check on each APK:

```sh
zipalign -v -c -P 16 4 path/to/app.apk
```

These structural checks establish ELF/APK alignment, not complete runtime
compatibility on a 16-KiB device. That still requires execution in a real 16-KiB
environment. The historical Samsung A16 results were measured with the earlier
4-KiB composite alignment and original compiler; rebuilding with inferred owner
names and 16-KiB alignment does not retroactively update those measurements.

Use identical source, trimming, profiles/partial-compilation, compression, host,
runtime, compiler, and optimization settings for the pair. Disable noisy diagnostics
during timing. Compare fresh processes with warm filesystem caches in balanced
alternating install blocks; record thermal/battery conditions and fixed-settle
memory readings. Do not call these reboot-cold launches. Only the run-owned
benchmark packages may be installed or replaced, never personal applications.
The build and verification scripts do not install or launch applications.
