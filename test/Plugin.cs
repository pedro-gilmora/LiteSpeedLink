#!
#:property OutputType=Library
#:property AllowUnsafeBlocks=true
#:property RuntimeIdentifier=win-x64
#:property SelfContained=true
#:property TargetFramework=net10.0
#:property PublishAot=true
#:property NativeLib=Shared
#:property ExperimentalFileBasedProgramEnableTransitiveDirectives=true
#:property StripSymbols=true

using System;
using System.Runtime.InteropServices;

namespace Plugin;

public static class Register
{
    [UnmanagedCallersOnly]
    private static IntPtr SayHelloNative()
    {
        return Marshal.StringToCoTaskMemUni("Hello from Plugin1!");
    }

    [UnmanagedCallersOnly(EntryPoint = "RegisterServices")]
    public static unsafe void RegisterServices(IntPtr registerCallbackPtr)
    {
        var callback = (delegate* unmanaged<IntPtr, void>)registerCallbackPtr;
        callback((IntPtr)(delegate* unmanaged<IntPtr>)&SayHelloNative);
    }
}