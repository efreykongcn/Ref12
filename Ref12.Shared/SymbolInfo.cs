using Microsoft.CodeAnalysis;

namespace SLaks.Ref12
{
	public class SymbolInfo
	{
		public SymbolInfo(Document document,
			ISymbol symbol,
			string assemblyName,
			string assemblyLocation,
			bool isReferenceAssembly,
			bool hasLocalSource,
			string namedTypeFullName)
		{
			ContainingDocument = document;
			Symbol = symbol;
			ImplementationAssemblyName = assemblyName;
			ImplementationAssemblyPath = assemblyLocation;
			IsReferenceAssembly = isReferenceAssembly;
			HasLocalSource = hasLocalSource;
			NamedTypeFullName = namedTypeFullName;
		}
		public string NamedTypeFullName { get; }
		public Document ContainingDocument { get; }
		public ISymbol Symbol { get; }
		public bool IsReferenceAssembly { get; }
		/// <summary>
		/// Gets implementation assembly full name
		/// </summary>
		public string ImplementationAssemblyName { get; }
		/// <summary>
		/// Gets implementation assembly file path
		/// </summary>
		public string ImplementationAssemblyPath { get; }
		public bool HasLocalSource { get; }

	}
}
