// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ISymbol = Microsoft.CodeAnalysis.ISymbol;

namespace SLaks.Ref12.MetadataAsSource
{
	internal class DecompilationMetadaAsSourceFileService
    {
		private static readonly FileVersionInfo s_decompilerVersion = FileVersionInfo.GetVersionInfo(typeof(CSharpDecompiler).Assembly.Location);
		private readonly Dictionary<UniqueDocumentKey, MetadataAsSourceGeneratedFileInfo> _keyToInformation = new Dictionary<UniqueDocumentKey, MetadataAsSourceGeneratedFileInfo>();

		private readonly Dictionary<string, MetadataAsSourceGeneratedFileInfo> _generatedFilenameToInformation = new Dictionary<string, MetadataAsSourceGeneratedFileInfo>(StringComparer.OrdinalIgnoreCase);
		private IBidirectionalMap<MetadataAsSourceGeneratedFileInfo, DocumentId> _openedDocumentIds = BidirectionalMap<MetadataAsSourceGeneratedFileInfo, DocumentId>.Empty;
		public async Task<MetadataAsSourceFile> GetGeneratedFileAsync(Workspace workspace, SymbolInfo symbolInfo, string tempPath, CancellationToken cancellationToken)
		{
			if (symbolInfo == null)
			{
				throw new ArgumentNullException(nameof(symbolInfo));
			}
			var symbol = symbolInfo.Symbol;
			var project = symbolInfo.ContainingDocument.Project;
			Location navigateLocation = null;
			var topLevelNamedType = MetadataAsSourceHelpers.GetTopLevelContainingNamedType(symbol);
			var compilation = await project.GetRequiredCompilationAsync(cancellationToken).ConfigureAwait(false);
			var topLevelNamedTypeFullName = symbolInfo.NamedTypeFullName;//GetFullReflectionName(topLevelNamedType);
			var reference = MetadataReference.CreateFromFile(symbolInfo.ImplementationAssemblyPath);
			var infoKey = new UniqueDocumentKey(topLevelNamedTypeFullName, symbolInfo.ImplementationAssemblyPath);
			var fileInfo = _keyToInformation.GetOrAdd(infoKey, _ => new MetadataAsSourceGeneratedFileInfo(tempPath, project, topLevelNamedType, symbolInfo.ImplementationAssemblyPath));

			_generatedFilenameToInformation[fileInfo.TemporaryFilePath] = fileInfo;

			if (!File.Exists(fileInfo.TemporaryFilePath))
			{
				// We need to generate this. First, we'll need a temporary project to do the generation into. We
				// avoid loading the actual file from disk since it doesn't exist yet.
				var temporaryProjectInfoAndDocumentId = fileInfo.GetProjectInfoAndDocumentId(loadFileFromDisk: false);
				var solution = workspace.CurrentSolution.AddProject(temporaryProjectInfoAndDocumentId.Item1);
				
				var temporaryDocument = solution.GetRequiredDocument(temporaryProjectInfoAndDocumentId.Item2);
				try
				{
					temporaryDocument = await AddSourceToAsync(temporaryDocument, compilation, symbolInfo, cancellationToken).ConfigureAwait(false);
				}
				catch(Exception e)
				{
					Debug.WriteLine(e.Message);
				}
				// We have the content, so write it out to disk
				var text = await temporaryDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);

				// Create the directory. It's possible a parallel deletion is happening in another process, so we may have
				// to retry this a few times.
				var directoryToCreate = Path.GetDirectoryName(fileInfo.TemporaryFilePath);
				while (!Directory.Exists(directoryToCreate))
				{
					try
					{
						Directory.CreateDirectory(directoryToCreate);
					}
					catch (DirectoryNotFoundException)
					{
					}
					catch (UnauthorizedAccessException)
					{
					}
				}

				using (var textWriter = new StreamWriter(fileInfo.TemporaryFilePath, append: false, encoding: MetadataAsSourceGeneratedFileInfo.Encoding))
				{
					text.Write(textWriter, cancellationToken);
				}

				// Mark read-only
				new FileInfo(fileInfo.TemporaryFilePath).IsReadOnly = true;

				// Locate the target in the thing we just created
				navigateLocation = await MetadataAsSourceHelpers.GetLocationInGeneratedSourceAsync(symbol, temporaryDocument, cancellationToken).ConfigureAwait(false);
			}

			// If we don't have a location yet, then that means we're re-using an existing file. In this case, we'll want to relocate the symbol.
			if (navigateLocation == null)
			{
				navigateLocation = await RelocateSymbol_NoLockAsync(workspace, fileInfo, symbol, cancellationToken).ConfigureAwait(false);
			}

			var documentName = string.Format(
				"{0} [{1}]",
				topLevelNamedType.Name,
				"Ref12");

			var documentTooltip = topLevelNamedType.ToDisplayString(new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces));

			return new MetadataAsSourceFile(fileInfo.TemporaryFilePath, navigateLocation, documentName, documentTooltip);
		}

		private async Task<Location> RelocateSymbol_NoLockAsync(Workspace workspace, MetadataAsSourceGeneratedFileInfo fileInfo, ISymbol symbol, CancellationToken cancellationToken)
		{
			// We need to relocate the symbol in the already existing file. If the file is open, we can just
			// reuse that workspace. Otherwise, we have to go spin up a temporary project to do the binding.
			if (_openedDocumentIds.TryGetValue(fileInfo, out var openDocumentId))
			{
				// Awesome, it's already open. Let's try to grab a document for it
				var document = workspace.CurrentSolution.GetRequiredDocument(openDocumentId);

				return await MetadataAsSourceHelpers.GetLocationInGeneratedSourceAsync(symbol, document, cancellationToken).ConfigureAwait(false);
			}

			// Annoying case: the file is still on disk. Only real option here is to spin up a fake project to go and bind in.
			var temporaryProjectInfoAndDocumentId = fileInfo.GetProjectInfoAndDocumentId(loadFileFromDisk: true);
			var temporaryDocument = workspace.CurrentSolution.AddProject(temporaryProjectInfoAndDocumentId.Item1)
															 .GetRequiredDocument(temporaryProjectInfoAndDocumentId.Item2);

			return await MetadataAsSourceHelpers.GetLocationInGeneratedSourceAsync(symbol, temporaryDocument, cancellationToken).ConfigureAwait(false);
		}

		public async Task<Document> AddSourceToAsync(Document document, Compilation symbolCompilation, SymbolInfo symbolInfo, CancellationToken cancellationToken)
		{
			// Get the name of the type the symbol is in
			//var containingOrThis = symbol.GetContainingTypeOrThis();
			//var fullName = GetFullReflectionName(containingOrThis);
			//Debug.WriteLine($"GetFullReflectionName: {fullName}");
			// Decompile
			document = PerformDecompilation(document, symbolCompilation, symbolInfo);

			return await AddAssemblyInfoRegionAsync(document, symbolInfo, cancellationToken).ConfigureAwait(false);
		}

		private static async Task<Document> AddAssemblyInfoRegionAsync(Document document, SymbolInfo symbolInfo, CancellationToken cancellationToken)
		{
			var assemblyInfo = string.Format(
					"{0} {1}",
					"Assembly",
					symbolInfo.ImplementationAssemblyName);
			var assemblyPath = symbolInfo.ImplementationAssemblyPath;

			var regionTrivia = SyntaxFactory.RegionDirectiveTrivia(true)
				.WithTrailingTrivia(new[] { SyntaxFactory.Space, SyntaxFactory.PreprocessingMessage(assemblyInfo) });

			var oldRoot = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
			var newRoot = oldRoot.WithLeadingTrivia(new[]
				{
					SyntaxFactory.Trivia(regionTrivia),
					SyntaxFactory.CarriageReturnLineFeed,
					SyntaxFactory.Comment("// " + assemblyPath),
					SyntaxFactory.CarriageReturnLineFeed,
					SyntaxFactory.Comment($"// Decompiled with ICSharpCode.Decompiler {s_decompilerVersion.FileVersion}"),
					SyntaxFactory.CarriageReturnLineFeed,
					SyntaxFactory.Trivia(SyntaxFactory.EndRegionDirectiveTrivia(true)),
					SyntaxFactory.CarriageReturnLineFeed,
					SyntaxFactory.CarriageReturnLineFeed
				});

			return document.WithSyntaxRoot(newRoot);
		}

		private static Document PerformDecompilation(Document document, Compilation compilation, SymbolInfo symbol)
		{
			string fullName = symbol.NamedTypeFullName;
			string assemblyLocation = symbol.ImplementationAssemblyPath;
			// Initialize a decompiler with default settings.
			var decompiler = new CSharpDecompiler(assemblyLocation, new DecompilerSettings());
			// Escape invalid identifiers to prevent Roslyn from failing to parse the generated code.
			// (This happens for example, when there is compiler-generated code that is not yet recognized/transformed by the decompiler.)
			decompiler.AstTransforms.Add(new EscapeInvalidIdentifiers());

			var fullTypeName = new FullTypeName(fullName);

			// Try to decompile; if an exception is thrown the caller will handle it
			string text;
			try
			{
				text = decompiler.DecompileTypeAsString(fullTypeName);
			}
			catch(Exception e)
			{
				text = "#if false // Decompilation log" + Environment.NewLine;
				text += e.Message;
				text += "#endif" + Environment.NewLine;
			}

			return document.WithText(SourceText.From(text));
		}

		public bool IsFileGeneratedByMe(string filename)
		{
			return _generatedFilenameToInformation.ContainsKey(filename);
		}

		public bool TryAddDocumentToWorkspace(Workspace workspace, string filePath, SourceTextContainer sourceTextContainer)
		{
			if (_generatedFilenameToInformation.TryGetValue(filePath, out var fileInfo))
			{
				if (_openedDocumentIds.ContainsKey(fileInfo))
				{
					throw new InvalidOperationException("Unexpected true");
				}

				// We do own the file, so let's open it up in our workspace
				var newProjectInfoAndDocumentId = fileInfo.GetProjectInfoAndDocumentId(loadFileFromDisk: true);

				//workspace.OnProjectAdded(newProjectInfoAndDocumentId.Item1);
				typeof(Workspace).GetMethod("OnProjectAdded", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(ProjectInfo) }, null)
						.Invoke(workspace, new object[] { newProjectInfoAndDocumentId.Item1 });

				//workspace.OnDocumentOpened(newProjectInfoAndDocumentId.Item2, sourceTextContainer);
				typeof(Workspace).GetMethod("OnDocumentOpened", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(DocumentId), typeof(SourceTextContainer), typeof(bool) }, null)
						.Invoke(workspace, new object[] { newProjectInfoAndDocumentId.Item2, sourceTextContainer, true });

				_openedDocumentIds = _openedDocumentIds.Add(fileInfo, newProjectInfoAndDocumentId.Item2);

				return true;
			}

			return false;
		}

		public bool TryRemoveDocumentFromWorkspace(Workspace workspace, string filePath)
		{
			if (_generatedFilenameToInformation.TryGetValue(filePath, out var fileInfo))
			{
				if (_openedDocumentIds.ContainsKey(fileInfo))
				{
					return RemoveDocumentFromWorkspace(workspace, fileInfo);
				}
			}

			return false;
		}

		private bool RemoveDocumentFromWorkspace(Workspace workspace, MetadataAsSourceGeneratedFileInfo fileInfo)
		{
			var documentId = _openedDocumentIds.GetValueOrDefault(fileInfo);
			if (documentId == null)
			{
				throw new InvalidOperationException("Unexcepted null");
			}

			//workspace.OnDocumentClosed(documentId, new FileTextLoader(fileInfo.TemporaryFilePath, MetadataAsSourceGeneratedFileInfo.Encoding));
			typeof(Workspace).GetMethod("OnDocumentClosed", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(DocumentId), typeof(TextLoader), typeof(bool) }, null)
					.Invoke(workspace, new object[] { documentId, new FileTextLoader(fileInfo.TemporaryFilePath, MetadataAsSourceGeneratedFileInfo.Encoding), false });
			//workspace.OnProjectRemoved(documentId.ProjectId);
			typeof(Workspace).GetMethod("OnProjectRemoved", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(ProjectId) }, null)
					.Invoke(workspace, new object[] { documentId.ProjectId });

			_openedDocumentIds = _openedDocumentIds.RemoveKey(fileInfo);

			return true;
		}

		public void CleanupGeneratedFiles(Workspace workspace)
		{
			// Clone the list so we don't break our own enumeration
			var generatedFileInfoList = _generatedFilenameToInformation.Values.ToList();

			foreach (var generatedFileInfo in generatedFileInfoList)
			{
				if (_openedDocumentIds.ContainsKey(generatedFileInfo))
				{
					if (workspace == null)
					{
						throw new InvalidOperationException("Unexcepted null");
					}

					RemoveDocumentFromWorkspace(workspace, generatedFileInfo);
				}
			}

			_generatedFilenameToInformation.Clear();
			_keyToInformation.Clear();
			if (!_openedDocumentIds.IsEmpty)
			{
				throw new InvalidOperationException("Unexcepted false");
			}
		}

		private class UniqueDocumentKey : IEquatable<UniqueDocumentKey>
		{
			private readonly string _topLevelNamedTypeFullName;
			private readonly string _assemblyFilePath;
			private readonly MetadataId _metadataId;
			public UniqueDocumentKey(string namedTypeFullName, string assemblyFilePath)
			{
				_topLevelNamedTypeFullName = namedTypeFullName;
				_assemblyFilePath = assemblyFilePath;
				var reference = MetadataReference.CreateFromFile(assemblyFilePath);
				_metadataId = reference.GetMetadataId();
			}
			public bool Equals(UniqueDocumentKey other)
			{
				if (other == null)
				{
					return false;
				}

				return StringComparer.OrdinalIgnoreCase.Equals(_topLevelNamedTypeFullName, other._topLevelNamedTypeFullName) &&
					StringComparer.OrdinalIgnoreCase.Equals(_assemblyFilePath, other._assemblyFilePath) &&
					object.Equals(_metadataId, other._metadataId);
			}

			public override bool Equals(object obj)
				=> Equals(obj as UniqueDocumentKey);

			public override int GetHashCode()
			{
				return
					HashCombine(StringComparer.OrdinalIgnoreCase.GetHashCode(_topLevelNamedTypeFullName ?? string.Empty),
						HashCombine(StringComparer.OrdinalIgnoreCase.GetHashCode(_assemblyFilePath ?? string.Empty),
							_metadataId?.GetHashCode() ?? 0));
			}

			internal static int HashCombine(int newKey, int currentKey)
			{
				return unchecked((currentKey * (int)0xA5555529) + newKey);
			}
		}

		sealed class MetadataAsSourceGeneratedFileInfo
		{
			public readonly ProjectId SourceProjectId;
			public readonly Workspace Workspace;
			public readonly AssemblyName AssemblyName;
			private readonly string Version;
			public readonly ImmutableArray<MetadataReference> References;

			public readonly string TemporaryFilePath;
			public MetadataAsSourceGeneratedFileInfo(string rootPath, Project sourceProject, INamedTypeSymbol topLevelNamedType, string assemblyFilePath)
			{
				this.SourceProjectId = sourceProject.Id;
				this.Workspace = sourceProject.Solution.Workspace;
				this.References = sourceProject.MetadataReferences.ToImmutableArray();
				this.AssemblyName = AssemblyName.GetAssemblyName(assemblyFilePath);
				//this.AssemblyName = Assembly.ReflectionOnlyLoadFrom(assemblyFilePath).GetName();
				Version = FileVersionInfo.GetVersionInfo(assemblyFilePath).FileVersion;				
				this.TemporaryFilePath = Path.Combine(rootPath,
					Guid.NewGuid().ToString("N"), 
					topLevelNamedType.MetadataName + ".cs");
			}

			public static Encoding Encoding => Encoding.UTF8;

			/// <summary>
			/// Creates a ProjectInfo to represent the fake project created for metadata as source documents.
			/// </summary>
			/// <param name="workspace">The containing workspace.</param>
			/// <param name="loadFileFromDisk">Whether the source file already exists on disk and should be included. If
			/// this is a false, a document is still created, but it's not backed by the file system and thus we won't
			/// try to load it.</param>
			public Tuple<ProjectInfo, DocumentId> GetProjectInfoAndDocumentId(bool loadFileFromDisk)
			{
				var projectId = ProjectId.CreateNewId();

				var extension = ".cs";

				// We need to include the version information of the assembly so InternalsVisibleTo and stuff works
				var assemblyInfoDocumentId = DocumentId.CreateNewId(projectId);
				var assemblyInfoFileName = "AssemblyInfo" + extension;
				var assemblyInfoString = string.Format(@"[assembly: System.Reflection.AssemblyVersion(""{0}"")]", Version);

				var assemblyInfoSourceTextContainer = SourceText.From(assemblyInfoString, Encoding).Container;

				var assemblyInfoDocument = DocumentInfo.Create(
					assemblyInfoDocumentId,
					assemblyInfoFileName,
					loader: TextLoader.From(assemblyInfoSourceTextContainer, VersionStamp.Default));

				var generatedDocumentId = DocumentId.CreateNewId(projectId);
				var generatedDocument = DocumentInfo.Create(
					generatedDocumentId,
					Path.GetFileName(TemporaryFilePath),
					filePath: TemporaryFilePath,
					loader: loadFileFromDisk ? new FileTextLoader(TemporaryFilePath, Encoding) : null);

				var projectInfo = ProjectInfo.Create(
					projectId,
					VersionStamp.Default,
					name: AssemblyName.Name,
					assemblyName: AssemblyName.Name,
					language: LanguageNames.CSharp,
					documents: new[] { assemblyInfoDocument, generatedDocument },
					metadataReferences: References);

				return Tuple.Create(projectInfo, generatedDocumentId);
			}
		}
			
	}
}
