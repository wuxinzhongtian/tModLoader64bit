using log4net.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Mdb;
using Mono.Cecil.Pdb;
using ReLogic.OS;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Terraria.Localization;
using Terraria.ModLoader.Exceptions;
using Terraria.ModLoader.UI;
using Terraria.Utilities;

namespace Terraria.ModLoader.Core
{
	// TODO further documentation
	// TODO too many inner classes
	internal class ModCompile
	{
		public interface IBuildStatus
		{
			void SetProgress(int i, int n = -1);
			void SetStatus(string msg);
			void LogCompilerLine(string msg, Level level);
		}

		private class ConsoleBuildStatus : IBuildStatus
		{
			public void SetProgress(int i, int n) { }

			public void SetStatus(string msg) => Console.WriteLine(msg);

			public void LogCompilerLine(string msg, Level level) =>
				((level == Level.Error) ? Console.Error : Console.Out).WriteLine(msg);
		}

		private class BuildingMod : LocalMod
		{
			public string path;
			public List<LocalMod> refMods;

			public BuildingMod(TmodFile modFile, BuildProperties properties, string path) : base(modFile, properties)
			{
				this.path = path;
			}
		}

		public static readonly string ModSourcePath = Path.Combine(Program.SavePath, "Mod Sources");

		internal static readonly string modCompileDir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "ModCompile");
		internal static readonly string modCompileVersionPath = Path.Combine(modCompileDir, "version");

		internal static string[] FindModSources()
		{
			Directory.CreateDirectory(ModSourcePath);
			return Directory.GetDirectories(ModSourcePath, "*", SearchOption.TopDirectoryOnly).Where(dir => new DirectoryInfo(dir).Name[0] != '.').ToArray();
		}

		// Silence exception reporting in the chat unless actively modding.
		public static bool activelyModding;

		public static bool DeveloperMode {
			get {
				return Debugger.IsAttached || File.Exists(modCompileVersionPath) || Directory.Exists(ModSourcePath) && FindModSources().Length > 0;
			}
		}

		internal static bool DeveloperModeReady(out string msg)
		{
			return RoslynCompatibleFrameworkCheck(out msg) &&
				ModCompileVersionCheck(out msg) &&
				ReferenceAssembliesCheck(out msg);
		}

		private static Version GetModCompileVersion()
		{
			string modCompileVersion;
			try {
				if (!File.Exists(modCompileVersionPath)) return new Version();
				modCompileVersion = File.ReadAllText(modCompileVersionPath);
			}
			catch (Exception e) {
				Logging.tML.Error(e);
				return new Version();
			}
			var mCvSplit = new string(modCompileVersion.Skip(1).ToArray()).Split('.');
			int mCvMajor = 0, mCvMinor = 0, mCvBuild = 0, mCvRevision = 0;
			if (mCvSplit.Length >= 1) mCvMajor = int.Parse(mCvSplit[0]);
			if (mCvSplit.Length >= 2) mCvMinor = int.Parse(mCvSplit[1]);
			if (mCvSplit.Length >= 3) mCvBuild = int.Parse(mCvSplit[2]);
			if (mCvSplit.Length >= 4) mCvRevision = int.Parse(mCvSplit[3]);
			return new Version(mCvMajor, mCvMinor, mCvBuild, mCvRevision);
		}

		internal static bool ModCompileVersionCheck(out string msg)
		{
			var version = GetModCompileVersion();
			if (version > ModLoader.version) {
				Logging.tML.Warn($"ModCompile version is above ModLoader version: {version} vs {ModLoader.version}" +
					$"\nThis not necessarily an issue, this log is for troubleshooting purposes.");
			}
			if (version.Major == ModLoader.version.Major && version.Minor == ModLoader.version.Minor && version.Build == ModLoader.version.Build) {
				msg = Language.GetTextValue("tModLoader.DMModCompileSatisfied");
				return true;
			}

#if DEBUG
			msg = Language.GetTextValue("tModLoader.DMModCompileDev", Path.GetFileName(Assembly.GetExecutingAssembly().Location));
#else
			if (version == default(Version))
				msg = Language.GetTextValue("tModLoader.DMModCompileMissing");
			else
				msg = Language.GetTextValue("tModLoader.DMModCompileUpdate", ModLoader.versionTag, version);
#endif
			return false;
		}

		private static readonly Version minDotNetVersion = new Version(4, 6);
		private static readonly Version minMonoVersion = new Version(5, 20);
		internal static bool RoslynCompatibleFrameworkCheck(out string msg)
		{
			// mono 5.20 is required due to https://github.com/mono/mono/issues/12362
			if (FrameworkVersion.Framework == Framework.NetFramework && FrameworkVersion.Version >= minDotNetVersion ||
				FrameworkVersion.Framework == Framework.Mono && FrameworkVersion.Version >= minMonoVersion) {

				msg = Language.GetTextValue("tModLoader.DMDotNetSatisfied", $"{FrameworkVersion.Framework} {FrameworkVersion.Version}");
				return true;
			}

			if (FrameworkVersion.Framework == Framework.NetFramework)
				msg = Language.GetTextValue("tModLoader.DMDotNetUpdateRequired", minDotNetVersion);
			else if (SystemMonoCheck())
				msg = Language.GetTextValue("tModLoader.DMMonoRuntimeRequired", minMonoVersion);
			else
				msg = Language.GetTextValue("tModLoader.DMMonoUpdateRequired", minMonoVersion);

			return false;
		}

		internal static bool systemMonoSuitable;
		private static bool SystemMonoCheck()
		{
			try {
				var monoPath = "mono";
				if (Platform.IsOSX) //mono installs on OSX don't resolve properly outside of terminal
					monoPath = "/Library/Frameworks/Mono.framework/Versions/Current/Commands/mono";

				string output = Process.Start(new ProcessStartInfo {
					FileName = monoPath,
					Arguments = "--version",
					UseShellExecute = false,
					RedirectStandardOutput = true
				}).StandardOutput.ReadToEnd();

				var monoVersion = new Version(new Regex("version (.+?) ").Match(output).Groups[1].Value);
				return systemMonoSuitable = monoVersion >= minMonoVersion;

			}
			catch (Exception e) {
				Logging.tML.Debug("System mono check failed: ", e);
				return false;
			}
		}

		internal static bool PlatformSupportsVisualStudio => !Platform.IsLinux;

		private static string referenceAssembliesPath;
		internal static bool ReferenceAssembliesCheck(out string msg)
		{
			msg = Language.GetTextValue("tModLoader.DMReferenceAssembliesSatisfied");
			if (referenceAssembliesPath != null)
				return true;

			if (Platform.IsWindows)
				referenceAssembliesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5";
			else if (Platform.IsOSX)
				referenceAssembliesPath = "/Library/Frameworks/Mono.framework/Versions/Current/lib/mono/4.5-api";
			else if (Platform.IsLinux)
				referenceAssembliesPath = "/usr/lib/mono/4.5-api";

			if (Directory.Exists(referenceAssembliesPath))
				return true;

			referenceAssembliesPath = Path.Combine(modCompileDir, "v4.5 Reference Assemblies");
			if (Directory.Exists(referenceAssembliesPath) && Directory.EnumerateFiles(referenceAssembliesPath).Any(x => Path.GetExtension(x) != ".tmp"))
				return true;

			if (FrameworkVersion.Framework == Framework.Mono)
				msg = Language.GetTextValue("tModLoader.DMReferenceAssembliesMissingMono", "lib/mono/4.5-api");
			else
				msg = Language.GetTextValue("tModLoader.DMReferenceAssembliesMissing");

			referenceAssembliesPath = null;
			return false;
		}

		internal static readonly string modReferencesPath = Path.Combine(Program.SavePath, "references");
		private static bool referencesUpdated = false;
		internal static void UpdateReferencesFolder()
		{
			if (referencesUpdated)
				return;

			if (!Directory.Exists(modReferencesPath))
				Directory.CreateDirectory(modReferencesPath);

			var tMLPath = Assembly.GetExecutingAssembly().Location;
			var touchStamp = $"{tMLPath} @ {File.GetLastWriteTime(tMLPath)}";
			var touchFile = Path.Combine(modReferencesPath, "touch");
			var lastTouch = File.Exists(touchFile) ? File.ReadAllText(touchFile) : null;
			if (touchStamp == lastTouch) {
				referencesUpdated = true;
				return;
			}

			// this will extract all the embedded dlls, and grab a reference to the GAC assemblies
			var libs = GetTerrariaReferenceLazy(null, PlatformUtilities.IsXNA);

			// delete any extra references that no-longer exist
			foreach (var file in Directory.GetFiles(modReferencesPath, "*.dll"))
				if (!libs.ContainsKey(Path.GetFileName(file)))
					File.Delete(file);

			// replace tML lib with inferred paths based on names 
			libs.Remove("Terraria.exe");

			var tMLDir = Path.GetDirectoryName(tMLPath);
#if CLIENT
			var tMLServerName = Path.GetFileName(tMLPath).Replace("tModLoader", "tModLoaderServer");
			if (tMLServerName == "Terraria.exe")
				tMLServerName = "tModLoaderServer.exe";
			var tMLServerPath = Path.Combine(tMLDir, tMLServerName);
#else
			var tMLServerPath = tMLPath;
			var tMLClientName = Path.GetFileName(tMLPath).Replace("tModLoaderServer", "tModLoader");
			tMLPath = Path.Combine(tMLDir, tMLClientName);
			if (!File.Exists(tMLPath))
				tMLPath = Path.Combine(tMLDir, "Terraria.exe");
#endif
			var tMLBuildServerPath = tMLServerPath;
			if (FrameworkVersion.Framework == Framework.Mono)
				tMLBuildServerPath = tMLServerPath.Substring(0, tMLServerPath.Length - 4);

			string MakeRef(string path, string name = null)
			{
				if (name == null)
					name = Path.GetFileNameWithoutExtension(path);
				if (Path.GetDirectoryName(path) == modReferencesPath)
					path = "$(MSBuildThisFileDirectory)" + Path.GetFileName(path);
				return $"    <Reference Include=\"{System.Security.SecurityElement.Escape(name)}\">\n      <HintPath>{System.Security.SecurityElement.Escape(path)}</HintPath>\n    </Reference>";
			}
			var referencesXMLList = libs.Values.Select(path => MakeRef(path.Value)).ToList();
			referencesXMLList.Insert(0, MakeRef("$(tMLPath)", "Terraria"));

			var tModLoaderTargets = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""14.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <TerrariaSteamPath>{System.Security.SecurityElement.Escape(tMLDir)}</TerrariaSteamPath>
    <tMLPath>{System.Security.SecurityElement.Escape(tMLPath)}</tMLPath>
    <tMLServerPath>{System.Security.SecurityElement.Escape(tMLServerPath)}</tMLServerPath>
    <tMLBuildServerPath>{System.Security.SecurityElement.Escape(tMLBuildServerPath)}</tMLBuildServerPath>
  </PropertyGroup>
  <ItemGroup>
{string.Join("\n", referencesXMLList)}
  </ItemGroup>
</Project>";

			File.WriteAllText(Path.Combine(modReferencesPath, "tModLoader.targets"), tModLoaderTargets);
			File.WriteAllText(touchFile, touchStamp);
			referencesUpdated = true;
		}

		internal static IList<string> sourceExtensions = new List<string> { ".csproj", ".cs", ".sln" };

		private IBuildStatus status;
		public ModCompile(IBuildStatus status)
		{
			this.status = status;

			// *gasp*, side-effects
			activelyModding = true;
			Logging.ResetPastExceptions();
		}

		internal void BuildAll()
		{
			var modList = new List<LocalMod>();
			foreach (var modFolder in FindModSources())
				modList.Add(ReadBuildInfo(modFolder));

			//figure out which of the installed mods are required for building
			var installedMods = ModOrganizer.FindMods().Where(mod => !modList.Exists(m => m.Name == mod.Name)).ToList();

			var requiredFromInstall = new HashSet<LocalMod>();
			void Require(LocalMod mod, bool includeWeak)
			{
				foreach (var dep in mod.properties.RefNames(includeWeak)) {
					var depMod = installedMods.SingleOrDefault(m => m.Name == dep);
					if (depMod != null && requiredFromInstall.Add(depMod))
						Require(depMod, false);
				}
			}

			foreach (var mod in modList)
				Require(mod, true);

			modList.AddRange(requiredFromInstall);

			//sort and version check
			List<BuildingMod> modsToBuild;
			try {
				ModOrganizer.EnsureDependenciesExist(modList, true);
				ModOrganizer.EnsureTargetVersionsMet(modList);
				var sortedModList = ModOrganizer.Sort(modList);
				modsToBuild = sortedModList.OfType<BuildingMod>().ToList();
			}
			catch (ModSortingException e) {
				throw new BuildException(e.Message);
			}

			//build
			int num = 0;
			foreach (var mod in modsToBuild) {
				status.SetProgress(num++, modsToBuild.Count);
				Build(mod);
			}
		}

		internal static void BuildModCommandLine(string modFolder)
		{
			// Once we get to this point, the application is guaranteed to exit
			if (!DeveloperModeReady(out string msg)) {
				Console.Error.WriteLine("Developer Mode is not ready: " + msg);
				Environment.Exit(1);
			}
			var lockFile = AcquireConsoleBuildLock();
			try {
				new ModCompile(new ConsoleBuildStatus()).Build(modFolder);
			}
			catch (BuildException e) {
				Console.Error.WriteLine("Error: " + e.Message);
				if (e.InnerException != null)
					Console.Error.WriteLine(e.InnerException);
				Environment.Exit(1);
			}
			catch (Exception e) {
				Console.Error.WriteLine(e);
				Environment.Exit(1);
			}
			finally {
				lockFile.Close();
			}
			// Mod was built with success, exit code 0 indicates success.
			Environment.Exit(0);
		}

		internal void Build(string modFolder) => Build(ReadBuildInfo(modFolder));

		private BuildingMod ReadBuildInfo(string modFolder)
		{
			if (modFolder.EndsWith("\\") || modFolder.EndsWith("/")) modFolder = modFolder.Substring(0, modFolder.Length - 1);
			var modName = Path.GetFileName(modFolder);
			status.SetStatus(Language.GetTextValue("tModLoader.ReadingProperties", modName));

			BuildProperties properties;
			try {
				properties = BuildProperties.ReadBuildFile(modFolder);
			}
			catch (Exception e) {
				throw new BuildException(Language.GetTextValue("tModLoader.BuildErrorFailedLoadBuildTxt", Path.Combine(modFolder, "build.txt")), e);
			}

			var file = Path.Combine(ModLoader.ModPath, modName + ".tmod");
			var modFile = new TmodFile(file, modName, properties.version);
			return new BuildingMod(modFile, properties, modFolder);
		}

		private void Build(BuildingMod mod)
		{
			try {
				status.SetStatus(Language.GetTextValue("tModLoader.Building", mod.Name));

				mod.refMods = FindReferencedMods(mod.properties);
				BuildModForPlatform(mod, true);
				BuildModForPlatform(mod, false);

				if (Program.LaunchParameters.TryGetValue("-eac", out var eacValue)) {
					mod.properties.eacPath = Path.ChangeExtension(eacValue, "pdb");
					status.SetStatus(Language.GetTextValue("tModLoader.EnabledEAC", mod.properties.eacPath));
				}

				PackageMod(mod);

				ModLoader.GetMod(mod.Name)?.Close();
				mod.modFile.Save();
				ModLoader.EnableMod(mod.Name);
			}
			catch (Exception e) {
				e.Data["mod"] = mod.Name;
				throw;
			}
		}

		private void PackageMod(BuildingMod mod)
		{
			status.SetStatus(Language.GetTextValue("tModLoader.Packaging", mod));
			status.SetProgress(0, 1);

			mod.modFile.AddFile("Info", mod.properties.ToBytes());

			var resources = Directory.GetFiles(mod.path, "*", SearchOption.AllDirectories)
				.Where(res => !IgnoreResource(mod, res))
				.ToList();

			status.SetProgress(packedResourceCount = 0, resources.Count);
			Parallel.ForEach(resources, resource => AddResource(mod, resource));

			// add dll references from the -eac bin folder
			var libFolder = Path.Combine(mod.path, "lib");
			foreach (var dllPath in mod.properties.dllReferences.Select(dllName => DllRefPath(mod, dllName, null)))
				if (!dllPath.StartsWith(libFolder))
					mod.modFile.AddFile("lib/" + Path.GetFileName(dllPath), File.ReadAllBytes(dllPath));
		}

		private bool IgnoreResource(BuildingMod mod, string resource)
		{
			var relPath = resource.Substring(mod.path.Length + 1);
			return IgnoreCompletely(mod, resource) ||
				relPath == "build.txt" ||
				!mod.properties.includeSource && sourceExtensions.Contains(Path.GetExtension(resource)) ||
				Path.GetFileName(resource) == "Thumbs.db";
		}

		// Ignore for both Compile and Packaging
		private bool IgnoreCompletely(BuildingMod mod, string resource)
		{
			var relPath = resource.Substring(mod.path.Length + 1);
			return mod.properties.ignoreFile(relPath) ||
				relPath[0] == '.' ||
				relPath.StartsWith("bin" + Path.DirectorySeparatorChar) ||
				relPath.StartsWith("obj" + Path.DirectorySeparatorChar);
		}

		private int packedResourceCount;
		private void AddResource(BuildingMod mod, string resource)
		{
			var relPath = resource.Substring(mod.path.Length + 1);
			using (var src = File.OpenRead(resource))
			using (var dst = new MemoryStream()) {
				if (!ContentConverters.Convert(ref relPath, src, dst))
					src.CopyTo(dst);

				mod.modFile.AddFile(relPath, dst.ToArray());
				Interlocked.Increment(ref packedResourceCount);
				status.SetProgress(packedResourceCount);
			}
		}

		private void VerifyModAssembly(string modName, AssemblyDefinition asmDef)
		{
			var asmName = asmDef.Name.Name;
			if (asmName != modName)
				throw new BuildException(Language.GetTextValue("tModLoader.BuildErrorModNameDoesntMatchAssemblyName", modName, asmName));

			if (modName.Equals("Terraria", StringComparison.InvariantCultureIgnoreCase))
				throw new BuildException(Language.GetTextValue("tModLoader.BuildErrorModNamedTerraria"));

			// Verify that folder and namespace match up
			var modClassType = asmDef.MainModule.Types.SingleOrDefault(x => x.BaseType?.FullName == "Terraria.ModLoader.Mod");
			if (modClassType == null)
				throw new BuildException(Language.GetTextValue("tModLoader.BuildErrorNoModClass"));

			string topNamespace = modClassType.Namespace.Split('.')[0];
			if (topNamespace != modName)
				throw new BuildException(Language.GetTextValue("tModLoader.BuildErrorNamespaceFolderDontMatch"));
		}

		private List<LocalMod> FindReferencedMods(BuildProperties properties)
		{
			var mods = new Dictionary<string, LocalMod>();
			FindReferencedMods(properties, mods, true);
			return mods.Values.ToList();
		}

		private void FindReferencedMods(BuildProperties properties, Dictionary<string, LocalMod> mods, bool requireWeak)
		{
			foreach (var refName in properties.RefNames(true)) {
				if (mods.ContainsKey(refName))
					continue;

				bool isWeak = properties.weakReferences.Any(r => r.mod == refName);
				LocalMod mod;
				try {
					var modFile = new TmodFile(Path.Combine(ModLoader.ModPath, refName + ".tmod"));
					using (modFile.Open())
						mod = new LocalMod(modFile);
				}
				catch (FileNotFoundException) when (isWeak && !requireWeak) {
					// don't recursively require weak deps, if the mod author needs to compile against them, they'll have them installed
					continue;
				}
				catch (Exception ex) {
					throw new BuildException(Language.GetTextValue("tModLoader.BuildErrorModReference", refName), ex);
				}
				mods[refName] = mod;
				FindReferencedMods(mod.properties, mods, false);
			}
		}

		private string tempDir = Path.Combine(ModLoader.ModPath, "compile_temp");
		private void BuildModForPlatform(BuildingMod mod, bool xna)
		{
			status.SetProgress(xna ? 0 : 1, 2);
			try {
				if (Directory.Exists(tempDir))
					Directory.Delete(tempDir, true);
				Directory.CreateDirectory(tempDir);

				string dllName = mod.Name + (xna ? ".XNA.dll" : ".FNA.dll");
				string dllPath = null;

				// look for pre-compiled paths
				if (mod.properties.noCompile) {
					var allPath = Path.Combine(mod.path, mod.Name + ".All.dll");
					dllPath = File.Exists(allPath) ? allPath : Path.Combine(mod.path, dllName);
				}
				else if (xna == PlatformUtilities.IsXNA && Program.LaunchParameters.TryGetValue("-eac", out var eacValue)) {
					dllPath = eacValue;
				}

				var refs = GetReferencesLazy(mod, xna);

				// precompiled load, or fallback to Roslyn compile
				if (File.Exists(dllPath))
					status.SetStatus(Language.GetTextValue("tModLoader.LoadingPrecompiled", dllName, Path.GetFileName(dllPath)));
				else if (dllPath != null)
					throw new BuildException(Language.GetTextValue("tModLoader.BuildErrorLoadingPrecompiled", dllPath));
				else {
					dllPath = Path.Combine(tempDir, dllName);
					CompileMod(mod, dllPath, refs, xna);
				}

				// add mod assembly to file
				mod.modFile.AddFile(dllName, File.ReadAllBytes(dllPath));

				// read mod assembly using cecil for verification and pdb processing
				using (var asmResolver = new BuildAssemblyResolver(Path.GetDirectoryName(dllPath), refs)) {
					var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { InMemory = true, ReadSymbols = mod.properties.includePDB, AssemblyResolver = asmResolver });
					VerifyModAssembly(mod.Name, asm);

					if (!mod.properties.includePDB)
						return;

					// when reading and writing a module with cecil, the debug sequence points need regenerating, even if the methods are not changed
					// write out the pdb file using cecil because doing it at runtime is difficult
					var tempDllPath = Path.Combine(tempDir, dllName); //use the temp dir to avoid overwriting a precompiled dll

					// force the native pdb writer when possible, to support stack traces on older .NET frameworks
					asm.Write(tempDllPath, new WriterParameters {
						WriteSymbols = true,
						SymbolWriterProvider = FrameworkVersion.Framework == Framework.NetFramework ? new NativePdbWriterProvider() : (ISymbolWriterProvider)new PortablePdbWriterProvider()
					});;

					mod.modFile.AddFile(Path.ChangeExtension(dllName, "pdb"), File.ReadAllBytes(Path.ChangeExtension(tempDllPath, "pdb")));

					if (dllPath == tempDllPath) { // load the cecil produced dll, which has the correct debug header
						mod.modFile.AddFile(dllName, File.ReadAllBytes(dllPath));
					}
					else { // with a pre-loaded dll, the symbols won't match cecil symbols unless we splice in the cecil debug header
						using (var cecilAsmDef = AssemblyDefinition.ReadAssembly(tempDllPath))
							mod.modFile.AddFile(dllName + ".cecildebugheader", cecilAsmDef.MainModule.GetDebugHeader().GetBytes());
					}

					// make an mdb for FNA
					if (!xna) {
						asm.Write(tempDllPath, new WriterParameters { WriteSymbols = true, SymbolWriterProvider = new MdbWriterProvider() });
						mod.modFile.AddFile(dllName + ".mdb", File.ReadAllBytes(tempDllPath + ".mdb"));
					}
				}
			}
			finally {
				try {
					if (Directory.Exists(tempDir))
						Directory.Delete(tempDir, true);
				}
				catch { }
			}
		}

		// Provide a map of dll names (including .dll suffix) to paths on disk
		// Paths on disk are lazy as the file may need extracting on demand
		// Mod compiling will extract all paths to pass to Roslyn, but cecil post-processing only needs some assemblies
		// This lets authors using no-compile prioritise dlls from the compiled dll dir, and sidestep weird dependency issues
		private IDictionary<string, Lazy<string>> GetReferencesLazy(BuildingMod mod, bool xna)
		{
			var refs = new Dictionary<string, Lazy<string>>();
			void AddFile(string path)  => refs[Path.GetFileName(path)] = new Lazy<string>(() => path);

			//everything used to compile the tModLoader for the target platform
			foreach (var entry in GetTerrariaReferenceLazy(tempDir, xna))
				refs.Add(entry.Key, entry.Value);

			// add framework assemblies
			if (ReferenceAssembliesCheck(out _)) {
				foreach (var path in Directory.GetFiles(referenceAssembliesPath, "*.dll", SearchOption.AllDirectories).Where(path => !path.EndsWith("Thunk.dll") && !path.EndsWith("Wrapper.dll")))
					AddFile(path);
			}

			//libs added by the mod
			foreach (var path in mod.properties.dllReferences.Select(dllName => DllRefPath(mod, dllName, xna)))
				AddFile(path);

			//all dlls included in all referenced mods
			foreach (var refMod in mod.refMods) {
				void AddModEntry(string dllName, Func<byte[]> getBytes)
				{
					refs[dllName] = new Lazy<string>(() => {
						using (refMod.modFile.Open()) {
							var path = Path.Combine(tempDir, dllName);
							File.WriteAllBytes(path, getBytes());
							return path;
						}
					});
				}

				var mainDll = $"{refMod.Name}.dll";
				AddModEntry(mainDll, () => refMod.modFile.GetModAssembly(xna));

				foreach (var dllName in refMod.properties.dllReferences) {
					AddModEntry($"{dllName}.dll", () => refMod.modFile.GetLibraryDll(dllName, xna));
				}
			}

			return refs;
		}

		private void CompileMod(BuildingMod mod, string outputPath, IDictionary<string, Lazy<string>> refs, bool xna)
		{
			UpdateReferencesFolder();

			status.SetStatus(Language.GetTextValue("tModLoader.Compiling", Path.GetFileName(outputPath)));
			if (!DeveloperModeReady(out string msg))
				throw new BuildException(msg);

			var files = Directory.GetFiles(mod.path, "*.cs", SearchOption.AllDirectories).Where(file => !IgnoreCompletely(mod, file)).ToArray();

			bool allowUnsafe =
				Program.LaunchParameters.TryGetValue("-unsafe", out var unsafeParam) &&
				bool.TryParse(unsafeParam, out var _allowUnsafe) && _allowUnsafe;

			var preprocessorSymbols = new List<string> { xna ? "XNA" : "FNA" };
			if (Program.LaunchParameters.TryGetValue("-define", out var defineParam))
				preprocessorSymbols.AddRange(defineParam.Split(';', ' '));

			var refPaths = refs.Select(entry => entry.Value.Value).ToArray();
			var results = RoslynCompile(mod.Name, outputPath, refPaths, files, preprocessorSymbols.ToArray(), mod.properties.includePDB, allowUnsafe);

			int numWarnings = results.Cast<CompilerError>().Count(e => e.IsWarning);
			int numErrors = results.Count - numWarnings;
			status.LogCompilerLine(Language.GetTextValue("tModLoader.CompilationResult", numErrors, numWarnings), Level.Info);
			foreach (CompilerError line in results)
				status.LogCompilerLine(line.ToString(), line.IsWarning ? Level.Warn : Level.Error);

			if (results.HasErrors) {
				var firstError = results.Cast<CompilerError>().First(e => !e.IsWarning);
				throw new BuildException(Language.GetTextValue("tModLoader.CompileError", Path.GetFileName(outputPath), numErrors, numWarnings) + $"\nError: {firstError}");
			}
		}

		private string DllRefPath(BuildingMod mod, string dllName, bool? xna)
		{
			string pathWithoutExtension = Path.Combine(mod.path, "lib", dllName);

			if (xna.HasValue) { //check for platform specific dll
				string engineSpecificPath = pathWithoutExtension + (xna.Value ? ".XNA.dll" : ".FNA.dll");

				if (File.Exists(engineSpecificPath))
					return engineSpecificPath;
			}

			string path = pathWithoutExtension + ".dll";

			if (File.Exists(path))
				return path;

			if (Program.LaunchParameters.TryGetValue("-eac", out var eacPath)) {
				var outputCopiedPath = Path.Combine(Path.GetDirectoryName(eacPath), dllName + ".dll");

				if (File.Exists(outputCopiedPath))
					return outputCopiedPath;
			}

			throw new BuildException("Missing dll reference: " + path);
		}

		private static IDictionary<string, Lazy<string>> GetTerrariaReferenceLazy(string tempDir, bool xna)
		{
			var refs = new Dictionary<string, Lazy<string>>();
			void AddFile(string path) => refs[Path.GetFileName(path)] = new Lazy<string>(() => path);
			void AddFiles(IEnumerable<string> paths) {
				foreach (var path in paths) 
					AddFile(path);
			}
			void AddStream(string fileName, string dir, Func<Stream> getStream)
			{
				refs[fileName] = new Lazy<string>(() => {
					var path = Path.Combine(dir, fileName);
					using (Stream s = getStream(), file = File.Create(path))
						s.CopyTo(file);

					return path;
				});
			}


			var xnaAndFnaLibs = new[] {
				"Microsoft.Xna.Framework.dll",
				"Microsoft.Xna.Framework.Game.dll",
				"Microsoft.Xna.Framework.Graphics.dll",
				"Microsoft.Xna.Framework.Xact.dll",
				"FNA.dll"
			};

			if (xna == PlatformUtilities.IsXNA) {
				var executingAssembly = Assembly.GetExecutingAssembly();
				refs["Terraria.exe"] = new Lazy<string>(() => executingAssembly.Location);

				// find xna/fna in the currently referenced assemblies (eg, via GAC)
				AddFiles(executingAssembly.GetReferencedAssemblies().Select(refName => Assembly.Load(refName).Location).Where(loc => xnaAndFnaLibs.Contains(Path.GetFileName(loc))));

				// avoid a double extract of the embedded dlls
				if (referencesUpdated) {
					AddFiles(Directory.GetFiles(modReferencesPath, "*.dll"));
					return refs;
				}

				//extract embedded resource dlls to the references path rather than the tempDir
				foreach (var resName in executingAssembly.GetManifestResourceNames().Where(n => n.EndsWith(".dll"))) {
					AddStream(Path.GetFileName(resName), modReferencesPath, () => executingAssembly.GetManifestResourceStream(resName));
				}
			}
			else {
				var tMLPath = Path.Combine(modCompileDir, xna ? "tModLoader.XNA.exe" : "tModLoader.FNA.exe");
				refs["Terraria.exe"] = new Lazy<string>(() => tMLPath);

				// find xna/fna in the ModCompile folder
				AddFiles(xnaAndFnaLibs.Select(f => Path.Combine(modCompileDir, f)).Where(File.Exists));

				//extract embedded resource dlls to a temporary folder
				foreach (var res in ModuleDefinition.ReadModule(tMLPath).Resources.OfType<EmbeddedResource>().Where(res => res.Name.EndsWith(".dll"))) {
					AddStream(Path.GetFileName(res.Name), tempDir, res.GetResourceStream);
				}
			}

			return refs;
		}

		private static Type roslynWrapper;
		private static Type RoslynWrapper {
			get {
				if (roslynWrapper == null) {
					AppDomain.CurrentDomain.AssemblyResolve += (o, args) => {
						var name = new AssemblyName(args.Name).Name;
						var f = Path.Combine(modCompileDir, name + ".dll");
						return File.Exists(f) ? Assembly.LoadFile(f) : null;
					};
					roslynWrapper = Assembly.LoadFile(Path.Combine(modCompileDir, "RoslynWrapper.dll")).GetType("Terraria.ModLoader.RoslynWrapper");
				}
				return roslynWrapper;
			}
		}

		/// <summary>
		/// Invoke the Roslyn compiler via reflection to avoid a .NET 4.6 dependency
		/// </summary>
		private static CompilerErrorCollection RoslynCompile(string name, string outputPath, string[] references, string[] files, string[] preprocessorSymbols, bool includePdb, bool allowUnsafe)
		{
			return (CompilerErrorCollection)RoslynWrapper.GetMethod("Compile")
				.Invoke(null, new object[] { name, outputPath, references, files, preprocessorSymbols, includePdb, allowUnsafe });
		}

		private static FileStream AcquireConsoleBuildLock()
		{
			var path = Path.Combine(modCompileDir, "buildlock");
			bool first = true;
			while (true) {
				try {
					return new FileStream(path, FileMode.OpenOrCreate);
				}
				catch (IOException) {
					if (first) {
						Console.WriteLine("Waiting for other builds to complete");
						first = false;
					}
				}
			}
		}

		// When building pdbs and mdbs using cecil, reference dlls will occasionally be requested to help with reading and writing.
		// This normally happens when using a constant value from another dll in a default value for a method parameter.
		// Fixes #896, #785
		// when we move to .NET Core we shouldn't need to generate pdbs/mdbs with cecil anymore, so this won't be necessary
		private class BuildAssemblyResolver : IAssemblyResolver
		{
			private readonly IDictionary<string, AssemblyDefinition> cache = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
			private readonly IDictionary<string, Lazy<string>> refs;
			private readonly string buildDir;

			public BuildAssemblyResolver(string buildDir, IDictionary<string, Lazy<string>> refs)
			{
				this.buildDir = buildDir;
				this.refs = refs;
			}

			public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());

			public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
			{
				if (cache.TryGetValue(name.Name, out var asmDef))
					return asmDef;

				// build dir first, it's a better source of truth for VS/no-compile
				if (FindRefInBuildDir(name.Name, out var path))
					return cache[name.Name] = AssemblyDefinition.ReadAssembly(path, parameters);

				if (refs.TryGetValue(name.Name+".dll", out var pathLazy) || refs.TryGetValue(name.Name+".exe", out pathLazy))
					return cache[name.Name] = AssemblyDefinition.ReadAssembly(pathLazy.Value, parameters);

				throw new AssemblyResolutionException(name);
			}

			private bool FindRefInBuildDir(string name, out string path)
			{
				path = Path.Combine(buildDir, name + ".dll");
				if (File.Exists(path))
					return true;

				path = Path.Combine(buildDir, name + ".exe");
				return File.Exists(path);
			}

			public void Dispose()
			{ }
		}
	}
}
