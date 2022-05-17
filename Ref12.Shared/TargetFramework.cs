using System;
using ICSharpCode.Decompiler.Metadata;

namespace SLaks.Ref12
{
	public sealed class TargetFramework
	{
		public TargetFramework(TargetFrameworkIdentifier targetFrameworkIdentifier, Version version)
		{
			this.Identifier = targetFrameworkIdentifier;
			this.Version = version;
		}
		public TargetFrameworkIdentifier Identifier { get; }
		public Version Version { get; }
	}
}
