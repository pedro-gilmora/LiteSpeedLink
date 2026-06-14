using SharedMemory;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink;

	public delegate byte[]? MemoryRequestHandler(MemoryRequestContext ctx, CancellationToken token);
	public delegate Task<byte[]?> MemoryAsyncRequestHandler(MemoryRequestContext ctx, CancellationToken token);

	public static partial class Server
	{
		[SupportedOSPlatform("windows")]
		public static RpcBuffer StartMemoryServer(
			string contextId,
			MemoryRequestHandler requestHandlers,
			Action? onFinalize = null,
			int timeout = 1000,
			CancellationToken cancelToken = default)
		{
			RpcBuffer slave = null!;

			return slave = new(contextId, (msgId, payload) =>
			{
				return requestHandlers(new(payload, timeout, cancelToken), cancelToken)!;
			});
		}
		[SupportedOSPlatform("windows")]
		public static RpcBuffer StartMemoryServerAsync(
			string contextId,
			MemoryAsyncRequestHandler requestHandlers,
			Action? onFinalize = null,
			int timeout = 1000,
			CancellationToken cancelToken = default)
		{
			RpcBuffer slave = null!;

			return slave = new(contextId, (msgId, payload) =>
			{
				return requestHandlers(new(payload, timeout, cancelToken), cancelToken)!;
			});
		}
	}

	[SupportedOSPlatform("windows")]
	public sealed class MemoryRequestContext(byte[] payload, int timeout, CancellationToken cancelToken) : BufferReader(payload)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public TOut? Read<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>() => Deserialize<TOut>(AsSpan());


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Yield<TData>(IEnumerable<TData> enumerate, CancellationToken token)
		{
			var streamSessionId = Deserialize<string>(AsSpan())!.ToString();
			using RpcBuffer subAgent = new(streamSessionId);
			
			foreach (var item in enumerate)
				subAgent.RemoteRequest(Serialize(item),  timeoutMs: timeout,cancellationToken: cancelToken);
				
			subAgent.RemoteRequest(null, timeoutMs: timeout, cancellationToken: cancelToken);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public async Task Yield<TData>(IAsyncEnumerable<TData> enumerate, CancellationToken token)
		{
			var streamSessionId = Deserialize<string>(AsSpan())!.ToString();
			using RpcBuffer subAgent = new(streamSessionId);
			
			await foreach (var item in enumerate)
				subAgent.RemoteRequest(Serialize(item), timeoutMs: timeout, cancellationToken: cancelToken);
				
			subAgent.RemoteRequest(null, timeoutMs: timeout, cancellationToken: cancelToken);
		}
	}


