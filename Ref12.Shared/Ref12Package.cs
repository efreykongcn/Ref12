using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SLaks.Ref12 {
	// These GUIDS and command IDs must match the VSCT.
	[ProvideMenuResource("Menus.ctmenu", 1)]
	[Guid(Vsix.GuidExtensionPackageString)]
	[InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version, IconResourceID = 400)]
	[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
	class Ref12Package : AsyncPackage {
	}

	[Guid(Vsix.GuidCommandIDString)]
	enum Ref12Command {
		GoToDefinitionNative = 0
	}
}
