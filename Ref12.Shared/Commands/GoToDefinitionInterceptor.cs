using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using SLaks.Ref12.Services;

namespace SLaks.Ref12.Commands
{
	class GoToDefinitionInterceptor : CommandTargetBase<VSConstants.VSStd97CmdID> {
		readonly IEnumerable<IReferenceSourceProvider> references;
		readonly ITextDocument doc;
		readonly Dictionary<string, ISymbolResolver> resolvers = new Dictionary<string, ISymbolResolver>();

		public GoToDefinitionInterceptor(IEnumerable<IReferenceSourceProvider> references, IServiceProvider sp, IVsTextView adapter, IWpfTextView textView, ITextDocument doc) : base(adapter, textView, VSConstants.VSStd97CmdID.GotoDefn) {
			this.references = references;
			this.doc = doc;

			RoslynAssemblyRedirector.Register();
			resolvers.Add("CSharp", CreateRoslynResolver());
			resolvers.Add("Basic", CreateRoslynResolver());
		}
		// This reference cannot be JITted in VS2012, so I need to wrap it in a separate method.
		static ISymbolResolver CreateRoslynResolver() { return new RoslynSymbolResolver(); }

		protected override bool Execute(VSConstants.VSStd97CmdID commandId, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut) {
			return ThreadHelper.JoinableTaskFactory.Run(async () =>
			{
				ISymbolResolver resolver = null;
				SnapshotPoint? caretPoint = TextView.GetCaretPoint(s => resolvers.TryGetValue(s.ContentType.TypeName, out resolver));
				if (caretPoint == null)
					return false;

				var symbol = await resolver.GetSymbolAtAsync(doc.FilePath, caretPoint.Value);
				if (symbol == null || symbol.HasLocalSource)
					return false;

				var target = references.Where(r => r.Supports(symbol.TargetFramework)).FirstOrDefault(r => r.AvailableAssemblies.Contains(symbol.AssemblyName));
				if (target == null)
					return false;

				Debug.WriteLine("Ref12: Navigating to IndexID " + symbol.IndexId);

				target.Navigate(symbol);
				return true;
			});
		}

		protected override bool IsEnabled() {
			return false;   // Always pass through to the native check
		}
	}
}
