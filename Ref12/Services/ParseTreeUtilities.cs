﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.RestrictedUsage.CSharp.Compiler.IDE;
using Microsoft.RestrictedUsage.CSharp.Core;
using Microsoft.RestrictedUsage.CSharp.Extensions;
using Microsoft.RestrictedUsage.CSharp.Syntax;
using Microsoft.VisualStudio.CSharp.Services.Language.Interop;
using Microsoft.VisualStudio.CSharp.Services.Language.Refactoring;
using Microsoft.VisualStudio.Text;

namespace SLaks.Ref12.Services {
	static class ParseTreeUtilities {
		private static readonly Lazy<IDECompilerHost> compilerHost = new Lazy<IDECompilerHost>();

		public static NativeMethods.FindSourceDefinitionsAndDetermineSymbolResult GetNode(SnapshotPoint point, Project project, string fileName) {
			var compiler = compilerHost.Value.CreateCompiler(project);
			var sourceFile = compiler.SourceFiles[new FileName(fileName)];

			var node = sourceFile.GetParseTree().FindLeafNode(LanguageUtilities.ToCSharpPosition(point));
			if (node == null) return null;

			var rNode = ParseTreeMatch.GetReferencedNode(node);
			if (rNode == null) return null;

			return NativeMethods.FindSourceDefinitionsAndDetermineSymbolFromParseTree((IDECompilation)compiler.GetCompilation(), null, rNode);
		}
	}

	static partial class NativeMethods {
		static bool ToBool(this int boolAsInt) { return boolAsInt != 0; }

		internal class SourceDefinitionOutputs : NodeAndFileNameArrayOutputs {
			public NamedSymbolKind definitionKind;
			public int hasExternalVisibility;
		}
		internal class NodeAndFileNameArrayOutputs {
			public string[] fileNames;
			public IntPtr[] nodeOwners;
			public IntPtr[] nodePointers;
		}
		internal class SymbolInfoHolder {
			public string rqName;
			public string RQNameForParameterFromOtherPartialMethod;
			public string assemblyName;
			public string[] namespaceDefiningAssemblies;
			public ParseTree.Handle anonymousTypePropertyRefOwnerHandle;
			public IntPtr anonymousTypePropertyRefNodePointer;
			public int[] anonymousTypePropertyReferenceToSelfArray;
		}
		internal class FindSourceDefinitionsResult {
			public readonly NamedSymbolKind DefinitionKind;
			public readonly bool HasExternalVisibility;
			public readonly ReadOnlyCollection<FileName> DefinitionFiles;
			public FindSourceDefinitionsResult(IDECompilation compilation, SourceDefinitionOutputs outputs) {
				DefinitionKind = outputs.definitionKind;
				HasExternalVisibility = outputs.hasExternalVisibility.ToBool();
				DefinitionFiles = outputs.fileNames.Select(f => new FileName(f)).ToList().AsReadOnly();
			}
		}
		internal class FindSourceDefinitionsAndDetermineSymbolResult : FindSourceDefinitionsResult {
			public readonly string RQName;
			public readonly string RQNameForParameterFromOtherPartialMethod;
			public readonly string AssemblyName;
			public readonly ReadOnlyCollection<FileName> NamespaceDefiningAssemblies;
			public readonly IList<bool> AnonymousTypePropertyReferenceToSelf;
			internal FindSourceDefinitionsAndDetermineSymbolResult(IDECompilation compilation, SourceDefinitionOutputs helper, SymbolInfoHolder symbolInfo) : base(compilation, helper) {
				RQName = symbolInfo.rqName;
				RQNameForParameterFromOtherPartialMethod = symbolInfo.RQNameForParameterFromOtherPartialMethod;
				AssemblyName = symbolInfo.assemblyName;

				if (symbolInfo.anonymousTypePropertyReferenceToSelfArray != null) {
					AnonymousTypePropertyReferenceToSelf =
						symbolInfo.anonymousTypePropertyReferenceToSelfArray.Select(ToBool).ToList();
				}
				NamespaceDefiningAssemblies =
					symbolInfo.namespaceDefiningAssemblies.Select(f => new FileName(f)).ToList().AsReadOnly();
			}
		}

		static Func<IDECompilation, SafeHandle> CompilationHandle = (Func<IDECompilation, SafeHandle>)Delegate.CreateDelegate(typeof(Func<IDECompilation, SafeHandle>), typeof(IDECompilation).GetProperty("SafeHandle", BindingFlags.Instance | BindingFlags.NonPublic).GetMethod);
		static Func<ParseTreeNode, IntPtr> ParseTreeNodePointer = (Func<ParseTreeNode, IntPtr>)Delegate.CreateDelegate(typeof(Func<ParseTreeNode, IntPtr>), typeof(ParseTreeNode).GetProperty("Pointer", BindingFlags.Instance | BindingFlags.NonPublic).GetMethod);

		// Stolen from Microsoft.VisualStudio.CSharp.Services.Language.Refactoring.RefactoringInterop
		internal static FindSourceDefinitionsAndDetermineSymbolResult FindSourceDefinitionsAndDetermineSymbolFromParseTree(IDECompilation compilation, IRefactorProgressUI progressUI, ParseTreeNode parseTreeNode) {
			SourceDefinitionOutputs sourceDefinitionOutputs = new SourceDefinitionOutputs();
			SymbolInfoHolder symbolInfoHolder = new SymbolInfoHolder();
			Refactoring_FindSourceDefinitionsAndDetermineSymbolFromParseTree(
				CompilationHandle(compilation),
				progressUI,
				ParseTreeNodePointer(parseTreeNode),
				out sourceDefinitionOutputs.definitionKind,
				out symbolInfoHolder.rqName,
				out symbolInfoHolder.RQNameForParameterFromOtherPartialMethod,
				out symbolInfoHolder.assemblyName,
				out symbolInfoHolder.namespaceDefiningAssemblies,
				out symbolInfoHolder.anonymousTypePropertyRefOwnerHandle,
				out symbolInfoHolder.anonymousTypePropertyRefNodePointer,
				out sourceDefinitionOutputs.hasExternalVisibility,
				out sourceDefinitionOutputs.fileNames,
				out sourceDefinitionOutputs.nodeOwners,
				out sourceDefinitionOutputs.nodePointers,
				out symbolInfoHolder.anonymousTypePropertyReferenceToSelfArray
			);
			return new FindSourceDefinitionsAndDetermineSymbolResult(compilation, sourceDefinitionOutputs, symbolInfoHolder);
		}

		[DllImport("CSLangSvc.dll", PreserveSig = false)]
		internal static extern void Refactoring_FindSourceDefinitionsAndDetermineSymbolFromParseTree(
			SafeHandle compilationScope,
			IRefactorProgressUI progressUI,
			IntPtr refNodePointer,
			out NamedSymbolKind definitionKind,
			[MarshalAs(UnmanagedType.BStr)] out string rqName,
			[MarshalAs(UnmanagedType.BStr)] out string RQNameForParameterFromOtherPartialMethod,
			[MarshalAs(UnmanagedType.BStr)] out string assemblyName,
			[MarshalAs(UnmanagedType.SafeArray)] out string[] namespaceDefiningAssemblies,
			out ParseTree.Handle anonymousTypePropertyRefOwner,
			out IntPtr anonymousTypePropertyRefPointer,
			out int hasExternalVisibility,
			[MarshalAs(UnmanagedType.SafeArray)] out string[] sourceLocationFilenames,
			[MarshalAs(UnmanagedType.SafeArray)] out IntPtr[] sourceLocationOwners,
			[MarshalAs(UnmanagedType.SafeArray)] out IntPtr[] sourceLocationNodePointers,
			[MarshalAs(UnmanagedType.SafeArray)] out int[] anonymousTypePropertyReferenceToSelf
		);
	}
}
