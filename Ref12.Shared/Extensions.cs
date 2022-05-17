using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Projection;
using SLaks.Ref12.Services;
using SRM = System.Reflection.Metadata;

namespace SLaks.Ref12 {
	internal static class Extensions {
		public static SnapshotPoint? GetCaretPoint(this ITextView textView, Predicate<ITextSnapshot> match) {
			CaretPosition position = textView.Caret.Position;
			SnapshotSpan? snapshotSpan = textView.BufferGraph.MapUpOrDownToFirstMatch(new SnapshotSpan(position.BufferPosition, 0), match);
			if (snapshotSpan.HasValue)
				return new SnapshotPoint?(snapshotSpan.Value.Start);
			return null;
		}
		public static SnapshotSpan? MapUpOrDownToFirstMatch(this IBufferGraph bufferGraph, SnapshotSpan span, Predicate<ITextSnapshot> match) {
			NormalizedSnapshotSpanCollection spans = bufferGraph.MapDownToFirstMatch(span, SpanTrackingMode.EdgeExclusive, match);
			if (!spans.Any())
				spans = bufferGraph.MapUpToFirstMatch(span, SpanTrackingMode.EdgeExclusive, match);
			return spans.Select(s => new SnapshotSpan?(s))
						.FirstOrDefault();
		}

		private static bool IsSourceBuffer(IProjectionBufferBase top, ITextBuffer bottom) {
			return top.SourceBuffers.Contains(bottom) || top.SourceBuffers.Any((ITextBuffer tb) => tb is IProjectionBufferBase && IsSourceBuffer((IProjectionBufferBase)tb, bottom));
		}

		public static void Execute(this IOleCommandTarget target, Enum commandId, uint execOptions = 0, IntPtr inHandle = default(IntPtr), IntPtr outHandle = default(IntPtr)) {
			var c = commandId.GetType().GUID;
			ErrorHandler.ThrowOnFailure(target.Exec(ref c, Convert.ToUInt32(commandId, CultureInfo.InvariantCulture), execOptions, inHandle, outHandle));
		}

		public static IAssemblyResolver GetAssemblyResolver(this PEFile file, bool loadOnDemand = true)
		{
			return GetLoadedAssembly(file).GetAssemblyResolver(loadOnDemand);
		}

		public static LoadedAssembly GetLoadedAssembly(this PEFile file)
		{
			if (file == null)
				throw new ArgumentNullException(nameof(file));
			LoadedAssembly loadedAssembly;
			lock (LoadedAssembly.loadedAssemblies)
			{
				if (!LoadedAssembly.loadedAssemblies.TryGetValue(file, out loadedAssembly))
					throw new ArgumentException("The specified file is not associated with a LoadedAssembly!");
			}
			return loadedAssembly;
		}

		/// <inheritdoc cref="RoslynServiceExtensions.GetService{TService, TInterface}(System.IServiceProvider, JoinableTaskFactory, bool)"/>
		public static TInterface GetServiceOnMainThread<TService, TInterface>(this System.IServiceProvider serviceProvider)
		{
			var service = serviceProvider.GetService(typeof(TService));
			if (service is null)
				throw new Microsoft.VisualStudio.Shell.ServiceUnavailableException(typeof(TService));
			if (!(service is TInterface @interface))
				throw new Microsoft.VisualStudio.Shell.ServiceUnavailableException(typeof(TInterface));

			return @interface;
		}

		public static SemaphoreDisposer DisposableWait(this SemaphoreSlim semaphore, CancellationToken cancellationToken = default)
		{
			semaphore.Wait(cancellationToken);
			return new SemaphoreDisposer(semaphore);
		}

		public static async ValueTask<SemaphoreDisposer> DisposableWaitAsync(this SemaphoreSlim semaphore, CancellationToken cancellationToken = default)
		{
			await semaphore.WaitAsync(cancellationToken);
			return new SemaphoreDisposer(semaphore);
		}

	}
	[AttributeUsage(AttributeTargets.Struct | AttributeTargets.GenericParameter)]
	internal sealed class NonCopyableAttribute : Attribute
	{
	}
	[NonCopyable]
	internal struct SemaphoreDisposer : IDisposable
	{
		private SemaphoreSlim _semaphore;

		public SemaphoreDisposer(SemaphoreSlim semaphore)
		{
			_semaphore = semaphore;
		}

		public void Dispose()
		{
			// Officially, Dispose() being called more than once is allowable, but in this case
			// if that were to ever happen that means something is very, very wrong. Since it's an internal
			// type, better to be strict.

			// Nulling this out also means it's a bit easier to diagnose some async deadlocks; if you have an
			// async deadlock where a SemaphoreSlim is held but you're unsure why, as long all the users of the
			// SemaphoreSlim used the Disposable helpers, you can search memory and find the instance that
			// is pointing to the SemaphoreSlim that hasn't nulled out this field yet; in that case you know
			// that's holding the lock and can figure out who is holding that SemaphoreDisposer.
			var semaphoreToDispose = Interlocked.Exchange(ref _semaphore, null);

			if (semaphoreToDispose is null)
			{
				throw new ObjectDisposedException($"Somehow a {nameof(SemaphoreDisposer)} is being disposed twice.");
			}

			semaphoreToDispose.Release();
		}
	}
}
