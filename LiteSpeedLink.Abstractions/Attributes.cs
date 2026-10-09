using System;

namespace SourceCrafter.LiteSpeedLink;


#pragma warning disable CS9113 // Parameter is unread.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ServiceHostAttribute(ServiceConnectionType connectionType = ServiceConnectionType.Memory) : Attribute;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ServiceClientAttribute(ServiceConnectionType connectionType = ServiceConnectionType.Memory) : Attribute;

/// <summary>Servicio expuesto por un [ServiceClient] como propiedad (IAuth -> Auth).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ServiceUnitAttribute<TService> : Attribute where TService : IServiceUnit;

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ServiceAttribute(string? name = null) : Attribute;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ServiceHandlerAttribute : Attribute;

//[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
//internal sealed class PipelineAttribute<TPipeline>(SourceCrafter.DependencyInjection.Constants.Lifetime lifetime = SourceCrafter.DependencyInjection.Constants.Lifetime.Scoped, string nameOrFormat = "GetPipeline{0}") : Attribute where TPipeline : IPipeline;

//[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
//internal sealed class AsyncPipelineAttribute<TPipeline>(SourceCrafter.DependencyInjection.Constants.Lifetime lifetime = SourceCrafter.DependencyInjection.Constants.Lifetime.Scoped, string nameOrFormat = "GetPipeline{0}Async") : Attribute where TPipeline : IPipelineAsync;
#pragma warning restore CS9113 // Parameter is unread.