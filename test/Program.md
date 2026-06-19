🟦 Project layout

```
test/
 ├── IMyService.cs
 ├── MyService.cs
 │    └── Register.cs
 └── Host/
      └── Host.csproj
      └── Program.cs
```

📌 Contracts file

```cs
namespace Contracts;

public interface IPlugin
{
    string SayHello();
}

```
📌 Plugin project (AOT‑compiled shared library)
-------
**Plugin1.csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <NativeLib>Shared</NativeLib>
    <StripSymbols>true</StripSymbols>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Contracts\Contracts.csproj" />
  </ItemGroup>
</Project>
```

MyService.cs
```cs
using Contracts;

namespace Plugin1;

public class MyService : IMyService
{
    public string SayHello() => "Hello from Plugin1!";
}
```
Register.cs

```cs
using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Contracts;

namespace Plugin1;

public static class Register
{
    [UnmanagedCallersOnly(EntryPoint = "RegisterServices")]
    public static void RegisterServices(IntPtr scPtr)
    {
        var services = (IServiceCollection)GCHandle.FromIntPtr(scPtr).Target;
        services.AddSingleton<IMyService, MyService>();
    }
}

```
📌 Host project (AOT executable)

Host.csproj

<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <PublishAot>true</PublishAot>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Contracts\Contracts.csproj" />
  </ItemGroup>
</Project>

```cs
Program.cs

using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Contracts;

class Program
{
    static void Main()
    {
        var services = new ServiceCollection();

        // Load plugin
        IntPtr handle = NativeLibrary.Load("Plugin1");
        IntPtr proc = NativeLibrary.GetExport(handle, "RegisterServices");

        var register = (delegate* unmanaged<IntPtr, void>)proc;
        register(GCHandle.ToIntPtr(GCHandle.Alloc(services)));

        var provider = services.BuildServiceProvider();
        var svc = provider.GetRequiredService<IMyService>();

        Console.WriteLine(svc.SayHello());
    }
}
```