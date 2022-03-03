using System;
using System.Linq;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler.Metadata;

namespace SLaks.Ref12.Services
{
    public class AssemblyFileFinder
	{
		public static string FindAssemblyFile(Mono.Cecil.AssemblyDefinition assemblyDefinition, string assemblyFile)
		{
			string tfi = DetectTargetFrameworkId(assemblyDefinition, assemblyFile);
			UniversalAssemblyResolver assemblyResolver;
			if (IsReferenceAssembly(assemblyDefinition, assemblyFile))
			{
				assemblyResolver = new UniversalAssemblyResolver(null, throwOnError: false, tfi);
			}
			else
			{
				assemblyResolver = new UniversalAssemblyResolver(assemblyFile, throwOnError: false, tfi);
			}

			return assemblyResolver.FindAssemblyFile(AssemblyNameReference.Parse(assemblyDefinition.Name.FullName));
		}

		static readonly string RefPathPattern = @"NuGetFallbackFolder[/\\][^/\\]+[/\\][^/\\]+[/\\]ref[/\\]";

		public static bool IsReferenceAssembly(Mono.Cecil.AssemblyDefinition assemblyDef, string assemblyFile)
		{
			if (assemblyDef.CustomAttributes.Any(ca => ca.AttributeType.FullName == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute"))
				return true;

			// Try to detect reference assembly through specific path pattern
			var refPathMatch = Regex.Match(assemblyFile, RefPathPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
			return refPathMatch.Success;
		}

		static readonly string DetectTargetFrameworkIdRefPathPattern =
			@"(Reference Assemblies[/\\]Microsoft[/\\]Framework[/\\](?<1>.NETFramework)[/\\]v(?<2>[^/\\]+)[/\\])" +
			@"|((NuGetFallbackFolder|packs|.nuget[/\\]packages)[/\\](?<1>[^/\\]+)\\(?<2>[^/\\]+)([/\\].*)?[/\\]ref[/\\])";

		public static TargetFramework DetectTargetFramework(Mono.Cecil.AssemblyDefinition assembly, string assemblyPath = null)
		{
			if (assembly == null)
				throw new ArgumentNullException(nameof(assembly));

			var targetFrameworkId = DetectTargetFrameworkId(assembly, assemblyPath);
			string[] tokens = targetFrameworkId.Split(',');
			TargetFrameworkIdentifier identifier;

			switch (tokens[0].Trim().ToUpperInvariant())
			{
				case ".NETCOREAPP":
					identifier = TargetFrameworkIdentifier.NETCoreApp;
					break;
				case ".NETSTANDARD":
					identifier = TargetFrameworkIdentifier.NETStandard;
					break;
				case "SILVERLIGHT":
					identifier = TargetFrameworkIdentifier.Silverlight;
					break;
				default:
					identifier = TargetFrameworkIdentifier.NETFramework;
					break;
			}

			Version version = null;

			for (int i = 1; i < tokens.Length; i++)
			{
				var pair = tokens[i].Trim().Split('=');

				if (pair.Length != 2)
					continue;

				switch (pair[0].Trim().ToUpperInvariant())
				{
					case "VERSION":
						var versionString = pair[1].TrimStart('v', ' ', '\t');
						if (identifier == TargetFrameworkIdentifier.NETCoreApp ||
							identifier == TargetFrameworkIdentifier.NETStandard)
						{
							if (versionString.Length == 3)
								versionString += ".0";
						}
						if (!Version.TryParse(versionString, out version))
							version = null;
						break;
				}
			}

			return new TargetFramework(identifier, version ?? new Version(0, 0, 0, 0));
		}
		public static string DetectTargetFrameworkId(Mono.Cecil.AssemblyDefinition assembly, string assemblyPath = null)
		{
			if (assembly == null)
				throw new ArgumentNullException(nameof(assembly));

			const string TargetFrameworkAttributeName = "System.Runtime.Versioning.TargetFrameworkAttribute";

			foreach (var attribute in assembly.CustomAttributes)
			{
				if (attribute.AttributeType.FullName != TargetFrameworkAttributeName)
					continue;
				if (attribute.HasConstructorArguments)
				{
					if (attribute.ConstructorArguments[0].Value is string value)
						return value;
				}
			}

			// Optionally try to detect target version through assembly path as a fallback (use case: reference assemblies)
			if (assemblyPath != null)
			{
				/*
				 * Detected path patterns (examples):
				 * 
				 * - .NETFramework -> C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.1\mscorlib.dll
				 * - .NETCore      -> C:\Program Files\dotnet\sdk\NuGetFallbackFolder\microsoft.netcore.app\2.1.0\ref\netcoreapp2.1\System.Console.dll
				 *                 -> C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\3.0.0\ref\netcoreapp3.0\System.Runtime.Extensions.dll
				 * - .NETStandard  -> C:\Program Files\dotnet\sdk\NuGetFallbackFolder\netstandard.library\2.0.3\build\netstandard2.0\ref\netstandard.dll
				 */
				var pathMatch = Regex.Match(assemblyPath, DetectTargetFrameworkIdRefPathPattern,
					RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ExplicitCapture);
				if (pathMatch.Success)
				{
					var type = pathMatch.Groups[1].Value;
					var version = pathMatch.Groups[2].Value;

					if (type == ".NETFramework")
					{
						return $".NETFramework,Version=v{version}";
					}
					else if (type.ToLower().Contains("netcore"))
					{
						return $".NETCoreApp,Version=v{version}";
					}
					else if (type.ToLower().Contains("netstandard"))
					{
						return $".NETStandard,Version=v{version}";
					}
				}
			}

			return string.Empty;
		}
	}
}
