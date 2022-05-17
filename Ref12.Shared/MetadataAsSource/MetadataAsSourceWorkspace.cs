// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;

namespace SLaks.Ref12.MetadataAsSource
{
	internal class MetadataAsSourceWorkspace : Workspace, IVsRunningDocTableEvents3
	{
		private readonly IMetadataAsSourceFileService _fileTrackingMetadataAsSourceService;
		private readonly IVsEditorAdaptersFactoryService _editorAdaptersFactoryService;
		private readonly SVsServiceProvider _serviceProvider;
		private readonly IVsRunningDocumentTable4 _runningDocumentTable;
		private uint _runningDocumentTableEventsCookie;

		/// <summary>
		/// <see cref="WorkspaceRegistration"/> instances for all open buffers being tracked by by this object
		/// for possible inclusion into this workspace.
		/// </summary>
		private IBidirectionalMap<string, WorkspaceRegistration> _monikerToWorkspaceRegistration = BidirectionalMap<string, WorkspaceRegistration>.Empty;
		/// <summary>
		/// The mapping of all monikers in the RDT and the <see cref="ProjectId"/> of the project and <see cref="SourceTextContainer"/> of the open
		/// file we have created for that open buffer. An entry should only be in here if it's also already in <see cref="_monikerToWorkspaceRegistration"/>.
		/// </summary>
		private readonly Dictionary<string, (ProjectId projectId, SourceTextContainer textContainer)> _monikersToProjectIdAndContainer = new Dictionary<string, (ProjectId, SourceTextContainer)>();

		public MetadataAsSourceWorkspace(IVsEditorAdaptersFactoryService editorAdaptersFactoryService,
				SVsServiceProvider serviceProvider,
				IMetadataAsSourceFileService fileTrackingMetadataAsSourceService, 
				HostServices hostServices) : base(hostServices, WorkspaceKind.MetadataAsSource) 
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			_editorAdaptersFactoryService = editorAdaptersFactoryService;
			_serviceProvider = serviceProvider;
			_fileTrackingMetadataAsSourceService = fileTrackingMetadataAsSourceService;

			var runningDocumentTable = _serviceProvider.GetServiceOnMainThread<IVsRunningDocumentTable, IVsRunningDocumentTable>();
			_runningDocumentTable = (IVsRunningDocumentTable4)runningDocumentTable;
			// Advise / Unadvise for the RDT is free threaded past 16.0
			runningDocumentTable.AdviseRunningDocTableEvents(this, out _runningDocumentTableEventsCookie);
		}

		#region IVsRunningDocTableEvents3
		public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
				=> VSConstants.E_NOTIMPL;

		public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
		{
			if (dwReadLocksRemaining + dwEditLocksRemaining == 0)
			{
				if (IsDocumentInitialized(_runningDocumentTable, docCookie))
				{
					OnCloseDocument(_runningDocumentTable.GetDocumentMoniker(docCookie));
				}
			}

			return VSConstants.S_OK;
		}

		public int OnAfterSave(uint docCookie)
			=> VSConstants.E_NOTIMPL;

		public int OnAfterAttributeChange(uint docCookie, uint grfAttribs)
			=> VSConstants.E_NOTIMPL;

		public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
		{
			// Doc data reloaded is not triggered for the underlying aspx.cs file when changes are made to the aspx file, so catch it here.
			if (fFirstShow != 0 && IsDocumentInitialized(_runningDocumentTable, docCookie) && TryGetMoniker(docCookie, out var moniker) && TryGetBuffer(docCookie, out var buffer))
			{
				_runningDocumentTable.GetDocumentHierarchyItem(docCookie, out var hierarchy, out _);
				OnOpenDocument(moniker, buffer, hierarchy, pFrame);
			}

			return VSConstants.S_OK;
		}

		public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame)
			=> VSConstants.E_NOTIMPL;

		public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew)
			=> VSConstants.E_NOTIMPL;

		public int OnBeforeSave(uint docCookie)
			=> VSConstants.E_NOTIMPL;

		#endregion


		/// <summary>
		/// Stops tracking a document in the RDT for whether we should attach to it.
		/// </summary>
		/// <returns>true if we were previously tracking it.</returns>
		private bool OnCloseDocument(string moniker)
		{
			var unregisteredRegistration = false;

			// Remove our registration changing handler before we call DetachFromDocument. Otherwise, calling DetachFromDocument
			// causes us to set the workspace to null, which we then respond to as an indication that we should
			// attach again.
			if (_monikerToWorkspaceRegistration.TryGetValue(moniker, out var registration))
			{
				registration.WorkspaceChanged -= Registration_WorkspaceChanged;
				_monikerToWorkspaceRegistration = _monikerToWorkspaceRegistration.RemoveKey(moniker);
				unregisteredRegistration = true;
			}

			DetachFromDocument(moniker);

			return unregisteredRegistration;
		}

		private void AttachToDocument(string moniker, ITextBuffer textBuffer)
		{
			_fileTrackingMetadataAsSourceService.TryAddDocumentToWorkspace(moniker, textBuffer.AsTextContainer());
		}

		private void DetachFromDocument(string moniker)
		{
			_fileTrackingMetadataAsSourceService.TryRemoveDocumentFromWorkspace(moniker);
		}

		private void Registration_WorkspaceChanged(object sender, EventArgs e)
		{
			// We may or may not be getting this notification from the foreground thread if another workspace
			// is raising events on a background. Let's send it back to the UI thread since we can't talk
			// to the RDT in the background thread. Since this is all asynchronous a bit more asynchrony is fine.
			try
			{
				//_foregroundThreadAffinitization.AssertIsForeground();
				ThreadHelper.ThrowIfNotOnUIThread();
			}
			catch
			{
				ScheduleTask(() => Registration_WorkspaceChanged(sender, e));
				return;
			}

			var workspaceRegistration = (WorkspaceRegistration)sender;

			// Since WorkspaceChanged notifications may be asynchronous and happened on a different thread,
			// we might have already unsubscribed for this synchronously from the RDT while we were in the process of sending this
			// request back to the UI thread.
			if (!_monikerToWorkspaceRegistration.TryGetKey(workspaceRegistration, out var moniker))
			{
				return;
			}

			// It's also theoretically possible that we are getting notified about a workspace change to a document that has
			// been simultaneously removed from the RDT but we haven't gotten the notification. In that case, also bail.
			// Is File Open
			if (!_runningDocumentTable.IsMonikerValid(moniker))
			{
				return;
			}

			if (workspaceRegistration.Workspace == null)
			{
				if (_monikersToProjectIdAndContainer.TryGetValue(moniker, out var projectIdAndSourceTextContainer))
				{
					// The workspace was taken from us and released and we have only asynchronously found out now.
					// We already have the file open in our workspace, but the global mapping of source text container
					// to the workspace that owns it needs to be updated once more.
					RegisterText(projectIdAndSourceTextContainer.textContainer);
				}
				else
				{
					// We should now try to claim this. The moniker we have here is the moniker after the rename if we're currently processing
					// a rename. It's possible in that case that this is being closed by the other workspace due to that rename. If the rename
					// is changing or removing the file extension, we wouldn't want to try attaching, which is why we have to re-check
					// the moniker. Once we observe the rename later in OnAfterAttributeChangeEx we'll completely disconnect.
					if (ShouldTrackFile(moniker))
					{
						if (TryGetBufferFromMoniker(moniker, out var buffer))
						{
							AttachToDocument(moniker, buffer);
						}
					}
				}
			}
			else if (IsClaimedByAnotherWorkspace(workspaceRegistration))
			{
				// It's now claimed by another workspace, so we should unclaim it
				if (_monikersToProjectIdAndContainer.ContainsKey(moniker))
				{
					DetachFromDocument(moniker);
				}
			}
		}

		private bool ShouldTrackFile(string filename)
		{
			return _fileTrackingMetadataAsSourceService.IsGeneratedFiles(filename);
		}

		private bool TryGetBufferFromMoniker(string moniker, out ITextBuffer textBuffer)
		{
			textBuffer = null;
			if (!_runningDocumentTable.IsMonikerValid(moniker))
			{
				return false;
			}

			var cookie = _runningDocumentTable.GetDocumentCookie(moniker);
			if (!IsDocumentInitialized(_runningDocumentTable, cookie))
			{
				return false;
			}

			return TryGetBuffer(_runningDocumentTable, _editorAdaptersFactoryService, cookie, out textBuffer);
		}

		private bool IsDocumentInitialized(IVsRunningDocumentTable4 runningDocTable, uint docCookie)
		{
			var flags = runningDocTable.GetDocumentFlags(docCookie);

			return (flags & (uint)_VSRDTFLAGS4.RDT_PendingInitialization) == 0;
		}

		private static bool IsClaimedByAnotherWorkspace(WorkspaceRegistration registration)
		{
			// Currently, we are also responsible for pushing documents to the metadata as source workspace,
			// so we count that here as well
			return registration.Workspace != null && registration.Workspace.Kind != WorkspaceKind.MetadataAsSource && registration.Workspace.Kind != WorkspaceKind.MiscellaneousFiles;
		}

		private void OnOpenDocument(string moniker, ITextBuffer textBuffer, IVsHierarchy _, IVsWindowFrame __)
		{
			if (!ShouldTrackFile(moniker))
			{
				// We can never put this document in a workspace, so just bail
				return;
			}

			// We don't want to realize the document here unless it's already initialized. Document initialization is watched in
			// OnAfterAttributeChangeEx and will retrigger this if it wasn't already done.
			if (!_monikerToWorkspaceRegistration.ContainsKey(moniker))
			{
				var registration = Workspace.GetWorkspaceRegistration(textBuffer.AsTextContainer());

				registration.WorkspaceChanged += Registration_WorkspaceChanged;
				_monikerToWorkspaceRegistration = _monikerToWorkspaceRegistration.Add(moniker, registration);

				if (!IsClaimedByAnotherWorkspace(registration))
				{
					AttachToDocument(moniker, textBuffer);
				}
			}
		}

		private bool TryGetMoniker(uint docCookie, out string moniker)
		{
			moniker = _runningDocumentTable.GetDocumentMoniker(docCookie);
			return !string.IsNullOrEmpty(moniker);
		}
		private bool TryGetBuffer(uint docCookie, out ITextBuffer textBuffer)
			=> TryGetBuffer(_runningDocumentTable, _editorAdaptersFactoryService, docCookie, out textBuffer);
		private bool TryGetBuffer(IVsRunningDocumentTable4 runningDocumentTable, IVsEditorAdaptersFactoryService editorAdaptersFactoryService,
			uint docCookie, out ITextBuffer textBuffer)
		{
			textBuffer = null;

			if (runningDocumentTable.GetDocumentData(docCookie) is IVsTextBuffer bufferAdapter)
			{
				textBuffer = editorAdaptersFactoryService.GetDocumentBuffer(bufferAdapter);
				return textBuffer != null;
			}

			return false;
		}

		protected override void Dispose(bool finalize)
		{
			try
			{
				ThreadHelper.ThrowIfNotOnUIThread();
				var runningDocumentTableForEvents = (IVsRunningDocumentTable)_runningDocumentTable;
				runningDocumentTableForEvents.UnadviseRunningDocTableEvents(_runningDocumentTableEventsCookie);
				_runningDocumentTableEventsCookie = 0;
			}
			finally
			{
				base.Dispose(finalize);
			}
		}
	}
}
