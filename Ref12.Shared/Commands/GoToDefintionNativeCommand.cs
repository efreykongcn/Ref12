using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using SLaks.Ref12.Services;
using IServiceProvider = System.IServiceProvider;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using SLaks.Ref12.MetadataAsSource;

namespace SLaks.Ref12.Commands {
	class GoToDefintionNativeCommand : CommandTargetBase<Ref12Command> {
		readonly RoslynSymbolResolver _symbolResolver;
		readonly ITextDocument _doc;
		readonly IEnumerable<IReferenceSourceProvider> _references;
		public GoToDefintionNativeCommand(IServiceProvider serviceProvider,
			IVsEditorAdaptersFactoryService editorAdaptersFactory,
			IVsTextView adapter, 
			IWpfTextView textView, 
			ITextDocument doc,
			IMetadataAsSourceFileService fileService) : base(adapter, textView, Ref12Command.GoToDefinitionNative) 
		{
			_symbolResolver = new RoslynSymbolResolver();
			_references = new List<IReferenceSourceProvider>() { new MetadataAsReferenceSourceProvider(serviceProvider, editorAdaptersFactory, fileService) };
			_doc = doc;
			
		}
		protected override bool Execute(Ref12Command commandId, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut) {
			return ThreadHelper.JoinableTaskFactory.Run(async () =>
			{
				var result = false;
				try
				{
					result = await ExecuteDecompilingAsync();
				}
				catch { }

				if (!result)
				{
					NextTarget.Execute(VSConstants.VSStd97CmdID.GotoDefn, nCmdexecopt, pvaIn, pvaOut);
				}
				return true;
			});
		}

		private async Task<bool> ExecuteDecompilingAsync()
		{
			SnapshotPoint? caretPoint = TextView.GetCaretPoint(s => s != null);
			if (caretPoint == null)
				return false;

			var (symbol, targetFramework) = await _symbolResolver.GetSymbolInfoAtAsync(_doc.FilePath, caretPoint.Value);
			if (symbol == null || symbol.HasLocalSource)
				return false;

			var target = _references.Where(r => r.Supports(targetFramework)).FirstOrDefault(r => r.CanNavigate(symbol));
			if (target == null)
				return false;

			return await target.TryToNavigateAsync(symbol);
		}

		protected override bool IsEnabled() {
			// We override QueryStatus directly to pass the raw arguments
			// to the inner command, so this method will never be called.
			throw new NotImplementedException();
		}
		public override int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) {
			if (pguidCmdGroup != CommandGroup || cCmds != 1 || prgCmds[0].cmdID != CommandIds[0])
				return NextTarget.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
			var innerGuid = typeof(VSConstants.VSStd97CmdID).GUID;
			var innerCommands = new[] { new OLECMD {
				cmdID = (uint)VSConstants.VSStd97CmdID.GotoDefn,
				cmdf=prgCmds[0].cmdf
			} };
			int result = NextTarget.QueryStatus(ref innerGuid, 1, innerCommands, pCmdText);
			prgCmds[0].cmdf = innerCommands[0].cmdf;
			return result;
		}
	}
}
