using System;
using System.IO;
using ICSharpCode.Decompiler.Metadata;
using Microsoft.VisualStudio.Text;

namespace SLaks.Ref12.Services {
	public interface ISymbolResolver {
		System.Threading.Tasks.Task<SymbolInfo> GetSymbolAtAsync(string sourceFileName, SnapshotPoint point);
	}
	public class SymbolInfo {
		public SymbolInfo(TargetFramework targetFramework, string indexId, bool isLocal, string assemblyPath) : this(targetFramework, indexId, isLocal, assemblyPath, Path.GetFileNameWithoutExtension(assemblyPath)) { }
		public SymbolInfo(TargetFramework targetFramework, string indexId, bool isLocal, string assemblyPath, string assemblyName) {
			this.TargetFramework = targetFramework;
			this.IndexId = indexId;
			this.AssemblyPath = assemblyPath;
			this.AssemblyName = assemblyName;
			this.HasLocalSource = isLocal;
		}
		public TargetFramework TargetFramework { get; }
		public string IndexId { get; private set; }
		public string AssemblyPath { get; private set; }
		public string AssemblyName { get; private set; }

		///<summary>Indicates whether this symbol is defined in the current solution.</summary>
		public bool HasLocalSource { get; private set; }
	}

	public sealed class TargetFramework
	{
		public TargetFramework(TargetFrameworkIdentifier targetFrameworkIdentifier, Version version)
		{
			this.Identifier = targetFrameworkIdentifier;
			this.Version = version;
		}
		public TargetFrameworkIdentifier Identifier { get; }
		public Version Version { get; }
	}
}
