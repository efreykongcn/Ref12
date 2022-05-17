using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;

namespace SLaks.Ref12.Services
{
	public interface ISymbolResolver {
		Task<(SymbolInfo, TargetFramework)> GetSymbolInfoAtAsync(string sourceFileName, SnapshotPoint point);
	}
}
