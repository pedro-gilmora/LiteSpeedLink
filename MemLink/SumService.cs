using MemoryPack;

namespace SourceCrafter.MemLink;

//delegate Guid CreateUser(string name, DateOnly createdDate, string? principalEmail = null, string? principalPhoneNumber = null);

public partial class SumService : ISumService
{
    public int Sum(IDivideSerice name, params int[] ints)
    {
        return ints.Sum();
    }
}

// Contracts layer
public interface ISumService : IServiceUnit
{
    int Sum([Service("sum")] IDivideSerice name, params int[] ints);
}

public interface IDivideSerice : IServiceUnit;
