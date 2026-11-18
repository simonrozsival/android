using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Xml;
using NUnit.Framework;

namespace Xamarin.Android.Build.Tests;

[TestFixture]
[Parallelizable (ParallelScope.Children)]
public class ReadyToRunElfIncrementalTests : BaseTest
{
	string directory => Path.Combine (Root, "temp", TestName);
	string output => Path.Combine (directory, "output");
	string component => Path.Combine (output, "App.dll");
	string script => Path.Combine (output, "libApp.r2r.so.o.ld");
	string settings => Path.Combine (output, "compilation.inputs");
	string compiler => Path.Combine (directory, "crossgen2");
	string host => Path.Combine (directory, "host");
	string project => Path.Combine (directory, "pipeline.proj");

	[SetUp]
	public void SetUp ()
	{
		Directory.CreateDirectory (directory);
		File.WriteAllText (compiler, "compiler-v1");
		File.WriteAllText (host, "host-v1");
		File.WriteAllText (Path.Combine (directory, "stock-compiler"), "stock");
		File.WriteAllText (Path.Combine (directory, "input.dll"), "managed input");
		var runtimeData = Path.Combine (directory, "runtime", "artifacts", "bin", "microsoft.netcore.app.runtime.android-arm64", "Release", "data");
		Directory.CreateDirectory (runtimeData);
		using (File.Create (Path.Combine (runtimeData, "RuntimeList.xml"))) { }
		var ndkHost = OperatingSystem.IsWindows () ? "windows-x86_64" : OperatingSystem.IsMacOS () ? "darwin-x86_64" : "linux-x86_64";
		var tools = Path.Combine (directory, "ndk", "toolchains", "llvm", "prebuilt", ndkHost, "bin");
		Directory.CreateDirectory (tools);
		var linker = Path.Combine (tools, "aarch64-linux-android24-clang" + (OperatingSystem.IsWindows () ? ".cmd" : ""));
		File.WriteAllText (linker, OperatingSystem.IsWindows () ? """
			@echo off
			:next
			if "%~1"=="" exit /b 1
			if "%~1"=="-o" goto output
			shift
			goto next
			:output
			> "%~2" echo native owner
			exit /b 0
			""" : """
			#!/bin/sh
			while [ "$#" -gt 0 ]; do
			  if [ "$1" = "-o" ]; then
			    printf 'native owner\n' > "$2"
			    exit 0
			  fi
			  shift
			done
			exit 1
			""");
		if (!OperatingSystem.IsWindows ()) {
			File.SetUnixFileMode (linker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		var elfTargets = TargetsPath ("Microsoft.Android.Sdk.ReadyToRun.Elf.targets");
		var compilationTargets = TargetsPath ("Microsoft.Android.Sdk.ReadyToRun.Compilation.targets");
		var xml = new XmlDocument ();
		xml.LoadXml ($$"""
			<Project>
			  <PropertyGroup>
			    <Crossgen2TasksOverriden>true</Crossgen2TasksOverriden>
			    <PublishReadyToRun>true</PublishReadyToRun>
			    <PublishReadyToRunComposite>true</PublishReadyToRunComposite>
			    <PublishReadyToRunUseCrossgen2>true</PublishReadyToRunUseCrossgen2>
			    <PublishReadyToRunContainerFormat>pe</PublishReadyToRunContainerFormat>
			    <StripSymbols>false</StripSymbols>
			    <_AndroidRuntime>CoreCLR</_AndroidRuntime>
			    <RuntimeIdentifier>android-arm64</RuntimeIdentifier>
			    <AssemblyName>App</AssemblyName>
			    <_ReadyToRunOutputPath>$(MSBuildProjectDirectory)/output</_ReadyToRunOutputPath>
			    <_AndroidReadyToRunCrossgen2>$(MSBuildProjectDirectory)/crossgen2</_AndroidReadyToRunCrossgen2>
			    <_CLRLocalRuntimePath>$(MSBuildProjectDirectory)/runtime</_CLRLocalRuntimePath>
			    <_AndroidNdkDirectory>$(MSBuildProjectDirectory)/ndk/</_AndroidNdkDirectory>
			    <AndroidNdkApiLevel_Arm64>24</AndroidNdkApiLevel_Arm64>
			    <MSBuildAllProjects>$(MSBuildProjectFullPath)</MSBuildAllProjects>
			  </PropertyGroup>
			  <UsingTask TaskName="RunReadyToRunCompiler" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
			    <ParameterGroup>
			      <CrossgenTool ParameterType="Microsoft.Build.Framework.ITaskItem[]" />
			      <Crossgen2Tool ParameterType="Microsoft.Build.Framework.ITaskItem[]" />
			      <UseCrossgen2 ParameterType="System.Boolean" />
			      <Crossgen2PgoFiles ParameterType="Microsoft.Build.Framework.ITaskItem[]" />
			      <Crossgen2ContainerFormat ParameterType="System.String" />
			      <Crossgen2ExtraCommandLineArgs ParameterType="System.String" />
			      <Crossgen2CompositeExtraCommandLineArgs ParameterType="System.String" />
			      <ImplementationAssemblyReferences ParameterType="Microsoft.Build.Framework.ITaskItem[]" />
			      <ShowCompilerWarnings ParameterType="System.Boolean" />
			      <CompilationEntry ParameterType="Microsoft.Build.Framework.ITaskItem[]" />
			      <ReadyToRunCompositeBuildReferences ParameterType="Microsoft.Build.Framework.ITaskItem[]" />
			      <ReadyToRunCompositeBuildInput ParameterType="Microsoft.Build.Framework.ITaskItem[]" />
			      <ReadyToRunCompositeUnrootedBuildInput ParameterType="Microsoft.Build.Framework.ITaskItem[]" />
			      <ExitCode ParameterType="System.Int32" Output="true" />
			      <WarningsDetected ParameterType="System.Boolean" Output="true" />
			    </ParameterGroup>
			    <Task>
			      <Using Namespace="System.IO" />
			      <Code Type="Fragment" Language="cs">
			        <![CDATA[
			        var tool = File.ReadAllText(Crossgen2Tool[0].ItemSpec);
			        if (tool == "validate-fail") {
			          Log.LogError("Compiler validation failure");
			          return false;
			        }
			        if (tool == "fail") {
			          ExitCode = 1;
			          Log.LogError("Compiler failure");
			          return false;
			        }
			        var host = Crossgen2Tool[0].GetMetadata("DotNetHostPath");
			        var marker = Crossgen2ContainerFormat + ":" + tool + ":" + (host.Length == 0 ? "" : File.ReadAllText(host));
			        foreach (var item in CompilationEntry) {
			          var output = item.GetMetadata("OutputR2RImage");
			          Directory.CreateDirectory(Path.GetDirectoryName(output));
			          File.WriteAllText(output, marker);
			          File.WriteAllText(Path.Combine(Path.GetDirectoryName(output), "App.dll"), marker);
			          if (Crossgen2ContainerFormat == "elf") File.WriteAllText(output + ".ld", "RVA script");
			        }
			        Log.LogMessage(Microsoft.Build.Framework.MessageImportance.High, "COMPILER_RUN=" + marker);
			        ExitCode = 0;
			        ]]>
			      </Code>
			    </Task>
			  </UsingTask>
			  <Import Project="$(MSBuildToolsPath)/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.CrossGen.targets" />
			  <Import Project="{{SecurityElement.Escape (elfTargets)}}" />
			  <Target Name="ResolveReadyToRunCompilers">
			    <ItemGroup>
			      <Crossgen2Tool Include="$(MSBuildProjectDirectory)/stock-compiler" TargetOS="android" TargetArch="arm64" />
			    </ItemGroup>
			  </Target>
			  <Target Name="_PrepareForReadyToRunCompilation" DependsOnTargets="ResolveReadyToRunCompilers">
			    <ItemGroup>
			      <_ReadyToRunCompileList Include="$(MSBuildProjectDirectory)/input.dll"
			          OutputR2RImage="$(_ReadyToRunOutputPath)/App.r2r.dll" CreateCompositeImage="true" />
			      <_ReadyToRunCompositeBuildInput Include="$(MSBuildProjectDirectory)/input.dll" />
			      <_ReadyToRunAssembliesToReference Include="$(ExtraReference)" Condition="'$(ExtraReference)' != ''" />
			      <_ReadyToRunFilesToPublish Include="$(_ReadyToRunOutputPath)/App.r2r.dll" RelativePath="App.r2r.dll" />
			      <_ReadyToRunFilesToPublish Include="$(_ReadyToRunOutputPath)/App.dll" RelativePath="App.dll" />
			    </ItemGroup>
			  </Target>
			  <Import Project="{{SecurityElement.Escape (compilationTargets)}}" Condition="'$(_AndroidReadyToRunCrossgen2)' != ''" />
			  <Target Name="Build" DependsOnTargets="_PrepareForReadyToRunCompilation;_CreateR2RImages">
			    <Error Condition="'@(_ReadyToRunCompilationFailures->Count())' != '0'" Text="R2R compilation failed" />
			    <Message Importance="high" Text="PUBLISH=@(_ReadyToRunFilesToPublish)" />
			    <Message Importance="high" Text="WRITES=@(FileWrites)" />
			  </Target>
			</Project>
			""");
		xml.Save (project);
	}

	[TestCase (false)]
	[TestCase (true)]
	public void UnchangedBuildSkipsCompilerAndPreservesSettings (bool elf)
	{
		StringAssert.Contains ("COMPILER_RUN=", Build (elf));
		var componentTime = File.GetLastWriteTimeUtc (component);
		var settingsTime = File.GetLastWriteTimeUtc (settings);
		StringAssert.DoesNotContain ("COMPILER_RUN=", Build (elf));
		Assert.AreEqual (componentTime, File.GetLastWriteTimeUtc (component));
		Assert.AreEqual (settingsTime, File.GetLastWriteTimeUtc (settings));
	}

	[TestCase (false)]
	[TestCase (true)]
	public void EnvelopeRoundTripRegeneratesSharedComponents (bool firstElf)
	{
		foreach (bool elf in new [] { firstElf, !firstElf, firstElf }) {
			StringAssert.Contains ("COMPILER_RUN=", Build (elf));
			Assert.AreEqual ((elf ? "elf" : "pe") + ":compiler-v1:", File.ReadAllText (component));
		}
	}

	[TestCase (false)]
	[TestCase (true)]
	public void UpdatingCompilerInPlaceRegeneratesImages (bool elf)
	{
		Build (elf);
		File.WriteAllText (compiler, "compiler-v2");
		File.SetLastWriteTimeUtc (compiler, File.GetLastWriteTimeUtc (component).AddSeconds (1));
		StringAssert.Contains ("COMPILER_RUN=", Build (elf));
		Assert.AreEqual ((elf ? "elf" : "pe") + ":compiler-v2:", File.ReadAllText (component));
	}

	[TestCase (false)]
	[TestCase (true)]
	public void SelectingAnOlderCompilerRegeneratesImages (bool elf)
	{
		Build (elf);
		var olderCompiler = Path.Combine (directory, "older-crossgen2");
		File.WriteAllText (olderCompiler, "older-compiler");
		File.SetLastWriteTimeUtc (olderCompiler, new DateTime (2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		StringAssert.Contains ("COMPILER_RUN=", Build (elf, true, "-p:_AndroidReadyToRunCrossgen2=" + olderCompiler));
		Assert.AreEqual ((elf ? "elf" : "pe") + ":older-compiler:", File.ReadAllText (component));
	}

	[Test]
	public void UpdatingHostInPlaceRegeneratesImages ()
	{
		Build (true, true, "-p:_AndroidReadyToRunCrossgen2Host=" + host);
		File.WriteAllText (host, "host-v2");
		File.SetLastWriteTimeUtc (host, File.GetLastWriteTimeUtc (component).AddSeconds (1));
		StringAssert.Contains ("COMPILER_RUN=", Build (true, true, "-p:_AndroidReadyToRunCrossgen2Host=" + host));
		Assert.AreEqual ("elf:compiler-v1:host-v2", File.ReadAllText (component));
	}

	[Test]
	public void SelectingAnOlderHostRegeneratesImages ()
	{
		Build (true, true, "-p:_AndroidReadyToRunCrossgen2Host=" + host);
		var olderHost = Path.Combine (directory, "older-host");
		File.WriteAllText (olderHost, "older-host");
		File.SetLastWriteTimeUtc (olderHost, new DateTime (2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		StringAssert.Contains ("COMPILER_RUN=", Build (true, true, "-p:_AndroidReadyToRunCrossgen2Host=" + olderHost));
		Assert.AreEqual ("elf:compiler-v1:older-host", File.ReadAllText (component));
	}

	[Test]
	public void ChangedCompilerArgumentsRegenerateButUnchangedArgumentsSkip ()
	{
		Build (true);
		var argument = "-p:PublishReadyToRunCrossgen2CompositeExtraArgs=--strip-inlining-info";
		StringAssert.Contains ("COMPILER_RUN=", Build (true, true, argument));
		StringAssert.DoesNotContain ("COMPILER_RUN=", Build (true, true, argument));
	}

	[Test]
	public void StockBuildWithoutPriorExperimentKeepsSdkBehavior ()
	{
		var argument = "-p:_AndroidReadyToRunCrossgen2=";
		StringAssert.Contains ("COMPILER_RUN=", Build (false, true, argument));
		Assert.AreEqual ("pe:stock:", File.ReadAllText (component));
		Assert.IsFalse (File.Exists (settings));
		StringAssert.DoesNotContain ("COMPILER_RUN=", Build (false, true, argument));
	}

	[Test]
	public void ReferenceMembershipChangesRegenerateImages ()
	{
		Build (true);
		var reference = Path.Combine (directory, "older-reference.dll");
		File.WriteAllText (reference, "reference");
		File.SetLastWriteTimeUtc (reference, new DateTime (2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		StringAssert.Contains ("COMPILER_RUN=", Build (true, true, "-p:ExtraReference=" + reference));
		StringAssert.Contains ("COMPILER_RUN=", Build (true));
		StringAssert.DoesNotContain ("COMPILER_RUN=", Build (true));
	}

	[TestCase (false)]
	[TestCase (true)]
	public void ReturningToStockCompilerInvalidatesPriorComponents (bool firstElf)
	{
		Build (firstElf);
		StringAssert.Contains ("COMPILER_RUN=", Build (false, true, "-p:_AndroidReadyToRunCrossgen2="));
		Assert.AreEqual ("pe:stock:", File.ReadAllText (component));
		StringAssert.DoesNotContain ("COMPILER_RUN=", Build (false, true, "-p:_AndroidReadyToRunCrossgen2="));
	}

	[Test]
	public void MissingRvaScriptRegeneratesObjectAndScript ()
	{
		Build (true);
		File.Delete (script);
		var log = Build (true);
		StringAssert.Contains ("COMPILER_RUN=", log);
		Assert.IsTrue (File.Exists (script));
		StringAssert.Contains (script, log);
	}

	[TestCase (false)]
	[TestCase (true)]
	public void MissingComponentRegeneratesImages (bool elf)
	{
		Build (elf);
		File.Delete (component);
		StringAssert.Contains ("COMPILER_RUN=", Build (elf));
		Assert.IsTrue (File.Exists (component));
	}

	[Test]
	public void MissingNativeLibraryRelinksWithoutRecompiling ()
	{
		Build (true);
		var library = Path.Combine (output, "libApp.r2r.so");
		File.Delete (library);
		StringAssert.DoesNotContain ("COMPILER_RUN=", Build (true));
		Assert.IsTrue (File.Exists (library));
	}

	[TestCase ("fail", "Compiler failure")]
	[TestCase ("validate-fail", "Compiler validation failure")]
	public void CompilationFailureDoesNotLinkStaleObject (string failure, string message)
	{
		Build (true);
		var library = Path.Combine (output, "libApp.r2r.so");
		var libraryTime = File.GetLastWriteTimeUtc (library);
		File.WriteAllText (compiler, failure);
		File.SetLastWriteTimeUtc (compiler, File.GetLastWriteTimeUtc (component).AddSeconds (1));
		StringAssert.Contains (message, Build (true, false));
		Assert.AreEqual (libraryTime, File.GetLastWriteTimeUtc (library));
	}

	static string TargetsPath (string name)
	{
		var repository = new DirectoryInfo (AppContext.BaseDirectory);
		while (repository != null && !File.Exists (Path.Combine (repository.FullName, "Configuration.props"))) {
			repository = repository.Parent;
		}
		var root = repository?.FullName ?? throw new InvalidOperationException ("Could not locate repository.");
		return Path.Combine (root, "src", "Xamarin.Android.Build.Tasks", "Microsoft.Android.Sdk", "targets", name);
	}

	string Build (bool elf, bool expectSuccess = true, params string [] arguments)
	{
		var start = new ProcessStartInfo ("dotnet") {
			WorkingDirectory = directory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		start.Environment ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
		foreach (var argument in new [] { "msbuild", project, "-t:Build", "-nologo", "-v:minimal", "-nr:false", "-p:_AndroidReadyToRunElf=" + elf.ToString ().ToLowerInvariant () }) {
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
		var log = stdout.GetAwaiter ().GetResult () + stderr.GetAwaiter ().GetResult ();
		Assert.IsTrue (completed, log);
		Assert.AreEqual (expectSuccess, process.ExitCode == 0, log);
		return log;
	}
}
