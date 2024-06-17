using MemoryPack;

namespace SourceCrafter.MemLink;

//delegate Guid CreateUser(string name, DateOnly createdDate, string? principalEmail = null, string? principalPhoneNumber = null);

public partial class SumService : ISumService
{
    public int Sum(out string name, params int[] ints)
    {
        name = "Pedro";
        return ints.Sum();
    }
}

// Contracts layer
public interface ISumService : IServiceUnit
{
    int Sum(out string name, params int[] ints);
}

// Framework abstraction file

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public sealed class ServiceOperationAttribute<T> : Attribute where T : Delegate;
