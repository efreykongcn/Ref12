using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SLaks.Ref12
{
	internal sealed partial class Vsix
	{
		public const string Id = "CF4413C3-032A-46B4-B2B1-F1B2449B9CB8";
		public const string Name = "Ref12-VS2022";
		public const string Description = @"Forwards F12 to source code instead of showing metadata.";
		public const string Language = "en-US";
		public const string Version = "5.1.0";
		public const string Author = "Efrey Kong";
		public const string Tags = "C#, Reference Source, Source, Roslyn";

		public const string GuidExtensionPackageString = "0B7E88BD-BE0B-4BD0-8130-AAB72D175E2B";
		public const string GuidCommandIDString = "E684498E-C716-41C6-B21F-AE6785412D1C";
		public static readonly Guid GuidCommandID = new Guid(GuidCommandIDString);
	}
}
