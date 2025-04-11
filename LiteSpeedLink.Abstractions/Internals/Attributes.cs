using SourceCrafter.DependencyInjection.Attributes;
using System.Text;
using SourceCrafter.DependencyInjection;
using SourceCrafter.LiteSpeedLink;

namespace LiteSpeedLink.Abstractions.Internals;


#pragma warning disable CS9113 // Parameter is unread.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class ServiceHostAttribute(ServiceConnectionType connectionType = ServiceConnectionType.Udp) : Attribute;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class ServiceClientAttribute(ServiceConnectionType connectionType = ServiceConnectionType.Udp) : Attribute;

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
internal sealed class ServiceAttribute(string? name = null) : Attribute;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class ServiceHandlerAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class PipelineAttribute<TPipeline>(Lifetime lifetime = Lifetime.Scoped, string nameOrFormat = "GetPipeline{0}") : Attribute where TPipeline : IPipeline;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class AsyncPipelineAttribute<TPipeline>(Lifetime lifetime = Lifetime.Scoped, string nameOrFormat = "GetPipeline{0}Async") : Attribute where TPipeline : IPipelineAsync;
#pragma warning restore CS9113 // Parameter is unread.