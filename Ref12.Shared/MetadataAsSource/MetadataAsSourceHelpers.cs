// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace SLaks.Ref12.MetadataAsSource
{
	internal class MetadataAsSourceHelpers
    {
		public static INamedTypeSymbol GetTopLevelContainingNamedType(ISymbol symbol)
		{
			// Traverse up until we find a named type that is parented by the namespace
			var topLevelNamedType = symbol;
#pragma warning disable RS1024 // Symbols should be compared for equality
			while (topLevelNamedType.ContainingSymbol != symbol.ContainingNamespace ||
				topLevelNamedType.Kind != SymbolKind.NamedType)
			{
				topLevelNamedType = topLevelNamedType.ContainingSymbol;
			}
#pragma warning restore RS1024 // Symbols should be compared for equality

			return (INamedTypeSymbol)topLevelNamedType;
		}

		public static async Task<Location> GetLocationInGeneratedSourceAsync(ISymbol originalSymbol, Document generatedDocument, CancellationToken cancellationToken)
		{
			var workspaceAssembly = typeof(Document).Assembly;
			var symbolKey = workspaceAssembly.GetType("Microsoft.CodeAnalysis.SymbolKey")
					.GetMethod("Create")
					.Invoke(null, new object[] {originalSymbol, null});
			var compilation = await generatedDocument.Project.GetRequiredCompilationAsync(cancellationToken).ConfigureAwait(false);
			var resolution = workspaceAssembly.GetType("Microsoft.CodeAnalysis.SymbolKey")
					.GetMethod("Resolve")
					.Invoke(symbolKey, new object[] { compilation, true, null });
			var symbol = workspaceAssembly.GetType("Microsoft.CodeAnalysis.SymbolKeyResolution")
					.GetProperty("Symbol")
					.GetValue(resolution) as ISymbol;
			var location = symbol != null ? GetFirstSourceLocation(symbol) : null;
			if (location == null)
			{
				var symbols = (ImmutableArray<ISymbol>)workspaceAssembly.GetType("Microsoft.CodeAnalysis.SymbolKeyResolution")
					.GetProperty("CandidateSymbols")
					.GetValue(resolution);
				foreach(var s in symbols)
				{
					location = GetFirstSourceLocation(s);
					if (location != null)
					{
						break;
					}
				}
			}

			if (location == null)
			{
				// If we cannot find the location of the  symbol.  Just put the caret at the 
				// beginning of the file.
				var tree = await generatedDocument.GetRequiredSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
				location = Location.Create(tree, new TextSpan(0, 0));
			}

			return location;

			Location GetFirstSourceLocation(ISymbol mySymbol)
			{
				foreach(var loc in mySymbol.Locations)
				{
					if (loc.IsInSource)
					{
						return loc;
					}
				}
				return null;
			}
		}
	}
}
