using System;
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

    // El generador aplica los atributos en el orden declarado, valida que T implemente un pipeline y lo resuelve de los servicios registrados en el host/cliente.
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
    public sealed class ProcessorAttribute<T> : Attribute;
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = true, Inherited = false)]
    public sealed class PreProcessorAttribute<T> : Attribute;
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = true, Inherited = false)]
    public sealed class ClientPreProcessorAttribute<T> : Attribute;
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = true, Inherited = false)]
    public sealed class ServerPreProcessorAttribute<T> : Attribute;
    [AttributeUsage(AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
    public sealed class PostProcessorAttribute<T> : Attribute;
    [AttributeUsage(AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
    public sealed class ServerPostProcessorAttribute<T> : Attribute;
    [AttributeUsage(AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
    public sealed class ClientPostProcessorAttribute<T> : Attribute;

    /// <summary>Politica de lote del stream fijada en el contrato; el host generado la emite como constantes (sin mirar la config del servidor).</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class StreamAttribute : Attribute
    {
        public int Batch { get; set; }
        public int MaxDelayMs { get; set; }
    }
}