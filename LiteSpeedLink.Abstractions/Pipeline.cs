using System;
using System.Threading;
using System.Threading.Tasks;

namespace SourceCrafter.LiteSpeedLink
{
    // Todo pipeline devuelve el estado del procesamiento: distinto de Success corta la llamada con Failed.
    // NotFound y StreamEnd no son validos en pipelines.
    public interface IPipeline<T> { (ResponseStatus, T) Process(T input); }
    public interface IPipeline<TIn, TOut> { (ResponseStatus, TOut) Process(TIn input); }
    public interface IAsyncPipeline<T> { Task<(ResponseStatus, T)> ProcessAsync(T input); }
    public interface IAsyncPipeline<TIn, TOut> { Task<(ResponseStatus, TOut)> ProcessAsync(TIn input); }
    public interface IAsyncValuePipeline<T> { ValueTask<(ResponseStatus, T)> ProcessAsync(T input); }
    public interface IAsyncValuePipeline<TIn, TOut> { ValueTask<(ResponseStatus, TOut)> ProcessAsync(TIn input); }

    /// <summary>Se lanza cuando un pipeline no devuelve Success; lo convierte en Failed el handler.</summary>
    public sealed class PipelineRejectedException(ResponseStatus status, string pipeline)
        : Exception($"Pipeline '{pipeline}' returned {status}.")
    {
        public ResponseStatus Status { get; } = status;
    }

    public static class PipelineResult
    {
        public static T Ensure<T>((ResponseStatus Status, T Value) result, string pipeline) =>
            result.Status is ResponseStatus.Success ? result.Value : throw new PipelineRejectedException(result.Status, pipeline);
    }

    // El generador aplica los atributos de cada lado en el orden declarado, valida que T implemente un pipeline y lo resuelve
    // de los servicios registrados en ese lado. En un parametro corre antes de la llamada (ida); en el retorno, despues (vuelta).
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
    public sealed class ClientProcessorAttribute<T> : Attribute;
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
    public sealed class ServerProcessorAttribute<T> : Attribute;

    /// <summary>
    /// Reintento de cliente: el generador emite un bucle inline alrededor de la llamada al transporte con estas constantes
    /// (sin structs ni delegados). Solo ante <see cref="TimeoutException"/> (plazo del transporte), hasta <see cref="Attempts"/>
    /// intentos con <see cref="IntervalMs"/> de pausa, nunca si el llamante cancelo. Los processors se ejecutan una vez, fuera
    /// del bucle. Exige handlers idempotentes. En streams se ignora (SCLSL016): repetir reenviaria lo ya entregado.
    /// </summary>
    // ponytail: pausa fija y solo TimeoutException; backoff/filtro configurable si un transporte lo necesita.
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ClientRetryAttribute(int attempts = 3, int intervalMs = 0) : Attribute
    {
        public int Attempts { get; } = attempts;
        public int IntervalMs { get; } = intervalMs;
    }

    /// <summary>
    /// Reintento de servidor: bucle inline solo alrededor de la invocacion al handler (p.ej. si este llama a otro servicio
    /// que vence su plazo). Misma semantica que <see cref="ClientRetryAttribute"/>; los server processors corren una vez.
    /// Junto a <see cref="ClientRetryAttribute"/> el generador avisa: los intentos se multiplican y con ellos la latencia.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ServerRetryAttribute(int attempts = 3, int intervalMs = 0) : Attribute
    {
        public int Attempts { get; } = attempts;
        public int IntervalMs { get; } = intervalMs;
    }

    /// <summary>Politica de lote del stream fijada en el contrato; el host generado la emite como constantes (sin mirar la config del servidor).</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class StreamAttribute : Attribute
    {
        public int Batch { get; set; }
        public int MaxDelayMs { get; set; }

        /// <summary>Watchdog de inactividad del cliente de memoria; 0 = timeout de la conexion, -1 (Timeout.Infinite) = suscripciones que pueden callar.</summary>
        public int IdleTimeoutMs { get; set; }
    }

    /// <summary>Unaria con stream QUIC propio en lugar del pool (respuestas grandes, ~1 MB+). Otros transportes la ignoran.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class DedicatedStreamAttribute : Attribute;

    /// <summary>
    /// Cache de cliente: el resultado final (tras los return processors) se guarda tipado por la clave de argumentos
    /// (<see cref="CacheKeyAttribute"/> o todos los de payload) durante <see cref="DurationMs"/>. Un acierto no toca el
    /// transporte. Solo respuestas Success; streams, comandos y out/ref la ignoran (SCLSL017).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ClientCacheAttribute(int durationMs, int capacity = 1024) : Attribute
    {
        public int DurationMs { get; } = durationMs;
        public int Capacity { get; } = capacity;
    }

    /// <summary>
    /// Cache de servidor: bytes de la peticion -> bytes de la respuesta ya procesada. Un acierto se salta lectura, DI,
    /// handler, return processors y serializacion; los param processors (p.ej. autorizacion) corren antes de consultar.
    /// Compartida entre conexiones: lo que dependa del llamante no debe cachearse.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ServerCacheAttribute(int durationMs, int capacity = 1024) : Attribute
    {
        public int DurationMs { get; } = durationMs;
        public int Capacity { get; } = capacity;
    }

    /// <summary>
    /// Que forma la clave de cache. En un parametro: el parametro entero o, con <paramref name="members"/>, rutas relativas
    /// a su tipo (<c>nameof(Order.Customer.Region)</c>). En el metodo: rutas con raiz en el parametro
    /// (<c>nameof(order.Id)</c>). El generador lee las rutas de la sintaxis (nameof pierde el prefijo). Sin [CacheKey],
    /// todos los parametros de payload.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class CacheKeyAttribute(params string[] members) : Attribute
    {
        public string[] Members { get; } = members;
    }
}