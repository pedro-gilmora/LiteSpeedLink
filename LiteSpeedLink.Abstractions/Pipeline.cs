namespace SourceCrafter.LiteSpeedLink
{
    public interface IPipeline<TIn, TOut>
    {
        TOut Process(TIn @in);
    }
    public interface IPipeline
    {
        TOut Process<TIn, TOut>(TIn @in);
    }
    public interface IPipelineAsync<TIn, TOut>
    {
        TOut ProcessAsync(TIn @in);
    }
    public interface IPipelineAsync
    {
        TOut ProcessAsync<TIn, TOut>(TIn @in);
    }
}