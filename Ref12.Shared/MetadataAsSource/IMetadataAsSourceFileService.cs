﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SLaks.Ref12.MetadataAsSource
{
	public interface IMetadataAsSourceFileService
    {
		Task<MetadataAsSourceFile> GetGeneratedFileAsync(SymbolInfo symbolInfo, CancellationToken cancellationToken);

		bool TryAddDocumentToWorkspace(string filePath, SourceTextContainer buffer);

		bool TryRemoveDocumentFromWorkspace(string filePath);

		void CleanupGeneratedFiles();
		/// <summary>
		/// Check specified file is generated by IMetadataAsSourceFileService or not
		/// </summary>
		/// <param name="filename"></param>
		/// <returns>true if file was generated by IMetadataAsSourceFileService, otherwise false</returns>
		bool IsGeneratedFiles(string filename);
	}

	[Export(typeof(IMetadataAsSourceFileService))]
	public class MetadataAsSourceFileService : IMetadataAsSourceFileService
	{
		/// <summary>
		/// A lock to guard parallel accesses to this type. In practice, we presume that it's not 
		/// an important scenario that we can be generating multiple documents in parallel, and so 
		/// we simply take this lock around all public entrypoints to enforce sequential access.
		/// </summary>
		private readonly SemaphoreSlim _gate = new SemaphoreSlim(initialCount: 1);

		private MetadataAsSourceWorkspace _workspace;

		/// <summary>
		/// We create a mutex so other processes can see if our directory is still alive. We destroy the mutex when
		/// we purge our generated files.
		/// </summary>
		private Mutex _mutex;
		private readonly string _rootTemporaryPath;
		private readonly SVsServiceProvider _serviceProvider;
		private readonly IVsEditorAdaptersFactoryService _editorAdaptersFactoryService;
		private string _rootTemporaryPathWithGuid;

		private readonly DecompilationMetadaAsSourceFileService _decompilationMetadataAsSourceFileService;

		[ImportingConstructor]
		public MetadataAsSourceFileService(SVsServiceProvider serviceProvider,
				IVsEditorAdaptersFactoryService editorAdaptersFactory)
		{
			_serviceProvider = serviceProvider;
			_editorAdaptersFactoryService = editorAdaptersFactory;
			_decompilationMetadataAsSourceFileService = new DecompilationMetadaAsSourceFileService();
			_rootTemporaryPath = Path.Combine(Path.GetTempPath(), "Ref12MetadataAsSource");
		}


		public async Task<MetadataAsSourceFile> GetGeneratedFileAsync(SymbolInfo symbolInfo, CancellationToken cancellationToken)
		{
			using (await _gate.DisposableWaitAsync(cancellationToken).ConfigureAwait(false))
			{
				var project = symbolInfo.ContainingDocument.Project;
				InitializeWorkspace(project);
				var tempPath = GetRootPathWithGuid_NoLock();
				return await _decompilationMetadataAsSourceFileService.GetGeneratedFileAsync(_workspace, symbolInfo, tempPath, cancellationToken);
			}
		}

		private static string CreateMutexName(string directoryName)
			=> "MetadataAsSource-" + directoryName;
		private string GetRootPathWithGuid_NoLock()
		{
			if (_rootTemporaryPathWithGuid == null)
			{
				var guidString = Guid.NewGuid().ToString("N");
				_rootTemporaryPathWithGuid = Path.Combine(_rootTemporaryPath, guidString);
				_mutex = new Mutex(initiallyOwned: true, name: CreateMutexName(guidString));
			}
			return _rootTemporaryPathWithGuid;
		}

		private void InitializeWorkspace(Project project)
		{
			if (_workspace == null)
			{
				_workspace = new MetadataAsSourceWorkspace(_editorAdaptersFactoryService, _serviceProvider, this, project.Solution.Workspace.Services.HostServices);
			}
		}

		public bool TryAddDocumentToWorkspace(string filePath, SourceTextContainer sourceTextContainer)
		{
			using (_gate.DisposableWait())
			{
				if (_decompilationMetadataAsSourceFileService.TryAddDocumentToWorkspace(_workspace, filePath, sourceTextContainer))
					return true;
			}

			return false;
		}

		public bool TryRemoveDocumentFromWorkspace(string filePath)
		{
			using (_gate.DisposableWait())
			{
				if (_decompilationMetadataAsSourceFileService.TryRemoveDocumentFromWorkspace(_workspace, filePath))
					return true;
			}

			return false;
		}

		public bool IsGeneratedFiles(string filename)
		{
			return _decompilationMetadataAsSourceFileService.IsFileGeneratedByMe(filename);
		}

		public void CleanupGeneratedFiles()
		{
			using (_gate.DisposableWait())
			{
				// Release our mutex to indicate we're no longer using our directory and reset state
				if (_mutex != null)
				{
					_mutex.Dispose();
					_mutex = null;
					_rootTemporaryPathWithGuid = null;
				}

				if (_workspace != null)
				{
					_workspace.Dispose();
				}

				// Only cleanup for providers that have actually generated a file. This keeps us from
				// accidentally loading lazy providers on cleanup that weren't used
				_decompilationMetadataAsSourceFileService.CleanupGeneratedFiles(_workspace);

				try
				{
					if (Directory.Exists(_rootTemporaryPath))
					{
						var deletedEverything = true;

						// Let's look through directories to delete.
						foreach (var directoryInfo in new DirectoryInfo(_rootTemporaryPath).EnumerateDirectories())
						{

							// Is there a mutex for this one?
							if (Mutex.TryOpenExisting(CreateMutexName(directoryInfo.Name), out var acquiredMutex))
							{
								acquiredMutex.Dispose();
								deletedEverything = false;
								continue;
							}

							TryDeleteFolderWhichContainsReadOnlyFiles(directoryInfo.FullName);
						}

						if (deletedEverything)
						{
							Directory.Delete(_rootTemporaryPath);
						}
					}
				}
				catch (Exception)
				{
				}
			}
		}

		private static void TryDeleteFolderWhichContainsReadOnlyFiles(string directoryPath)
		{
			try
			{
				foreach (var fileInfo in new DirectoryInfo(directoryPath).EnumerateFiles("*", SearchOption.AllDirectories))
				{
					fileInfo.IsReadOnly = false;
				}

				Directory.Delete(directoryPath, recursive: true);
			}
			catch (Exception)
			{
			}
		}
	}
}
