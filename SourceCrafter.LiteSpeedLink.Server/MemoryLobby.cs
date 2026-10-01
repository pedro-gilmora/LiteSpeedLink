using SharedMemory;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// <see cref="RpcBuffer"/> es un par master/slave: un cliente por nombre. El <c>contextId</c> actua de lobby: el cliente
/// (serializado por <c>_LSL_Gate</c>) escribe el nombre de su sesion en <c>_LSL_Lobby</c>, senala <c>_LSL_Req</c> y espera
/// <c>_LSL_Ready</c> mientras el servidor abre un <see cref="RpcBuffer"/> dedicado con ese nombre.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MemoryLobby : IDisposable
{
    internal const int Size = 256; // cabe [len:4][nombre de sesion]
    private readonly MemoryMappedFile _mmf;
    private readonly EventWaitHandle _request, _ready, _stop = new ManualResetEvent(false);
    private readonly ConcurrentBag<RpcBuffer> _sessions = [];
    private readonly Action? _onFinalize;

    // ponytail: las sesiones viven hasta que se libera el servidor; sin deteccion de cliente caido.
    public MemoryLobby(string contextId, Action? onFinalize, Func<string, RpcBuffer> open)
    {
        _onFinalize = onFinalize;
        _mmf = MemoryMappedFile.CreateNew(contextId + "_LSL_Lobby", Size);
        _request = new EventWaitHandle(false, EventResetMode.AutoReset, contextId + "_LSL_Req");
        _ready = new EventWaitHandle(false, EventResetMode.AutoReset, contextId + "_LSL_Ready");

        var view = _mmf.CreateViewAccessor();
        new Thread(() =>
        {
            using (view)
                while (WaitHandle.WaitAny([_stop, _request]) == 1)
                {
                    var name = new byte[view.ReadInt32(0)];
                    view.ReadArray(4, name, 0, name.Length);
                    _sessions.Add(open(Encoding.UTF8.GetString(name)));
                    _ready.Set();
                }
        }) { IsBackground = true, Name = contextId + " lobby" }.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        foreach (var s in _sessions) s.Dispose();
        _mmf.Dispose();
        _request.Dispose();
        _ready.Dispose();
        _onFinalize?.Invoke();
    }
}
