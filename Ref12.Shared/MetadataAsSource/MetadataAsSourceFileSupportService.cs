// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.ComponentModel.Composition;
using System.Threading;
using IAsyncServiceProvider = Microsoft.VisualStudio.Shell.IAsyncServiceProvider;
using Task = System.Threading.Tasks.Task;

namespace SLaks.Ref12.MetadataAsSource
{
	[Export(typeof(MetadataAsSourceFileSupportService))]
	public sealed class MetadataAsSourceFileSupportService : IVsSolutionEvents
	{
		private readonly IMetadataAsSourceFileService _fileService;
		[ImportingConstructor]
		public MetadataAsSourceFileSupportService(IMetadataAsSourceFileService fileService)
		{
			_fileService = fileService;
		}

		public async Task InitializeAsync(IAsyncServiceProvider serviceProvider, CancellationToken cancellationToken)
		{
			
			var solution = await serviceProvider.GetServiceAsync<SVsSolution, IVsSolution>();
			// Intentionally ignore the event-cookie we get back out.  We never stop listening to solution events.
			ErrorHandler.ThrowOnFailure(solution.AdviseSolutionEvents(this, out _));
		}

		#region IVsSolutionEvents
		public int OnAfterCloseSolution(object pUnkReserved)
		{
			_fileService.CleanupGeneratedFiles();

			return VSConstants.S_OK;
		}

		public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
			=> VSConstants.E_NOTIMPL;

		public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel)
			=> VSConstants.E_NOTIMPL;

		public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)
			=> VSConstants.E_NOTIMPL;

		public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
			=> VSConstants.E_NOTIMPL;

		public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel)
			=> VSConstants.E_NOTIMPL;

		public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy)
			=> VSConstants.E_NOTIMPL;

		public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
			=> VSConstants.E_NOTIMPL;

		public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel)
			=> VSConstants.E_NOTIMPL;

		public int OnBeforeCloseSolution(object pUnkReserved)
			=> VSConstants.E_NOTIMPL;
		
		#endregion
	}
}
