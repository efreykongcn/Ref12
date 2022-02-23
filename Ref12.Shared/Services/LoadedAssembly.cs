// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Util;

namespace SLaks.Ref12.Services
{
    [DebuggerDisplay("[LoadedAssembly {shortName}]")]
    public sealed class LoadedAssembly
    {
		public sealed class LoadResult
		{
			public PEFile PEFile { get; }
			public Exception PEFileLoadException { get; }

			public LoadResult(PEFile peFile)
			{
				this.PEFile = peFile ?? throw new ArgumentNullException(nameof(peFile));
			}
			public LoadResult(Exception peFileLoadException)
			{
				this.PEFileLoadException = peFileLoadException ?? throw new ArgumentNullException(nameof(peFileLoadException));
			}
		}

		internal static readonly ConditionalWeakTable<PEFile, LoadedAssembly> loadedAssemblies = new ConditionalWeakTable<PEFile, LoadedAssembly>();

		readonly Task<LoadResult> loadingTask;
		readonly string fileName;
        readonly string shortName;
		readonly IAssemblyResolver providedAssemblyResolver = null;

		public bool IsAutoLoaded { get; set; }

		public LoadedAssembly(string fileName)
        {
            this.fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            this.loadingTask = Task.Run(() => LoadAsync()); // requires that this.fileName is set
            this.shortName = Path.GetFileNameWithoutExtension(fileName);
        }

		string targetFrameworkId;

		/// <summary>
		/// Returns a target framework identifier in the form '&lt;framework&gt;Version=v&lt;version&gt;'.
		/// Returns an empty string if no TargetFrameworkAttribute was found
		/// or the file doesn't contain an assembly header, i.e., is only a module.
		/// 
		/// Throws an exception if the file does not contain any .NET metadata (e.g. file of unknown format).
		/// </summary>
		public async Task<string> GetTargetFrameworkIdAsync()
		{
			var value = LazyInit.VolatileRead(ref targetFrameworkId);
			if (value == null)
			{
				var assembly = await GetPEFileAsync().ConfigureAwait(false);
				value = assembly.DetectTargetFrameworkId() ?? string.Empty;
				value = LazyInit.GetOrSet(ref targetFrameworkId, value);
			}

			return value;
		}
		string runtimePack;
		public async Task<string> GetRuntimePackAsync()
		{
			var value = LazyInit.VolatileRead(ref runtimePack);
			if (value == null)
			{
				var assembly = await GetPEFileAsync().ConfigureAwait(false);
				value = assembly.DetectRuntimePack() ?? string.Empty;
				value = LazyInit.GetOrSet(ref runtimePack, value);
			}

			return value;
		}
		public bool IsLoaded => loadingTask.IsCompleted;
		async Task<LoadResult> LoadAsync()
		{
			// Read the module from disk
			using (var fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read))
			{
				return LoadAssembly(fileStream, PEStreamOptions.PrefetchEntireImage);
			}
		}

		LoadResult LoadAssembly(Stream stream, PEStreamOptions streamOptions)
		{
			PEFile module = new PEFile(fileName, stream, streamOptions, metadataOptions: MetadataReaderOptions.ApplyWindowsRuntimeProjections);

			lock (loadedAssemblies)
			{
				loadedAssemblies.Add(module, this);
			}
			return new LoadResult(module);
		}

		/// <summary>
		/// Gets the <see cref="PEFile"/>.
		/// </summary>
		public async Task<PEFile> GetPEFileAsync()
		{
			var loadResult = await loadingTask.ConfigureAwait(false);
			if (loadResult.PEFile != null)
				return loadResult.PEFile;
			else
				throw loadResult.PEFileLoadException;
		}

		/// <summary>
		/// Gets the <see cref="PEFile"/>.
		/// Returns null in case of load errors.
		/// </summary>
		public PEFile GetPEFileOrNull()
		{
			try
			{
				var loadResult = loadingTask.GetAwaiter().GetResult();
				return loadResult.PEFile;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.TraceError(ex.ToString());
				return null;
			}
		}

		/// <summary>
		/// Gets the <see cref="PEFile"/>.
		/// Returns null in case of load errors.
		/// </summary>
		public async Task<PEFile> GetPEFileOrNullAsync()
		{
			try
			{
				var loadResult = await loadingTask.ConfigureAwait(false);
				return loadResult.PEFile;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.TraceError(ex.ToString());
				return null;
			}
		}

		sealed class MyAssemblyResolver : IAssemblyResolver
		{
			readonly LoadedAssembly parent;
			readonly bool loadOnDemand;

			readonly IAssemblyResolver providedAssemblyResolver;
			readonly LoadedAssembly[] alreadyLoadedAssemblies;
			readonly Task<string> tfmTask;
			//readonly ReferenceLoadInfo referenceLoadInfo;

			public MyAssemblyResolver(LoadedAssembly parent, bool loadOnDemand)
			{
				this.parent = parent;
				this.loadOnDemand = loadOnDemand;

				this.providedAssemblyResolver = parent.providedAssemblyResolver;
				// Note: we cache a copy of the assembly list in the constructor, so that the
				// resolve calls only search-by-asm-name in the assemblies that were already loaded
				// at the time of the GetResolver() call.
				this.alreadyLoadedAssemblies = new LoadedAssembly[0];
				//this.alreadyLoadedAssemblies = assemblyList.GetAssemblies();

				// If we didn't do this, we'd also search in the assemblies that we just started to load
				// in previous Resolve() calls; but we don't want to wait for those to be loaded.
				this.tfmTask = parent.GetTargetFrameworkIdAsync();
				//this.referenceLoadInfo = parent.LoadedAssemblyReferencesInfo;
			}

			public PEFile Resolve(IAssemblyReference reference)
			{
				return ResolveAsync(reference).GetAwaiter().GetResult();
			}

			Dictionary<string, PEFile> asmLookupByFullName;
			Dictionary<string, PEFile> asmLookupByShortName;

			/// <summary>
			/// Opens an assembly from disk.
			/// Returns the existing assembly node if it is already loaded.
			/// </summary>
			/// <remarks>
			/// If called on the UI thread, the newly opened assembly is added to the list synchronously.
			/// If called on another thread, the newly opened assembly won't be returned by GetAssemblies()
			/// until the UI thread gets around to adding the assembly.
			/// </remarks>
			public LoadedAssembly OpenAssembly(string file, bool isAutoLoaded = false)
			{
				file = Path.GetFullPath(file);
				return OpenAssembly(file, () => {
					var newAsm = new LoadedAssembly(file);
					newAsm.IsAutoLoaded = isAutoLoaded;
					return newAsm;
				});
			}

			LoadedAssembly OpenAssembly(string file, Func<LoadedAssembly> load)
			{
				LoadedAssembly asm = load();
				return asm;
			}

			/// <summary>
			/// 0) if we're inside a package, look for filename.dll in parent directories
			/// 1) try to find exact match by tfm + full asm name in loaded assemblies
			/// 2) try to find match in search paths
			/// 3) if a.deps.json is found: search %USERPROFILE%/.nuget/packages/* as well
			/// 4) look in /dotnet/shared/{runtime-pack}/{closest-version}
			/// 5) if the version is retargetable or all zeros or ones, search C:\Windows\Microsoft.NET\Framework64\v4.0.30319
			/// 6) For "mscorlib.dll" we use the exact same assembly with which ILSpy runs
			/// 7) Search the GAC
			/// 8) search C:\Windows\Microsoft.NET\Framework64\v4.0.30319
			/// 9) try to find match by asm name (no tfm/version) in loaded assemblies
			/// </summary>
			public async Task<PEFile> ResolveAsync(IAssemblyReference reference)
			{
				PEFile module;
				// 0) if we're inside a package, look for filename.dll in parent directories
				if (providedAssemblyResolver != null)
				{
					module = await providedAssemblyResolver.ResolveAsync(reference).ConfigureAwait(false);
					if (module != null)
						return module;
				}

				string tfm = await tfmTask.ConfigureAwait(false);

				bool isWinRT = reference.IsWindowsRuntime;
				string key = tfm + ";" + (isWinRT ? reference.Name : reference.FullName);

				// 1) try to find exact match by tfm + full asm name in loaded assemblies
				var lookup = LazyInit.VolatileRead(ref isWinRT ? ref asmLookupByShortName : ref asmLookupByFullName);
				if (lookup == null)
				{
					lookup = await CreateLoadedAssemblyLookupAsync(shortNames: isWinRT).ConfigureAwait(false);
					lookup = LazyInit.GetOrSet(ref isWinRT ? ref asmLookupByShortName : ref asmLookupByFullName, lookup);
				}
				if (lookup.TryGetValue(key, out module))
				{
					//referenceLoadInfo.AddMessageOnce(reference.FullName, MessageKind.Info, "Success - Found in Assembly List");
					return module;
				}

				string file = parent.GetUniversalResolver().FindAssemblyFile(reference);

				if (file != null)
				{
					// Load assembly from disk
					LoadedAssembly asm;
					if (loadOnDemand)
					{
						asm = OpenAssembly(file, isAutoLoaded: true);
					}
					else
                    {
						asm = null;
                    }
					if (asm != null)
					{
						//referenceLoadInfo.AddMessage(reference.ToString(), MessageKind.Info, "Success - Loading from: " + file);
						return await asm.GetPEFileOrNullAsync().ConfigureAwait(false);
					}
					return null;
				}
				else
				{
					// Assembly not found; try to find a similar-enough already-loaded assembly
					var candidates = new List<(LoadedAssembly assembly, Version version)>();

					foreach (LoadedAssembly loaded in alreadyLoadedAssemblies)
					{
						module = await loaded.GetPEFileOrNullAsync().ConfigureAwait(false);
						var reader = module?.Metadata;
						if (reader == null || !reader.IsAssembly)
							continue;
						var asmDef = reader.GetAssemblyDefinition();
						var asmDefName = reader.GetString(asmDef.Name);
						if (reference.Name.Equals(asmDefName, StringComparison.OrdinalIgnoreCase))
						{
							candidates.Add((loaded, asmDef.Version));
						}
					}

					if (candidates.Count == 0)
					{
						//referenceLoadInfo.AddMessageOnce(reference.ToString(), MessageKind.Error, "Could not find reference: " + reference);
						return null;
					}

					candidates.SortBy(c => c.version);

					var bestCandidate = candidates.FirstOrDefault(c => c.version >= reference.Version).assembly ?? candidates.Last().assembly;
					//referenceLoadInfo.AddMessageOnce(reference.ToString(), MessageKind.Info, "Success - Found in Assembly List with different TFM or version: " + bestCandidate.fileName);
					return await bestCandidate.GetPEFileOrNullAsync().ConfigureAwait(false);
				}
			}

			private async Task<Dictionary<string, PEFile>> CreateLoadedAssemblyLookupAsync(bool shortNames)
			{
				var result = new Dictionary<string, PEFile>(StringComparer.OrdinalIgnoreCase);
				foreach (LoadedAssembly loaded in alreadyLoadedAssemblies)
				{
					try
					{
						var module = await loaded.GetPEFileOrNullAsync().ConfigureAwait(false);
						if (module == null)
							continue;
						var reader = module.Metadata;
						if (reader == null || !reader.IsAssembly)
							continue;
						string tfm = await loaded.GetTargetFrameworkIdAsync().ConfigureAwait(false);
						string key = tfm + ";"
							+ (shortNames ? module.Name : module.FullName);
						if (!result.ContainsKey(key))
						{
							result.Add(key, module);
						}
					}
					catch (BadImageFormatException)
					{
						continue;
					}
				}
				return result;
			}

			public PEFile ResolveModule(PEFile mainModule, string moduleName)
			{
				return ResolveModuleAsync(mainModule, moduleName).GetAwaiter().GetResult();
			}

			public async Task<PEFile> ResolveModuleAsync(PEFile mainModule, string moduleName)
			{
				if (providedAssemblyResolver != null)
				{
					var module = await providedAssemblyResolver.ResolveModuleAsync(mainModule, moduleName).ConfigureAwait(false);
					if (module != null)
						return module;
				}


				string file = Path.Combine(Path.GetDirectoryName(mainModule.FileName), moduleName);
				if (File.Exists(file))
				{
					// Load module from disk
					LoadedAssembly asm;
					if (loadOnDemand)
					{
						asm = OpenAssembly(file, isAutoLoaded: true);
					}
					else
					{
						asm = null;
					}
					if (asm != null)
					{
						return await asm.GetPEFileOrNullAsync().ConfigureAwait(false);
					}
				}
				else
				{
					// Module does not exist on disk, look for one with a matching name in the assemblylist:
					foreach (LoadedAssembly loaded in alreadyLoadedAssemblies)
					{
						var module = await loaded.GetPEFileOrNullAsync().ConfigureAwait(false);
						var reader = module?.Metadata;
						if (reader == null || reader.IsAssembly)
							continue;
						var moduleDef = reader.GetModuleDefinition();
						if (moduleName.Equals(reader.GetString(moduleDef.Name), StringComparison.OrdinalIgnoreCase))
						{
							//referenceLoadInfo.AddMessageOnce(moduleName, MessageKind.Info, "Success - Found in Assembly List");
							return module;
						}
					}
				}
				return null;
			}
		}

		public string FileName => fileName;
		public string ShortName => shortName;

		private UniversalAssemblyResolver GetUniversalResolver()
		{
			return LazyInitializer.EnsureInitialized(ref this.universalResolver, () => {
				var targetFramework = this.GetTargetFrameworkIdAsync().Result;
				var runtimePack = this.GetRuntimePackAsync().Result;

				var readerOptions = MetadataReaderOptions.ApplyWindowsRuntimeProjections;

				var rootedPath = Path.IsPathRooted(this.FileName) ? this.FileName : null;

				return new UniversalAssemblyResolver(rootedPath, throwOnError: false, targetFramework,
					runtimePack, PEStreamOptions.PrefetchEntireImage, readerOptions);
			});
		}

		UniversalAssemblyResolver universalResolver;

		public IAssemblyResolver GetAssemblyResolver(bool loadOnDemand = true)
		{
			return new MyAssemblyResolver(this, loadOnDemand);
		}
	}
}
