﻿using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace SourceCrafter.LiteSpeedLink.Client
{
    public interface IConnection : IDisposable, IAsyncDisposable
    {
        ValueTask SendAsync(int op, CancellationToken token = default, [CallerMemberName] string name = "");
        ValueTask SendAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn>(int op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
        ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(int op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
        ValueTask<TOut?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(int op, CancellationToken token = default, [CallerMemberName] string name = "");
        IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TIn, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(int op, TIn payload, CancellationToken token = default, [CallerMemberName] string name = "");
        IAsyncEnumerable<TOut?> EnumerateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TOut>(int op, CancellationToken token = default, [CallerMemberName] string name = "");
    }
}