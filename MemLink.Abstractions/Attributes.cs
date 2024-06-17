using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;
using System.IO;

namespace SourceCrafter.MemLink;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ServiceHostAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]

#pragma warning disable CS9113 // Parameter is unread.
public class ServiceClientAttribute(string hostname, int port) : Attribute;

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public class ServiceAttribute(string? name = null) : Attribute;
#pragma warning restore CS9113 // Parameter is unread.

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ServiceHandlerAttribute : Attribute;