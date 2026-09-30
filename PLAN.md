# Plan de mejoras — LiteSpeedLink

## Arranque de sesión *(leer primero)*

Este archivo es el **único traspaso** entre sesiones. Al terminar un paso: marcar su estado aquí,
añadir lo aprendido en *Trampas conocidas* y actualizar *Siguiente paso*.

### Mapa del repo

| Proyecto | Rol |
|---|---|
| `LiteSpeedLink.Abstractions` | Contratos compartidos: `ResponseStatus` (incl. `Batch`), `Pipeline.cs` (interfaces + atributos de procesadores + `[Stream(Batch, MaxDelayMs)]`). |
| `Internals` | `Framing`/`FrameWriter`, enlazados (`<Compile Include>`) en Client, Server, Tests y Benchmarks; no es proyecto ni API pública. |
| `SourceCrafter.LiteSpeedLink.Helpers` | **Generador**: `ServiceHandlers.GenerateServiceHost.cs` (host, `__Handler`, políticas de stream), `ServiceHandlers.GenerateServiceClient.cs` (cliente), `ServiceHandlers.Raw.cs` (lectores/escritores raw por operación), `ServiceHandlers.Pipelines.cs` (etapas y diagnósticos). |
| `SourceCrafter.LiteSpeedLink.Client` / `.Server` | Transportes: `MemoryConnection`, `Tcp/Uds/Udp/QuicConnection` (`StreamConnection` + `MultiplexedChannel` para TCP/UDS), `Server.*` (`Server.Pipes` compartido). |
| `Application.Contracts` | Contratos de ejemplo `IAuth` y `IStreams` (8 formas de stream) + pipelines de muestra (`TrimName`, `Upper`, `Bracket`, `Tag`, `Exclaim`, `ParseInt`, `IntToString`). |
| `LiteSpeedLink` | App de muestra: `TextService`/`TextServiceClient` (Memory), `TransportSamples.cs` (Udp/Tcp/Quic), `StreamService`, `AuthService`, `Program.cs`. |
| `LiteSpeedLink.Tests` | xUnit: servidores, framing, pipelines, lotes de stream, raw, pool QUIC. |
| `LiteSpeedLink.Benchmarks` | BenchmarkDotNet (6.1): escenarios, POCs del estudio (`Scenarios/*Poc*`) y comparativas (`Comparison/`, `QuicComparison`). |
| `docs/SourceGenStudy.md` | Inventario de decisiones runtime → generador, con estado y evidencia. |
| `..\SourceCrafter.DependencyInjection\SharedMemory` | **Repo aparte** (fork, rama `migrating-to-modern-memory-management`). Sus cambios se commitean allí. |

### Verificar (PowerShell, desde `D:\Code\MemLink`)

```powershell
dotnet build-server shutdown; dotnet build LiteSpeedLink.slnx --no-incremental -nodeReuse:false
dotnet test LiteSpeedLink.Tests --no-build     # esperado: 69/69
dotnet run --project LiteSpeedLink --no-build   # esperado: saludo, Echo procesado y "Rejected: Pipeline 'TrimName'..."
```

Código generado: `LiteSpeedLink\obj\gen\SourceCrafter.DependencyInjection\ServiceProviders\*.g.cs`.

### Reglas de trabajo

- Las premisas P1-P4 son vinculantes: nada de handshakes, descubrimiento ni `ITransport` público.
- Economía de bytes primero: framing/correlación solo donde el transporte lo exige.
- Diff mínimo; `ponytail:` para atajos con techo conocido; un check ejecutable por lógica no trivial.
- Pipelines: implementación **implícita** obligatoria; etapas emitidas como `if (... is not (Success, TOut x)) throw`, de dentro hacia fuera (`var` solo para `Nullable<T>`).
- Lo conocido en compile-time (contrato, transporte, forma, tipos, política de lote) lo decide el generador; ver `docs/SourceGenStudy.md`.
- Mejoras probadas entran como opt-in (P6); pasan a defecto solo con benchmark (p. ej. `coalesceStreams`).
- Comparativas: los peores casos son **unaria** y **stream simple**, no el stream por lotes.

### Trampas conocidas

- **Generador cacheado**: tras tocar `Helpers`, sin `build-server shutdown` + `--no-incremental` el `.g.cs` no cambia.
- **Ediciones que no llegan a disco** (scripts PowerShell con caracteres no ASCII incluidos): verificar con `Select-String` antes de compilar.
- **Deadlock sync tras async en Memory**: resuelto con `RunContinuationsAsynchronously` en `RpcBuffer.ResponseReady` (fork). Si reaparece un timeout en una llamada sync, mirar ahí.
- **Id de operación**: `GetServiceId` se calcula sobre el nombre completo del método; no alterarlo al cambiar el formato de firmas (5.3).
- Warnings `MSB3270` (MSIL vs AMD64 de `SharedMemory.dll`): conocidos e inocuos.
- **`SharedMemory.dll` se consume publicado**: tras tocar el fork, `dotnet publish ..\SourceCrafter.DependencyInjection\SharedMemory\SharedMemory\SharedMemory.csproj -c Release -f net10.0 -r win-x64 --self-contained false -o ..\SourceCrafter.DependencyInjection\publish\net10.0\win-x64`.
- **Ediciones desde VS que no llegan a disco** en ficheros abiertos (`ServersTest.cs`): verificar con `Test-Path`/`Select-String`.
- **Puertos en tests paralelos** (xUnit): cada test de red usa un puerto propio; una colisión se ve como `QuicException` al enlazar, no como fallo del transporte.
- **HTTP/3 en benchmarks**: `https://localhost` resuelve a `::1` y Kestrel escucha en 127.0.0.1 ⇒ error ALPN. Usar IP.
- **Consumidor de stream inline** (`AllowSynchronousContinuations`): sync-over-async dentro de un `await foreach` interbloquea al lector.
- **Comandos git en paralelo** chocan con `index.lock`: una operación por comando.

### Siguiente paso

0. Regla: cada item hecho lleva test o PoC. `CoverageGapTests.cs` cubre 2.4, 3.1, 3.3 (trama incompleta),
   3.4 (TCP y memoria), 3.7 (QUIC concurrente) y 5.4. Sin test automático (requieren arnés Roslyn):
   1.3, 3.2, 5.2, 5.3 → hoy su PoC es `LiteSpeedLink/Program.cs` (compila el código generado y lo ejecuta).

Pendientes en orden (uno a uno; cada uno con test o PoC y verde antes del siguiente):

1. [x] Sincronizar este plan con el código y `docs/SourceGenStudy.md`.
2. [-] ~~Estado del rechazo en pipelines~~: no es regresión. Un pipeline solo distingue `Success`/`Failed` (corte anticipado), así que `Failed` fijo es correcto.
3. [x] **`MultiplexedChannel.DisposeAsync`**: `CancelPendingRead()` antes de esperar el bucle de lectura; ya no depende de cerrar el socket antes. Test `DisposeTest` (TCP/UDS, ≤ 2 s).
4. [x] **`ToListAsync()` sobre `EnumerateAsync` en Memory**: ya no se reproduce (10/10); lo resolvió `RunContinuationsAsynchronously` en `RpcBuffer.ResponseReady` (5.2). Regresión: `TestMemoryStreamsRoutedPerClient`.
5. [x] **`ConfigureAwait(false)`** en todos los `await` emitidos: host (`Start` QUIC, resolución async, `ReturnAsync`/`EnumerateAsync`/`NotFoundAsync`/`FailAsync`), etapas de pipeline (`ProcessAsync` y resolución async) y cliente tipado (`GetAsync`/`SendAsync`). Verificación: 0 `await` sin `ConfigureAwait` en `obj/gen` + POC (Memory/UDP/TCP/QUIC) y suite 71/71 en verde.
`Enumerate*` raw diferido por POC (6d, `StreamRawDecodePoc`); lectores de respuesta con la firma del contrato (6e).
7. [ ]
8. [ ] **Sesiones Memory de clientes caídos** (`MemoryLobby`).
9. [ ] **Resto de 5.2**: early-return sin excepción, `ref`/`out`/`in` y streams con procesadores (hoy SCLSL012), test de SCLSL014 (arnés Roslyn).
10. [ ] **PoC #14**: pool QUIC vs stream propio para unarias grandes/lentas; generar la ruta por operación solo si gana.
11. [ ] **`LocalConnection` seleccionada por el generador** (5.6): RpcBuffer en Windows, UDS en el resto; quitar los `[SupportedOSPlatform("windows")]` propagados.
12. [ ] **5.7 + 2.1**: `SharedMemory` interno y APIs `Span`/`IBufferWriter` en `RpcBuffer`.
13. [ ] **Autenticación** como pipeline de servidor (3.8) y **retry** como pipeline de cliente (5.2-R).

Pospuestos: 5.8 (endpoints por configuración), estudio #5/#6/#7 (solo con perfil), cookie anti-amplificación UDP, test de servidor falso byte a byte.

---

## Premisas

### P1 · Contrato cerrado *(firme)*

Cliente y host se generan del mismo contrato en tiempo de compilación. El conjunto de operaciones,
sus tipos y los transportes disponibles son **conocidos y finitos** en build-time.

**Excluye**: negociación de protocolo, handshakes de versión, descubrimiento dinámico de servicios,
reflexión en runtime, metadata de tipos en el canal de error, `ITransport` como punto de extensión
público para terceros.

**No es handshake**: el marcador `opId = long.MinValue` al abrir un stream QUIC reutilizable es una etiqueta fija del tipo de stream, conocida en compile-time, sin ida y vuelta.

### P2 · Emparejamiento de extremos: responsabilidad del desarrollador *(acordado)*

No hay forma unificada de garantizar que ambos binarios salieron de la misma fuente, y **no se va a
intentar**. Si el id hasheado de la firma no coincide, no hay colisión posible: simplemente no hay match.

**Única consecuencia que se mantiene** (ver 3.4): que el no-match sea **ruidoso y no silencioso**.
Hoy un id desconocido cae en `default: return null`, el cliente deserializa `null` y devuelve `default`
— un `false`/`null` indistinguible de una respuesta legítima. No se pide validar nada de más: se pide
que el `default` ya existente devuelva `ResponseStatus.NotFound` en lugar de `null`. Coste: 1 byte.

### P3 · Los clientes deben ser multihilo *(decidido)*

Una instancia de cliente generado **puede usarse concurrentemente desde varios hilos**.

> **Nota sobre "ejes ortogonales"** *(explicación pedida)*
> Dos propiedades son *ortogonales* cuando conocer una no te dice **nada** sobre la otra: son ejes
> independientes, como la anchura y la altura de un rectángulo.
>
> Aquí: **"el contrato se conoce en compile-time"** (P1) y **"cuántos hilos usan el cliente"** (P3)
> son ortogonales. El compilador sabe perfectamente que `Greet` recibe `string` y devuelve `string`
> — eso es P1, y es cierto. Pero ese conocimiento **no dice nada** sobre si dos hilos van a llamar
> a `Greet` a la vez sobre el mismo objeto. Un contrato totalmente estático es compatible tanto con
> un cliente de un solo hilo como con uno compartido por cien.
>
> El error de la premisa original era usar P1 ("uso predecible") para dar por resuelto P3
> ("luego no hay concurrencia"). No se deduce. Eran dos decisiones y solo una estaba tomada.

**Consecuencias directas** (P3 resuelto ⇒ esto deja de ser opcional):

- El estado compartido de cada conexión (`PipeReader`/`PipeWriter`, socket, `RpcBuffer`) **no puede**
  usarse asumiendo un solo request en vuelo.
- Se necesita **correlación de mensajes** y un **read-loop único** por conexión (ver 3.6 / PoC-A).
- El **framing** (3.3) pasa de "conveniente" a **prerrequisito**: sin delimitación de mensajes no se
  puede demultiplexar.
- La correlación (corrId) la usan TCP, UDS y el **pool QUIC de unarias**; los streams QUIC de `EnumerateAsync` siguen sin corrId (el stream empareja).

### P5 · Red antes que implementación *(decidido)*

El objetivo es perder menos paquetes; si se pierden, que sea un problema de la red y no de cliente/servidor. Lo decidible en compile-time no se adivina en runtime.

### P6 · Opcionalidad primero *(decidido)*

Una mejora probada por PoC entra primero como **opción** (opt-in); pasa a defecto solo si el benchmark lo justifica sin cerrar escenarios más complejos. Rendimiento y menos memoria sin perder flexibilidad.

### P4 · Regla transversal

Lo que es **propiedad del transporte** no se descarta por premisas de uso.
La fragmentación de TCP ocurre aunque el uso sea perfectamente predecible.

---

Estado: `[ ]` pendiente · `[~]` en curso · `[x]` hecho · `[-]` descartado

---

## Fase 1 — Limpieza

- [x] **1.1 `Console.WriteLine` fuera de la ruta caliente** — *hecho en `MemoryConnection`*
  - [x] ~~Pendiente~~: ya no quedan `Console.WriteLine` en `Server.*` ni en `AuthService`.
	La de la línea 62 (`Server return: {@in}`) está en `MemoryRequestContext.Return<T>`, es decir,
	**en cada respuesta**, con boxing de `TIn`.
  - También `AuthService.cs:15` (servicio de prueba, contamina el benchmark).

- [-] **1.2 Quitar `DynamicallyAccessedMembers(All)`** — **descartado**
  - Motivo: `MemoryPackSerializer.Serialize<T>/Deserialize<T>` anota sus propios parámetros genéricos
	con `[DynamicallyAccessedMembers(All)]`. Al reenviar un `T` genérico desde `IConnection`, el
	analizador de trimming exige propagar la anotación (IL2091). No es opcional.
  - **Consecuencia a registrar**: el techo de trimming/AOT lo fija MemoryPack, no nuestro código.
	Si en algún momento se quiere NativeAOT agresivo, la vía es **cerrar el genérico en el código
	generado** (el generador conoce los tipos concretos) en lugar de propagarlo, no quitar atributos.
	Queda como nota, no como tarea.

- [x] **1.3 Limpiar ruido del código generado** *(acordado)*
  - `private readonly static object _lock` emitido en el cliente y nunca usado
	(`ServiceHandlers.GenerateServiceClient.cs`, ~línea 70).
  - `MemoryRequestContext.ResponseWriter` (`BufferBuilder`) nunca emitido (`Server.Memory.cs:54`).
  - `StartMemoryServer` recibe `onFinalize` y lo ignora; `slave` es variable muerta
	(`Server.Memory.cs:21-28`, `38-45`).

---

## Fase 2 — Asignaciones por petición

- [x] **2.1 `SerializePayload` sin asignaciones intermedias** *(acordado)*
  - Hecho: el fork acepta `ReadOnlyMemory<byte>` hasta `WriteProtocolV1` (antes `Send(ReadOnlyMemory)`
	descartaba el payload si no era un array completo). El cliente envía `WrittenMemory` del writer por
	hilo: 0 asignaciones de payload; es seguro porque `RpcBuffer` copia de forma síncrona antes de
	devolver la `Task`. Streams siguen con `ToArray()` (una asignación por apertura).
  - Hecho (0 copias): `RpcBuffer.Send/SendAsync<TState>(state, Action<IBufferWriter<byte>, TState>)`;
	un `NodeWriter` escribe directamente en el nodo de memoria compartida. Si el payload no cabe en un
	nodo, desborda a un buffer reutilizable y se trocea como antes (test `TestMemoryMultiPacketPayload`).
	El cliente usa `WriteRequest` para Get/Send con payload.
  - `MemoryConnection.cs:263-276`: 3 asignaciones por llamada.
  - Paso 1 (independiente de `SharedMemory`): un único `ArrayPool<byte>` +
	`BinaryPrimitives` + `MemoryPackSerializer.Serialize(bufferWriter, payload)`. 3 allocs → 1 alquiler.

  **Respuesta: ¿qué ganaría `RpcBuffer` con `ReadOnlySpan<byte>`?**
  Hoy la cadena es: `Serialize` → `byte[]` nuevo → copia al buffer de petición → **`RpcBuffer` lo
  vuelve a copiar dentro de la región de memoria compartida**. Son 2 copias y ≥1 array heap por llamada,
  y el array es inevitable porque la firma es `RemoteRequest(byte[])`.

  Con una sobrecarga `RemoteRequest(ReadOnlySpan<byte>)` se elimina la asignación (el span puede venir
  de `ArrayPool` o de un `stackalloc` acotado), pero **queda 1 copia**.
  El óptimo real es que `RpcBuffer` exponga el destino:

  ```csharp
  RemoteRequest(int length, SpanAction<byte> fill)      // o
  IBufferWriter<byte> BeginRequest(); void EndRequest();
  ```

  Así MemoryPack serializa **directamente sobre la memoria compartida**: 0 asignaciones y 0 copias.
  Esa es la ganancia real, y es la que justifica tocar el fork.
  - **Depende de**: 5.7 (integrar `SharedMemory` como código interno hace este cambio trivial).

- [x] **2.2 `BuildRequest`: quitar `stackalloc` + `ToArray()`** *(acordado)*
  - `TcpConnection.cs:532`, `UdpConnection.cs:76`, `QuicConnection.cs:496`.
  - `stackalloc` no acotado (StackOverflow con payload grande) y anulado por `.ToArray()`.
  - → escribir en `writer.GetSpan(...)`/`Advance(...)`; UDP con `ArrayPool`.

- [x] **2.3 No copiar el payload en el servidor** *(acordado)*
  - `Server.Memory.cs:27,44`: `payload[8..]` copia el array completo por request.
  - → `ReadOnlyMemory<byte>` con offset.
  - ⚠️ El `readonly ref struct` que propuse para `MemoryRequestContext` **queda descartado**:
	es incompatible con los handlers `async` y con `Yield`. Se mantiene clase, posiblemente pooled.

- [x] **2.4 `BinaryPrimitives` para el `opId`** — *propuesta pedida*

  **Hoy** hay 3 formas distintas de escribir el mismo `long`:

  ```csharp
  // MemoryConnection.cs:62,84  -> asigna un byte[8] en el heap
  MemoryRpc.RemoteRequest(BitConverter.GetBytes(op), _timeout, token)

  // TcpConnection.cs:104 / Udp / Quic -> MemoryPack para 8 bytes fijos
  BuildRequest(Serialize(op), Serialize(payload))

  // Server.Memory.cs:25 -> ok, pero acoplado a la endianness de la máquina
  long op = BitConverter.ToInt64(payload.AsSpan()[..8]);
  ```

  **Propuesta** — un único helper compartido, endianness explícita, cero asignaciones:

  ```csharp
  // LiteSpeedLink.Abstractions/Framing.cs
  internal static class Framing
  {
	  public const int OpIdSize = sizeof(long);

	  [MethodImpl(MethodImplOptions.AggressiveInlining)]
	  public static void WriteOpId(Span<byte> destination, long op) =>
		  BinaryPrimitives.WriteInt64LittleEndian(destination, op);

	  [MethodImpl(MethodImplOptions.AggressiveInlining)]
	  public static long ReadOpId(ReadOnlySpan<byte> source) =>
		  BinaryPrimitives.ReadInt64LittleEndian(source);
  }
  ```

  Uso en el cliente de memoria (combinado con 2.1):

  ```csharp
  var buffer = ArrayPool<byte>.Shared.Rent(Framing.OpIdSize + estimated);
  Framing.WriteOpId(buffer, op);
  // ...serializar el payload a partir de buffer[Framing.OpIdSize..]
  ```

  Uso en TCP/QUIC (combinado con 2.2) — sin buffer intermedio:

  ```csharp
  Framing.WriteOpId(writer.GetSpan(Framing.OpIdSize), op);
  writer.Advance(Framing.OpIdSize);
  MemoryPackSerializer.Serialize(writer, payload);
  await writer.FlushAsync(token);
  ```

  Beneficio: elimina `BitConverter.GetBytes` (1 alloc), elimina MemoryPack para 8 bytes fijos,
  y fija la endianness — hoy `BitConverter` depende de la máquina, lo cual es irrelevante en local
  pero **no** en TCP entre arquitecturas distintas.

- [-] **2.5 `lock` en `MemoryRpc`** — **descartado en su forma original**
  - Tienes razón: el `lock` protege la **creación**, y un CAS permitiría construir varios `RpcBuffer`
	en carrera. No procede cambiar la estrategia de sincronización.
  - **Lo que sí queda** (trivial, sin tocar el lock): la propiedad se invoca **varias veces por
	operación**. Basta con `var rpc = MemoryRpc;` una vez por método y reutilizar el local.
  - Reclasificado como sub-tarea cosmética de 2.1.

---

## Fase 3 — Correctitud

- [x] **3.1 `MemoryConnection.Dispose` resucita el buffer** *(acordado)*
  - `MemoryConnection.cs:278-284`: usa la *propiedad*, que recrea el `RpcBuffer` si ya estaba dispuesto.
	Debe usar el campo de respaldo.

- [x] **3.2 `GetServiceId` determinista** *(acordado)*
  - `GenerateServiceHost.cs:585-591`: `Encoding.Default` → **`Encoding.UTF8`** + hash estable
	(`XxHash64` / FNV-1a en lugar de MD5).
  - Diagnóstico del generador si dos métodos colisionan (compile-time, coherente con P1).

- [x] **3.3 Framing por longitud en TCP** *(acordado)* — `Framing.TryReadFrame` en `MultiplexedChannel`/`ServePipeAsync`. Test `TestTcpFragmentedFrames` (2 peticiones byte a byte, respuestas por `corrId`).
  - [ ] Test de cliente con servidor falso que responda byte a byte (bucle de `MultiplexedChannel`). Hoy de bajo valor: mismo `TryReadFrame` y bucle calcado de `ServePipeAsync`. Hacerlo cuando el bucle del cliente diverja (TLS 3.8, streaming 4.2).
  - `TcpConnection.cs:108-112` asume `1 ReadAsync == 1 mensaje`. Falso por definición en TCP (P4).
  - Formato: `[int32 length][int64 opId][payload]`, lectura con `SequenceReader<byte>` en bucle.
  - **Prerrequisito de 3.6** (sin delimitación no hay demultiplexación).
  - *Discusión cerrada*: TCP = remoto, SharedMemory = local. Ambos de primera clase, dominios distintos.
	Mi pregunta sobre "principal vs secundario" era solo de **priorización de esfuerzo**, no de diseño;
	queda sin efecto: se hace.
  - QUIC: revisar aparte. Si cada RPC abre su propio stream, QUIC ya delimita y no hace falta framing;
	pero entonces 3.6 se resuelve distinto (un stream por request en vuelo).

- [x] **3.4 Errores del servicio** *(acordado, con tu simplificación)*
  - **Un único `try/catch` envolviendo todo el `switch`**, no uno por caso.
  - 1 byte de `ResponseStatus` al frente de la respuesta: `Ok` / `Failed` / `NotFound`.
  - `default:` → `NotFound` en lugar de `null` (ver P2: hacer ruidoso el no-match).
  - Elimina el control de flujo por excepciones del cliente (`TcpConnection.cs:116-131`,
	que hoy captura `MemoryPackSerializationException` para inferir el estado).

- [x] **3.5 Fire-and-forget en streaming** — *A + B hechas en `MemoryConnection.OpenStream`*: los 4 `Enumerate*` comparten un iterador que crea la sesión antes de enviar (sin carrera) y termina con error si la petición falla (`Failed`/`NotFound`/excepción). El timeout de `RpcBuffer` no cuenta como fallo (la respuesta llega al final del stream). Test `TestMemoryStreamFailureDoesNotHang`. C queda en 4.2.

  **Problema**: `MemoryConnection.cs:113, 143, 228, 258`

  ```csharp
  _ = MemoryRpc.RemoteRequestAsync(SerializePayload(op, (streamSessionId, payload)), _timeout, token);
  ```

  Dos fallos: **(a)** si la petición falla, la excepción se pierde y el consumidor **se cuelga**
  esperando items que no llegarán; **(b)** carrera — el `RpcBuffer` receptor se crea en la línea 99
  pero nada garantiza que esté escuchando antes de que el servidor empiece a emitir.

  **Propuesta A — observar el fallo y propagarlo al consumidor** *(mínima, recomendada)*
  Al migrar a `Channel` (4.1), el canal ya tiene terminación con error:

  ```csharp
  var channel = Channel.CreateBounded<TOut>(capacity);

  _ = MemoryRpc.RemoteRequestAsync(request, _timeout, token)
	  .AsTask()
	  .ContinueWith(static (t, s) =>
	  {
		  var writer = (ChannelWriter<TOut>)s!;
		  if (t.IsFaulted)             writer.TryComplete(t.Exception!.GetBaseException());
		  else if (t.IsCanceled)       writer.TryComplete(new OperationCanceledException());
		  else if (!t.Result.Success)  writer.TryComplete(new InvalidOperationException("Stream request rejected"));
	  }, channel.Writer, TaskContinuationOptions.ExecuteSynchronously);
  ```

  El consumidor recibe la excepción al iterar en lugar de bloquearse. Coste: cero en el camino feliz.

  **Propuesta B — eliminar la carrera de arranque**
  Que la respuesta de `RemoteRequestAsync` sea el **ACK de "sesión abierta"**: no descartar el `Task`,
  esperarlo *antes* de devolver el enumerable y solo entonces empezar a consumir.
  Convierte `Enumerate` en asíncrono de verdad.

  **Propuesta C — eliminar la sesión aparte** *(la buena; es 4.2)*
  Si el streaming se multiplexa sobre el canal principal con un `streamId`, no hay `RpcBuffer`
  secundario, no hay carrera de arranque y no hay fire-and-forget. A y B dejan de ser necesarias.

  → **Sugerencia**: implementar A ahora (barato, corrige el cuelgue) y dejar que C la deje obsoleta.

- [x] **3.7 Correlación

  | Transporte | ¿Quién delimita? | ¿Quién empareja? | Cabecera petición | Cabecera respuesta |
  |---|---|---|---|---|
  | TCP | nosotros (`len`) | nosotros (`corrId`) — 1 stream, N en vuelo | `[len][corrId][opId]` 16 B | `[len][corrId][status]` 9 B |
  | QUIC | FIN del stream (último msg) / `len` (items de stream) | **el propio stream** en `EnumerateAsync`; unarias por **pool de streams** multiplexados con corrId (`unaryStreams`, defecto 4) | `[opId]` 8 B | `[status]` 1 B |
  | UDP | el datagrama | `corrId` (socket compartido, P3) | `[corrId][opId]` 12 B | `[corrId][status]` 5 B |
  | Memory | `RpcBuffer` | `RpcBuffer` (ids propios) | `[opId]` 8 B | `[status]` 1 B |

  - [x] TCP: multiplexado con `corrId` (`MultiplexedChannel` \+ `ServePipeAsync`). Tests OK.
  - [x] `FrameWriter(inner, correlated)`: el mismo escritor sirve con o sin `corrId`.
  - [x] QUIC: **antes reutilizaba
	(8 B/msg) y un read-loop que QUIC ya resuelve. Pasar a stream bidireccional por RPC:
	se eliminan `corrId`, diccionario de pendientes y bloqueo de escritura; la concurrencia la da el
	límite `MaxInboundBidirectionalStreams`. Solo el streaming necesita `len` por item.
  - [x] UDP: antes **no** correlacionaba
	cruzan. Añadir `corrId` (4 B) sin `len` (el datagrama ya delimita). Sin `corrId` solo sería válido
	con un socket por llamada, que cuesta más que 4 B.
  - [-] Memory: no se toca, `RpcBuffer` ya empareja.

- [x] **3.6 Concurrencia del cliente
  - Estado compartido hoy: `TcpConnection.reader/writer`. Dos hilos llamando a `GetAsync`
	**intercalan bytes en el mismo `PipeWriter`** y compiten por el `PipeReader`.
	Corrupción prácticamente garantizada bajo carga.
  - Propuesta: `correlationId` en la cabecera (int32, `Interlocked.Increment`), un único read-loop
	por conexión que despacha a `ConcurrentDictionary<int, TaskCompletionSource<...>>`, y escritura
	serializada con `SemaphoreSlim(1)` o un `Channel` de salida.
  - Memoria compartida: verificar si `RpcBuffer` ya es thread-safe para `RemoteRequest` concurrente.
	**Si lo es, no hay nada que hacer en ese transporte.**
  - → Validar con **PoC-A** antes de implementar.

---

- [~] **3.8 Seguridad en la frontera de red**
  - [x] `Framing.MaxFrameSize` (16 MiB): TCP y QUIC cortan la conexión ante longitudes hostiles. Test `TestTcpRejectsOversizedFrame`.
  - [x] TCP valida trama < `opId`; `Failed` envía `Message`, no la traza.
  - [~] Confidencialidad/integridad: QUIC ya usa TLS. TCP → `SslStream` opcional (`cert`): hecho, mTLS con pinning por hash del
    certificado configurado (antes fallaba con autofirmados: `UntrustedRoot`). Test `TestTcpTls`. UDP → sin cifrar (DTLS no está en .NET); solo red de confianza.
  - [ ] Autenticación: pipeline de servidor (5.2), no transporte.
  - [x] Límite de peticiones en vuelo por conexión TCP: `Server.MaxInFlightPerConnection` (256). Al
    llenarse, el lector deja de leer el socket → back-pressure TCP, sin rechazos. Test `TestTcpInFlightLimit`.
    QUIC no lo necesita (un stream por petición, lo limita QUIC).
  - [x] Límite por endpoint UDP: `Server.MaxInFlightPerEndpoint` (256). Sin back-pressure posible, el
    exceso se descarta (el cliente lo ve como pérdida); la entrada se elimina al llegar a 0 para no
    crecer con IPs falsas. Test `TestUdpInFlightLimit`.
  - [x] Límites configurables: parámetros opcionales `maxInFlightPerConnection` (`StartTcpServer`) y
    `maxInFlightPerEndpoint` (`StartUdpServer`), por defecto las constantes; `< 1` lanza. Test `TestCustomInFlightLimits`.
  - [x] UDP anti-amplificación: `StartUdpServer(..., maxAmplification)` — bytes enviados por petición ≤ N × recibidos; el exceso se descarta. 0 = sin límite (red de confianza, por defecto). Cookie/token queda pendiente si hiciera falta. Test `TestUdpAmplificationBudget`.

## Fase 4 — Streaming

- [x] **4.1 `System.Threading.Channels` en lugar de TPL Dataflow** — hecho sin benchmark: mismo código, sin dependencia Dataflow, fallo propagado con `TryComplete(ex)`. `ToBlockingEnumerable` sigue en los `Enumerate` síncronos (API síncrona por diseño). Cubierto por los tests de streaming en memoria (`TestMemoryStreamFailureDoesNotHang`).
  - `MemoryConnection.cs:95,125`: `BufferBlock<T>`; `ToBlockingEnumerable()` (115, 145) bloquea
	hilos del ThreadPool.
  - **Condición tuya**: se implementa si el benchmark demuestra ganancia. Nota: esto crea dependencia
	con la Fase 6 (benchmarks), que ahora va al final → **adelantar solo el escenario de streaming**.
  - Beneficio no discutible aparte del rendimiento: `Channel` da terminación con excepción (3.5-A)
	y back-pressure, que `ToBlockingEnumerable` no da.

- [x] **4.2 No crear un `RpcBuffer` por stream** *(opción 2 implementada)*
  - Opciones:
    1. Canal propio por cliente: un `RpcBuffer` por `MemoryConnection`, streams multiplexados por `streamId` dentro. Aísla clientes; coste por cliente en vez de por stream.
    2. **(elegida)** En el fork, `MessageType.StreamItem` con `ResponseId` = `MsgId` de la petición que abrió el stream; solo lo entrega quien la hizo. Fin = la propia respuesta (`StreamEnd`). Sin `RpcBuffer`/eventos/`Guid` por stream ni ack por item.
    3. Dejarlo pendiente: coste actual = un `RpcBuffer` nombrado por stream, sin medir si importa.
  - `MemoryConnection.cs:93-111, 123-141`: cada `Enumerate` crea memoria compartida + eventos
	nombrados + `Guid.NewGuid()` formateado a string.
  - → multiplexar sobre el canal principal con `streamId` en la cabecera.
  - Resuelve además 3.5 por completo.
  - Hecho: `RemoteStreamAsync`/`SendStreamItem` en el fork; `OpenStream` con timeout de inactividad (se reinicia por item) para que un mensaje perdido no cuelgue el stream. Test: `TestMemoryStreamsRoutedPerClient` (8 streams intercalados en un canal).
  - Optimizado: `SendStreamItem<TState>` serializa directo al nodo (sin `byte[]` por item en servidor); el cliente anota `TickCount64` por item y un `Timer` periodico vigila la inactividad (sin `CancelAfter` por item).
  - [x] **Validar la optimizacion de streams** — `MemoryStreamBatchingTest`: lotes >16 KB (orden/cantidad), items > nodo (multipaquete), buffers de lote reutilizados bajo concurrencia, y watchdog que cancela un stream parado.
  1124 µs / 481,6 KB → 852 µs / 278,5 KB.
    (~90 B/item: `Channel` + boxing del iterador).
      +~8 KB por los flush en el pool).
        - [x] **Closures por paquete en `RpcBuffer`** (medido con `--alloc`, `AllocProbe` = GCAllocationTick por tipo): la lambda de los paquetes 2..N y el `Task.Run` de `RpcRequest` capturaban locales y el closure se asignaba en cada llamada aunque no se usara. Movidos a `WriteRemainingPackets`/`DispatchRequest`. Memory 1000 items: 91 KB → 5 KB (88,7 → 0,1 B/item).
        - [x] **Spin antes de `DataExists.WaitOne`** en `CircularBuffer.GetNodeForReading`: Memory 1000 items 1008 → 702 µs. Memory 1000 items 1008 → 702 µs.
  - [x] **POC A · lotes por nodo** (`MemoryRequestContext.Append/Flush`): el servidor agrupa `[int32 len][item]` en un solo `StreamItem` (lote 16 KB, buffer reutilizado; se vacía si el productor async va a esperar). Sin tipo de mensaje nuevo. Memory 1000 items: 702 → 109 µs.
  - [x] **POC B · drenado en lote en el cliente** (`WaitToReadAsync` + `TryRead` en `OpenStream`): 109 → ~100 µs, marginal pero gratis. Resultado: Memory 100 µs / 8 KB vs UDS 304 µs y TCP 344 µs (1000 items). Tests de streaming Memory cubren ambos.
  - [x] **Varios clientes por `contextId`**: Hecho con `MemoryLobby` (servidor) + `MemoryConnection.Join`: mutex `_LSL_Gate` serializa, el cliente escribe su nombre de sesion en la MMF `_LSL_Lobby`, senala `_LSL_Req`, espera `_LSL_Ready`; el servidor abre un `RpcBuffer` dedicado. `MemoryMultiClientTest` activo y en verde. Pendiente: liberar sesiones de clientes caidos. Contexto previo: `RpcBuffer` es un par master/slave. Dos `MemoryConnection` al mismo nombre comparten el buffer circular de lectura (lectura destructiva) y sus `MsgId` colisionan => respuestas cruzadas (esperado 1, llega 4) y streams cancelados. Evidencia: `MemoryMultiClientTest`. Arreglo real = opcion 1 (canal por cliente: handshake en canal de control que asigna `contextId_n`); no es de streams, afecta a toda la RPC.
  - [x] **Investigar `ToListAsync()`** sobre `EnumerateAsync` (Memory): `TestMemoryStreamsRoutedPerClient` fallaba con `TaskCanceledException`. Causa: continuaciones síncronas en el hilo lector del `RpcBuffer`. Resuelto por `RunContinuationsAsynchronously` en `RpcBuffer.ResponseReady`; el test ya usa `ToListAsync()` y pasa 10/10.

- [x] **4.3 Streaming UDP ordenado**
  - Item: `[corrId][status][seq][body]`; fin: `[corrId][StreamEnd][count]`.
  - Cliente: buffer de reorden; el enumerador libera solo el tramo contiguo desde `next`.
  - Termina cuando `next == count`. Huecos → los resuelve 5.x (retry), no el transporte.

---

## Fase 5 — Diseño

- [x] **5.1 Deduplicar transportes** *(acordado)* — cerrado con PoC-B.
  - `Tcp/Udp/Quic` repiten `BuildRequest`, manejo de errores y flujo de lectura.
  - Base `internal` compartida, **no** extensible públicamente (P1).
  - → Alcance real a decidir con **PoC-B**.
  - [x] Primer paso, sin abstraccion: `ResponseError.Create` (internal) unifica el mapeo status -> excepcion que repetian `Datagram`/`Memory`/`MultiplexedChannel`/`Quic` (4 switches -> 1). Cubierto por los tests de `NotFound`/`Failed` existentes de cada transporte.
  orden de cierre (transporte antes que canal).
  - Lo que queda duplicado (escritura `[opId][cuerpo]`, `Invalid parameters`) difiere por transporte (`corrId`, FIN de QUIC, nodo de memoria); unificarlo exige la base comun => PoC-B.

- [~] **5.2 Pipelines diseñados en compile-time**

  - [x] **5.2-A Procesadores v1**: `IPipeline/IAsyncPipeline/IAsyncValuePipeline<T|TIn,TOut>` devuelven `(ResponseStatus, T)`; atributos `[(Client|Server)?(Pre|Post)?Processor<T>]` en parámetros/retorno, en orden declarado. Host aplica Server*/sin lado; cliente aplica Client*/sin lado; la firma pública del cliente la fijan la 1ª etapa por parámetro y la última del retorno; si difiere del contrato, éste se implementa explícito con `InvalidOperationException`. Status != Success -> `PipelineRejectedException` -> Failed en el catch del handler. Diagnósticos SCLSL010-013. El pipeline se resuelve de los servicios registrados en el host/cliente ([Singleton<TrimName>]...); sin registro -> SCLSL013. Los pipelines registrados no se publican como operaciones RPC.
  Cada etapa es una sentencia `if (etapa is not (Success, TOut __x_n)) throw new PipelineRejectedException(Failed, ...)`, de dentro afuera; post igual tras la llamada; líneas en blanco entre lotes. Patrón con el tipo declarado (`var` solo para `Nullable<T>`, CS8116): un `TOut` referencia `null` con Success también corta.
  miembros re-sangrados un nivel dentro de la clase.
  - [x] **5.2-D Ejemplos TIn≠TOut**: `ParseInt : IPipeline<string,int>`, `IntToString : IPipeline<int,string>`. `IAuth.Square` (servidor: red string ↔ contrato int; cliente público `string Square(string)`) e `IAuth.Twice` (cliente: público `int Twice(int)` ↔ red/contrato string). Demo en `Program.cs`.
  - [x] **5.2-D Ejemplo completo**: `IAuth.Echo` usa los 7 atributos (`TrimName`, `Upper`, `Bracket` Task, `Tag`, `Exclaim` ValueTask); `Program.cs` muestra async, sync y rechazo. Salida: `[[HI#]#]#!#`.
  - [x] **Fix transporte memoria**: `RpcBuffer.ResponseReady` con `TaskCreationOptions.RunContinuationsAsynchronously` (fork SharedMemory). Antes la continuación del `await` corría en el hilo lector y una llamada sync posterior lo bloqueaba (deadlock -> timeout 100 s).
  - [x] **Rechazo = `Failed`**: un pipeline solo corta (`Failed`) o sigue (`Success`); no se propaga otro estado. Emitido como `if (etapa is not (Success, TOut x)) throw new PipelineRejectedException(Failed, ...)`.
  - [ ] Pendiente: ref/out/in y streams con procesadores (hoy SCLSL012), early-return sin excepción, test de SCLSL014 (requiere arnés Roslyn con el generador de DI; los tests no referencian `Helpers`).

  `Pipeline.cs` actual: 4 interfaces sin uso, y `IPipelineAsync.ProcessAsync` devuelve `TOut` en vez
  de `Task<TOut>`. Se reemplaza entero.

  **Objetivo**: interceptores (auth, logging, validación, retry) **compuestos por el generador**,
  sin delegados encadenados, sin `IEnumerable<IMiddleware>`, sin asignaciones por request.

  - [ ] **5.2-R Retry como pipeline de cliente** *(para después)*: opt-in, registrado en DI, solo
	útil en UDP (TCP/QUIC ya retransmiten). Timeout + reintento por corrId; en streams re-pide
	los `seq` faltantes (4.3). Requiere handlers idempotentes.

  **Contrato** — comportamientos como `struct` para que el JIT los desvirtualice e inline:

  ```csharp
  // LiteSpeedLink.Abstractions
  public interface IRequestBehavior<TRequest, TResponse>
  {
	  /// Devuelve false para cortocircuitar; 'response' se envía tal cual.
	  bool OnRequest(ref TRequest request, out TResponse response);

	  void OnResponse(ref TResponse response);

	  /// Devuelve true si la excepción queda manejada.
	  bool OnError(Exception error, out TResponse response);
  }
  ```

  **Declaración** — atributo genérico, aplicable al host, la interfaz o el método:

  ```csharp
  [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Method,
				  AllowMultiple = true)]
  public sealed class PipelineAttribute<TBehavior> : Attribute
  {
	  public int Order { get; init; }
  }
  ```

  ```csharp
  [ServiceHost]
  [Pipeline<AuditBehavior>(Order = 0)]          // a todas las operaciones del host
  [Scoped<IAuthService, AuthService>]
  public partial class TextService;

  public interface IAuthService : IServiceUnit
  {
	  [Pipeline<RateLimitBehavior>(Order = 1)]  // solo a esta operación
	  string Greet(string name);
  }
  ```

  **Código generado** — composición estática, plana, sin indirección:

  ```csharp
  case 3602003588388692431:
  {
	  var request = __context.Get<string>();
	  string response;

	  var __b0 = default(AuditBehavior);
	  var __b1 = default(RateLimitBehavior);

	  if (!__b0.OnRequest(ref request, out response) ||
		  !__b1.OnRequest(ref request, out response))
		  return __context.Return(response);          // cortocircuito

	  response = ___scope.AuthService.Greet(request);

	  __b1.OnResponse(ref response);                  // orden inverso al de entrada
	  __b0.OnResponse(ref response);
	  return __context.Return(response);
  }
  ```

  **Propiedades**: el conjunto de behaviors es fijo en build-time (P1); los `struct` sin estado cuestan
  0 bytes y se inlinean; un behavior mal tipado es **error de compilación**, no de runtime; el `Order`
  se resuelve en el generador, no ordenando listas en cada petición.

  **A decidir**: ¿los behaviors pueden tener dependencias inyectadas? Si sí, dejan de poder ser
  `default(TBehavior)` y pasarían a resolverse del scope (`___scope.AuditBehavior`) — sigue siendo
  compile-time, pero con asignación por scope. Recomiendo soportar ambos: `struct` sin estado por
  defecto, y resolución por DI si el tipo está registrado en el contenedor.

- [x] **5.3 `MethodFullName` sin el tipo propietario** *(acordado, con tu enfoque)*
  - Sustituir el frágil `method.GlobalNamespaced.Replace(fullTypeName + ".", "")` introducido en
	`GenerateServiceClient.cs` por un formato dedicado que **construya la firma desde los símbolos**
	y omita el tipo propietario (en `SourceCrafter.LiteSpeedLink.Helpers\Helpers.cs`).
  - ⚠️ **Cuidado**: `GetServiceId` se calcula sobre `globalizedMethodName`, que **sí** incluye el tipo.
	El nuevo formato es solo para **emitir la firma del cliente**; el id debe seguir calculándose
	sobre el nombre completo, o cliente y host dejarán de coincidir.

- [x] **5.4 `Constants.GetDevCert` sin PowerShell** *(acordado)*
  - `Constants.cs:14-47`: lanza un proceso, requiere admin, contraseña hardcodeada.
  - → `CertificateRequest.CreateSelfSigned`, puro .NET y cross-platform.

- [~] **5.5 Timeout y cancelación** *(acordado)* — timeout por defecto ya es 5000 ms; `Yield` usa el token del servidor por diseño (el generador no inyecta otro).
  - `MemoryConnection`: `timeout = 100` ms por defecto, muy agresivo para handlers no triviales.
  - ~~`Server.Memory.cs:67-88`: `Yield` recibe `token` como parámetro y **usa `cancelToken` del
	contexto**, ignorando el argumento.~~ Resuelto: `Yield` ya no recibe token; usa el del contexto.
  - Pendiente: timeouts por defecto divergentes (cliente 5000 ms, `AsMemoryConnection` 100000 ms, servidor 1000 ms).

- [~] **5.6 Unix Domain Sockets como equivalente local de memoria compartida** *(acordado)* — Hecho: `Server.StartUdsServer` + `UdsConnection` reutilizando `ServePipeAsync`/`MultiplexedChannel` (framing 3.3). Test: `UdsTest.TestUdsRoundtrip`. `MultiplexedChannel.DisposeAsync` no cuelga aunque el socket siga abierto (`CancelPendingRead`, `DisposeTest`). Pendiente: selección automática `LocalConnection` (RpcBuffer/UDS) en el generador.
  - `MemoryConnection` es `[SupportedOSPlatform("windows")]` por `RpcBuffer`.
  - Nuevo transporte `LocalConnection`: `RpcBuffer` en Windows, UDS (`UnixDomainSocketEndPoint`)
	en Linux/macOS, seleccionado por el generador o en runtime vía `OperatingSystem.IsWindows()`.
  - Reutiliza el framing de 3.3 — UDS es un stream, mismas reglas que TCP (P4).
  - Elimina los `#if`/`SupportedOSPlatform` que hoy se propagan hasta `Program.cs:37`.

- [~] **5.7 `SharedMemory` como código interno, no como dependencia expuesta** *(decisión tuya)*
  - Hecho: `StartMemoryServer*` y el host generado devuelven `IDisposable`; `RpcBuffer` ya no aparece
	Aplazado (no necesario para funcionar): hacer `internal` los tipos del fork
	`InternalsVisibleTo`); los tests usan `RpcBuffer` directamente y habría que darles acceso.
  - Hoy: `SharedMemory.csproj` referenciado como proyecto y **público** en la superficie de
	`Client`/`Server`.
  - → incorporar como **linked files** `internal` dentro de `SourceCrafter.LiteSpeedLink.Server` y
	`.Client`, sin referencia de proyecto.
  - Desbloquea 2.1 (añadir APIs `Span`/`IBufferWriter` a `RpcBuffer` sin versionar un paquete externo)
	y evita filtrar `RpcBuffer` al consumidor.
  - ⚠️ Ojo con duplicar los tipos en dos ensamblados si ambos comparten memoria en el mismo proceso;
	valorar un tercer proyecto `internal` compartido o `InternalsVisibleTo`.

- [ ] **5.8 Configuración de endpoints** *(de tus descartes)* — **Pospuesto** por decisión del usuario; hoy la dirección/nombre se pasa por constructor.
  - El diseño es compile-time, pero **host/puerto/nombre de canal no pueden serlo**: cambian por entorno.
  - Propuesta: mantener el *contrato* en compile-time y externalizar solo la *dirección*, vía variables
	de entorno o `appsettings`, leídas en el constructor generado.
  - `Program.cs:11` ya lo hace a mano (`rpcName` generado y pasado a ambos extremos) — formalizarlo.
  - **Pendiente de diseño**: ¿el generador emite un constructor adicional que lee configuración,
	o se deja al llamante como ahora?

---

## PoCs (antes de comprometer diseño)

(`ServiceHandlers.Raw.cs`, test `UdsTest.TestRawRoundtrip`) para unarias, `Task` sin resultado y pipelines; faltan streams (POC).

- [x] **PoC-A · Multiplexación y concurrencia del cliente** — TCP/UDP ya multiplexados (3.6/3.7, tests `Test{Tcp,Udp}ConcurrentRoundtrips`). Memory: `RpcBuffer` ya empareja y es seguro con N en vuelo; fallaba por **inanición del thread pool** (bucle lector bloqueante en `Task.Run`; cuello a partir de ~64 llamadas con `Get` sync). Fix en el fork: lector con `TaskCreationOptions.LongRunning`. Test `TestMemoryConcurrentRoundtrips` (200 llamadas mixtas sync/async/stream/NotFound, una instancia).
  - Escenario: N hilos (1, 2, 8, 32) llamando a `Greet` sobre **una misma instancia** de cliente.
  - Medir, por transporte (Memory y TCP): corrección (¿respuestas cruzadas o corruptas?),
	throughput y latencia p99.
  - **Hipótesis a falsar**: *"sin correlación ni serialización de escrituras, TCP corrompe datos con
	≥2 hilos"*. Si con un `SemaphoreSlim` simple (sin multiplexar) el throughput ya es suficiente,
	la multiplexación completa (3.6) no se implementa.
  - Salida esperada: decidir entre *(a)* serializar por conexión, *(b)* pool de conexiones,
	*(c)* multiplexación real con `correlationId`.

- [x] **PoC-B · ¿Necesita la librería una abstracción `ITransport`?** — **No.** `StreamConnection` elimina la duplicación real (TCP/UDS: ~75 líneas de TCP y todo el reenvío de UDS) y el único despacho virtual es `OpenAsync`/`Close`, una vez por conexión: coste nulo en la ruta caliente, no hace falta medir. UDP (datagramas, `corrId` propio) y QUIC (stream por llamada, FIN) no comparten canal; unificarlos bajo `ITransport` añadiría indirección sin quitar código. Genéricos `where TTransport` descartados por lo mismo.
  - Extraer la base común de `Tcp`/`Udp`/`Quic` en una rama y medir: ¿cuántas líneas se eliminan?
	¿se degrada el rendimiento por la indirección (interfaz vs. llamada directa)?
  - Alternativa a evaluar: genéricos con `where TTransport : ITransport` para que el JIT desvirtualice,
	en lugar de una interfaz con despacho virtual.
  - **Criterio de decisión**: si no elimina duplicación significativa o cuesta rendimiento, no se hace.

- [x] **API raw de petición/respuesta** — `IConnection.GetRaw` / `IAsyncConnection.GetRawAsync` (antes
  `IConnectionAsync`) reciben el cuerpo ya construido y devuelven el cuerpo de la respuesta. El generador
  (`ServiceHandlers.Raw.cs`) emite por operación unaria un escritor/lector estático con llamadas MemoryPack
  específicas por tipo (`WriteUnmanaged`/`WriteString`/`WritePackable`, `WriteValue` solo como fallback);
  el host lee `ctx.Body` con el lector espejo. Bloques secuenciales = mismos bytes que la tupla (verificado).
  Operaciones sin retorno (void sincrono, con o sin out/ref) tambien van por raw; streams siguen por la API
  tipada. Host: `ref` ahora viaja de vuelta (antes se contaba `in`). Capacidad del escritor:
  exacta en compilacion si todos los parametros son primitivos/enum; si no, 64 y crece.
  - [x] `Task`/`ValueTask` sin resultado por raw; si el contrato no trae token se emite la implementación explícita. El host
    espera el `Task` (`await` en red, `GetAwaiter().GetResult()` en Memory) en vez de intentar serializarlo.
  - [x] Métodos con pipelines por `GetRaw*` con `__Req{n}`/`__Res{n}` sobre los tipos de wire.
  - [x] Capacidad: strings con cota `8 + 3·Length` (MemoryPack escribe UTF-8 con cabecera 8; nunca crece); packables 64.
  - [x] `Enumerate*` raw (6d): **diferido** por POC `StreamRawDecodePoc` (lector específico vs `Deserialize(span, ref item)`):
    1 item +125 % (alquiler del estado), 64 items −29 % strings / −17 % packables (~3,6 ns/item ≈ 2 % de `Lsl_Stream`).
    No compensa una API `EnumerateRawAsync` en 5 transportes; reabrir si un perfil de stream señala la decodificación.
  - [x] Lectores de respuesta con la firma del contrato (6e): `__Res{n}(span, out …)` devuelve el retorno y escribe
    los out/ref (`ref` se pasa como `out`), en vez de tupla + copias `.ItemN`. Sync: `return __Res{n}(…, out token);`
    (4 ramas → 1); async: `return (__Res{n}(span, out var __o0), __o0);`. `void` con out/ref: lector `void`.
    Los lectores de petición del host (`__ReqM`) y de pipelines siguen devolviendo valor/tupla.
    POC: `TryAuth`/`TryAuthAsync` y `IAuth.Bump(ref int, out string)` en `LiteSpeedLink/Program.cs` (4 transportes).
  Test: `UdsTest.TestRawRoundtrip`, `RawCapacityTest`, POC `IAuth.TouchAsync` en `LiteSpeedLink/Program.cs` (4 transportes) + suite (75/75).

- [x] **QUIC intermitente en suite** — `StreamBatchPolicyTest.QuicStreamsMatchBatched(0)` y
  `QuicPoolCancellationTest` usaban el mismo puerto 5030 en paralelo (xUnit) ⇒ `QuicException` al enlazar.
  Fallo del test, no del transporte. Movido a 5040+.
- [x] **Ejemplos por transporte** —
  `ServiceConnectionType` (Memory, Udp, Tcp, Quic) y ejercitarlos desde `Program.cs`.
  - [x] `TransportSamples.cs`: `Udp/Tcp/QuicTextService(Client)` compilan con el mismo contrato `IAuth`.
  - Sync: solo Memory es síncrono nativo (`IConnection` sobre `RpcBuffer`). Tcp/Udp/Quic comparten socket con read
    loop async + corrId ⇒ sin ruta sync real; los miembros sync del contrato se resuelven por extensiones
    sync-over-async sobre `IAsyncConnection` (`ClientExtensions`), bloquean un hilo por llamada.
  - [x] `Program.cs` ejercita Memory/Udp/Tcp/Quic (sync + async + rechazo de pipeline); los 4 responden igual.
- [x] **Benchmark par vs ASP.NET Core slim** — mismo contrato mímico en TCP (HTTP/1.1) y QUIC (HTTP/3).
  - TCP: `ComparisonBenchmarks` (ya existía). QUIC: `QuicComparisonBenchmarks` (suite `QuicComparison` = 2048),
    reutiliza `MapSlim`/`SlimUnary`/`SlimStream` de la tabla TCP. Arnés `--fast` (tiempos orientativos):

    | Op | TCP LSL | TCP slim h1 | QUIC LSL | QUIC slim h3 |
    |---|---:|---:|---:|---:|
    | Unaria | 42 µs / 903 B | 50 µs / 2.8 KB | 75 µs / 2.1 KB | 184 µs / 22.7 KB |
    | Stream 1000 | 324 µs | 202 µs | 316 µs | 600 µs |
    | Stream por lotes (256) | 94 µs | 94 µs | 166 µs | 226 µs |

  - Lectura: unaria gana LSL en ambos. En TCP, stream por item pierde contra SSE (un frame+flush por item frente
    a bytes en la tubería de Kestrel); con lotes empata. En QUIC, LSL gana en todo.
  - Trampa HTTP/3: `https://localhost` resuelve a `::1` y Kestrel escucha solo en 127.0.0.1 ⇒ error ALPN. Usar IP.

---

## Fase 6

- [x] **6.1 Proyecto `LiteSpeedLink.Benchmarks` (BenchmarkDotNet)** — escenarios `Control`, `RequestBuilding`, `ResponseStatus`, `ServerParsing`, `TcpConcurrency`, `Streaming` (suite 32), `MemoryConcurrency` (suite 64); `--alloc` muestrea asignaciones por tipo.
  - **Streaming, 1000 items (`--fast`)** — inicial → actual:

    | Transporte | Inicial | Actual |
    |---|---|---|
    | Memory | 1124 µs / 481,6 KB | **100 µs / 8,2 KB** |
    | UDS | 1257 µs / 3,1 KB | 304 µs / 12,4 KB |
    | TCP | 8962 µs / 2,5 KB | 479 µs / 26 KB (completo) |
    | QUIC | 414 µs / 45 KB | 440 µs / 11,5 KB (completo) |

  - **Streaming, 10 items**: Memory 33 µs / 4,5 KB, UDS 36 µs, TCP 45 µs, QUIC 211 µs / 41 KB.
  - **QUIC**: hereda el flush diferido (`ResponseChannel`). Coste dominado por lo fijo por llamada (~200 µs y ~40 KB: stream nuevo + `PipeReader/Writer` por RPC, ver 3.7); el incremento por item (~0,2 µs) es como TCP/UDS. Optimizarlo = reutilizar streams, contrario al diseño stream-por-RPC: no se hace salvo que QUIC sea el transporte principal.
  - **QUIC**: `QuicOverheadBenchmarks` descompone lo fijo por llamada: stream crudo 86 µs / 3,2 KB (suelo), + pipes 94 µs / 4,3 KB, RPC LSL 211 → 110 µs / 7,2 KB. Causa: `Call.DisposeAsync` cerraba sin consumir el FIN y QuicStream creaba una `QuicException` con traza por llamada. Test: `TestQuicCallDoesNotThrowInternally`.
  - **Pipes (TCP/UDS/QUIC) por item**: `ResponseChannel.WriteAsync` con camino síncrono y caminos async con `PoolingAsyncValueTaskMethodBuilder`. TCP 19 → 6,8 B/item. `--alloc tcp|uds|memory|quicstream` y `--alloc quic`.
  - **Memory, petición de stream**: `RpcBuffer.RemoteStreamAsync<TState>` (fork) escribe directo en el nodo; fuera `ToArray()`. 1,9 → 0,6 B/item.
  - **Arnés completo (publicable), 1000 items**: Memory 108 µs / 10,9 KB, UDS 293 µs / 9 KB, TCP 479 µs / 26 KB, QUIC 440 µs / 11,5 KB. 10 items: Memory 33, UDS 39, TCP 69, QUIC 208 µs. TCP/1000 con 26 KB es la anomalía a investigar.
  - El `ArgumentNullException ('array')` del arranque viene de `SemanticCheck` (clientes POC rotos a propósito).
  Siguiente: agrupar items en TCP como en Memory (**hecho**: `coalesceStreams` por defecto, ver final).
  - **MemoryConcurrency** (N llamadas sobre una `MemoryConnection`, medición inicial): 64 → Memory 195 µs / 188 KB vs UDS 219 µs / 158 KB; sin respuestas cruzadas.
  - Escenarios: `Greet` (string→string), `TryAuthenticate` (record struct + `out`), streaming,
	y concurrencia multihilo (alimenta PoC-A).
  - Métricas: latencia media/p99, **allocs por operación**, ops/s.
  - ⚠️ **Dependencia invertida**: 4.1 y ambos PoCs están condicionados a medición. Conviene adelantar
	de esta fase, como mínimo, el arnés básico y el escenario de streaming.

---

## Descartes vigentes

- [-] Handshake / negociación de versión de protocolo — P1 y P2.
- [-] Descubrimiento dinámico de servicios / catálogo en runtime — P1.
- [-] Contrato de error con metadata de tipos — P1 (los tipos ya se conocen en ambos lados).
- [-] `ITransport` como punto de extensión **público** — P1. *(La deduplicación interna sigue viva: 5.1 / PoC-B.)*
- [-] Quitar `DynamicallyAccessedMembers` — impuesto por MemoryPack (1.2).
- [-] CAS en lugar de `lock` para crear el `RpcBuffer` — 2.5.
- [-] `MemoryRequestContext` como `readonly ref struct` — incompatible con `async` (2.3).

**Ya no descartado**: multiplexación del cliente → PoC-A (P3 la reabre).

## PoC stream batching (Suite 256, aislado)
- PerItem (actual) vs Coalesced (lector agrupa por ReadAsync, mismo cable) vs Batched (opt-in [len][n][items]).
- 100k ints TCP loopback: 27.6 ms / 2.6 ms / 1.6 ms. Coste = Channel+alquiler por item, no red.
- ~~Siguiente: Coalesced en MultiplexedChannel (sin cambio de cable) + Batched opt-in; benchmarks nuevos en Comparison.~~ Hecho (ver final).


## Stream opt-in (TCP/UDS) - implementado
- Cliente: coalesceStreams (TcpConnection/UdsConnection) agrupa items de cada ReadAsync; cable intacto.
- Servidor: streamBatch (StartTcpServer/StartUdsServer) emite ResponseStatus.Batch [len][item]...; el cliente lo decodifica siempre.
- Test: StreamBatchingTest (4 combinaciones). Comparison 1000 ints: Lsl 292us, Coalesced 199us, Batched 95us, AspNetSlim 203us, AspNetSlim batched 91us, gRPC 440-506us.
- ~~Pendiente: QUIC y Memory no usan estas opciones.~~ QUIC acepta `streamBatch` en el servidor (el cliente no multiplexa streams: sin coalesce); Memory agrupa por su cuenta en `Yield`.


## Desglose de lotes de stream (StreamBatchBreakdown, 1000 int, --fast)

| Caso | TCP | QUIC |
|---|---|---|
| Base (Batch=0) | 281 us / 8.7 KB | 308 us / 11.7 KB |
| Solo coalesce cliente | 166 us / 4.7 KB | n/a |
| Batch=8 | 138 us | 276 us |
| Batch=64 | 115 us / 5.2 KB | 202 us |
| Batch=256 | 109 us | 195 us |

- Handoff por item en el lector: ~41% (coalesce solo). Framing/escrituras por item: ~60% (Batch=64). Se solapan: con lotes, coalesce ya no suma.
- El tamano de lote configurable si influye; rendimientos decrecientes a partir de ~64. QUIC gana ~35% (sin lector multiplexado, solo aplica el lote).
- streamBatchMaxDelay + flush al esperar el productor acotan la latencia (test SlowProducerIsNotHeldByBatch).


## Comparacion tras BatchPolicy (Comparison, --fast)

- Stream: Lsl 324 us, Coalesced 179 us, Batched 109 us; AspNetSlim 195 us, AspNetSlim batched 89 us; gRPC 412 us.
- Unary: Lsl 41 us, AspNetSlim 48 us, gRPC 133 us.
- Hueco restante vs AspNetSlim batched (~20 us): bytes por item (8 KB vs 3 KB, prefijo de longitud por item + corrId).


## Lote compacto (sin prefijo de longitud por item)

- Batch = items MemoryPack concatenados; el cliente avanza con Deserialize(span, ref value). Aplica a servidor (TCP/UDS/QUIC) y al agrupado del cliente.
- Lsl_StreamBatched: 109 -> 101 us, 7.99 -> 5.81 KB. AspNetSlim batched: 92 us / 2.99 KB.
- Resto de asignacion: ArrayBufferWriter por stream (ponytail en Server.Pipes); siguiente paso = buffer del pool.


## Buffer de lote en pool (Server.Pipes)

- ArrayBufferWriter reutilizado entre streams (ConcurrentBag, como Server.Memory); se devuelve en Complete().
- Lsl_StreamBatched: 101 us / 5.81 KB -> 99 us / 3.67 KB; AspNetSlim batched 105 us / 2.99 KB (ruido --fast).
- MemoryPack SerializeAsync/DeserializeAsync: descartado; son wrappers sobre Stream con buffer propio, anaden copia y estado async frente a IBufferWriter/span sincrono.


## Politica de stream como tipo (SourceGen #1b) - implementado

- [Stream(Batch, MaxDelayMs)] -> el host emite EnumerateAsync<T, TPolicy> con struct __Policy_B{n}_D{ms} (uno por configuracion) o Unbatched; sin atributo, politica del servidor.
- POC SourceGenPocBenchmarks.Policy_* (1000 int): campo runtime 15.1 us -> tipo 3.0 us (sin plazo, el JIT elimina Stopwatch); con plazo 14.2 -> 13.9 us (manda el reloj). 0 B ambos.
- Cobertura: IStreams/StreamService (8 formas sync/async x default/unbatched/batched/timed) + StreamPolicyTypeTest.
- Cliente: sin cambios; solo decodifica Batch, no decide politica.

## Unaria sin canal (SourceGen #8) - implementado

- MultiplexedChannel: UnarySink (IValueTaskSource reutilizable, pool por conexion) para Get/Send; streams mantienen Channel. Id 0 reservado como desarmado; entrega tardia descartada por CAS de id.
- Lsl_Unary (--fast): 41 us / 2.07 KB -> 39 us / 903 B.

## Indice denso de operacion (SourceGen #4) - descartado

- DenseDispatchPoc: denso mas lento (N=10 +74 %, N=100 +25 %); 0 B en ambos. Solo ahorraria 7 B de cable; no compensa.

## Comparacion tras #8 (Comparison, --fast)

- Unary: Lsl 42 us / 904 B; AspNetSlim 53 us / 2.8 KB; AspNet 66 us / 5.6 KB; gRPC 82 us / 7.1 KB.
- Stream 1000 int: Lsl 336 us / 8.9 KB, Coalesced 206 / 5.0 KB, Batched 103 / 4.0 KB; AspNetSlim 205 / 4.5 KB, AspNetSlim batched 94 / 3.1 KB; gRPC 417-425 / 71 KB.

### POC QUIC: stream reutilizado vs stream por llamada (QuicOverheadBenchmarks, --fast)
| Method | Mean | Allocated |
|---|---|---|
| RawReused | 90.5 us | 498 B |
| RawStream | 164.3 us | 3234 B |
| RawPipes | 178.7 us | 4452 B |
| Lsl | 118.1 us | 7537 B |

Ganancia real (~45% latencia, ~85% memoria en crudo). Pendiente de decision: multiplexar sobre un stream (como TCP) pierde el aislamiento head-of-line entre llamadas propio de QUIC; alternativa: pool de N streams multiplexados.


### QUIC opcion 2: pool de streams multiplexados para unarias (por defecto, DefaultUnaryStreams=4)
| Method | Mean | Allocated |
|---|---|---|
| LslUnary (stream por llamada) | 108.1 us | 6774 B |
| LslUnaryPooled (4 streams) | 69.9 us | 2133 B |
| RawReused (suelo) | 66.8 us | 496 B |

Unarias: -35% latencia, -69% memoria. EnumerateAsync sigue con stream propio (aislamiento HOL). Marcador opId=long.MinValue abre el stream como canal multiplexado (8 B una vez). unaryStreams: 0 restaura el modo anterior. Test: TestQuicUnaryStreams(0|4).


### TCP: tecnicas de Kestrel (Transport.Sockets) probadas contra LSL
Kestrel: Pipe entre app y socket con bucles DoSend/DoReceive, BufferList multi-segmento, SocketAwaitableEventArgs reutilizable, SocketSenderPool, IOQueue, PinnedBlockMemoryPool, AggressiveOptimization, UnsafePreferInlineScheduling.
Resultados (arnes completo salvo indicacion; peores casos: unaria y stream simple):
- [x] Consumidor de stream inline en el cliente (Channel AllowSynchronousContinuations, analogo a UnsafePreferInlineScheduling): Lsl_Stream 336 -> 227-270 us, 8.9 -> 2.5-5.3 KB. Techo marcado ponytail: sync-over-async dentro del await foreach interbloquea al lector.
- [-] Pipe de salida desacoplado (DoSend)
- [-] Bucle de envio con PipeScheduler.Inline
- [-] Unaria inline (UnarySink
- Final: Unary Lsl 47 us / 0.9 KB vs slim 58 us / 2.8 KB; Stream Lsl 227-270 us vs slim 200-223 us. Hueco residual en stream simple: 1 trama con corrId por item frente a SSE con flush agrupado de Kestrel; con Coalesced/Batched LSL ya empata o gana.

### TCP/UDS: coalesceStreams por defecto
- [x] El hueco del stream simple era el handoff por item en el lector del cliente (Coalesced = mismo servidor y mismo cable, ~80 us menos). TcpConnection/UdsConnection pasan a coalesceStreams = true: agrupa solo lo ya leido en cada ReadAsync, sin espera ni cambio de cable; unarias intactas (grupo de 1 = Success).
- Arnes completo: Lsl_Stream 196 us / 5.0 KB vs AspNetSlim_Stream 229 us / 4.6 KB; Lsl_StreamPerItem (coalesceStreams:false) 274 us; Lsl_Unary 47 us vs slim 57 us.
- Prueba: StreamBatchingTest/StreamBatchConcurrencyTest cubren coalesce true/false. Descartado sink sin Channel para streams: ya no hace falta (YAGNI).
