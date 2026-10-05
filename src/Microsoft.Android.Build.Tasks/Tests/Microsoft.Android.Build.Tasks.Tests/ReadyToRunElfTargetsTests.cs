using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Xml;
using NUnit.Framework;

namespace Xamarin.Android.Build.Tests;

[TestFixture]
[Parallelizable (ParallelScope.Children)]
public class ReadyToRunElfTargetsTests : BaseTest
{
	string directory => Path.Combine (Root, "temp", TestName);

	[TestCase ("")]
	[TestCase ("false")]
	public void PeCompilationAndPublicationAreUnchanged (string enabled)
	{
		var output = Build (enabled);
		StringAssert.Contains ("FORMAT=pe", output);
		StringAssert.Contains ("COMPILE=obj/R2R/App.r2r.dll", output);
		StringAssert.Contains ("PUBLISH=obj/R2R/App.r2r.dll|App.r2r.dll", output);
	}

	[Test]
	public void ElfPublishesInferredNativeOwnerAndPeComponents ()
	{
		var output = Build ("true");
		StringAssert.Contains ("FORMAT=elf", output);
		StringAssert.Contains ("COMPILE=obj/R2R/libApp.r2r.so.o", output);
		StringAssert.Contains ("PUBLISH=obj/R2R/stripped/libApp.r2r.so|libApp.r2r.so|android-arm64|native", output);
		StringAssert.Contains ("PUBLISH=obj/R2R/App.dll|App.dll", output);
		StringAssert.Contains ("WRITES=obj/R2R/libApp.r2r.so.o", output);
		StringAssert.Contains ("WRITES=obj/R2R/libApp.r2r.so.o.ld", output);
		StringAssert.DoesNotContain ("PUBLISH=obj/R2R/App.r2r.dll", output);
	}

	[TestCase ("false")]
	[TestCase ("true")]
	public void CompositeArgumentsArePreserved (string enabled)
	{
		var output = Build (enabled, true, "-p:PublishReadyToRunCrossgen2CompositeExtraArgs=--strip-inlining-info");
		StringAssert.Contains ("ARGS=--strip-inlining-info" + Environment.NewLine, output);
	}

	[TestCase ("-p:AndroidIncludeDebugSymbols=true")]
	[TestCase ("-p:StripSymbols=false")]
	public void ExplicitNativeSymbolsPublishUnstrippedOwner (string argument)
	{
		var output = Build ("true", true, argument);
		StringAssert.Contains ("PUBLISH=obj/R2R/libApp.r2r.so|libApp.r2r.so|android-arm64|native", output);
		StringAssert.DoesNotContain ("PUBLISH=obj/R2R/stripped/", output);
	}

	[Test]
	public void LocalCompilerOverrideAppliesToPeControl ()
	{
		var output = Build ("false");
		StringAssert.Contains ("COMPILER=" + Path.Combine (directory, "crossgen2"), output);
		StringAssert.Contains ("TARGET=android|arm64", output);
	}

	[Test]
	public void DefaultCompilerRemainsUnchanged ()
	{
		StringAssert.Contains ("COMPILER=stock-crossgen2", Build ("false", true, "-p:_AndroidReadyToRunCrossgen2=", "-p:_CLRLocalRuntimePath="));
	}

	[Test]
	public void MissingLocalRuntimeCannotSilentlyUseStockRuntime ()
	{
		StringAssert.Contains ("Refusing to use the stock runtime", Build ("false", false, "-p:_CLRLocalRuntimePath=missing-runtime"));
	}

	[TestCase ("-p:PublishReadyToRunComposite=false", "requires CoreCLR composite")]
	[TestCase ("-p:RuntimeIdentifier=android-x64", "requires CoreCLR composite")]
	[TestCase ("-p:_AndroidRuntime=NativeAOT", "requires CoreCLR composite")]
	[TestCase ("-p:_CLRLocalRuntimePath=", "matching local runtime")]
	[TestCase ("-p:SeparateCompilation=true", "no separately compiled exclusions")]
	public void UnsupportedElfConfigurationsFailExplicitly (string argument, string message)
	{
		StringAssert.Contains (message, Build ("true", false, argument));
	}

	[Test]
	public void ReadyToRunDisabledDoesNotChangeInputs ()
	{
		var output = Build ("true", true, "-p:PublishReadyToRun=false", "-p:PublishReadyToRunComposite=false");
		StringAssert.Contains ("FORMAT=pe", output);
		StringAssert.Contains ("COMPILE=obj/R2R/App.r2r.dll", output);
	}

	[Test]
	public void NativeLinkUsesSixteenKiBPagesWithoutCodeLayoutTransforms ()
	{
		var targets = new XmlDocument ();
		targets.Load (TargetsPath ());
		var link = targets.SelectSingleNode ("/Project/Target[@Name='_AndroidLinkReadyToRunElf']/Exec")
			?? throw new InvalidOperationException ("Could not locate ELF linker command.");
		var command = link.Attributes? ["Command"]?.Value
			?? throw new InvalidOperationException ("ELF linker command is missing.");
		StringAssert.Contains ("-Wl,-z,max-page-size=16384", command);
		StringAssert.Contains ("-Wl,-z,common-page-size=16384", command);
		StringAssert.Contains ("-Wl,--no-relax", command);
		StringAssert.Contains ("-Wl,--no-undefined", command);
		StringAssert.Contains ("-Wl,--icf=none", command);
		StringAssert.DoesNotContain ("--gc-sections", command);
		StringAssert.DoesNotContain ("4096", command);
	}

	static string TargetsPath ()
	{
		var repository = new DirectoryInfo (AppContext.BaseDirectory);
		while (repository != null && !File.Exists (Path.Combine (repository.FullName, "Configuration.props"))) {
			repository = repository.Parent;
		}
		var root = repository?.FullName ?? throw new InvalidOperationException ("Could not locate repository.");
		return Path.Combine (root, "src", "Xamarin.Android.Build.Tasks", "Microsoft.Android.Sdk", "targets", "Microsoft.Android.Sdk.ReadyToRun.Elf.targets");
	}

	string Build (string enabled, bool expectSuccess = true, params string [] arguments)
	{
		Directory.CreateDirectory (directory);
		var targets = TargetsPath ();
		var compiler = Path.Combine (directory, "crossgen2");
		using (File.Create (compiler)) { }
		var ndk = Path.Combine (directory, "ndk") + Path.DirectorySeparatorChar;
		var runtime = Path.Combine (directory, "local-runtime");
		var runtimeData = Path.Combine (runtime, "artifacts", "bin", "microsoft.netcore.app.runtime.android-arm64", "Release", "data");
		Directory.CreateDirectory (runtimeData);
		using (File.Create (Path.Combine (runtimeData, "RuntimeList.xml"))) { }
		var x64RuntimeData = Path.Combine (runtime, "artifacts", "bin", "microsoft.netcore.app.runtime.android-x64", "Release", "data");
		Directory.CreateDirectory (x64RuntimeData);
		using (File.Create (Path.Combine (x64RuntimeData, "RuntimeList.xml"))) { }
		var host = OperatingSystem.IsWindows () ? "windows-x86_64" : OperatingSystem.IsMacOS () ? "darwin-x86_64" : "linux-x86_64";
		var linker = Path.Combine (ndk, "toolchains", "llvm", "prebuilt", host, "bin", "aarch64-linux-android24-clang" + (OperatingSystem.IsWindows () ? ".cmd" : ""));
		var linkerDirectory = Path.GetDirectoryName (linker) ?? throw new InvalidOperationException ("Missing linker directory.");
		Directory.CreateDirectory (linkerDirectory);
		using (File.Create (linker)) { }
		using (File.Create (Path.Combine (linkerDirectory, "llvm-strip" + (OperatingSystem.IsWindows () ? ".exe" : "")))) { }
		var project = Path.Combine (directory, "pipeline.proj");
		var xml = new XmlDocument ();
		xml.LoadXml ($"""
			<Project>
			  <PropertyGroup>
			    <PublishReadyToRun>true</PublishReadyToRun>
			    <PublishReadyToRunComposite>true</PublishReadyToRunComposite>
			    <PublishReadyToRunContainerFormat>pe</PublishReadyToRunContainerFormat>
			    <_AndroidRuntime>CoreCLR</_AndroidRuntime>
			    <RuntimeIdentifier>android-arm64</RuntimeIdentifier>
			    <AssemblyName>App</AssemblyName>
			    <_ReadyToRunOutputPath>obj/R2R</_ReadyToRunOutputPath>
			    <_AndroidReadyToRunCrossgen2>{SecurityElement.Escape (compiler)}</_AndroidReadyToRunCrossgen2>
			    <_CLRLocalRuntimePath>{SecurityElement.Escape (runtime)}</_CLRLocalRuntimePath>
			    <_AndroidNdkDirectory>{SecurityElement.Escape (ndk)}</_AndroidNdkDirectory>
			    <AndroidNdkApiLevel_Arm64>24</AndroidNdkApiLevel_Arm64>
			  </PropertyGroup>
			  <Import Project="{SecurityElement.Escape (targets)}" />
			  <Target Name="ResolveReadyToRunCompilers">
			    <ItemGroup>
			      <Crossgen2Tool Include="stock-crossgen2" TargetOS="android" TargetArch="arm64" />
			    </ItemGroup>
			  </Target>
			  <Target Name="_PrepareForReadyToRunCompilation">
			    <ItemGroup>
			      <_ReadyToRunCompileList Include="App.dll" OutputR2RImage="obj/R2R/App.r2r.dll" CreateCompositeImage="true" />
			      <_ReadyToRunCompileList Include="Other.dll" OutputR2RImage="obj/R2R/Other.dll" Condition="'$(SeparateCompilation)' == 'true'" />
			      <_ReadyToRunFilesToPublish Include="obj/R2R/App.r2r.dll" RelativePath="App.r2r.dll" />
			      <_ReadyToRunFilesToPublish Include="obj/R2R/App.dll" RelativePath="App.dll" />
			    </ItemGroup>
			  </Target>
			  <Target Name="Build" DependsOnTargets="ResolveReadyToRunCompilers;_PrepareForReadyToRunCompilation">
			    <Message Importance="high" Text="FORMAT=$(PublishReadyToRunContainerFormat)" />
			    <Message Importance="high" Text="COMPILE=%(_ReadyToRunCompileList.OutputR2RImage)" />
			    <Message Importance="high" Text="PUBLISH=%(_ReadyToRunFilesToPublish.Identity)|%(_ReadyToRunFilesToPublish.RelativePath)|%(_ReadyToRunFilesToPublish.RuntimeIdentifier)|%(_ReadyToRunFilesToPublish.AssetType)" />
			    <Message Importance="high" Text="ARGS=$(PublishReadyToRunCrossgen2CompositeExtraArgs)" />
			    <Message Importance="high" Text="WRITES=%(FileWrites.Identity)" />
			    <Message Importance="high" Text="COMPILER=%(Crossgen2Tool.Identity)" />
			    <Message Importance="high" Text="TARGET=%(Crossgen2Tool.TargetOS)|%(Crossgen2Tool.TargetArch)" />
			  </Target>
			</Project>
			""");
		xml.Save (project);
		var start = new ProcessStartInfo ("dotnet") {
			WorkingDirectory = directory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (var argument in new [] { "msbuild", project, "-t:Build", "-nologo", "-v:minimal", "-nr:false", "-p:_AndroidReadyToRunElf=" + enabled }) {
			start.ArgumentList.Add (argument);
		}
		foreach (var argument in arguments) {
			start.ArgumentList.Add (argument);
		}
		using var process = Process.Start (start) ?? throw new InvalidOperationException ("Could not start MSBuild.");
		var stdout = process.StandardOutput.ReadToEndAsync ();
		var stderr = process.StandardError.ReadToEndAsync ();
		bool completed = process.WaitForExit (120000);
		if (!completed) {
			process.Kill (entireProcessTree: true);
			process.WaitForExit ();
		}
		var output = stdout.GetAwaiter ().GetResult () + stderr.GetAwaiter ().GetResult ();
		Assert.IsTrue (completed, output);
		Assert.AreEqual (expectSuccess, process.ExitCode == 0, output);
		return output;
	}
}
