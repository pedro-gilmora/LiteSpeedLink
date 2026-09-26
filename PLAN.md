# Plan de mejoras — LiteSpeedLink

## Arranque de sesión *(leer primero)*

Este archivo es el **único traspaso** entre sesiones. Al terminar un paso: marcar su estado aquí,
añadir lo aprendido en *Trampas conocidas* y actualizar *Siguiente paso*.

### Mapa del repo

| Proyecto | Rol |
|---|---|
| `LiteSpeedLink.Abstractions` | Contratos compartidos: `Framing`, `ResponseStatus`, `Pipeline.cs` (interfaces + atributos de procesadores). |
| `SourceCrafter.LiteSpeedLink.Helpers` | **Generador**: `ServiceHandlers.GenerateServiceHost.cs` (host), `ServiceHandlers.GenerateServiceClient.cs` (cliente), `ServiceHandlers.Pipelines.cs` (etapas y diagnósticos). |
| `SourceCrafter.LiteSpeedLink.Client` / `.Server` | Transportes: `MemoryConnection`, `Tcp/Udp/QuicConnection`, `Server.*`. |
| `Application.Contracts` | Contrato de ejemplo `IAuth` + pipelines de muestra (`TrimName`, `Upper`, `Bracket`, `Tag`, `Exclaim`). |
| `LiteSpeedLink` | App de muestra: `TextService` (host), `TextServiceClient` (cliente), `AuthService`, `Program.cs`. |
| `LiteSpeedLink.Tests` | xUnit: servidores, framing, pipelines. |
| `LiteSpeedLink.Benchmarks` | BenchmarkDotNet (6.1). |
| `..\SourceCrafter.DependencyInjection\SharedMemory` | **Repo aparte** (fork, rama `migrating-to-modern-memory-management`). Sus cambios se commitean allí. |

### Verificar (PowerShell, desde `D:\Code\MemLink`)

```powershell
dotnet build-server shutdown; dotnet build LiteSpeedLink.slnx --no-incremental -nodeReuse:false
dotnet test LiteSpeedLink.Tests --no-build     # esperado: 11/11
dotnet run --project LiteSpeedLink --no-build   # esperado: saludo, Echo procesado y "Rejected: Pipeline 'TrimName'..."
```

Código generado: `LiteSpeedLink\obj\Generated\SourceCrafter.DependencyInjection\ServiceProviders\*.g.cs`.

### Reglas de trabajo

- Las premisas P1-P4 son vinculantes: nada de handshakes, descubrimiento ni `ITransport` público.
- Economía de bytes primero: framing/correlación solo donde el transporte lo exige.
- Diff mínimo; `ponytail:` para atajos con techo conocido; un check ejecutable por lógica no trivial.
- Pipelines: implementación **implícita** obligatoria; etapas emitidas como `if (... is not (Success, var x)) throw`, de dentro hacia fuera.

### Trampas conocidas

- **Generador cacheado**: tras tocar `Helpers`, sin `build-server shutdown` + `--no-incremental` el `.g.cs` no cambia.
- **Ediciones que no llegan a disco** (scripts PowerShell con caracteres no ASCII incluidos): verificar con `Select-String` antes de compilar.
- **Deadlock sync tras async en Memory**: resuelto con `RunContinuationsAsynchronously` en `RpcBuffer.ResponseReady` (fork). Si reaparece un timeout en una llamada sync, mirar ahí.
- **Id de operación**: `GetServiceId` se calcula sobre el nombre completo del método; no alterarlo al cambiar el formato de firmas (5.3).
- Warnings `MSB3270` (MSIL vs AMD64 de `SharedMemory.dll`): conocidos e inocuos.

### Siguiente paso

1. Commit pendiente en ambos repos (`MemLink`/`dev` y `SharedMemory`/`migrating-to-modern-memory-management`).
2. Pendientes de 5.2: estado real del rechazo en el cliente, test del diagnóstico de implementación implícita, `ref`/`out`/`in` y streams con procesadores.
3. Después, por prioridad: PoC-A (concurrencia) → 3.3 framing TCP → 5.7 + 2.1 (`SharedMemory` interno, cero copias).

---

## Premisas

### P1 · Contrato cerrado *(firme)*

Cliente y host se generan del mismo contrato en tiempo de compilación. El conjunto de operaciones,
sus tipos y los transportes disponibles son **conocidos y finitos** en build-time.

**Excluye**: negociación de protocolo, handshakes de versión, descubrimiento dinámico de servicios,
reflexión en runtime, metadata de tipos en el canal de error, `ITransport` como punto de extensión
público para terceros.

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

### P4 · Regla transversal

Lo que es **propiedad del transporte** no se descarta por premisas de uso.
La fragmentación de TCP ocurre aunque el uso sea perfectamente predecible.

---

Estado: `[ ]` pendiente · `[~]` en curso · `[x]` hecho · `[-]` descartado

---

## Fase 1 — Limpieza

- [x] **1.1 `Console.WriteLine` fuera de la ruta caliente** — *hecho en `MemoryConnection`*
  - **Pendiente**: quedan 3 en el servidor → `Server.Memory.cs:26`, `:43`, `:62`.
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

- [~] **2.1 `SerializePayload` sin asignaciones intermedias** *(acordado)*
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

- [~] **3.3 Framing por longitud en TCP** *(acordado)*
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

- [ ] **3.5 Fire-and-forget en streaming** — *propuestas pedidas*

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
  | QUIC | FIN del stream (último msg) / `len` (items de stream) | **el propio stream** (1 stream por RPC) | `[opId]` 8 B | `[status]` 1 B |
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
  - [ ] Confidencialidad/integridad: QUIC ya usa TLS. TCP → `SslStream` opcional. UDP → sin cifrar (DTLS no está en .NET); solo red de confianza.
  - [ ] Autenticación: pipeline de servidor (5.2), no transporte.
  - [ ] Límite de peticiones en vuelo por conexión TCP y por endpoint UDP (hoy ilimitado → DoS).
  - [ ] UDP: el servidor responde a la IP de origen sin verificarla → amplificación posible. Respuesta ≤ petición o token/cookie.

## Fase 4 — Streaming

- [ ] **4.1 `System.Threading.Channels` en lugar de TPL Dataflow** *(acordado, condicionado a medición)*
  - `MemoryConnection.cs:95,125`: `BufferBlock<T>`; `ToBlockingEnumerable()` (115, 145) bloquea
	hilos del ThreadPool.
  - **Condición tuya**: se implementa si el benchmark demuestra ganancia. Nota: esto crea dependencia
	con la Fase 6 (benchmarks), que ahora va al final → **adelantar solo el escenario de streaming**.
  - Beneficio no discutible aparte del rendimiento: `Channel` da terminación con excepción (3.5-A)
	y back-pressure, que `ToBlockingEnumerable` no da.

- [ ] **4.2 No crear un `RpcBuffer` por stream** *(acordado)*
  - `MemoryConnection.cs:93-111, 123-141`: cada `Enumerate` crea memoria compartida + eventos
	nombrados + `Guid.NewGuid()` formateado a string.
  - → multiplexar sobre el canal principal con `streamId` en la cabecera.
  - Resuelve además 3.5 por completo.

- [x] **4.3 Streaming UDP ordenado**
  - Item: `[corrId][status][seq][body]`; fin: `[corrId][StreamEnd][count]`.
  - Cliente: buffer de reorden; el enumerador libera solo el tramo contiguo desde `next`.
  - Termina cuando `next == count`. Huecos → los resuelve 5.x (retry), no el transporte.

---

## Fase 5 — Diseño

- [ ] **5.1 Deduplicar transportes** *(acordado)*
  - `Tcp/Udp/Quic` repiten `BuildRequest`, manejo de errores y flujo de lectura.
  - Base `internal` compartida, **no** extensible públicamente (P1).
  - → Alcance real a decidir con **PoC-B**.

- [~] **5.2 Pipelines diseñados en compile-time**

  - [x] **5.2-A Procesadores v1**: `IPipeline/IAsyncPipeline/IAsyncValuePipeline<T|TIn,TOut>` devuelven `(ResponseStatus, T)`; atributos `[(Client|Server)?(Pre|Post)?Processor<T>]` en parámetros/retorno, en orden declarado. Host aplica Server*/sin lado; cliente aplica Client*/sin lado; la firma pública del cliente la fijan la 1ª etapa por parámetro y la última del retorno; si difiere del contrato, éste se implementa explícito con `InvalidOperationException`. Status != Success -> `PipelineRejectedException` -> Failed en el catch del handler. Diagnósticos SCLSL010-013. El pipeline se resuelve de los servicios registrados en el host/cliente ([Singleton<TrimName>]...); sin registro -> SCLSL013. Los pipelines registrados no se publican como operaciones RPC.
  - [x] **5.2-B Procesadores: forma del código generado**: implementación implícita obligatoria (una sola interfaz de pipeline, método público no explícito; si no, SCLSL014) -> llamada directa sin cast. Cada etapa es una sentencia `if (etapa is not (Success, var __x_n)) throw new PipelineRejectedException(...)`, de dentro afuera; post igual tras la llamada; líneas en blanco entre lotes. `var` en el patrón: un patrón de tipo rechazaría `null` con Success. Sin `__w_*` para parámetros sin etapas en ese lado. `PipelineResult.Ensure` ya no se emite (queda como API manual).
  miembros re-sangrados un nivel dentro de la clase.
  - [x] **5.2-D Ejemplos TIn≠TOut**: `ParseInt : IPipeline<string,int>`, `IntToString : IPipeline<int,string>`. `IAuth.Square` (servidor: red string ↔ contrato int; cliente público `string Square(string)`) e `IAuth.Twice` (cliente: público `int Twice(int)` ↔ red/contrato string). Demo en `Program.cs`.
  - [x] **5.2-D Ejemplo completo**: `IAuth.Echo` usa los 7 atributos (`TrimName`, `Upper`, `Bracket` Task, `Tag`, `Exclaim` ValueTask); `Program.cs` muestra async, sync y rechazo. Salida: `[[HI#]#]#!#`.
  - [x] **Fix transporte memoria**: `RpcBuffer.ResponseReady` con `TaskCreationOptions.RunContinuationsAsynchronously` (fork SharedMemory). Antes la continuación del `await` corría en el hilo lector y una llamada sync posterior lo bloqueaba (deadlock -> timeout 100 s).
  - [ ] Pendiente: ref/out/in y streams con procesadores (hoy SCLSL012), early-return sin excepción, estado real del rechazo en el cliente (hoy siempre `Failed`), test de SCLSL014.

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

- [ ] **5.5 Timeout y cancelación** *(acordado)*
  - `MemoryConnection`: `timeout = 100` ms por defecto, muy agresivo para handlers no triviales.
  - `Server.Memory.cs:67-88`: `Yield` recibe `token` como parámetro y **usa `cancelToken` del
	contexto**, ignorando el argumento.

- [ ] **5.6 Unix Domain Sockets como equivalente local de memoria compartida** *(acordado)*
  - `MemoryConnection` es `[SupportedOSPlatform("windows")]` por `RpcBuffer`.
  - Nuevo transporte `LocalConnection`: `RpcBuffer` en Windows, UDS (`UnixDomainSocketEndPoint`)
	en Linux/macOS, seleccionado por el generador o en runtime vía `OperatingSystem.IsWindows()`.
  - Reutiliza el framing de 3.3 — UDS es un stream, mismas reglas que TCP (P4).
  - Elimina los `#if`/`SupportedOSPlatform` que hoy se propagan hasta `Program.cs:37`.

- [ ] **5.7 `SharedMemory` como código interno, no como dependencia expuesta** *(decisión tuya)*
  - Hoy: `SharedMemory.csproj` referenciado como proyecto y **público** en la superficie de
	`Client`/`Server`.
  - → incorporar como **linked files** `internal` dentro de `SourceCrafter.LiteSpeedLink.Server` y
	`.Client`, sin referencia de proyecto.
  - Desbloquea 2.1 (añadir APIs `Span`/`IBufferWriter` a `RpcBuffer` sin versionar un paquete externo)
	y evita filtrar `RpcBuffer` al consumidor.
  - ⚠️ Ojo con duplicar los tipos en dos ensamblados si ambos comparten memoria en el mismo proceso;
	valorar un tercer proyecto `internal` compartido o `InternalsVisibleTo`.

- [ ] **5.8 Configuración de endpoints** *(de tus descartes)*
  - El diseño es compile-time, pero **host/puerto/nombre de canal no pueden serlo**: cambian por entorno.
  - Propuesta: mantener el *contrato* en compile-time y externalizar solo la *dirección*, vía variables
	de entorno o `appsettings`, leídas en el constructor generado.
  - `Program.cs:11` ya lo hace a mano (`rpcName` generado y pasado a ambos extremos) — formalizarlo.
  - **Pendiente de diseño**: ¿el generador emite un constructor adicional que lee configuración,
	o se deja al llamante como ahora?

---

## PoCs (antes de comprometer diseño)

- [ ] **PoC-A · Multiplexación y concurrencia del cliente** *(no descartado; hay que demostrarlo)*
  - Escenario: N hilos (1, 2, 8, 32) llamando a `Greet` sobre **una misma instancia** de cliente.
  - Medir, por transporte (Memory y TCP): corrección (¿respuestas cruzadas o corruptas?),
	throughput y latencia p99.
  - **Hipótesis a falsar**: *"sin correlación ni serialización de escrituras, TCP corrompe datos con
	≥2 hilos"*. Si con un `SemaphoreSlim` simple (sin multiplexar) el throughput ya es suficiente,
	la multiplexación completa (3.6) no se implementa.
  - Salida esperada: decidir entre *(a)* serializar por conexión, *(b)* pool de conexiones,
	*(c)* multiplexación real con `correlationId`.

- [ ] **PoC-B · ¿Necesita la librería una abstracción `ITransport`?**
  - Extraer la base común de `Tcp`/`Udp`/`Quic` en una rama y medir: ¿cuántas líneas se eliminan?
	¿se degrada el rendimiento por la indirección (interfaz vs. llamada directa)?
  - Alternativa a evaluar: genéricos con `where TTransport : ITransport` para que el JIT desvirtualice,
	en lugar de una interfaz con despacho virtual.
  - **Criterio de decisión**: si no elimina duplicación significativa o cuesta rendimiento, no se hace.

---

## Fase 6 — Línea base medible *(movida al final)*

- [~] **6.1 Proyecto `LiteSpeedLink.Benchmarks` (BenchmarkDotNet)** — *existen arnés y escenarios `Control`, `RequestBuilding`, `ResponseStatus`, `ServerParsing`, `TcpConcurrency`; faltan streaming y Memory multihilo*
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
