﻿using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VSSDK.Tools.VsIdeTesting;
using SLaks.Ref12;
using SLaks.Ref12.Commands;
using SLaks.Ref12.Services;

namespace Ref12.Tests {
	[TestClass]
	public class IntegrationTests {
		public static DTE DTE { get { return VsIdeTestHostContext.Dte; } }

		// Hosted tests run out of a TestResults directory in the solution path.
		static readonly string SolutionDir = Path.Combine(Path.GetDirectoryName(typeof(IntegrationTests).Assembly.Location), @"..\..\..\Ref12.Tests\Fixtures\TestBed");

		class RecordingSourceProvider : IReferenceSourceProvider {
			public RecordingSourceProvider() { AvailableAssemblies = new HashSet<string> { "mscorlib" }; }
			public ISet<string> AvailableAssemblies { get; private set; }

			public SymbolInfo LastSymbol { get; private set; }

			void IReferenceSourceProvider.Navigate(SymbolInfo symbol) {
				LastSymbol = symbol;
			}

			public void Reset() {
				LastSymbol = null;
			}

			public bool Supports(TargetFramework targetFramework)
			{
				throw new System.NotImplementedException();
			}

			public bool CanNavigate(SymbolInfo symbol)
			{
				throw new System.NotImplementedException();
			}

			public void Navigate(SymbolInfo symbol)
			{
				throw new System.NotImplementedException();
			}

			public Task<bool> TryToNavigateAsync(SymbolInfo symbol, CancellationToken cancellationToken = default)
			{
				throw new System.NotImplementedException();
			}
		}

		static readonly RecordingSourceProvider sourceRecord = new RecordingSourceProvider();
		private static IComponentModel componentModel;
		private static string fileName;
		private static ITextView textView;

		[ClassInitialize]
		public static void PrepareSolution(TestContext context) {
			componentModel = (IComponentModel)VsIdeTestHostContext.ServiceProvider.GetService(typeof(SComponentModel));
			componentModel.GetService<TextViewListener>().ReferenceProviders = new[] { sourceRecord };

			DTE.Solution.Open(Path.Combine(SolutionDir, "TestBed.sln"));

			fileName = Path.GetFullPath(Path.Combine(SolutionDir, "CSharp", "File.cs"));
			DTE.ItemOperations.OpenFile(fileName).Activate();
			textView = GetCurentTextView();
			System.Threading.Thread.Sleep(2500);	// Wait for the language service to bind the file; this can really take 2 seconds
		}

		[TestInitialize]
		public void ResetRecorder() {
			sourceRecord.Reset();
		}

		[TestMethod]
		[HostType("VS IDE")]
		public async Task CSharpGoToDefTest() {
			// Hop on to the UI thread so the language service APIs work
			await Application.Current.Dispatcher.NextFrame(DispatcherPriority.ApplicationIdle);

			textView.Caret.MoveTo(textView.FindSpan("Environment.GetFolderPath").End);
			GetCurrentNativeTextView().Execute(VSConstants.VSStd97CmdID.GotoDefn);
			Assert.IsFalse(sourceRecord.LastSymbol.HasLocalSource);
			Assert.AreEqual("mscorlib", sourceRecord.LastSymbol.AssemblyName);
			Assert.AreEqual("M:System.Environment.GetFolderPath(System.Environment.SpecialFolder)", sourceRecord.LastSymbol.IndexId);

			textView.Caret.MoveTo(textView.FindSpan("Environment.SpecialFolder.CommonOemLinks").End);
			GetCurrentNativeTextView().Execute(VSConstants.VSStd97CmdID.GotoDefn);
			Assert.IsFalse(sourceRecord.LastSymbol.HasLocalSource);
			Assert.AreEqual("mscorlib", sourceRecord.LastSymbol.AssemblyName);
			Assert.AreEqual("F:System.Environment.SpecialFolder.CommonOemLinks", sourceRecord.LastSymbol.IndexId);
		}

		[TestMethod]
		[HostType("VS IDE")]
		public async Task CSharpMetadataTest() {
			// Hop on to the UI thread so the language service APIs work
			await Application.Current.Dispatcher.NextFrame(DispatcherPriority.ApplicationIdle);

			// Use a type that is not in the public reference source
			textView.Caret.MoveTo(textView.FindSpan("System.IO.Log.LogStore").End);
			GetCurrentNativeTextView().Execute(VSConstants.VSStd97CmdID.GotoDefn);

			var metadataTextView = GetCurentTextView();
			var docService = componentModel.GetService<ITextDocumentFactoryService>();
			ITextDocument document;
			Assert.IsTrue(docService.TryGetTextDocument(metadataTextView.TextDataModel.DocumentBuffer, out document));
			ISymbolResolver resolver = new RoslynSymbolResolver();
			if (resolver != null) {
				var symbol = resolver.GetSymbolAt(document.FilePath, metadataTextView.FindSpan("public LogStore(SafeFileHandle").End);
				Assert.IsFalse(symbol.HasLocalSource);
				Assert.AreEqual("mscorlib", symbol.AssemblyName);
				Assert.AreEqual("T:Microsoft.Win32.SafeHandles.SafeFileHandle", symbol.IndexId);
			}
		}

		///<summary>Gets the TextView for the active document.</summary>
		public static ITextView GetCurentTextView() {
			var editorAdapter = componentModel.GetService<IVsEditorAdaptersFactoryService>();

			return editorAdapter.GetWpfTextView(GetCurrentNativeTextView());
		}
		public static IVsTextView GetCurrentNativeTextView() {
			var textManager = (IVsTextManager)VsIdeTestHostContext.ServiceProvider.GetService(typeof(SVsTextManager));

			IVsTextView activeView = null;
			ErrorHandler.ThrowOnFailure(textManager.GetActiveView(1, null, out activeView));
			return activeView;
		}
	}
}
