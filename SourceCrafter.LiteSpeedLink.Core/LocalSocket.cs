namespace SourceCrafter.LiteSpeedLink;

/// <summary>Ruta del socket UDS de <c>Local</c>: host y cliente la derivan igual del mismo nombre.</summary>
public static class LocalSocket
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Fuera de Windows, en un directorio solo del usuario: <c>$XDG_RUNTIME_DIR</c>; si no, root usa <c>/var/run/lsl</c> (solo root crea en el)
    /// y el resto <c>&lt;tmp&gt;/lsl-&lt;usuario&gt;</c> (0700, propio). Un <c>/tmp</c> compartido permitiria a otro usuario crear la ruta antes,
    /// dejar al host sin arrancar o, si el host es root, suplantarlo. En Windows <c>%TEMP%</c> ya es del usuario.
    /// </summary>
    public static string GetPath(string name)
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(Path.GetTempPath(), name + ".lsl");

        string directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
            ? runtime
            // root puede hacer chmod de cualquier cosa: la prueba de propiedad no le sirve, asi que no usa /tmp.
            : EnsurePrivate(Environment.IsPrivilegedProcess ? "/var/run/lsl" : Path.Combine(Path.GetTempPath(), "lsl-" + Environment.UserName));

        return Path.Combine(directory, name + ".lsl");
    }

    /// <summary>
    /// Crea el directorio 0700 o comprueba que el existente sea propio, 0700 y no un enlace; si no, falla cerrado. La propiedad se prueba
    /// con <c>chmod</c>, que solo puede hacer el dueno (o root): .NET no expone el UID del dueno sin P/Invoke.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static string EnsurePrivate(string directory)
    {
        var info = Directory.CreateDirectory(directory, PrivateDirectory);

        // Antes del chmod: chmod sigue los enlaces.
        if (info.LinkTarget is not null)
            throw new UnauthorizedAccessException($"'{directory}' must be a private directory (not a link) to hold LiteSpeedLink sockets.");

        try
        {
            File.SetUnixFileMode(directory, PrivateDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"'{directory}' belongs to another user: LiteSpeedLink sockets need a directory of their own.", ex);
        }

        info.Refresh();

        if (info.UnixFileMode != PrivateDirectory)
            throw new UnauthorizedAccessException($"'{directory}' must be a private directory (0700) to hold LiteSpeedLink sockets.");

        return directory;
    }
}
