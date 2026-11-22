namespace SourceCrafter.LiteSpeedLink.Client;

/// <summary>
/// La peticion no llego a procesarse: se envio tras la trama de cierre del host (que cierra solo sin nada en curso) o la conexion ya
/// estaba cerrada antes de enviarla. El cliente la reintenta en una conexion nueva; solo sale si no puede reconectar.
/// </summary>
internal sealed class UnprocessedRequestException(string message) : IOException(message);
