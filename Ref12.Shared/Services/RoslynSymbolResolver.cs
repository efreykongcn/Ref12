using System;
using System.Linq;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using SymbolKind = Microsoft.CodeAnalysis.SymbolKind;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using System.Reflection.Metadata;

namespace SLaks.Ref12.Services {
	public class RoslynSymbolResolver : ISymbolResolver {
		public async System.Threading.Tasks.Task<SymbolInfo> GetSymbolAtAsync(string sourceFileName, SnapshotPoint point) {
			// Yes; this is evil and synchronously waits for async tasks.
			// That is exactly what Roslyn's GoToDefinitionCommandHandler
			// does; apparently a VS command handler can't be truly async
			// (Roslyn does use IWaitIndicator, which I can't).

			var doc = point.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
			var model = await doc.GetSemanticModelAsync();
			var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, point, doc.Project.Solution.Workspace);
			if (symbol == null || symbol.ContainingAssembly == null)
				return null;

			if (symbol.Kind == SymbolKind.Local || symbol.Kind == SymbolKind.Namespace)
				return null;

			// F12 on the declaration of a lambda parameter should jump to its type; all other parameters shouldn't be handled at all.
			var param = symbol as IParameterSymbol;
			if (param != null) {
				var method = param.ContainingSymbol as IMethodSymbol;
				if (method == null || method.MethodKind != MethodKind.LambdaMethod)
					return null;
				if (param.Locations.Length != 1)
					return null;

				if (param.Locations[0].IsInSource
				 && !param.Locations[0].SourceSpan.Contains(point)
				 && param.Locations[0].SourceSpan.End != point)		// Contains() is exclusive
					return null;
				else
					symbol = param.Type;
			}
			symbol = IndexIdTranslator.GetTargetSymbol(symbol);

			PortableExecutableReference reference = null;
			Compilation comp;
			if (doc.Project.TryGetCompilation(out comp))
				reference = comp.GetMetadataReference(symbol.ContainingAssembly) as PortableExecutableReference;

			string assemblyPath, assemblyName = symbol.ContainingAssembly.Identity.Name;
			TargetFramework targetFramework = null;
			if (reference != null)
			{
				assemblyPath = reference.Display;
				using (var assemblyDef = AssemblyDefinition.ReadAssembly(assemblyPath))
				{
					targetFramework = AssemblyFileFinder.DetectTargetFramework(assemblyDef);
					if (targetFramework.Identifier != TargetFrameworkIdentifier.NETFramework && targetFramework.Identifier != TargetFrameworkIdentifier.Silverlight)
					{
						var resolvedAssemblyFile = AssemblyFileFinder.FindAssemblyFile(assemblyDef, assemblyPath);

						var asm = new LoadedAssembly(resolvedAssemblyFile);
						IEntity mr = FindEntityInRelevantAssemblies((symbol.OriginalDefinition ?? symbol).GetDocumentationCommentId(), asm);
						if (mr != null && mr.ParentModule.PEFile != null)
						{
							assemblyPath = mr.ParentModule.PEFile.FullName;
							assemblyName = mr.ParentModule.AssemblyName;
						}
					}
				}
			}
			else
			{
				assemblyPath = null;
			}

			return new SymbolInfo(
					targetFramework,
					IndexIdTranslator.GetId(symbol),
					isLocal: doc.Project.Solution.Workspace.Kind != WorkspaceKind.MetadataAsSource && doc.Project.Solution.GetProject(symbol.ContainingAssembly) != null,
					assemblyPath: assemblyPath,
					assemblyName: assemblyName
				);
		}
		internal static IEntity FindEntityInRelevantAssemblies(string navigateTo, LoadedAssembly asm)
		{
			IMemberReference memberRef = null;
			ITypeReference typeRef;
			if (navigateTo.StartsWith("T:", StringComparison.Ordinal))
			{
				typeRef = IdStringProvider.ParseTypeName(navigateTo);
			}
			else
			{
				memberRef = IdStringProvider.ParseMemberIdString(navigateTo);
				typeRef = memberRef.DeclaringTypeReference;
			}
			var module = asm.GetPEFileOrNull();
			if (CanResolveTypeInPEFile(module, typeRef, out var typeHandle))
			{
				ICompilation compilation = typeHandle.Kind == HandleKind.ExportedType
					? new DecompilerTypeSystem(module, module.GetAssemblyResolver())
					: new SimpleCompilation(module, MinimalCorlib.Instance);
				return memberRef == null
					? typeRef.Resolve(new SimpleTypeResolveContext(compilation)) as ITypeDefinition
					: (IEntity)memberRef.Resolve(new SimpleTypeResolveContext(compilation));
			}
			return null;
		}

		static bool CanResolveTypeInPEFile(PEFile module, ITypeReference typeRef, out EntityHandle typeHandle)
		{
			if (module == null)
			{
				typeHandle = default;
				return false;
			}

			// We intentionally ignore reference assemblies, so that the loop continues looking for another assembly that might have a usable definition.
			if (module.IsReferenceAssembly())
			{
				typeHandle = default;
				return false;
			}

			switch (typeRef)
			{
				case GetPotentiallyNestedClassTypeReference topLevelType:
					typeHandle = topLevelType.ResolveInPEFile(module);
					return !typeHandle.IsNil;
				case NestedTypeReference nestedType:
					if (!CanResolveTypeInPEFile(module, nestedType.DeclaringTypeReference, out typeHandle))
						return false;
					if (typeHandle.Kind == HandleKind.ExportedType)
						return true;
					var typeDef = module.Metadata.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
					typeHandle = typeDef.GetNestedTypes().FirstOrDefault(t => {
						var td = module.Metadata.GetTypeDefinition(t);
						var typeName = ReflectionHelper.SplitTypeParameterCountFromReflectionName(module.Metadata.GetString(td.Name), out int typeParameterCount);
						return nestedType.AdditionalTypeParameterCount == typeParameterCount && nestedType.Name == typeName;
					});
					return !typeHandle.IsNil;
				default:
					typeHandle = default;
					return false;
			}
		}
	}

}
