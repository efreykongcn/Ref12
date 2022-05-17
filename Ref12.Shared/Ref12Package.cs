using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using SLaks.Ref12.MetadataAsSource;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace SLaks.Ref12
{
	[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
	[InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version, IconResourceID = 400)]
	// These GUIDS and command IDs must match the VSCT.
	[ProvideMenuResource("Menus.ctmenu", 1)]
	[Guid(Vsix.GuidExtensionPackageString)]
	[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
	[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
	[ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
	[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasMultipleProjects_string, PackageAutoLoadFlags.BackgroundLoad)]
	[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasSingleProject_string, PackageAutoLoadFlags.BackgroundLoad)]
	public class Ref12Package : AsyncPackage {
		internal IComponentModel ComponentModel { get; private set; }
		protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
		{
			await base.InitializeAsync(cancellationToken, progress);
			ComponentModel = (IComponentModel)await GetServiceAsync(typeof(SComponentModel)).ConfigureAwait(true);

			LoadComponentAsync(cancellationToken).Forget();			
		}

		private async Task LoadComponentAsync(CancellationToken cancellationToken)
		{
			if (!KnownUIContexts.SolutionExistsAndFullyLoadedContext.IsZombie)
			{
				await KnownUIContexts.SolutionExistsAndFullyLoadedContext;
				await this.ComponentModel.GetService<MetadataAsSourceFileSupportService>().InitializeAsync(this, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	[Guid(Vsix.GuidCommandIDString)]
	enum Ref12Command {
		GoToDefinitionNative = 0
	}
}
