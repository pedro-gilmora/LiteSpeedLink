using MemoryPack;

using Microsoft.Extensions.Logging;

namespace SourceCrafter.LiteSpeedLink;

//delegate Guid CreateUser(string name, DateOnly createdDate, string? principalEmail = null, string? principalPhoneNumber = null);

public partial class SumService : ISumService
{
    public int Sum(params int[] ints)
    {
        return ints.Sum();
    }
}

// Contracts layer
public interface ISumService : IServiceUnit
{
    int Sum(params int[] ints);
}

public interface IDivideSerice : IServiceUnit;
