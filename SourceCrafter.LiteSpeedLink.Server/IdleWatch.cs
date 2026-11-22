using System.Diagnostics;
using System.IO.Pipelines;

namespace SourceCrafter.LiteSpeedLink;

/// <summary>
/// Conexion ociosa (sin peticiones en curso, sin stream abierto, sin trama a medias) durante <c>idleTimeoutMs</c>: el lector la cierra con
/// la trama de cierre <c>[len=4][corrId=0]</c>. Un temporizador por conexion; el lector solo escribe una marca de tiempo por lectura.
/// Mientras la conexion esta ocupada el temporizador renueva la marca, asi que el plazo cuenta desde la ultima actividad observada.
/// </summary>
internal sealed class IdleWatch : IDisposable
{
    private readonly PipeReader _reader;
    private readonly Inflight _inflight;
    private readonly int _idleMs;
    private readonly long _idleTicks;
    private readonly Timer _timer;
    private long _lastActivity;
    private int _partial, _expired;

    public IdleWatch(PipeReader reader, Inflight inflight, int idleMs)
    {
        (_reader, _inflight, _idleMs) = (reader, inflight, idleMs);
        _idleTicks = idleMs * Stopwatch.Frequency / 1000;
        _lastActivity = Stopwatch.GetTimestamp();
        _timer = new Timer(static s => ((IdleWatch)s!).Check(), this, idleMs, Timeout.Infinite);
    }

    public bool Expired => Volatile.Read(ref _expired) == 1;

    /// <summary>Tras cada lectura: sin asignar ni tocar el temporizador.</summary>
    public void Touch(bool partialFrame)
    {
        Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
        Volatile.Write(ref _partial, partialFrame ? 1 : 0);
    }

    /// <summary>Vencio, pero el lector encontro trabajo: sigue abierta y vuelve a contar.</summary>
    public void Resume()
    {
        Touch(false);
        Volatile.Write(ref _expired, 0);
        _timer.Change(_idleMs, Timeout.Infinite);
    }

    private void Check()
    {
        long now = Stopwatch.GetTimestamp();

        if (!_inflight.IsIdle || Volatile.Read(ref _partial) == 1)
        {
            // Ocupada: la actividad es ahora; se vuelve a mirar dentro de un plazo completo.
            Volatile.Write(ref _lastActivity, now);
            _timer.Change(_idleMs, Timeout.Infinite);
            return;
        }

        long idleFor = now - Volatile.Read(ref _lastActivity);

        if (idleFor < _idleTicks)
        {
            _timer.Change(Math.Max(1, (_idleTicks - idleFor) * 1000 / Stopwatch.Frequency), Timeout.Infinite);
            return;
        }

        // El lector confirma con lo que tenga a mano (puede haber llegado una peticion a la vez) antes de cerrar.
        Volatile.Write(ref _expired, 1);
        _reader.CancelPendingRead();
    }

    public void Dispose() => _timer.Dispose();
}
