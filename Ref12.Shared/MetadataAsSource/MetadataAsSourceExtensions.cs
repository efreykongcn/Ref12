// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using VsTextSpan = Microsoft.VisualStudio.TextManager.Interop.TextSpan;

namespace SLaks.Ref12.MetadataAsSource
{
	internal static partial class MetadataAsSourceExtensions
    {
		public static async Task<Compilation> GetRequiredCompilationAsync(this Project project, CancellationToken cancellationToken)
		{
			var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
			if (compilation == null)
			{
				throw new InvalidOperationException($"Compilation is required to accomplish the task but is not supported by project: {project.Name}");
			}

			return compilation;
		}
		public static async ValueTask<SyntaxTree> GetRequiredSyntaxTreeAsync(this Document document, CancellationToken cancellationToken)
		{
			if (document.TryGetSyntaxTree(out var syntaxTree))
				return syntaxTree;

			syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
			return syntaxTree ?? throw new InvalidOperationException($"SyntaxTree is required to accomplish the task but is not supported by document: {document.Name}");
		}

		public static V GetOrAdd<K, V>(this IDictionary<K, V> dictionary, K key, Func<K, V> callback)
		{
			if (!dictionary.TryGetValue(key, out var value))
			{
				value = callback(key);
				dictionary.Add(key, value);
			}

			return value;
		}

		public static INamedTypeSymbol GetContainingTypeOrThis(this ISymbol symbol)
		{
			if (symbol is INamedTypeSymbol namedType)
			{
				return namedType;
			}

			return symbol.ContainingType;
		}

		public static Document GetRequiredDocument(this Solution solution, DocumentId documentId)
			=> solution.GetDocument(documentId) ?? throw CreateDocumentNotFoundException();
		private static Exception CreateDocumentNotFoundException()
			=> new InvalidOperationException("The solution does not contain the specified document");

		public static async ValueTask<SyntaxNode> GetRequiredSyntaxRootAsync(this Document document, CancellationToken cancellationToken)
		{
			if (document.TryGetSyntaxRoot(out var root))
				return root;

			root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
			return root ?? throw new InvalidOperationException($"SyntaxTree is required to accomplish the task but is not supported by document: {document.Name}");
		}

		public static void GetLineAndOffset(this SourceText text, int position, out int lineNumber, out int offset)
		{
			var line = text.Lines.GetLineFromPosition(position);

			lineNumber = line.LineNumber;
			offset = position - line.Start;
		}
		public static VsTextSpan GetVsTextSpanForSpan(this SourceText text, TextSpan textSpan)
		{
			text.GetLinesAndOffsets(textSpan, out var startLine, out var startOffset, out var endLine, out var endOffset);

			return new VsTextSpan()
			{
				iStartLine = startLine,
				iStartIndex = startOffset,
				iEndLine = endLine,
				iEndIndex = endOffset
			};
		}
		public static void GetLinesAndOffsets(
			this SourceText text,
			TextSpan textSpan,
			out int startLineNumber,
			out int startOffset,
			out int endLineNumber,
			out int endOffset)
		{
			text.GetLineAndOffset(textSpan.Start, out startLineNumber, out startOffset);
			text.GetLineAndOffset(textSpan.End, out endLineNumber, out endOffset);
		}

		public static VsTextSpan GetVsTextSpanForPosition(this SourceText text, int position, int virtualSpace)
		{
			text.GetLineAndOffset(position, out var lineNumber, out var offset);

			offset += virtualSpace;

			return text.GetVsTextSpanForLineOffset(lineNumber, offset);
		}
#pragma warning disable IDE0060 // Remove unused parameter - 'text' is used for API consistency with other extension methods in this file.
		public static VsTextSpan GetVsTextSpanForLineOffset(this SourceText text, int lineNumber, int offset)
#pragma warning restore IDE0060 // Remove unused parameter
		{
			return new VsTextSpan
			{
				iStartLine = lineNumber,
				iStartIndex = offset,
				iEndLine = lineNumber,
				iEndIndex = offset
			};
		}

		/// <summary>
		/// Gets the documents from the corresponding workspace's current solution that are associated with the text container. 
		/// </summary>
		public static ImmutableArray<Document> GetRelatedDocuments(this SourceTextContainer container)
		{
			if (Workspace.TryGetWorkspace(container, out var workspace))
			{
				Solution solution = workspace.CurrentSolution;
				var documentId = workspace.GetDocumentIdInCurrentContext(container);
				if (documentId != null)
				{
					var result = typeof(Document).Assembly
								.GetType("Microsoft.CodeAnalysis.Solution")
								.GetMethod("GetRelatedDocumentIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new Type[] { typeof(DocumentId) }, null)
								.Invoke(solution, new object[] { documentId });
					var relatedIds = (ImmutableArray<DocumentId>)result;

					return ImmutableArray.CreateRange(relatedIds, (id, mySolution) => mySolution.GetRequiredDocument(id), solution);
				}
			}

			return ImmutableArray<Document>.Empty;
		}
	}
}
