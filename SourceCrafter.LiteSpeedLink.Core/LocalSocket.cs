namespace SourceCrafter.LiteSpeedLink;

/// <summary>Ruta del socket UDS de <c>Local</c>: host y cliente la derivan igual del mismo nombre.</summary>
public static class LocalSocket
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Fuera de Windows, en un directorio solo del usuario: <c>$XDG_RUNTIME_DIR</c> o <c>&lt;tmp&gt;/lsl-&lt;usuario&gt;</c> (0700).
    /// Un <c>/tmp</c> compartido permitiria a otro usuario ocupar la ruta y suplantar al host. En Windows <c>%TEMP%</c> ya es del usuario.
    /// </summary>
    public static string GetPath(string name)
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(Path.GetTempPath(), name + ".lsl");

        string directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
            ? runtime
            : EnsurePrivate(Path.Combine(Path.GetTempPath(), "lsl-" + Environment.UserName));

        return Path.Combine(directory, name + ".lsl");
    }

    /// <summary>Crea el directorio 0700 o comprueba que el existente lo sea y no sea un enlace; si no, falla cerrado.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static string EnsurePrivate(string directory)
    {
        var info = Directory.CreateDirectory(directory, PrivateDirectory);

        if (info.LinkTarget is not null || info.UnixFileMode != PrivateDirectory)
            throw new UnauthorizedAccessException($"'{directory}' must be a private directory (0700, not a link) to hold LiteSpeedLink sockets.");

        return directory;
    }
}
