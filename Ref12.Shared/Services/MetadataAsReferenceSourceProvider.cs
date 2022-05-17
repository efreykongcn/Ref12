// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using ICSharpCode.Decompiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using SLaks.Ref12.MetadataAsSource;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using VsTextSpan = Microsoft.VisualStudio.TextManager.Interop.TextSpan;

namespace SLaks.Ref12.Services
{
	/*
	 * For the native `Go To Definition` command, Decompilaton metadata as source function is still not working as expected for .NETCore project,
	 * Visual Studio only shows METADATA definition due to the decompile service cannot resolve the implementation aseembly for the metadata reference assembly of .NETCore.
	 * MetadataAsReferenceSourceProvider is designed to support .NETCore/.NET5-6 metadata reference decompilation for `Go To Definition - Native` command
	 */
	internal class MetadataAsReferenceSourceProvider : IReferenceSourceProvider
	{
		private readonly IServiceProvider _serviceProvider;
		private readonly IVsEditorAdaptersFactoryService _editorAdaptersFactoryService;
		private readonly IMetadataAsSourceFileService _metadataAsSourceFileService;
		public MetadataAsReferenceSourceProvider(IServiceProvider serviceProvider,
					IVsEditorAdaptersFactoryService editorAdaptersFactoryService,
					IMetadataAsSourceFileService metadataAsSourceFileService)
		{
			_serviceProvider = serviceProvider;
			_editorAdaptersFactoryService = editorAdaptersFactoryService;
			_metadataAsSourceFileService = metadataAsSourceFileService;
		}
		public bool Supports(TargetFramework targetFramework)
		{
			switch (targetFramework.Identifier)
			{
				case TargetFrameworkIdentifier.NETCoreApp:
				case TargetFrameworkIdentifier.NET:
				case TargetFrameworkIdentifier.NETStandard:
					return true;
			}
			return false;
		}
		public bool CanNavigate(SymbolInfo symbol)
		{
			// Only intercept reference assembly.
			return symbol.IsReferenceAssembly;
		}
		public async Task<bool> TryToNavigateAsync(SymbolInfo symbolInfo, CancellationToken cancellationToken = default)
		{
			if (symbolInfo == null)
			{
				throw new ArgumentNullException(nameof(symbolInfo));
			}
			var symbol = symbolInfo.Symbol;
			var useDecompiler = !symbol.ContainingAssembly.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == nameof(SuppressIldasmAttribute)
					&& attribute.AttributeClass.ToDisplayString(NameFormat) == typeof(SuppressIldasmAttribute).FullName);
			if (!useDecompiler)
			{
				return false;
			}

			var result = await _metadataAsSourceFileService.GetGeneratedFileAsync(symbolInfo, cancellationToken);

			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
			//Navigation
			var vsRunningDocumentTable4 = _serviceProvider.GetServiceOnMainThread<SVsRunningDocumentTable, IVsRunningDocumentTable4>();
			var fileAlreadyOpen = vsRunningDocumentTable4.IsMonikerValid(result.FilePath);

			var openDocumentService = _serviceProvider.GetServiceOnMainThread<SVsUIShellOpenDocument, IVsUIShellOpenDocument>();
			openDocumentService.OpenDocumentViaProject(result.FilePath, VSConstants.LOGVIEWID.TextView_guid, out _, out _, out _, out var windowFrame);

			var documentCookie = vsRunningDocumentTable4.GetDocumentCookie(result.FilePath);

			var vsTextBuffer = (IVsTextBuffer)vsRunningDocumentTable4.GetDocumentData(documentCookie);

			// Set the buffer to read only, just in case the file isn't
			ErrorHandler.ThrowOnFailure(vsTextBuffer.GetStateFlags(out var flags));
			flags |= (int)BUFFERSTATEFLAGS.BSF_USER_READONLY;
			ErrorHandler.ThrowOnFailure(vsTextBuffer.SetStateFlags(flags));

			var textBuffer = _editorAdaptersFactoryService.GetDataBuffer(vsTextBuffer);

			if (!fileAlreadyOpen)
			{
				ErrorHandler.ThrowOnFailure(windowFrame.SetProperty((int)__VSFPROPID5.VSFPROPID_IsProvisional, true));
				ErrorHandler.ThrowOnFailure(windowFrame.SetProperty((int)__VSFPROPID5.VSFPROPID_OverrideCaption, result.DocumentTitle));
				ErrorHandler.ThrowOnFailure(windowFrame.SetProperty((int)__VSFPROPID5.VSFPROPID_OverrideToolTip, result.DocumentTooltip));
			}

			windowFrame.Show();

			var openedDocument = textBuffer?.AsTextContainer().GetRelatedDocuments().FirstOrDefault();
			//var openedDocument = GetDocument(_workspace, result.FilePath);
			if (openedDocument != null)
			{
				var editorWorkspace = openedDocument.Project.Solution.Workspace;
				await TryNavigateToSpanAsync(
					editorWorkspace,
					openedDocument,
					result.IdentifierLocation.SourceSpan,
					cancellationToken).ConfigureAwait(false);
			}

			return true;
		}

		private async Task<bool> TryNavigateToSpanAsync(Workspace workspace, Document document, TextSpan textSpan, CancellationToken cancellationToken)
		{
			if (!workspace.IsDocumentOpen(document.Id))
				return false;

			// Reacquire the SourceText for it as well.  This will be a practically free as this just wraps
			// the open text buffer.  So it's ok to do this in the navigation step.
			var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);


			// Map the given span to the right location in the buffer.  If we're in a projection scenario, ensure
			// the span reflects that.
			var vsTextSpan = GetVsTextSpan(text, textSpan, allowInvalidSpan: false);

			return await NavigateToTextBufferAsync(
				text.Container.GetTextBuffer(), vsTextSpan, cancellationToken).ConfigureAwait(false);
		}

		public async Task<bool> NavigateToTextBufferAsync(
			ITextBuffer textBuffer, VsTextSpan vsTextSpan, CancellationToken cancellationToken)
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
			var vsTextBuffer = _editorAdaptersFactoryService.GetBufferAdapter(textBuffer);
			if (vsTextBuffer == null)
			{
				Debug.Fail("Could not get IVsTextBuffer for document!");
				return false;
			}

			var textManager = (IVsTextManager2)_serviceProvider.GetService(typeof(SVsTextManager));
			if (textManager == null)
			{
				Debug.Fail("Could not get IVsTextManager service!");
				return false;
			}

			return ErrorHandler.Succeeded(
				textManager.NavigateToLineAndColumn2(
					vsTextBuffer,
					VSConstants.LOGVIEWID.TextView_guid,
					vsTextSpan.iStartLine,
					vsTextSpan.iStartIndex,
					vsTextSpan.iEndLine,
					vsTextSpan.iEndIndex,
					(uint)_VIEWFRAMETYPE.vftCodeWindow));
		}

		static async Task<TextSpan> GetTextSpanFromPositionAsync(Document document, int position, int virtualSpace, CancellationToken cancellationToken)
		{
			var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
			text.GetLineAndOffset(position, out var lineNumber, out var offset);

			offset += virtualSpace;

			var linePosition = new LinePosition(lineNumber, offset);
			return text.Lines.GetTextSpan(new LinePositionSpan(linePosition, linePosition));
		}

		private static VsTextSpan GetVsTextSpan(SourceText text, TextSpan textSpan, bool allowInvalidSpan)
		{
			var boundedTextSpan = GetSpanWithinDocumentBounds(textSpan, text.Length);
			if (boundedTextSpan != textSpan && !allowInvalidSpan)
			{
				//try
				//{
				//	throw new ArgumentOutOfRangeException();
				//}
				//catch (ArgumentOutOfRangeException e)
				//{
				//}
			}

			return text.GetVsTextSpanForSpan(boundedTextSpan);
		}
		/// <summary>
		/// It is unclear why, but we are sometimes asked to navigate to a <see cref="TextSpan"/>
		/// that is not inside the bounds of the associated <see cref="Document"/>. This method
		/// returns a span that is guaranteed to be inside the <see cref="Document"/> bounds. If
		/// the returned span is different from the given span, then the worst observable behavior
		/// is either no navigation or navigation to the end of the document.
		/// See https://github.com/dotnet/roslyn/issues/7660 for more details.
		/// </summary>
		private static TextSpan GetSpanWithinDocumentBounds(TextSpan span, int documentLength)
			=> TextSpan.FromBounds(GetPositionWithinDocumentBounds(span.Start, documentLength), GetPositionWithinDocumentBounds(span.End, documentLength));

		private static int GetPositionWithinDocumentBounds(int position, int documentLength)
			=> Math.Min(documentLength, Math.Max(position, 0));

		[Obsolete("Use TryNavigateToSpanAsync method instead")]
		public void Navigate(SymbolInfo symbol)
		{
			TryToNavigateAsync(symbol).GetAwaiter().GetResult();
		}

		/// <summary>
		/// Standard format for displaying to the user.
		/// </summary>
		/// <remarks>
		/// No return type.
		/// </remarks>
		public static readonly SymbolDisplayFormat NameFormat =
			new SymbolDisplayFormat(
				globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
				typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
				propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
				genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
				memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeExplicitInterface,
				parameterOptions:
					SymbolDisplayParameterOptions.IncludeParamsRefOut |
					SymbolDisplayParameterOptions.IncludeExtensionThis |
					SymbolDisplayParameterOptions.IncludeType |
					SymbolDisplayParameterOptions.IncludeName,
				miscellaneousOptions:
					SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
					SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
	}
}
