#!
#:property OutputType=Exe
#:property TargetFramework=net10.0
#:property PublishAot=true
#:property ExperimentalFileBasedProgramEnableTransitiveDirectives=true
#:property StripSymbols=true
#:property AllowUnsafeBlocks=true
#:property RuntimeIdentifier=win-x64
#:property SelfContained=true

using System;
using System.Runtime.InteropServices;

namespace Test;

class Program
{
    private static IntPtr s_sayHelloFunc;

    [UnmanagedCallersOnly]
    private static void OnRegisterService(IntPtr sayHelloFunc)
    {
        s_sayHelloFunc = sayHelloFunc;
    }

    unsafe static void Main(string[] args)
    {
        IntPtr handle = NativeLibrary.Load("Plugin");
        IntPtr proc = NativeLibrary.GetExport(handle, "RegisterServices");
        var registerServices = (delegate* unmanaged<IntPtr, void>)proc;

        registerServices((IntPtr)(delegate* unmanaged<IntPtr, void>)&OnRegisterService);

        var sayHello = (delegate* unmanaged<IntPtr>)s_sayHelloFunc;
        IntPtr result = sayHello();
        string text = Marshal.PtrToStringUni(result)!;
        Marshal.FreeCoTaskMem(result);

        Console.WriteLine(text);
    }
}