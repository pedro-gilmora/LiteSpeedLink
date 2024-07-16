namespace SourceCrafter.LiteSpeedLink;


#pragma warning disable CS9113 // Parameter is unread.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ServiceHostAttribute(ServiceConnectionType connectionType = ServiceConnectionType.Udp) : Attribute;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ServiceClientAttribute(ServiceConnectionType connectionType = ServiceConnectionType.Udp) : Attribute;

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public class ServiceAttribute(string? name = null) : Attribute;
#pragma warning restore CS9113 // Parameter is unread.

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ServiceHandlerAttribute : Attribute;