# Plan de mejoras — LiteSpeedLink

## Arranque

Este archivo es el **único traspaso** entre sesiones. Al terminar un paso: marcar su estado aquí,
añadir lo aprendido en *Trampas conocidas* y actualizar *Siguiente paso*.

### Mapa del repo

| Proyecto | Rol |
|---|---|
| `LiteSpeedLink.Abstractions` | Contratos compartidos: `ResponseStatus` (incl. `Batch`), `Pipeline.cs` (pipelines, middlewares, atributos de processors/middleware/retry/cache + `[Stream(Batch, MaxDelayMs)]`). |
| `Internals` | `Framing`/`FrameWriter`, enlazados (`<Compile Include>`) en Client, Server, Tests y Benchmarks; no es proyecto ni API pública. |
| `SourceCrafter.LiteSpeedLink.Helpers` | **Código común de generadores** (enlazado vía `Generator.props`): base abstracta `ServiceHandlersGenerator` (`AnalyzeContainer` con `#if CLIENT_PARTIAL`, `GetServiceId`), `ServiceHandlers.Raw.cs` (lectores/escritores raw por operación), `ServiceHandlers.Pipelines.cs` (etapas y diagnósticos), `Helpers.cs`. |
| `SourceCrafter.LiteSpeedLink.ServerGenerator` | **Generador host** (`ServiceHostGenerator`, ensamblado `SourceCrafter.DependencyInjection.Partial.LiteSpeedLink.Server`): `ServiceHandlers.GenerateServiceHost.cs` (host, `__Handler`, políticas de stream). |
| `SourceCrafter.LiteSpeedLink.ClientGenerator` | **Generador cliente** (`ServiceClientGenerator`, `CLIENT_PARTIAL`, ensamblado `...Partial.LiteSpeedLink.Client`): `ServiceHandlers.GenerateServiceClient.cs`. |
| `SourceCrafter.LiteSpeedLink.Client` / `.Server` | Transportes: `MemoryConnection`, `Tcp/Uds/Udp/QuicConnection` (`StreamConnection` + `MultiplexedChannel` para TCP/UDS), `Server.*` (`Server.Pipes` compartido). |
| `Application.Contracts` | Contratos de ejemplo `IAuth` y `IStreams` (8 formas de stream) + pipelines de muestra (`TrimName`, `Upper`, `Bracket`, `Tag`, `Exclaim`, `ParseInt`, `IntToString`). |
| `LiteSpeedLink` | App de muestra: `TextService`/`TextServiceClient` (Memory), `TransportSamples.cs` (Udp/Tcp/Quic), `StreamService`, `AuthService`, `MiddlewareSample.cs`, `ComplexSample.cs` (cache + retry + middleware + processor async), `Program.cs`. |
| `test/POC` | **Repo aparte**; POCs en la carpeta de solucion `Tests/POC` (`AuthRetryPoc`, `PipelineMiddlewarePoc`, `RetryPolicyIsolated`). |
| `LiteSpeedLink.Tests` | xUnit: servidores, framing, pipelines, lotes de stream, raw, pool QUIC. |
| `LiteSpeedLink.Benchmarks` | BenchmarkDotNet (6.1): escenarios, POCs del estudio (`Scenarios/*Poc*`) y comparativas (`Comparison/`, `QuicComparison`). |
| `docs/SourceGenStudy.md` | Inventario de decisiones runtime → generador, con estado y evidencia. |
| `..\SourceCrafter.DependencyInjection\SharedMemory` | **Repo aparte** (fork, rama `migrating-to-modern-memory-management`). Sus cambios se commitean allí. LiteSpeedLink **no** referencia `SharedMemory.csproj` ni su DLL: Client/Server enlazan 6 fuentes (`MemChannel`, `CircularBuffer`, `SharedBuffer`, `SharedHeader`, `FastStructure`, `MemoryHelpers`) como `internal` (`SG_CONTEXT`). El `.csproj` solo está en la solución para sus propios tests. |

### Verificar (PowerShell, desde `D:\Code\MemLink`)

```powershell
dotnet build-server shutdown; dotnet build LiteSpeedLink.slnx --no-incremental -nodeReuse:false
dotnet test LiteSpeedLink.Tests --no-build     # esperado: 94/94
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

### Trampas conocidas (known issues: ignorar, no investigar)

- **`PSSecurityException` / `UnauthorizedAccess` en la salida de benchmarks**: no es red ni transporte. Un `powershell.exe` hijo (BenchmarkDotNet, info de hardware) no puede cargar el perfil del usuario (`ExecutionPolicy` indefinida ⇒ `Restricted`). Inocuo; la consulta funciona igual. El código de salida 1 de `dotnet run ... | Select-String` viene de ahí (stderr de proceso nativo), no de un fallo de medición.
- **`ArgumentNullException` en `--check` (TCP, fila `A) sin proteccion`)**: carrera demostrada a propósito (`[INFO]`); la verificación termina en `TODO CORRECTO`.
- **`PipelineExamplesTest.PostgresAuthorization` falla** sin Postgres en `127.0.0.1:5432`: dependencia externa, no regresión.
- **Generador cacheado**: tras tocar `Helpers`, `ServerGenerator` o `ClientGenerator`, sin `build-server shutdown` + `--no-incremental` el `.g.cs` no cambia.
- **Ediciones que no llegan a disco** (scripts PowerShell con caracteres no ASCII incluidos): verificar con `Select-String` antes de compilar.
- **Deadlock sync tras async en Memory**: resuelto con `RunContinuationsAsynchronously` en `RpcBuffer.ResponseReady` (fork). Si reaparece un timeout en una llamada sync, mirar ahí.
- **Id de operación**: `GetServiceId` se calcula sobre el nombre completo del método; no alterarlo al cambiar el formato de firmas (5.3).
- **SharedMemory enlazado, no publicado**: los cambios en las 6 fuentes del fork entran en la siguiente compilación de Client/Server; no hace falta `dotnet publish` del fork.
- **Ediciones desde VS que no llegan a disco** en ficheros abiertos (`ServersTest.cs`): verificar con `Test-Path`/`Select-String`.
- **Puertos en tests paralelos** (xUnit): cada test de red usa un puerto propio; una colisión se ve como `QuicException` al enlazar, no como fallo del transporte.
- **HTTP/3 en benchmarks**: `https://localhost` resuelve a `::1` y Kestrel escucha en 127.0.0.1 ⇒ error ALPN. Usar IP.
- **Consumidor de stream inline** (`AllowSynchronousContinuations`): sync-over-async dentro de un `await foreach` interbloquea al lector.
- **Comandos git en paralelo** chocan con `index.lock`: una operación por comando.
- **`MemoryOwnerClosePoc` intermitente en la suite completa**: mide hilos del proceso mientras otras colecciones corren en paralelo; aislado pasa. No es regresión.
- **`replace_string_in_file` puede comerse el prefijo de la línea editada** en `Helpers` (p. ej. `code.Append(`): verificar con `Select-String` antes de compilar.

### Siguiente paso

0. Regla: cada item hecho lleva test o PoC. `CoverageGapTests.cs` cubre 2.4, 3.1, 3.3 (trama incompleta),
   3.4 (TCP y memoria), 3.7 (QUIC concurrente) y 5.4. Diagnósticos del generador: `GeneratorHarness.cs` +
   `PipelineDiagnosticsTest` (5.2). Resto (1.3, 3.2, 5.3) → PoC en `LiteSpeedLink/Program.cs`; nuevos casos de generador van al arnés.

Pendientes en orden (uno a uno; cada uno con test o PoC y verde antes del siguiente):

1. [x] Sincronizar este plan con el código y `docs/SourceGenStudy.md`.
2. [-] ~~Estado del rechazo en pipelines~~: no es regresión. Un pipeline solo distingue `Success`/`Failed` (corte anticipado), así que `Failed` fijo es correcto.
3. [x] **`MultiplexedChannel.DisposeAsync`**: `CancelPendingRead()` antes de esperar el bucle de lectura; ya no depende de cerrar el socket antes. Test `DisposeTest` (TCP/UDS, ≤ 2 s).
4. [x] **`ToListAsync()` sobre `EnumerateAsync` en Memory**: ya no se reproduce (10/10); lo resolvió `RunContinuationsAsynchronously` en `RpcBuffer.ResponseReady` (5.2). Regresión: `TestMemoryStreamsRoutedPerClient`.
5. [x] **`ConfigureAwait(false)`** en todos los `await` emitidos: host (`Start` QUIC, resolución async, `ReturnAsync`/`EnumerateAsync`/`NotFoundAsync`/`FailAsync`), etapas de pipeline (`ProcessAsync` y resolución async) y cliente tipado (`GetAsync`/`SendAsync`). Verificación: 0 `await` sin `ConfigureAwait` en `obj/gen` + POC (Memory/UDP/TCP/QUIC) y suite 71/71 en verde.
6. [x] **Raw (#12) completo**: `Task` sin resultado, pipelines y cota UTF-8 de `string` (6a–c); `Enumerate*` raw diferido por POC (6d, `StreamRawDecodePoc`); lectores de respuesta con la firma del contrato (6e).
7. [x] **Timeouts de Memory** (5.5): el `timeout` de `StartMemoryServer*` estaba muerto (nunca se usaba) → eliminado del API y del host generado; `onFinalize` también se ignoraba → ahora se invoca en `Dispose` (test `TestMemoryServerDisposeRunsOnFinalize`). Cliente unificado a 5000 ms (`AsMemoryConnection` tenía 100000). Streams sin timeout de petición, solo watchdog de inactividad (`_timeout`).
PoC `MemorySessionLeakPoc.DisposedClientsReleaseServerSessions`: 0 sesiones vivas y los hilos lectores se recuperan en <1 s.
   Mismo PoC: 0 clientes vivos en el host.
      Bench (antes lobby → ahora multicliente): MemoryConcurrency c=1 35.7 → 21.5 µs (3.22 → 2.88 KB), c=8 37.8 → 37.7 µs (23.85 → 21.35 KB), c=64 195 → 166 µs (188 → 168 KB); Streaming 10 items 32.6 → 29.7 µs (4.13 KB igual), 1000 items 108 → 88 µs (10.86 → 7.77 KB).
[x] `ref`/`out` con procesadores (POC `Normalize(ref, out)` en `Program.cs`, 4 transportes; async devuelve tupla). Processor de parámetro sobre `out` sigue en SCLSL012 a propósito: no hay valor de entrada. [x] Cliente generado de streams (`IEnumerable<T>`/`IAsyncEnumerable<T>`, `TryGenerateStreamClientMethod`; antes no compilaba) + POC `StreamServiceClient` en `Program.cs`. [x] Streams: processors de retorno → SCLSL012 (cliente y host; procesar = desempaquetar/reempaquetar cada elemento) y `[ClientRetry]`/`[ServerRetry]` → SCLSL016, ignorados (repetir reenviaría lo ya entregado; reanudar exigiría checkpoint en el contrato). Tests `PipelineDiagnosticsTest`, `RetryAttributeTest.RetryOnStreamIsIgnoredWithWarning`.
Stream propio gana desde ~1 MB (p99 de las pequeñas −66 %); ≤64 KB empate. Implementado como opt-in: `[DedicatedStream]` en el método del contrato → el cliente QUIC generado llama a `__connection.Dedicated` (misma conexión física, stream por unaria); otros transportes lo ignoran (el contrato se comparte entre hosts). Test `DedicatedStreamTest`.
11. [x] **`LocalConnection` seleccionada por el generador** (5.6): `ServiceConnectionType.Local` (valor 4, opt-in). El DI combina
    FAMWN con `AnalyzerConfigOptions.GlobalOptions` (`ServiceProviderInfo.GlobalOptions`, paquete 10.1.3); el parcial resuelve
    Memory si `RuntimeIdentifier` es `win*` o el TFM es `*-windows`, UDS en otro caso; sin RID ni TFM con plataforma decide `RuntimeInformation` (SO de compilación). Host
    `Start(string name)` y cliente `new X(string name)` exponen `IAsyncDisposable` en ambos destinos (`LocalHost`,
    `MemoryConnection.DisposeAsync`); el `[SupportedOSPlatform]` del transporte efectivo lo emite el generador en la clase generada,
    no el usuario. Test: `LocalConnectionTest` (5 destinos).
12. [x] **5.7 + 2.1**: SharedMemory enlazado como `internal` (`SG_CONTEXT`) en Client/Server, sin referencia de proyecto ni `InternalsVisibleTo`; `RpcBuffer` sustituido por `MemChannel`, que escribe con `IBufferWriter<byte>` directo al nodo y lee desde `ReadOnlySpan<byte>` (ver *Integracion MemChannel*).
POC `test/POC/AuthRetryPoc` (Memory, fallos inyectados en el host, 8 checks en verde):
    - Autenticación: **ya funciona sin cambios** con `[ServerProcessor<Authenticate>] Principal caller` (`IPipeline<string, Principal>` con `TokenStore` inyectado por DI): el cable lleva el token, el handler recibe el `Principal`, un token falso devuelve `Failed` sin ejecutar el handler.
    - Retry: política como tipo (`IRetryPolicy` con miembros `static abstract`), envoltorio a mano alrededor de `HitAsync`. Memory no pierde tramas: la pérdida se simula atascando el handler 400 ms (plazo del intento 200 ms) antes o después de ejecutar. Petición "perdida" → 1 reintento, 1 ejecución; respuesta "perdida" → el handler se ejecuta 2 veces (exige idempotencia); agotado → `OperationCanceledException`. La respuesta tardía del intento abandonado la descarta `MemClient` (id con generación). Sin fallos: tiempo igual (ruido de 11,5-13,7 µs), **+432 B/llamada** (336 → 768 B: CTS enlazado + `CancelAfter`).
    - [x] Retry generado: `[ClientRetry(attempts, intervalMs)]` (alrededor del transporte) y `[ServerRetry(attempts, intervalMs)]` (alrededor del handler), bucle inline sin structs ni delegados, solo `TimeoutException`, nunca tras cancelación; processors fuera del bucle. Ambos a la vez → SCLSL015 (intentos multiplicados). Argumentos con nombre: Roslyn normaliza `ConstructorArguments` al orden del constructor, la lectura por símbolo es posicional. Plazo por intento = el del transporte. Tests `RetryAttributeTest`; POC `Fetch` con `[ServerRetry(3)]`.
14. [x] **Generadores separados**: `ServerGenerator` (`ServiceHostGenerator`) y `ClientGenerator` (`ServiceClientGenerator`, `CLIENT_PARTIAL`) heredan de la base abstracta `ServiceHandlersGenerator`; el código común se enlaza desde `Helpers` vía `Generator.props`. Clases con nombre distinto porque el registro de DI desduplica parciales por nombre de tipo. `GeneratorHarness` carga ambos ensamblados. Suite 94/94 (commit `9f31c34`).

Pendientes reales (refinados):

- [x] **5.8 Endpoints por DI Configuration**: `RemoteOptions` (Host/Port, TCP/UDP/QUIC) y `LocalOptions` (Name, Memory/UDS/Local) con `Timeout` comun. MsConfiguration ya enlaza `[JsonSetting<T>]`; el parcial solo detecta el atributo en el contenedor (con su `JsonConfiguration` por `configKey`) y deriva el nombre del miembro (`nameFormat` + `key`). Con setting: host `Start()` sin endpoint (QUIC sigue pidiendo certificado) y cliente con parametros opcionales (`hostname ?? Settings.Host`, `port ?? Settings.Port`, `name ??= Settings.Name`; Memory usa `Timeout`); el explicito gana. Sin setting: API intacta. Test `EndpointSettingsTest` (compila con ambos generadores). Nota: MsConfiguration con `[JsonConfiguration]` sobre un contenedor sin servicios DI emite `EnvironmentName` sin declarar (CS0103) — fallo del paquete, no de LSL.
- [ ] **5.2-R `[ClientRetry]` E2E en `AuthRetryPoc`**: sustituir `Retry.RunAsync` por el bucle generado con timeout corto configurado (5.8); checks B2-B5 contra el cliente generado.
- [x] **Cola por stream reutilizable en el cliente**: ya existia (`StreamSink` pooled en `MultiplexedChannel`). Lo que quedaba era `RequestContext` por peticion (servidor) y la clausura del handler del benchmark: `RequestContext` ahora pooled (`Rent`/`Return` en `DispatchAsync`, mismo patron que `_batches`) y el benchmark usa `EnumerateAsync(secuencia)` como el host generado. `--alloc cmp` (5000 llamadas, misma maquina): Lsl_Stream 544 -> 356 B/op; Lsl_StreamBatched ~1360 -> ~1200 B/op (varia con el timing: si el productor no espera no hay cajas de flush). Tests 121/121.
- [x] **Ejemplo combinado** (`LiteSpeedLink/ComplexSample.cs` + bloque `[Complex]` en `Program.cs`): `[ClientCache]` + `[ServerRetry]` + `[ServerMiddleware<Timing>]` + processor async de parametro (`IAsyncValuePipeline<string, Customer>`) en `QuoteAsync`, y el mismo trio sin cache en `void Reserve` y `Task ReleaseAsync`. Checks: 3 intentos con 1 middleware y 1 processor, acierto de cache sin viaje, token falso rechazado antes del middleware. Sin resultado el host responde `Return(false)` y envuelve el handler sync en `ValueTask<bool>`: bytes/asignacion evitables, sin abordar.
- [x] **Concurrencia del cliente multiplexado (TCP/UDS)**: `_pending` pasa de `ConcurrentDictionary` a tabla fija de 4096 slots con CAS (`id & SlotMask`, el sink compara su id armado; slot ocupado -> id siguiente; tope 4096 en vuelo por conexion). `SendRequestAsync` ya no espera en `SemaphoreSlim`: escritura agrupada (cola `ArrayBufferWriter` bajo `Lock`, un solo vaciador hace un flush por tanda, doble buffer). BDN `MemoryConcurrencyBenchmarks` c64: UDS 286 -> 186 us, TCP 978 -> 649 us, -24 % memoria; c1 igual o mejor. Tests `UdsTest.TestPendingSlotWrapSkipsLiveStream`, `TestBatchedSendKeepsFramesIntact`. Detalle en `docs/PerformanceMemoryEnhancements.md` punto 4.
- [x] **Sin lote de stream en runtime**: fuera `streamBatch`/`streamBatchMaxDelay`, `DefaultStreamBatch` y `BatchPolicy` de TCP/UDS/QUIC; el defecto es la politica de tipo interna `DefaultBatched` y un lote distinto solo se fija con `[Stream(Batch, MaxDelayMs)]`. `--alloc cmplat` (intercalado, p50): Stream 67,0 us / 241 B, lote runtime 256 82,5 us / 703 B. `UnaryRaw` (31,6 frente a 31,3 us) y `coalesceStreams` (66,7 frente a 67,0 us) se quedan: Raw sostiene cache y out/ref. Test `StreamBatchPolicyTest.NoRuntimeBatchKnob`. Detalle en `docs/PerformanceMemoryEnhancements.md` punto 7.
- [x] **`MemoryConnection.SendAsync` con builder pooled**: los dos `SendAsync` (los unicos `async` de la clase; `GetAsync`/`GetRawAsync` devuelven el `ValueTask` de `MemChannel` sin maquina) usan `PoolingAsyncValueTaskMethodBuilder`, como `MultiplexedChannel.SendAsync`. `--alloc memc` (fila `MemoryConnection.SendAsync`): c=1 169 -> 40 B/llamada, c=64 170 -> 42 B; la caja `AsyncStateMachineBox` (~130 B) desaparece y queda el mismo coste que `GetAsync`.
- [ ] **Segundo nivel distribuido para `[ServerCache]` (Redis, SQL; despues)**: **por configuracion, nunca por defecto**. Opt-in por atributo (p.ej. `[ServerCache(..., Distributed = true)]`): el generador emite la consulta a L2 solo en esas operaciones, sin rama en runtime ni deteccion de "hay `IDistributedCache` registrado". El backend (Redis/SQL, cadena de conexion) va por DI en el host; si falta, error al arrancar, no degradacion silenciosa. Por que no por defecto: exige un almacen externo, anade un viaje de red a cada fallo de L1 (con loopback ~40 us, Redis cuesta lo mismo que la llamada) y solo compensa con varias instancias detras de un balanceador. `[ClientCache]` queda fuera: guarda valores tipados por cliente y compartirlos entre clientes no tiene sentido. El primer nivel sigue siendo `ServerCacheManager` (acierto sin asignar, clave por span); L2 guarda los mismos bytes de respuesta, sin reserializar. Candidato: `HybridCache` (`Microsoft.Extensions.Caching.Hybrid`) solo tras fallar L1: clave `string` y `ValueTask` por consulta lo descartan para el acierto local. Limites a documentar: single-flight solo por instancia y TTL de L1 + L2 acumulables. Con test/POC multi-instancia.

Descartados/aparcados: SCLSL012 en streams (decision), test byte a byte (framing compartido ya cubierto), `Pipe` + bombeo por conexion (el `TaskNode` ya no aparece), cookie UDP (solo si UDP se expone a Internet; hoy `maxAmplification`), coste de `RpcBuffer` (sustituido por MemChannel), estudio #5/#6/#7 (solo con perfil).

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
  - **Depende de**: 5.7 (hecho). Estado final: `MemChannel.Send<TState>(..., Action<IBufferWriter<byte>, TState>)`
    escribe sobre el nodo con `NodeWriter` y las respuestas se leen como `ReadOnlySpan<byte>`; el texto de arriba
    sobre `RpcBuffer` queda como histórico.

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
  - [-] Test de cliente con servidor falso que responda byte a byte: descartado. Cliente y servidor comparten `Framing.TryReadFrame` (ya cubierto por `TestTcpFragmentedFrames`) y TLS/streaming ya divergieron sin regresiones.
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

- [x] **3.8 Seguridad en la frontera de red** (UDP sin cifrar por diseno: solo red de confianza)
  - [x] `Framing.MaxFrameSize` (16 MiB): TCP y QUIC cortan la conexión ante longitudes hostiles. Test `TestTcpRejectsOversizedFrame`.
  - [x] TCP valida trama < `opId`; `Failed` envía `Message`, no la traza.
  - [x] Confidencialidad/integridad: QUIC ya usa TLS. TCP → `SslStream` opcional (`cert`): hecho, mTLS con pinning por hash del
    certificado configurado (antes fallaba con autofirmados: `UntrustedRoot`). Test `TestTcpTls`. UDP → sin cifrar (DTLS no está en .NET); solo red de confianza.
  - [x] Autenticación: pipeline de servidor (5.2), no transporte. Cubierta por los pipelines actuales (`[ServerProcessor<Authenticate>]`, etapa con dependencias de DI); POC `test/POC/AuthRetryPoc` (paso 13).
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
    3. ~~Dejarlo pendiente~~: obsoleto, `RpcBuffer` sustituido por MemChannel.
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
        - [x] **Latencia unaria Memory** (`MemoryConcurrencyBenchmarks`, conc. 1/8/64; base 36,4/40,9/170,8 µs · 2,74/20,04/157,84 KB → 18,8/32,7/163,8 µs · 1,87/13,16/102,84 KB):
          `TimeoutOrCancel` con `Task.WaitAsync(TimeSpan, token)` nativo; respuestas del servidor con `SendReply` directo al nodo (sin `RpcRequest`/TCS/array por paquete);
          spin por tiempo (~30 µs) antes del `WaitOne`; `DispatchRequest` con `ThreadPool.UnsafeQueueUserWorkItem`. Descartado: continuaciones inline en `ResponseReady` (conc. 64: 160 → 183 µs).
          `MemoryMultiClientTest.TwoClientsRequests` es intermitente desde antes (base: 9/12 fallos; ahora menos).
        sin `SemaphoreSlim` ni máquina async en el camino caliente; el token solo cancela la espera de cada llamante.
                  Medido en `QuicComparison` (antes → después): Lsl_Unary 68,55 → 68,46 µs (2,08 → 2,09 KB), Lsl_Stream 260,3 → 262,7 µs, Lsl_StreamBatched 157,8 → 159,9 µs; ratio frente a ASP.NET slim H3 sin cambio (2,32 → 2,30). Neutro: el camino caliente ya no asignaba; se queda por semántica (reintento, cancelación por llamante).
  - [x] **POC A · lotes por nodo** (`MemoryRequestContext.Append/Flush`): el servidor agrupa `[int32 len][item]` en un solo `StreamItem` (lote 16 KB, buffer reutilizado; se vacía si el productor async va a esperar). Sin tipo de mensaje nuevo. Memory 1000 items: 702 → 109 µs.
  - [x] **POC B · drenado en lote en el cliente** (`WaitToReadAsync` + `TryRead` en `OpenStream`): 109 → ~100 µs, marginal pero gratis. Resultado: Memory 100 µs / 8 KB vs UDS 304 µs y TCP 344 µs (1000 items). Tests de streaming Memory cubren ambos.
  - [x] **Varios clientes por `contextId`**: Hoy con `RpcBuffer.Host`/`Connect` multicliente (ver paso 8); antes con `MemoryLobby` (eliminado). `MemoryMultiClientTest` activo y en verde. Historico: con `MemoryLobby` (servidor) + `MemoryConnection.Join`:
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
  - [x] **Early-return**: el host ya no lanza al rechazar; emite `return __context.Fail[Async](string)` (sobrecargas nuevas en los 4 contextos). El cliente lanza (no hay respuesta que devolver).
  - [x] **`in` con procesadores** (cliente y host). `IAuth.Shout`.
  - [x] **Arnés Roslyn** (`GeneratorHarness.cs`) y `PipelineDiagnosticsTest`: SCLSL010-014.
  - [x] `ref`/`out` con procesadores (POC `Normalize` en `Program.cs`). [-] Streams con procesadores: se quedan en SCLSL012 por decision.

  `Pipeline.cs` actual: 4 interfaces sin uso, y `IPipelineAsync.ProcessAsync` devuelve `TOut` en vez
  de `Task<TOut>`. Se reemplaza entero.

  **Objetivo**: interceptores (auth, logging, validación, retry) **compuestos por el generador**,
  sin delegados encadenados, sin `IEnumerable<IMiddleware>`, sin asignaciones por request.

  - [~] **5.2-R Retry como pipeline de cliente**: generado (ver abajo); falta el E2E de `[ClientRetry]`. Opt-in, solo
	útil en UDP (TCP/QUIC ya retransmiten). Timeout + reintento por corrId; en streams re-pide
	los `seq` faltantes (4.3). Requiere handlers idempotentes.
	POC `test/POC/AuthRetryPoc` (paso 13, sobre Memory): semántica validada; el envoltorio por llamada cuesta +432 B, así que la versión generada debe usar el plazo del transporte.
	Test `RetryAttributeTest`.
		- [x] `[ClientMiddleware<T>]` / `[ServerMiddleware<T>]` (`IMiddleware`/`IAsyncMiddleware`): lambdas `static` anidadas (delegados cacheados) con un único estado compartido, sin structs por capa; el primero declarado es el más externo; envuelven solo la llamada (transporte/handler) con el retry dentro, processors y cache fuera. Solo la capa interna async es pooled; en Memory sin esperas el handler sigue sync. `SCLSL020` (no registrado/sin la variante sync|async), `SCLSL021` (stream). POC `test/POC/PipelineMiddlewarePoc`; test `MiddlewareGeneratorTest`.
	- [x] `[ClientCache(durationMs, capacity = 1024)]` / `[ServerCache(...)]` + `[CacheKey]`: propuesta en `docs/CacheProposal.md` (servidor: bytes de petición → bytes de respuesta tras return processors, lookup por span sin asignar; con `[CacheKey]`, clave tipada; cliente: `CacheManager<TKey, TValue>` tipado, clave = valores completos o bytes, sin boxing; `SCLSL017`/`018`/`019`).
		- [x] 1. Atributos
		- [x] 2. Host: `ReturnRaw*` en los tres contextos, escritor `__ResN`, consulta/guardado; `SCLSL017`. Manager por método perezoso (`LazyInitializer`); consulta antes de deserializar o tras los server param processors; single-flight (`TryLead`/`Complete`/`Fail`, fallos no cacheados) también en `ClientCacheManager`. Tests `ServerCacheGeneratorTest` (Tcp/Udp/Memory, SCLSL017) y `CacheManagerTest.SingleFlight…`.
		- [x] 3. Clave (rev. 3 de la propuesta): igualdad por valor comprobada en el generador (primitivos, string, enum, `IEquatable<T>`, records con campos que la tengan); si no la tienen y no hay `[CacheKey]`, clave = bytes serializados + `SCLSL018` (info, elevable a error para forzar igualdad por valor). `[CacheKey(params string[] members)]` en parámetro o método, rutas leídas de la sintaxis (`nameof(a.b.c)` evalúa a `"c"`); `SCLSL019` para rutas inválidas u hojas sin igualdad. Clave = hoja tal cual o `ValueTuple` (sin `HashCode` como identidad). `ByteContentComparer` y `CacheManager<TKey, TValue>` (antes `ClientCacheManager`) movidos a Abstractions: los usan cliente y host (`[ServerCache]` + `[CacheKey]` → clave tipada tras los param processors). Test `CacheKeyGeneratorTest`.
		- [x] 4. Cliente: consulta/single-flight/guardado en la ruta raw (clave por bytes = la propia petición) y en la de processors (clave = argumentos originales, consulta antes de processors y retry). Test `CacheKeyGeneratorTest`.
		Campos `__cacheN` sin `?` (el código generado no tiene contexto nullable: CS8669).
				- [x] 6. Benchmark `FeatureBenchmarks` (suite `Features` = 4096): contrato generado TCP vs ASP.NET Core slim (HTTP/1.1, binario, `OutputCache` nativo). Setup valida por deltas que los aciertos no ejecutan handler (`MemoryRandomization` repite `GlobalSetup` en el mismo proceso). Arnés de publicación (`4097`, control incluido; Processor reejecutado aparte por `SocketException 10048` de puertos efímeros): Plain 37,8 vs 50,1 µs · 1,2 vs 2,9 KB; Processor 34,6 vs 45,3 µs · 1,4 vs 6,0 KB; ClientRetry 37,8 vs 42,5 µs (±7 µs en Slim); ServerRetry 37,5 vs 47,2 µs; ClientCacheHit **15 ns / 72 B** vs 52,4 µs (Slim acierta en servidor: sí viaja); ServerCacheHit 46,5 vs 51,3 µs · 1,2 vs 4,2 KB; CacheKeyHit 44,4 vs 50,4 µs · 1,4 vs 4,3 KB; SingleFlight (16 concurrentes en frío) 39,5 vs 228 µs · 4,9 vs 45 KB.
	- [~] POC `AuthRetryPoc` E2E con el retry generado: `[ServerRetry]` cubierto (D1/D2). `[ClientRetry]` no: B usa el envoltorio manual `Retry.RunAsync` porque el cliente generado no expone el timeout (5000 ms fijo) y el bucle generado solo reintenta `TimeoutException`. Depende de 5.8.

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

- [x] **5.5 Timeout y cancelación** *(acordado)* — cliente 5000 ms (`MemoryConnection` y `AsMemoryConnection`); servidor Memory sin timeout (era un parámetro muerto); `Yield` usa el token del servidor por diseño (el generador no inyecta otro).
  - ~~`MemoryConnection`: `timeout = 100` ms por defecto, muy agresivo para handlers no triviales.~~ Hoy 5000 ms.
  - ~~`Server.Memory.cs:67-88`: `Yield` recibe `token` como parámetro y **usa `cancelToken` del
	contexto**, ignorando el argumento.~~ Resuelto: `Yield` ya no recibe token; usa el del contexto.
  - ~~Pendiente: timeouts por defecto divergentes (cliente 5000 ms, `AsMemoryConnection` 100000 ms, servidor 1000 ms).~~ Resuelto (paso 7).

- [x] **5.6 Unix Domain Sockets como equivalente local de memoria compartida** *(acordado)* — Hecho: `Server.StartUdsServer` + `UdsConnection` reutilizando `ServePipeAsync`/`MultiplexedChannel` (framing 3.3). Test: `UdsTest.TestUdsRoundtrip`. `MultiplexedChannel.DisposeAsync` no cuelga aunque el socket siga abierto (`CancelPendingRead`, `DisposeTest`). `ServiceConnectionType.Local` lo resuelve el generador en compilacion (RID/TFM, si no el SO que compila): Memory en Windows, UDS en otro caso. Test `LocalConnectionTest`; POC `samples/Jobs` (Agent/Cli con `Local`).
  - `MemoryConnection` es `[SupportedOSPlatform("windows")]` por `RpcBuffer`.
  - Nuevo transporte `LocalConnection`: `RpcBuffer` en Windows, UDS (`UnixDomainSocketEndPoint`)
	en Linux/macOS, seleccionado por el generador o en runtime vía `OperatingSystem.IsWindows()`.
  - Reutiliza el framing de 3.3 — UDS es un stream, mismas reglas que TCP (P4).
  - Elimina los `#if`/`SupportedOSPlatform` que hoy se propagan hasta `Program.cs:37`.

- [x] **5.7 SharedMemory interno**
  - `StartMemoryServer*` y el host generado devuelven `IDisposable`; ningún tipo del fork aparece en la API pública.
  - Client/Server enlazan 6 fuentes del fork (`MemChannel`, `CircularBuffer`, `SharedBuffer`, `SharedHeader`,
	`FastStructure`, `MemoryHelpers`) compiladas `internal` con `SG_CONTEXT`; sin `ProjectReference`, sin DLL
	publicada y sin `InternalsVisibleTo`. `RpcBuffer.cs` ya no se enlaza (solo lo usa `MemChannelPoc` como baseline).
  - Tipos duplicados en Client y Server: inocuo, ambos extremos se encuentran por el nombre de la MMF/EWH, no por
	identidad de tipo; los tests in-process de Memory lo cubren.
  - Los tests del fork viven en `SharedMemory.Tests` (repo aparte), no en `LiteSpeedLink.Tests`.

- [x] **5.8 Configuración de endpoints** *(de tus descartes)* — hecho: `RemoteOptions`/`LocalOptions` por `[JsonSetting<T>]` (ver *Endpoints por DI Configuration* arriba). Notas de diseño originales:
  - El diseño es compile-time, pero **host/puerto/nombre de canal no pueden serlo**: cambian por entorno.
  - Propuesta: mantener el *contrato* en compile-time y externalizar solo la *dirección*, vía variables
	de entorno o `appsettings`, leídas en el constructor generado.
  - `Program.cs:11` ya lo hace a mano (`rpcName` generado y pasado a ambos extremos) — formalizarlo.
  - **Pendiente de diseño**: ¿el generador emite un constructor adicional que lee configuración,
	o se deja al llamante como ahora?
  - Via DI Configuration (`SourceCrafter.DependencyInjection.MsConfiguration`, ya referenciado): la direccion
	(host/puerto/nombre, timeout) como opciones inyectadas en el contenedor generado; el generador solo emite
	la lectura de la seccion del cliente/host (nombre fijado en compilacion), sin reflexion ni binding dinamico.
  - Desbloquea el E2E de `[ClientRetry]` (5.2-R): hoy el timeout del cliente Memory generado es fijo (5000 ms).

---

## PoCs (antes de comprometer diseño)

(`ServiceHandlers.Raw.cs`, test `UdsTest.TestRawRoundtrip`) para unarias, `Task` sin resultado y pipelines; streams raw diferidos tras POC `StreamRawDecodePoc` (6d).

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
  - [x] Peticion raw escrita directamente en la trama del transporte (como Memory): `GetRaw(Async)<TState>(op, state,
    static (w, s) => __ReqN(w, ...))`; el transporte envuelve en `RawBody<TState>` (struct) y lo escribe tras su cabecera.
    Sin `ArrayBufferWriter` ni copia por llamada; solo la clave de cache por bytes sigue materializando (`__cacheKey`).
    POC `RawBodyWritePoc`: 8 chars 34.3 ns/120 B -> 26.9 ns/0 B; 1024 chars 235.7 ns/6216 B -> 61.5 ns/0 B.
    Respuesta sin copia: `GetRaw(Async)<TState,TOut>(..., RawReader<TOut> read)` decodifica sobre el buffer del transporte (pool TCP/UDS, pipe QUIC, nodo Memory, datagrama UDP); se eliminaron las APIs que devolvian `ReadOnlyMemory<byte>` (3 `ToArray`). Lector generado devuelve valor/tupla (retorno + out/ref).
    Resto (+43 B vs tipada): perfilado y resuelto (abajo: 157 vs 153 B/op).
        `--alloc cmp unary` (200k llamadas): Lsl_Unary 711 B/op, Lsl_UnaryRaw 754 B/op. Los +43 B estan todos en `AsyncStateMachineBox` (capas StreamConnection -> MultiplexedChannel.GetRawAsync -> UnaryAsync transportan `state`+`write`+`read` en vez de `payload`); `RawBody` no se encajona (box). Mejora posible: camino rapido sin async en `StreamConnection.Get(Raw)Async` cuando el canal ya existe (como `EnumerateAsync`): elimina una caja entera (~130-220 B/op).
        [x] Implementado: camino rapido sin async en `StreamConnection.Get/GetRaw/SendAsync` (canal ya conectado) + `PoolingAsyncValueTaskMethodBuilder` en `MultiplexedChannel.Get/GetRaw/Send/Unary/SendRequestAsync`. `--alloc cmp unary`: Lsl_Unary 711 -> 153 B/op, Lsl_UnaryRaw 754 -> 157 B/op (resto: caja del benchmark + nodo de `_pending`). Contratos publicos intactos; tests 128/129 (falla solo Postgres por entorno).
        [x] `EnumerateAsync`: `StreamEnumerable` separado en enumerable ligero (sin pool, reenumerable) + `StreamEnumerator<TIn,TOut>` pooled por tipo (vuelve al pool en `DisposeAsync`, idempotente). `--alloc cmp`: Lsl_Stream 359 -> 282 B/op, Lsl_StreamBatched 1202 -> 1107 B/op. BDN: Lsl_Stream 543 -> 469 B, Lsl_StreamBatched 1360 -> 1195 B; tiempo igual. Test: `PooledEnumeratorTest`.
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
    | Memory | 1124 µs / 481,6 KB | **46 µs / 471 B** |
    | UDS | 1257 µs / 3,1 KB | 58 µs / 563 B |
    | TCP | 8962 µs / 2,5 KB | 65 µs / 572 B |
    | QUIC | 414 µs / 45 KB | 270 µs / 9,5 KB |

  - **Streaming, 10 items**: Memory 13 µs / 453 B, UDS 28 µs, TCP 34 µs, QUIC 105 µs / 6,9 KB.
  - **QUIC**: hereda el flush diferido (`ResponseChannel`). Coste dominado por lo fijo por llamada (~200 µs y ~40 KB: stream nuevo + `PipeReader/Writer` por RPC, ver 3.7); el incremento por item (~0,2 µs) es como TCP/UDS. Optimizarlo = reutilizar streams, contrario al diseño stream-por-RPC: no se hace salvo que QUIC sea el transporte principal.
  - **QUIC**: `QuicOverheadBenchmarks` descompone lo fijo por llamada: stream crudo 86 µs / 3,2 KB (suelo), + pipes 94 µs / 4,3 KB, RPC LSL 211 → 110 µs / 7,2 KB. Causa: `Call.DisposeAsync` cerraba sin consumir el FIN y QuicStream creaba una `QuicException` con traza por llamada. Test: `TestQuicCallDoesNotThrowInternally`.
  - **Pipes (TCP/UDS/QUIC) por item**: `ResponseChannel.WriteAsync` con camino síncrono y caminos async con `PoolingAsyncValueTaskMethodBuilder`. TCP 19 → 6,8 B/item. `--alloc tcp|uds|memory|quicstream` y `--alloc quic`.
  - **Memory, petición de stream**: `RpcBuffer.RemoteStreamAsync<TState>` (fork) escribe directo en el nodo; fuera `ToArray()`. 1,9 → 0,6 B/item.
  - **Arnés completo (publicable), 1000 items** (histórico): Memory 108 µs / 10,9 KB, UDS 293 µs / 9 KB, TCP 479 µs / 26 KB, QUIC 440 µs / 11,5 KB. 10 items: Memory 33, UDS 39, TCP 69, QUIC 208 µs. ~~TCP/1000 con 26 KB es la anomalía a investigar.~~ Resuelto por lotes por defecto (ver *Resultados vigentes*).
  - El `ArgumentNullException ('array')` del arranque viene de `SemanticCheck` (clientes POC rotos a propósito).
  Siguiente: agrupar items en TCP como en Memory (**hecho**: `coalesceStreams` por defecto, ver final).
  - **MemoryConcurrency** (N llamadas sobre una `MemoryConnection`, medición inicial): 64 → Memory 195 µs / 188 KB vs UDS 219 µs / 158 KB; sin respuestas cruzadas. Vigente: c1 16 µs / 687 B, c8 26 µs / 3,5 KB, c64 134 µs / 25,4 KB (UDS 32 / 46 / 202 µs).
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
- ~~Pendiente: QUIC y Memory no usan estas opciones.~~ QUIC acepta `streamBatch` en el servidor (el cliente no multiplexa streams: sin coalesce) y, como TCP/UDS, agrupa por defecto (`DefaultStreamBatch`; `streamBatch: 0` = item a item). Test `StreamBatchPolicyTest.EveryStreamTransportBatchesByDefault`. Memory agrupa por su cuenta en `Yield`.


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
- [x] [Stream(IdleTimeoutMs)] -> el cliente de memoria generado pasa el watchdog como constante a `MemoryConnection.EnumerateAsync(op, payload, idleTimeoutMs, token)`; 0 = timeout de la conexion, -1 = suscripciones silenciosas (Chat.Join). Antes un Join callado 5 s moria con TimeoutException. Test: `MemoryStreamHostTest.IdleTimeoutFromContractReachesMemoryClient`.
POC: `samples/Hello/hello.cs` sin namespace. Publicado en DI 2.26.282.289 (LSL y Hello lo consumen).
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

Ganancia real (~45% latencia, ~85% memoria en crudo). Decidido: pool de N streams multiplexados (opcion 2, abajo) + `[DedicatedStream]` opt-in para respuestas grandes.


### QUIC opcion 2: pool de streams multiplexados para unarias (por defecto, DefaultUnaryStreams=4)
| Method | Mean | Allocated |
|---|---|---|
| LslUnary (stream por llamada) | 108.1 us | 6774 B |
| LslUnaryPooled (4 streams) | 69.9 us | 2133 B |
| RawReused (suelo) | 66.8 us | 496 B |

Unarias: -35% latencia, -69% memoria. EnumerateAsync sigue con stream propio (aislamiento HOL). Marcador opId=long.MinValue abre el stream como canal multiplexado (8 B una vez). unaryStreams: 0 restaura el modo anterior. Test: TestQuicUnaryStreams(0|4).

### PoC #14: unaria grande en el pool vs stream propio (`--quicpool [KB]`)
Misma conexion (pool de 4). 2 bucles de unaria grande en segundo plano; 4 workers x 2000 unarias pequeñas. Latencia de las pequeñas (us):

| Grande | Ruta | media | p50 | p99 |
|---|---|---|---|---|
| — | sin grande | 76-101 | 70-98 | 126-190 |
| 16 KB | pool / stream | 173 / 164 | 170 / 159 | 306 / 273 |
| 64 KB | pool / stream | 234 / 229 | 213 / 210 | 448 / 456 |
| 256 KB | pool / stream | 690 / 491 | 604 / 438 | 1493 / 1463 |
| 1 MB | pool / stream | 1998 / 1262 | 2041 / 1631 | 7067 / 2378 |
| 4 MB | pool / stream | 14686 / 4793 | 14470 / 5048 | 18250 / 8199 |

Rendimiento de las grandes igual en ambas rutas. Conclusion: el HOL del pool solo pesa desde ~256 KB-1 MB; por debajo, empate y el pool es mas barato por llamada (70 vs 108 us). Una heuristica por tipo ("respuesta de longitud variable => stream propio") penalizaria las unarias pequeñas con `string`/`byte[]`; la ruta debe ser opt-in explicita por operacion.


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

### Flush diferido sin asignaciones (ResponseChannel, rama fix/stream-flush-alloc)
- [x] `--alloc cmp` (AllocProbe sobre las filas de Comparison) mostro que el 54 % de Lsl_Stream era `FlushLaterAsync`: caja async por flush (`_ = ...` nunca se awaitaba, el pool no la reciclaba) + `TaskNode` al esperar el candado del productor.
- `ResponseChannel` es su propio `IThreadPoolWorkItem`: flush sincrono si el candado esta libre; si esta ocupado marca `_flushWanted` y quien lo suelta (`Release()`) reencola; async solo si `FlushAsync` no completa.
- BenchmarkDotNet (Comparison): Lsl_Stream 4891 -> 3352 B (slim 4535 B), Lsl_StreamPerItem 5684 -> 3443 B, Lsl_StreamBatched 3766 -> 3537 B (slim 3067 B); tiempos sin cambio (163 / 255 / 90 us). Tests 89/89.
- ~~Pendiente: Lsl_StreamBatched aun +470 B vs slim por las cajas async de `FlushBatchAsync`/`WriteRawAsync` por lote.~~ Bajado a ~215 B (ver *Lotes sin maquina de estados*).

### Lotes sin maquina de estados (rama fix/batched-alloc)

- `ResponseChannel.WriteRawAsync`/`WriteStatusAsync` y `RequestContext.FlushBatchAsync` ya no son `async`: camino sincrono con `_gate.Wait(0)` y flush completado en linea; solo si el candado o el flush esperan se entra en un metodo `async` con `PoolingAsyncValueTaskMethodBuilder`.
- `FlushAndReleaseAsync` comprueba `IsCompleted` antes de esperar (tambien beneficia a `WriteAsync`).
- El lote solo se resetea tras completar la copia al `PipeWriter` (vida del buffer intacta).
- BenchmarkDotNet (Comparison, 1000 items): `Lsl_StreamBatched` 3537 B -> ~3287 B (3.21 KB), 87.7 us; `AspNetSlim_StreamBatched` ~3072 B (3 KB), 103.8 us. Brecha ~470 B -> ~215 B.
- ~~Resto: `Channel<Response>` por stream en el cliente.~~ Resuelto con `StreamSink` pooled; ver pendiente cerrado de cola por stream.
- Tests: 88/89; `MemoryOwnerClosePoc` (RpcBuffer, conteo de hilos) falla solo en la suite completa y pasa aislado; no toca `ResponseChannel`.

### Memory RPC directo (RpcBuffer <-> MemoryConnection/Server.Memory)

- [x] Unario tipado: `RpcBuffer.Call` escribe `[op][body]` en el nodo (`WriteProtocolV1<TState>`) y lee `[status][body]` desde el span del nodo en el lector (`PendingCall<TState,TOut>`); sin `RpcResponse` ni array de respuesta en single-packet. Multi-packet sigue ensamblando.
- [x] Streaming separado: `RpcBuffer.CallStream` (items por `StreamItemHandler`, timeout infinito); `MemoryConnection` mantiene Channel/watchdog/batches.
- [x] Servidor: `RpcRequestHandler`/`RpcAsyncRequestHandler` reciben `ReadOnlyMemory` sobre buffer de ArrayPool (devuelto al terminar el handler, tambien async); `MemoryRequestContext` responde con `RpcBuffer.Reply` directo al nodo. Handlers devuelven `ResponseStatus`/`ValueTask<ResponseStatus>` (generador incluido); fuera `MemoryResponse`.
- [x] Carrera lector/Dispose (AccessViolation en `SharedBuffer.ShuttingDown`, ~2/8 corridas): `_disposed` se marca bajo `m_ReadThreadIsReadingLock` y el lector lo comprueba al reclamar el buffer; el dueño levanta `MarkShutdown` antes del desmapeo diferido.
- BenchmarkDotNet MemoryConcurrency (--fast): c=1 1.87 -> 1.5 KB, c=8 13.16 -> 10.16 KB, c=64 102.84 -> 78.85 KB; tiempos sin cambio (17.7 / 38 / 162 us).
- Tests: LiteSpeedLink 93/93 (35 corridas seguidas sin crash), SharedMemory 47/47 (12 corridas).

### POC MemChannel: canal propio sobre CircularBuffer, sin RpcBuffer (`test/POC/MemChannelPoc`)

- Se conserva `SharedBuffer`+`CircularBuffer` (MMF+EWH); reescribirlo no aporta. Lo que sobra es `RpcBuffer`.
- Canal: frame de 12 B (`Total,Id,Client,Kind`) frente a la cabecera V1; slots con generacion + `Pending<TOut>` pooled (`IValueTaskSource`), sin Task/TCS/ConcurrentDictionary por llamada; timeouts por un timer de barrido (100 ms); `MemRequest` pooled; anillos con refcount y lector unido (`Join`) en Dispose.
- Hallazgo: varios writers sin serializar sobre el mismo anillo se duermen en `NodeAvailable` (AutoReset) hasta el timeout -> lock por anillo (igual que `lock_sendQ`).
- Self-checks (`dotnet run -c Release`): unario sync/async, 400 concurrentes en 2 clientes, 100 KB multi-packet, streams (1000 items, 8 concurrentes + 50 unarios), error, doble reply, timeout, cancelacion, respuestas tardias, host caido, dispose storm x30. 6/6 corridas OK.
- Unario int, 20k llamadas (mediana de 6 corridas): c=1 9.6 -> 6.2 us, 808 -> 0 B; c=8 22 -> 9.5 us, 808 -> 0 B; c=64 165 -> 75 us, 809 -> 1 B; sync c=1 23 -> 14.5 us, 756 -> 0 B.
- [x] Sustituir `RpcBuffer` en `MemoryConnection`/`Server.Memory` por este canal: hecho (ver *Integracion MemChannel*).

#### Modos de stream en MemChannel (mismo canal, ruta propia)

- Frames nuevos: `Stream` (request con `int32` de creditos delante del body), `Credit`, `Cancel`; unarias sin cambios (0 B, mismos tiempos).
- `credits` fijado por operacion (lo emitiria el generador): `0` server-sized/push hasta la respuesta final; `-N` client-sized, el host corta tras N items (`Item` devuelve false); `N>0` ventana, el host envia como mucho N por delante de `Grant`.
- `idleTimeoutMs`: watchdog re-armado por cada item; `Timeout.Infinite` para streams abiertos/silenciosos.
- Todo fin local (cancel, watchdog, fallo del callback) envia `Cancel`; `Close` del cliente y Dispose del host cancelan sus productores (`MemRequest.IsCancelled`/`Aborted`).
- Checks (5/5 corridas): take 50 sobre productor infinito; push cancelado detiene el host; ventana 16 con consumidor lento, backlog max 16 y 200 unarias a 14-19 us; watchdog 200 ms (~260 ms) y re-armado (8 x 100 ms con limite 400 ms); stream abierto silencioso 600 ms y fin por cancel; dispose del cliente detiene sus productores; sin slots perdidos.
- Contrato propuesto: `[Stream(Take = nameof(count))]`, `[Stream(Window = n)]`, `[Stream(IdleTimeoutMs = Timeout.Infinite)]`; ServerSized `(int Count, IAsyncEnumerable<T>)` solo con caso real.

#### Integracion MemChannel (sustituye a RpcBuffer)

- Comparativa de streaming con el batching de produccion (lotes `[int32 len][item]` de 16 KB, nodos 50 KB x 10), us/stream, 4 corridas: N=10 c=1 ~12 -> ~10, c=8 ~28 -> ~19; N=1000 c=1 ~60 -> ~57, c=8 ~100 -> ~87; N=20000 c=1 ~150 -> ~138, c=8 empate (900-990 vs 860-1120, ruido). Asignacion ~470 -> 0-8 B/stream. Unario: c=1 8.9 -> 5.8 us, c=64 114 -> 58 us, 696 -> 0 B.
- Veredicto: gana o empata en todos los escenarios, nunca pierde de forma consistente -> integrado.
- `MemChannel.cs` vive en el fork SharedMemory (namespace `SharedMemory`, internal por SG_CONTEXT); Client/Server lo enlazan en lugar de `RpcBuffer.cs`. La POC enlaza el mismo archivo (RpcBuffer solo como baseline).
- `MemoryConnection`: `MemClient` (reconexion si `!IsAlive`); unarias sync sin `GetAwaiter().GetResult()`, async sin `Task`; streaming por `StreamAsync` con watchdog nativo (fuera Timer propio) y `Cancel` al host si se abandona la enumeracion.
- `Server.Memory`: `MemHost.Start` + `MemoryRequestContext` sobre `MemRequest` (`Reply`/`Item`); un `Item` rechazado (cliente cancelo/se fue) detiene el productor con `OperationCanceledException`.
- `MemRequest.Reply` deshace el claim si no se publico nada (serializador fallo), para que `Fail` aun responda.
- Cambio observable: el watchdog de inactividad lanza `TimeoutException` (antes `OperationCanceledException`); `MemoryStreamBatchingTest.WatchdogCancelsStalledStream` actualizado. `MemorySessionLeakPoc` mira `MemHost._peers`.
- Tests: LiteSpeedLink 93/93 (3 corridas); POC 22/22 checks.
- BenchmarkDotNet publicable (mascara 97, control OK), RpcBuffer -> MemChannel: MemoryConcurrency c=1 17.7 -> 16.4 us, 1.5 KB -> 663 B; c=8 38 -> 28 us, 10.16 -> 3.56 KB; c=64 162 -> 146 us, 78.85 -> 25.96 KB (UDS: 35.6 / 51 / 200 us). Streaming 10 items 29.7 -> 19.6 us, 4.13 -> 1.97 KB; 1000 items 88 -> 92 us (ruido), 7.77 -> 5.79 KB (UDS 33 / 128 us).
- [x] Hueco MemoryConcurrency (16.5 us) vs POC (6.2 us): es del arnes (Task.Run + WhenAll por op en BDN), no de `MemoryConnection`. `--alloc memc` con el arnes de la POC: c1 7.5-11.3 us, c8 13.7-23, c64 97-144, 41 B (`MemoryRequestContext` del host); POC misma maquina 8.5 / 11 / 91 us, 0 B. Sin cambios de produccion.
- [x] Streaming Memory asigna mas que UDS
  - [x] POC `MemPull` (MemChannel.cs): el hilo lector copia cada item como `[int32 len][payload]` en dos buffers del ArrayPool intercambiados; un unico `ManualResetValueTaskSourceCore` despierta al consumidor, `Pending.Complete/Fail` señala el fin. Sin Channel ni Task de `Complete`. Checks: orden + final, 8 pulls concurrentes + 50 unary, fallo del consumidor cancela al productor.
    POC pull (us / B por stream): N=10 8.9/345 (c1), 14.8/349 (c8); N=1000 12.1/353, 18.5/358; N=20000 73.1/1635, 155.9/7486. RpcBuffer ~465 B. Los ~350 B fijos son `MemPull` + su `Lock` + la caja async del consumidor.
  - [x] `MemPull` pooled (`Rent`/`Dispose`, buffers retenidos hasta 256 KB): solo se recicla si termino por respuesta en el hilo lector (sin items en vuelo); cancel/watchdog/dispose devuelven los buffers al ArrayPool y no se reutiliza. Check: 50 streams seguidos sobre la misma instancia, items exactos.
    ya no hay `byte[]` ni `MemPull`.
      - [x] `MemPullEnumerator<TItem,TEnd>` (MemChannel.cs): `IAsyncEnumerator` pooled sobre `MemPull` pooled, `MoveNextAsync` sincrono mientras haya registros y si no via su propio `IValueTaskSource<bool>` (sin maquina de estados async ni Channel). Items decodificados en el hilo consumidor; `DisposeAsync` temprano cancela al productor; token de enumeracion enlazado solo si ambos cancelan. Checks: orden + final, 8 lotes concurrentes, dispose temprano, cancel por token, fallo del handler.
        `alloc` POC (B/stream): pull rent 218-234 -> enumerador 35-73 (resto: work item del pool y ruido del muestreo).
      - [x] Portado a `MemoryConnection.OpenStream`: `StreamEnumerable` (unico objeto por llamada) sobre `MemPullEnumerator`; fuera Channel, CTS enlazado y Task de `Complete`. Suite 93/93.
        `--alloc memory`: 1 item 1975 -> 414 B/llamada (el resto es del harness: `Drain`, `Range` y `MemoryRequestContext` del host); 100000 items 0.5 B/item.
        BenchmarkDotNet (mascara 97, control estable): Streaming Memory 10 items 14.98 us / 440 B (antes 19.62 us / 1.97 KB) vs UDS 37.38 us / 1040 B; 1000 items 58.71 us / 472 B (antes 91.83 us / 5.79 KB) vs UDS 170.44 us / 1662 B. MemoryConcurrency sin cambios: c1 13.98 us / 655 B, c8 28.59 / 3559, c64 150.80 / 25959 (UDS 38.92 / 52.54 / 208.69 us).

### Estudio: llevar a UDS lo aprendido en Memory (sin cambios de produccion)

- Medicion: `--alloc uds` (1 / 1000 / 100000 items, repeticiones ampliadas para que el muestreo por tipo sea fiable) y `StreamBatchBreakdown` con fila UDS (mascara 513, `--filter *Uds*`).
- Asignacion (B/llamada): 1 item 1052 (~550 los dos iteradores `async` anidados del cliente: `StreamConnection.EnumerateAsync` sobre `MultiplexedChannel.EnumerateAsync`; resto harness + `RequestContext`); 1000 items 3416 (45 % `TaskNode`, 14 % caja de `DispatchAsync`, `ContinueWith` de `inflight`); 100000 items 0.8 B/item (93 % `TaskNode`).
- `TaskNode` = `ResponseChannel._gate.WaitAsync` en `WriteSlowAsync`: el productor choca con el work item del flush diferido, que retiene el candado durante la escritura al socket. Al pasar a async, el handler deja de completar en linea y aparecen las cajas de `DispatchAsync`/`EnumerateAsync` y la continuacion de `inflight` (~500 B por stream).
- Lo que Memory ya no tiene y UDS si: Channel no (UDS ya reutiliza `StreamSink`); CTS enlazado no; iteradores async si; lotes en el servidor por defecto si (Memory siempre agrupa a 16 KB, UDS/TCP con `streamBatch = 0`).
- Tiempo, UDS 1000 int (BDN, us / B): Batch 0 sin coalesce 328 / 1.64 KB; Batch 0 coalesce (defecto actual) 177 / 1.69 KB; Batch 8 134-148; Batch 64 96-100 / 1.5-1.7 KB; Batch 256 89-91 / 1.3-1.4 KB. Memory 59 us / 472 B.
- Conclusion: la palanca de tiempo es el lote en el servidor (~2x); la de asignacion fija, los iteradores del cliente; el `TaskNode` por item baja con lotes (un choque por lote, no por item). Suelo de 10 items (UDS 37 us vs Memory 15 us): ida y vuelta del socket, no asignaciones.
- [x] Lotes por defecto en TCP/UDS (como Memory), conservando el flush cuando el productor va a esperar. `Server.DefaultStreamBatch = int.MaxValue` (lote limitado por 32 KB o por espera del productor); `streamBatch: 0` sigue desactivandolo.
- [x] `StreamConnection.EnumerateAsync` sin iterador propio si el canal ya esta conectado; el iterador `async` solo queda para la primera llamada antes de conectar.
- [x] Enumerador manual en `MultiplexedChannel` (`StreamEnumerable`): un objeto por llamada, perezoso (envio en el primer `MoveNextAsync`), lotes decodificados sin esperar, `MoveNextSlowAsync` con caja pooled solo para envio/espera; si `TryRead` deja el sink armado se espera antes de releer. Test: `UdsTest.TestEarlyExitReenumerateAndReuse` (salida temprana, reenumeracion, sink pooled reutilizado, conexion sana).
- [-] Escritura via `Pipe` + bombeo por conexion para que el flush al socket no retenga el candado del productor: aparcado, el `TaskNode` ya no aparece en `--alloc uds` tras los lotes.
- Resultado `--alloc uds` (B/llamada, antes -> despues): 1 item 1052 -> 553; 1000 items 3416 -> 549; 100000 items 82886 -> 3239 (resto: `byte[]` del pool). Lo que queda a 1 item es harness + `RequestContext` + el propio enumerador.
- Resultado BDN `--fast`, 1000 int (us / B): UDS lote por defecto 89 / 1.01 KB -> 79 / 642 B; TCP 112 / 1.01 KB -> 101 / 670 B; UDS Batch 0 coalesce 120 / 1.97 KB -> 166 / 1.09 KB (tiempo ruidoso en modo rapido). Tests: 94/94.

### Liberacion de la conexion del cliente via proveedor DI
- Contrato de parciales: `PartialContribution.AddDisposer(member, disposability)` (Item3 del resultado; los parciales viejos siguen valiendo). El emisor DI lo encadena al `Dispose`/`DisposeAsync` raiz y emite el proveedor aunque no tenga servicios.
- ClientGenerator registra `__connection` (Memory/UDP: `IDisposable`; TCP/QUIC/Local: `IAsyncDisposable`). Sin `IAsyncDisposable` manual en el cliente.
- Root `Dispose` solo es `virtual` si hay `Scoped` (CS0549 en contenedores sellados).
- DI empaquetado localmente (2.26.283.31, `nuget.config` -> `external/SourceCrafter.DependencyInjection/publish`) hasta publicarlo.
- Test: `ClientDisposalTest` (4 transportes) + `LocalConnectionTest` ajustado.

### Cliente generado: caja async pooled
- [x] El generador emite `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]` en los metodos `ValueTask` raw y de pipelines (ServiceHandlers.Raw/Pipelines). Los contratos `Task` quedan igual (no se pueden poolear). Firmas y semantica sin cambios. Test: `PooledClientBuilderTest`.
- Medicion: `--alloc gen` (FeatureServiceClient, 200k llamadas, B/op antes -> despues): Plain 361 -> 231; Processor 454 -> 309; ClientRetry 363 -> 226; ServerRetry 355 -> 226. La caja `<XAsync>d__N` del cliente desaparece del muestreo; queda la del propio benchmark.
- BDN `FeatureBenchmarks` (ShortRun, Lsl vs AspNetSlim): Plain 63 us / 319 B vs 77 us / 2890 B; Processor 63 us / 365 B vs 77 us / 6131 B; ServerRetry 66 us / 403 B vs 76 us / 2914 B; default job: ClientRetry 38 us / 679 B vs 57 us / 2950 B; SingleFlight 42 us / 5292 B vs 246 us / 46375 B. Tests: 130/131 (falla solo Postgres, sin servidor local).

### Estudio: concurrencia alta y registro de llamadas en curso
- Medicion `--alloc conc` (ComparisonBenchmarks, 256k unarias UDS, c llamadas a la vez; B/op, us/op de throughput): Lsl_Unary c1 153 / 49.3; c8 185 / 29.0; c64 283 / 18.9; c256 366 / 16.4. Raw: 153 / 181 / 277 / 387. Lo que crece es la caja pooled de `SendRequestAsync` (0 -> 22 -> 99 -> 158-183 B/op): se suspende en `_gate.WaitAsync`/`FlushAsync` y el pool se agota. `UnaryAsync`/`GetAsync` siguen en ~0-1 B/op.
- [-] 5.x-3 Fusionar `GetRawAsync`/`UnaryAsync`: descartado, sus cajas no se agotan ni con c=256. La palanca de concurrencia es `SendRequestAsync` (escritura sin suspender: candado/flush), no el anidamiento.
- [x] POC `PendingSlotsPoc` (Benchmarks/Scenarios): `PendingSlots<T>` = array fijo 4096, id global, slot `id & Mask` por CAS, salta slots ocupados, tabla llena -> excepcion; el que entrega compara el id armado. SelfCheck: id viejo tras liberar, slot ocupado por llamada larga, respuesta tardia con mismos bits bajos, tabla llena, 2xCPU hilos sin cruces ni fugas.
- Resultado BDN (registrar+entregar+liberar por llamada): ConcurrentDictionary 40-43 ns / 40 B -> slots 14-16 ns / 0 B (ventanas 1, 64, 1024); 8 hilos en paralelo 175-186 ns / 40 B -> 94-98 ns / 0 B.
- [x] `PendingSlots` en `MultiplexedChannel._pending` (UDS/TCP): array de 4096 slots con id armado (punto 4 de `docs/PerformanceMemoryEnhancements.md`).

### Servidor: handlers asincronos (S1-S3)
- POC `AsyncHandlerPoc` (handler TCP generado que hace `await Task.Yield()`) + `--alloc srv`. Con handlers sincronos estas cajas no aparecian.
- [x] S1: el generador emite caja pooled en `HandleRequestsAsync` del host (no Memory).
- [x] S2+S3: `ServePipeAsync` ya no usa `DispatchAsync` (`async Task`) ni `ConcurrentDictionary<Task,byte>` + `ContinueWith`. El `RequestContext` pooled es la continuacion del `ValueTask` del handler (delegado cacheado) y libera contexto/cuerpo/hueco; `Inflight` = contador por conexion (la lectura cuenta como una) + un TCS por conexion para el vaciado. `DispatchAsync` (solo QUIC, ya awaited) pasa a `ValueTask` pooled.
- Resultado A/B intercalado (3 rondas, B/op): c=1 ~552 -> ~223; c=64 ~1060 -> ~745. Tiempo neutro (ruido de maquina; c=64 algo mejor). Queda: caja del harness, `Scoped`/impl por peticion (DI scoped), y a c=64 `TaskNode`/`WriteSlowAsync`/`SendRequestAsync` (candado de escritura).
- Test: `UdsTest.TestAsyncHandlerCompletionReleasesSlots` (4x MaxInFlight peticiones, exito y fallo tras await). Tests 131/132 (Postgres).
- [x] Ningun handler generado es sincrono, en ningun transporte: `HandleRequestsAsync` siempre `async ValueTask` pooled; Memory pasa a `StartMemoryServerAsync`. Llamada al servicio, etapas, retry, espera del seguidor de [ServerCache] y stream async usan `await` (fuera todos los `GetAwaiter().GetResult()` del camino de la peticion). Las respuestas de Memory (`Return`/`NotFound`/`Fail`) siguen sincronas: escriben en memoria compartida. `StartMemoryServer` (sync) queda para handlers manuales. Test: `MemoryStreamHostTest.StreamsUseYield`. Tests 131/132 (Postgres).
- [x] Asincronia contagiosa en Memory: si algo del camino de la peticion espera (metodo, etapa/procesador, dependencia, retry, cache, stream async), todo el handler es `async ValueTask` pooled con `StartMemoryServerAsync`; si nada espera, handler sync con `StartMemoryServer` (sin maquina de estados). Resto de transportes siempre async. Test: `MemoryStreamHostTest.HandlerIsAsyncOnlyIfSomethingAwaits`. Tests 134/135 (Postgres).
- [x] Metodo por operacion: `HandleRequestsAsync` pasa a `id switch` sin `async`/`try` (FNV-1a intacto) que llama a `__<Op>_<id>(__context, __token)`. La asincronia deja de ser contagiosa: cada operacion es `async` pooled solo si su cuerpo espera; en Memory las sync devuelven `ResponseStatus` y el despachador las envuelve en `new(...)` (si ninguna espera, todo sync con `StartMemoryServer`). Excepciones -> Failed: las convierte el servidor (`Observe`/`DispatchAsync`/`DispatchUdpAsync`/Memory), que ya rodea la llamada; UDP ahora tambien responde Failed a un `OperationCanceledException` que no es de su token. Orden de los `case`: irrelevante en runtime (Roslyn ordena las constantes y emite busqueda binaria). Tests: `MemoryStreamHostTest`, `ServerCacheGeneratorTest`, `RetryAttributeTest`, `MiddlewareGeneratorTest`. Tests 142/143 (Postgres).

### Kestrel como aceptador de conexiones TCP
- [x] Host TCP generado implementa `IConnectionHost.ServeAsync(PipeReader, PipeWriter, token)` sobre `Server.ServePipeAsync` (ahora publico): mismo protocolo en conexiones aceptadas por otro servidor. Kestrel: `listen.Use(_ => c => host.ServeAsync(c.Transport.Input, c.Transport.Output, c.ConnectionClosed))`, sin HTTP en ese puerto. Test: `EndpointSettingsTest.TcpHostServesExternalConnections`; POC: `samples/WinChat` (verificado e2e con cliente TCP generado contra Kestrel).
- [x] POC `test/POC/KestrelPoc` (`dotnet run --project test/POC/KestrelPoc`, termina con OK): unaria, 4 conexiones x 256 concurrentes, stream en orden + abandono del cliente, HTTP en otro puerto del mismo Kestrel, parada con stream abierto.
  - Parada: con solo `ConnectionClosed`, `StopAsync` agota `ShutdownTimeout` (2013 ms con 2 s) porque Kestrel espera a la conexion y luego aborta. Enlazando `ApplicationStopping` al token: 3 ms. `UseLiteSpeedLink` lo enlaza.
  - Coste (unaria secuencial, cliente + host en proceso): Kestrel ~46-52 us vs `StartTcpServer` ~34-35 us; mismos 872 B/llamada. Kestrel anade ~12-17 us por ida y vuelta (su transporte de sockets + scheduling de pipes).
  - `GetRequiredService<T>()` sobre el `IServiceProvider` de ASP.NET Core da SCDI11 (el analizador de SourceCrafter.DependencyInjection lo toma por su contenedor); se usa `GetService(typeof(T))`.
- [x] Servicios del host en el contenedor generado: el host TCP emite `IServiceProvider? HostServices { get; init; }` y `private T HostService<T>()` (via `GetService(Type)`, sin SCDI11). Se registra en la app (`AddSingleton(sp => new XHost { HostServices = sp })`) y `UseLiteSpeedLink<XHost>()` lo toma de `ListenOptions.ApplicationServices`. Los servicios de la app/intrinsecos entran por metodos factoria: `[Singleton<IHostEnvironment>(source: nameof(Env))] static IHostEnvironment Env([Root] XHost h) => h.HostService<IHostEnvironment>();`. Sin HTTP. POC: `KestrelPoc` C1/C2; sample: `WinChat` (`ILogger<ChatHandler>` de la app). Tests 151/152 (Postgres).
  - ponytail (resuelto con `[Hosted<T>]`, que no construye ni libera): un servicio de la app registrado como singleton del contenedor generado lo liberaria tambien este si es `IDisposable` (doble Dispose).
  - [x] `[Hosted<T>]` (SourceCrafter.DependencyInjection, sustituye a `owned`): atributo propio, sin tocar `Singleton`/`Scoped`/`Transient`. Cada resolucion es `(T)(HostServices?.GetService(typeof(T)) ?? throw ...)` inline: sin construir, cachear ni liberar; no cuenta para la disposability; los consumidores no se inlinean en interceptores estaticos (leen la instancia); los ambitos heredan `HostServices` en `CreateScope`; `Task`/`ValueTask` -> SCDI23. El DI emite `HostServices` salvo que el contenedor lo declare; el host TCP de LSL ya no lo emite si hay `[Hosted]`. Probado en el repo DI (`HostedTests`, 180/180). Publicado en DI 2.26.325.281 (trusted publishing de nuget.org via OIDC en ambos repos, sin `NUGET_API_KEY`); LSL y `KestrelPoc` usan ya los paquetes. `WinChat` pasa de metodo factoria (`[Singleton<ILogger<ChatHandler>>(source: ...)]` + `[Root]`) a `[Hosted<ILogger<ChatHandler>>]`, y el host generado ya no emite `HostService<T>()`. E2E: servidor WinChat (Kestrel) + cliente generado (Join + Post) -> "probe joined/left lobby" en el log de la app.
  - [x] Servicios del host con vida por peticion (`DbContext`): `[Hosted<T>(Lifetime.Scoped)]`. `CreateScope()` abre un `AsyncServiceScope` del host junto a cada ambito del contenedor (sin `HostServices` queda vacio y solo falla la resolucion), resuelve de el y lo libera al liberarse (los ambitos pasan a `IAsyncDisposable`); en la raiz lanza `InvalidOperationException`. Coste: un ambito del host por ambito del contenedor aunque la peticion no lo use. Los parciales (LSL) reciben la disposability ya promovida a `AsyncDisposable`: sin eso el host generado abria el ambito por peticion sin `await using` y el ambito del host (y su DbContext) no se liberaba. Pendiente tras el release: check en `KestrelPoc` de que un servicio scoped del host se libera al acabar cada peticion.
  - [x] E2E (antes del release, `KestrelPoc` con el generador y atributos DI del submodulo): `[Hosted<Greeting>]`, `[Hosted<IHostEnvironment>]`, `[Hosted<RequestDb>(Lifetime.Scoped)]` sustituyen a los metodos factoria. G1 instancia distinta por peticion, G2 compartida dentro de la peticion (transient `DbReader`), G3 todas liberadas al acabar su peticion (`await using` del ambito). DI: resolucion directa (`GetRequiredService<RequestDb>()` en el ambito, via `provider.__HostScopeServices`) y consumidor async (`Task<T>`) probados (191/191).
  - Coste medido del ambito del host abierto en `CreateScope` (D, unaria secuencial `Add`, que no usa RequestDb): Kestrel con `HostServices` 1067 B/llamada vs `StartTcpServer` sin `HostServices` 936 B -> ~130 B por peticion por el ambito del host aunque la operacion no lo use.
  - [x] Ambito del host perezoso: se abre al primer `[Hosted(Lifetime.Scoped)]` resuelto en el ambito (`IServiceScope?` + `Interlocked.CompareExchange`; dos primeras resoluciones concurrentes comparten uno y la perdedora libera el suyo, vacio). D: Kestrel 936 B = `StartTcpServer` 936 B (los ~130 B desaparecen). DI `HostedScopedTests`: sin uso no se abre ambito, 16 hilos -> un solo `RequestDb`.
  - [x] Analizador DI: (1) `container.CreateScope().GetRequiredService<T>()` encadenado con `using Microsoft.Extensions.DependencyInjection;` ya no da SCDI11/13 (una invocacion sin enlazar no aporta el tipo de un candidato ajeno como receptor); (2) `GetRequiredService<T>()` sobre un `IServiceProvider` ajeno (el de ASP.NET Core) ya no da SCDI11: una llamada que ningun contenedor reclama con receptor ligado en ejecucion no se reporta. Tests: `HostedScopedTests` (compila con el using), `HostedTests.TheAppServiceProviderIsNotMistakenForTheContainer`.
  - [x] `GetRequiredService<T>()` (raiz o ambito) en un contenedor sin `genericApi: true` (ni `IServiceProvider`) daba SCDI11 "no hay resolver" de un servicio registrado. Causa: ese contenedor no tiene API generica (opt-in), no reclama llamadas y caian en SCDI11. Ahora SCDI26 "'X' has no 'GetRequiredService<T>()': add [ServiceProvider(genericApi: true)]..." (`DesignConstraintTests`, raiz/variable/encadenado). Interceptores se mantienen: `DispatchBenchmark` (DI Benchmarks, formas del codigo generado) -> miembro 0,82 ns vs `Unsafe.As<IProvider<T>>` 1,85 ns (mediana) en instancia cacheada; transient igual (3,14 ns, 24 B). DI 201/201.
  - [x] `GetHostedService<T>()` / `GetHostedRequiredService<T>()` / `GetHostedServices<T>()`: el contenedor (y sus ambitos) implementa `IHostedProvider<T>` (interno, en `Utils.g`) solo por cada `[Hosted<T>]`; un tipo no declarado es CS1929 en compilacion. Los `Scoped` resuelven del ambito del host. Sin `Keyed` (`[Hosted]` no admite clave) ni `Async` (SCDI23). DI 198/198.
  - [x] EF Core real (`KestrelPoc` H/E6, `AddDbContext<NotesDb>(UseNpgsql)` contra PostgreSQL local :5432 (base de `PipelineExamplesTest`, tabla propia `lsl_poc_notes` creada y borrada por el POC) + `[Hosted<NotesDb>(Lifetime.Scoped)]`): datos persistentes con DbContext nuevo por peticion (H1), compartido dentro de la peticion (H2), 64 concurrentes en una conexion = 64 DbContext (H3), peticion fallida -> error en cliente y sus cambios pendientes descartados (H4), 5 SaveChanges seguidos en una peticion (H6), 32 escrituras concurrentes = 40 filas (H7), stream = un DbContext toda su vida y liberado al abandonarlo (H5), stream abierto liberado al parar la app (E6); 107/107 DbContext liberados. Suites: DI 191/191, LSL 151/152 (Postgres), POC 28/28, samples compilan, Chat OK. Singleton que dependa de uno (directo o via transients) -> SCDI24; sin `Microsoft.Extensions.DependencyInjection.Abstractions` -> SCDI25. `HostedScopedTests` (188/188), sin commit.
- [x] Paquete `SourceCrafter.LiteSpeedLink.Kestrel`: `builder.WebHost.UseLiteSpeedLink<THost>(port | IPEndPoint)` con `THost : IHostedLiteSpeedLink, new()` (antes `IConnectionHost`; ahora tambien `HostServices { get; init; }`). Una llamada: singleton de la app con `HostServices = sp`, escucha en Kestrel enlazando `ApplicationStopping`, y libera el host en `IHostedLifecycleService.StoppedAsync` (tras vaciar Kestrel, antes de liberar los servicios de la app; la segunda liberacion es no-op). POC `KestrelPoc` E4/E5; sample `WinChat`. Empaquetado en CI (`dev.yml`).
- [x] TLS via Kestrel: `UseLiteSpeedLink<THost>(endpoint, certificate)` aplica `UseHttps` (solo `SslStream` sobre el socket, sin HTTP) con TLS mutuo como `StartTcpServer` (TLS 1.2, certificado de cliente obligatorio, pinning). El cliente TCP generado acepta `X509Certificate2? certificate = null` (antes no habia forma de usar TLS con el cliente generado). POC `KestrelPoc` F1 (con certificado) / F2 (sin TLS, rechazado).
- [x] Ver `[Hosted<T>]` arriba.
  - DI: un servicio declarado despues de su consumidor da CS7036 en el codigo generado en lugar de un diagnostico SCDI (visto en `KestrelPoc` con `[Singleton<Probe>]` tras `[Scoped<IGreeter, Greeter>]`).

## Seguridad (rama `security/hardening`)

Revision de seguridad local con el skill `claude-security-review` (prompt de Anthropic, modo AUDIT + HARDENING, subagentes de Copilot). 7 hallazgos, 6 confirmados (confianza >= 9/10); descartado: el mTLS TCP/Kestrel acepta "cadena valida o pin", politica documentada (ahora configurable con `Check`). Tests: `SecurityHardeningTest` (13), LSL 165/165 tras los cambios de API (de 152).

- [x] TLS fijado en compilacion: `[ServiceHost|ServiceClient(Tls = TlsMode.Default|None|Server|Mutual, Check = CertificateCheck.ChainOrPinned|Chain|Pinned)]`. El generador emite la firma exacta del certificado (sin parametro / opcional / obligatorio) y pasa las constantes a `Start*`/`As*Connection`. `TlsPolicy` (Core) construye las opciones una vez por servidor/cliente (antes: opciones + lambda por conexion) y el pin compara SHA-256 en la pila (antes `GetCertHashString`: SHA-1 + string por handshake). `IHostedLiteSpeedLink` expone `static abstract Tls/Check` para que Kestrel los lea de `THost`. QUIC + `None` -> SCLSL022. Tests: `ValidatorHonorsTheConfiguredCheck`, `TcpServerModeAuthenticatesOnlyTheHost`, `MutualPinnedTcpIsFixedAtCompileTime`, `ServerChainClientTakesNoCertificate`, `QuicWithoutTlsIsRejected`.
- [x] Hallazgo 1 (HIGH): el cliente QUIC aceptaba cualquier certificado (`=> true`, `cert` ignorado, sin `TargetHost`) y el host QUIC no pedia el del cliente. Ahora QUIC `Default` = TLS mutuo con la validacion de `Check`; el cliente QUIC generado recibe `certificate`. Tests: `QuicRejectsAnUnpinnedHostAndAClientWithoutCertificate`, `QuicClientTakesTheCertificate`; demo `LiteSpeedLink` (QUIC + stream dedicado) OK.
- [x] Hallazgo 2 (HIGH): QUIC sin autenticar con 1000 streams x 16 MB por conexion. Con mTLS por defecto solo entran clientes con certificado; `MaxInboundBidirectionalStreams` 1000 -> 256 (`Server.MaxQuicStreamsPerConnection`; el cliente espera por control de flujo QUIC, sin error). ponytail: el stream dedicado QUIC no tiene plazo propio (lo cubren mTLS + `IdleTimeout`).
- [x] Hallazgo 3 (HIGH): TCP/UDS/Kestrel. El cuerpo se alquila despues de `slots.WaitAsync` (antes: un cuerpo de hasta 16 MB extra por conexion con el limite lleno); `maxConnections` (1024) en TCP/UDS (el exceso espera en el backlog del SO); handshake TLS 10 s (TCP: `WaitAsync`, Kestrel: `TlsHandshakeCallbackOptions.HandshakeTimeout`); trama a medias sin bytes nuevos 30 s (`partialFrameTimeoutMs`; un CTS por conexion, solo si llega una trama partida; rearmar no asigna; una conexion ociosa no expira). Sin presupuesto de bytes por conexion (decision: rendimiento). Tests: `APartialFrameTimesOutButAnIdleConnectionDoesNot`, `TheConnectionCapQueuesExtraConnections`.
- [x] Hallazgo 4 (HIGH): reflexion/amplificacion UDP. Decision: UDP = red de confianza (un limite fijo romperia respuestas normales y un token de retorno anade bytes y un viaje); el `Start` UDP generado expone `maxAmplification` (0 = sin limite). Test: `UdpHostExposesMaxAmplification`; README (Security).
- [x] Hallazgo 5 (MEDIUM): `Local` fuera de Windows usaba `/tmp/<name>.lsl` (ocupable por otro usuario). Ahora `LocalSocket.GetPath`: `$XDG_RUNTIME_DIR` o `<tmp>/lsl-<usuario>` 0700 verificado (falla cerrado si no es privado o es un enlace); socket 0600. Tests: `LocalSocketDirectoryMustBePrivate` (en Linux, CI), `LocalOnLinuxUsesThePrivateSocketPath`.
- [x] Hallazgo 6 (MEDIUM): un corrId repetido con su stream vivo pisaba la entrada: CTS enlazado al token del servidor filtrado por par y cancelacion cruzada. `BeginStream` usa `TryAdd` (el duplicado falla con `Failed`) y `EndStream` solo quita su propio CTS. Test: `ADuplicateStreamIdIsRejected`.
- Coste: `--alloc cmp unary` Lsl_Unary 113 B/op (toda la caja async del propio benchmark; la biblioteca no asigna por llamada); `KestrelPoc` D 936 B/llamada igual que antes; F1/F2 (TLS por Kestrel) OK.
- [x] Cualquier `IHost` (paquete nuevo `SourceCrafter.LiteSpeedLink.Hosting`): `services.AddLiteSpeedLink(new THost.Endpoint { ... })` registra el host como singleton de la app (`HostServices` = sus servicios) y un `IHostedService` que escucha al arrancar (hasta `ApplicationStopping`) y deja de escuchar al parar. Todo transporte. `THost.Endpoint` (struct generado si el proyecto referencia Hosting) con miembros `required` segun transporte y TLS de compilacion (`Port`/`Name`, `Certificate`, `MaxAmplification` en UDP): falta -> CS9035. Ejecucion: certificado `null` en un TLS que lo exige y certificado sin clave privada fallan al arrancar (`TlsPolicy`); con `[JsonSetting]`, `Port`/`Name` opcionales y leidos de la configuracion de la app si esta cargada (`HostSettings`, puerto invalido -> falla el arranque) y si no del contenedor. El generador emite `HostServices` en todo host hospedable (antes solo TCP). Tests: `HostingTest` (11: required por transporte, certificado segun `Tls`, fallback de settings, Generic Host real arranca/responde/para, puerto invalido); POC `KestrelPoc` I1-I3 (Generic Host sin Kestrel con `[Hosted<T>]` de la app).
- [x] Kestrel: se toma su configuracion y su conexion en vez de construirlas. TLS por su adaptador HTTPS (`UseHttps(o => ...)` sobre `ConfigureHttpsDefaults`: certificado, protocolos, `HandshakeTimeout`) con `ClientCertificateMode` y `ClientCertificateValidation` desde `THost.Tls/Check` (validador construido una vez); tope = `Limits.MaxConcurrentConnections`; trama a medias = `Limits.RequestHeadersTimeout` (`IHostedLiteSpeedLink.ServeAsync(..., partialFrameTimeoutMs)`); fin de conexion = `IConnectionLifetimeNotificationFeature.ConnectionClosedRequested` (antes `ApplicationStopping`). POC `KestrelPoc` K1 (MaxConcurrentConnections = 1 rechaza la segunda), K2 (RequestHeadersTimeout 300 ms cierra la trama a medias en 313 ms), E1 parada 3 ms y F1/F2 TLS siguen OK; D 936 B/llamada.
- [x] Revision del diff de `security/hardening` (skill `claude-security-review` en modo DIFF + HARDENING, filtrado de falsos positivos por subagente) + auditoria de dependencias (`dotnet list package --vulnerable --include-transitive` en los 8 proyectos publicados: ninguno vulnerable; el MCP de NuGet falla con el SDK 11 por un conflicto de `NuGet.Frameworks`). Confirmados:
  - (HIGH 9/10) El tope de conexiones nuevo permitia bloquear el host con 1024 conexiones ociosas sin autenticar (TCP plano o `Tls = Server`). Cerrar conexiones ociosas romperia suscripciones legitimas (el cliente generado no reconecta), asi que: `AddressLimiter` (Server, publico) con `maxConnectionsPerAddress` = `MaxConnections / 16` = 64 por defecto; IPv4 por direccion, IPv6 por /64; la conexion sobrante se cierra al aceptarla (TCP) o antes del handshake TLS (middleware de Kestrel, que no tiene limite por direccion). Investigacion del valor: Kestrel, nginx, HAProxy, Redis, RabbitMQ y PostgreSQL no traen limite por direccion; cada cliente generado usa una conexion multiplexada, asi que 64 cubre un NAT de hasta 64 clientes y exige 16 direcciones para llenar el servidor. Tests: `ThePerAddressLimitClosesExtraConnectionsFromTheSameAddress`; POC K3a-c.
  - (MEDIUM 9/10) `LocalSocket.EnsurePrivate` no comprobaba el dueno: otro usuario podia crear antes `<tmp>/lsl-<usuario>` 0700 (DoS, o suplantacion si el host es root). Ahora la propiedad se prueba con `chmod 0700` (solo el dueno puede; sin P/Invoke), despues de rechazar enlaces; root (`Environment.IsPrivilegedProcess`) usa `/var/run/lsl` (solo root crea ahi). Test: `LocalSocketDirectoryMustBePrivate` (propio abierto -> 0700; enlace rechazado; CI Linux).
  - Huecos de garantia corregidos: G1 `client.NoDelay` fuera del try dejaba sin devolver el hueco de conexion; G2 goteo: ritmo minimo por trama a medias como `MinRequestBodyDataRate` de Kestrel (240 B/s, 5 s de gracia; plazo total = gracia + longitud / ritmo desde su primer byte, un temporizador por conexion sin asignar; Kestrel pasa su `MinRequestBodyDataRate`), test `ATrickledFrameIsClosedByTheMinimumDataRate`; G3 un CTS de trama a medias ya vencido se descarta en vez de reutilizarse. G4 (el `Failed` de un id duplicado llega con el id del stream original) solo afecta al par que se porta mal: documentado.
  - Descartado (FALSE_POSITIVE 9/10): `ChainOrPinned` acepta cualquier cadena valida = politica documentada; `Pinned` por defecto seria endurecimiento de valores por defecto (no rompe nada del repo).
  - LSL 178/178; POC OK (D 936 B/llamada).
- [x] Comparativa `dev` (e309944) vs `security/hardening` (1f0c626), mismo equipo, Release, worktree aparte con los mismos submodulos. BDN de publicacion (mascara 6369, filas Lsl + control + streaming + concurrencia) en orden ABBA (dev, rama, rama, dev) para anular la deriva del equipo: el run 1 fue el mas lento y el 4 el mas rapido en casi todas las filas, y las filas del transporte Memory (codigo sin cambios) se mueven igual que TCP/UDS, asi que las diferencias de tiempo son del entorno: ninguna fila Lsl empeora fuera del ruido. Asignaciones con las sondas exactas (`--alloc`, cientos de miles de llamadas), dev -> rama: cmp unary Lsl_Unary 113 -> 113 B, Lsl_UnaryRaw 114 -> 115; cmp Lsl_Stream 244 -> 243; gen (host/cliente generados) Plain 199 -> 199, Processor 275 -> 276, ClientRetry 194 -> 194, ServerRetry 193 -> 194, ServerCacheHit 169 -> 169, CacheKeyHit 266 -> 266; srv AsyncHandler c=1 266-272 -> 268-269, c=64 467-473 -> 470-473; quic (stream por llamada) 6810 -> 6800; quiccmp (nuevo, `QuicComparison` por el pool multiplexado) Lsl_Unary 995 -> 1002, Lsl_Stream 1016 -> 1005; streams por transporte tcp 371 -> 354, uds 396 -> 371, memory 435 -> 432 (mismos tipos; la diferencia es de temporizacion). BDN mostro +320 B en QuicComparison.Lsl_Unary en ambos runs de la rama, pero la sonda exacta (`--alloc quiccmp`, 100k llamadas) da 995 vs 1002 B con los mismos tipos: era si `QuicStream.ReadAsync` completa sincrono. Las filas Feature de BDN varian 60-130 B entre runs de la misma version (438 vs 523 B en ServerRetry dev), por eso cuentan las sondas.
- [x] Memoria por conexion (lo mayor que quedaba: un par sin autenticar en TCP plano podia retener 256 x 16 MB = 4 GB por conexion). Referencias: gRPC limita el mensaje a 4 MB; Kestrel/HTTP2 acota los bytes sin leer por conexion (`InitialConnectionWindowSize`). Sin bytes nuevos en el cable:
  - `[MaxMessageSize(bytes)]` por operacion en compilacion (por defecto `Framing.DefaultMaxMessageSize` = 4 MB; tope 16 MB - corrId - opId, fuera -> SCLSL023). Host: `IRequestHandler.MaxMessageSize(op)` (switch generado `__MaxMessageSize`; sin atributos, constante) comprobado al leer el opId, antes de retener el cuerpo; por encima, corta la conexion. Kestrel acota con `Limits.MaxRequestBodySize`. Cliente: el generado pasa `maxMessageSize: static id => __MaxMessageSize(id)` solo si alguna operacion lo declara (sin atributos, ni delegado ni consulta); `MultiplexedChannel` retira de la cola la peticion que no cabe y falla solo esa llamada. QUIC dedicado: limite comprobado en cuanto llega el opId.
  - Presupuesto por conexion `ByteBudget` (4 MB, `maxBufferedBytes`), como la ventana de conexion de HTTP/2: reserva antes de alquilar y libera con el hueco; por encima deja de leer (back-pressure TCP). Con la conexion vacia admite siempre la siguiente (sin interbloqueo con mensajes mayores que el presupuesto). Coste: dos atomicas por peticion, sin asignar; solo espera (un `SemaphoreSlim` por conexion) una conexion por encima del presupuesto. Una conexion retiene como mucho 4 MB (o un mensaje mayor permitido) en vez de 4 GB.
  - El cuerpo de una trama a medias se copia a su buffer final segun llega (se consume del transporte): sin doble buffer y sin depender del umbral de pausa del transporte. Nota: se supuso que Kestrel (`MaxRequestBufferSize` 1 MB) colgaba mensajes > 1 MB; la prueba A/B con la biblioteca anterior lo desmintio (2 MB pasaban). El test lo reproduce con un `Pipe` de umbral 1 KB.
  - Fallo encontrado al medir (anterior a la rama): `MultiplexedChannel.DisposeAsync` lanzaba `ObjectDisposedException` si el remoto cortaba a mitad de un envio (`StreamConnection` cierra el socket antes y completar el escritor volcaba lo pendiente en el stream liberado); en `KestrelPoc` tumbo el proceso. Liberar ya no lanza. Test `DisposingAClientCutOffMidSendDoesNotThrow` (falla sin el arreglo).
  - Coste por llamada: `--alloc cmp unary` Lsl_Unary 113 B/op (igual; todo la caja del benchmark); `--alloc gen` sin tipos nuevos por llamada; `KestrelPoc` en Release 125 B (Kestrel) / 96 B (TCP) igual que con la biblioteca anterior. En Debug sube 936 -> 952 B: el compilador eleva todos los locales de `SendRequestAsync` a la maquina de estados (solo Debug).
  - Tests: `MessageSizeTest` (10: limite completo y a medias, cuerpo mayor que el umbral del transporte, presupuesto con espera, conexion vacia admite uno mayor, rechazo en el cliente sin perder la conexion, liberar tras corte, generador con y sin atributo, SCLSL023); POC K4a-c. LSL 188/188.
- [x] Plazo de lectura de streams QUIC (dedicados y la espera de sus 8 primeros bytes): antes un stream abierto sin enviar nada, o sin FIN, retenia uno de los 256 huecos de la conexion hasta el `IdleTimeout` de 5 min (que el goteo evita). Mismos plazos que una trama a medias en TCP: sin bytes nuevos 30 s y en total gracia + recibido / ritmo minimo (240 B/s, 5 s); `StartQuicServerAsync` acepta `partialFrameTimeoutMs`, `minDataRate` y `dataRateGraceMs` (tambien los pasa al canal multiplexado, que antes usaba los valores por defecto). Lectura con datos ya disponibles sin asignar; solo la que espera arma un temporizador que la cancela con `CancelPendingRead`, con CTS reutilizados (`TryReset`) y caja pooled. Tambien `InvalidDataException`/`TimeoutException` en el stream (limite de tamano, plazo) cierran el stream limpio en vez de quedar como excepcion no observada. Coste `--alloc quic` (un stream por llamada, A/B x3 contra la version anterior): 6788 -> 6845 B/llamada (+57 B, 0,8 %: estado mayor de las dos maquinas async por stream); sin el pool de CTS eran +221 B. Test `AStalledQuicStreamIsCutByTheReadDeadline` (1 y 8 bytes; sin plazo ambos fallan por timeout). LSL 190/190; demo QUIC (stream dedicado 1 MB) OK.
- [x] Bloqueo con TCP plano (lo que dejaba el limite por direccion: 64 conexiones ociosas sin autenticar por direccion, para siempre). Decision acordada: cierre por inactividad + trama de cierre + reconexion y reintento transparentes en el cliente + aviso en compilacion.
  - Host TCP/UDS: `IdleWatch` (un `Timer` por conexion, sin asignar por lectura: el lector sella un `Stopwatch` y el temporizador comprueba el sello). Al vencer cancela la lectura pendiente; el lector confirma la inactividad (buffer vacio, sin trama a medias, `Inflight.IsIdle`: ni peticiones ni streams abiertos) y solo entonces escribe la trama de cierre `[len=4][corrId=0]` (8 bytes una vez por conexion cerrada, 0 por llamada; el cliente nunca usa el id 0) y cierra; si no, rearma. Como solo el lector despacha, tras confirmar no puede empezar nada nuevo. Valor: 130 s (`Server.IdleTimeoutMs`, `idleTimeoutMs` en `Start*`), el `KeepAliveTimeout` de Kestrel y por debajo de los cortes silenciosos de flujos ociosos (Azure LB 4 min, AWS NLB/NAT 350 s, NAT domesticos ~5 min). Uno menor no encarece el ataque (cualquier peticion valida lo reinicia). Kestrel pasa su `Limits.KeepAliveTimeout` (`IHostedLiteSpeedLink.ServeAsync(..., idleTimeoutMs)`).
  - Cliente: la trama de cierre (semantica GOAWAY: el host solo la manda sin nada en vuelo) retira el canal (`_retired` antes de barrer) y falla lo pendiente con `UnprocessedRequestException` (interna); `GetAsync`/`GetRawAsync`/`SendAsync` reconectan (`_reconnect`, el `GetChannelAsync` de la conexion) y reintentan hasta 3 veces; los streams reintentan mientras no hayan recibido nada. `StreamConnection` usa el canal en la ruta rapida solo si `IsUsable` y si no reconecta (cerrando el anterior). QUIC: `MuxTask` sustituye canales inservibles y `OpenStreamAsync` reinicia la conexion perdida (CAS) y reintenta una vez.
  - SCLSL024 (aviso): host TCP con `Tls = TlsMode.Default`. Riesgo residual documentado: un par que envia peticiones validas nunca esta ocioso (hasta 64 conexiones por direccion); solo TLS mutuo lo cierra. Benchmarks, demo `LiteSpeedLink` y WinChat pasan a `Tls = TlsMode.None` (texto plano intencionado); `KestrelPoc` mantiene `Default` (F usa TLS de Kestrel).
  - Coste (sondas Release, ABBA contra e21b21c): cmp unary Lsl_Unary 113 -> 113 B, Lsl_UnaryRaw 114-115 -> 114; gen Plain 198 -> 196-199, Processor 275 -> 276; quiccmp Lsl_Unary 936-938 -> 926-942; stream tcp items=1 347-362 -> 348-355 (ruido). `KestrelPoc` (Debug) D 952 -> 1016 B: locales del bucle de reintento elevados en la maquina de estados, solo Debug.
  - Tests: `IdleConnectionTest` (10: trama de cierre, stream abierto mantiene la conexion, reconexion tras inactividad, reintento al recibir la trama de cierre en unaria y stream contra un host falso, reconexion cuando el host corta una conexion ociosa, reconexion QUIC, SCLSL024 x4). Sin reconexion/reintento fallan 3. LSL 200/200; `KestrelPoc` OK.
- [x] `Pinned` por defecto (antes `ChainOrPinned`, descartado como falso positivo en la revision del diff): en un host `Mutual`, "cadena valida" acepta el certificado de cliente de cualquier CA en la que confie la maquina (las publicas incluidas), asi que no autentica al par. En el cliente `Chain` si comprueba el nombre del host. `CertificateCheck` reordenado (`Pinned` = 0, el valor sin `Check`); el generador emite el nombre por indice y `CheckChain` sigue siendo 1; parametros por defecto de `Start*`/`As*Connection` a `Pinned`. Kestrel ya fijaba el pin con el certificado de `ConfigureHttpsDefaults` salvo con `Chain`. Sin coste (validador construido una vez). Test `TheDefaultCheckIsPinned` (falla sin el cambio). LSL 201/201; `KestrelPoc` F1/F2 OK.
- [x] UDP: limite de amplificacion por defecto (antes 0 = sin limite, "solo redes de confianza"). Investigacion: RFC 9000 8.1 (QUIC no envia mas de 3x lo recibido hasta validar la direccion) y RFC 9175 2.4 (actualiza CoAP/RFC 7252: un servidor que amplifica a un par sin autenticar DEBERIA mitigarlo; 3x aceptable, contando cabeceras del cable); la validacion de direccion (Echo, cookies DTLS, Retry de QUIC) exige un viaje de ida y vuelta, fuera de las reglas del proyecto; memcached desactivo UDP por defecto tras 2018. Decision: `[ServiceHost(Udp, MaxAmplification = N)]` en compilacion, 3 por defecto, 0 = sin limite explicito, negativo -> SCLSL025; fuera el parametro de `Start` y el miembro de `Endpoint`. Presupuesto en bytes de cable (+28 IPv4 / +48 IPv6 por datagrama, en ambos lados) acumulado por peticion. La respuesta que no cabe no sale: va `ResponseStatus.TooLarge` (nuevo, 5 B sin cuerpo; su coste se reserva al crear el contexto, asi que siempre cabe: 33 B <= 1x la peticion minima de 40) y el cliente falla al momento con un mensaje claro (antes: silencio y timeout; con streams, cuelgue sin token). Un stream se corta en el primer item que no cabe. Sin asignaciones nuevas (aritmetica por envio). Tests: `UdpMaxAmplificationIsFixedAtCompileTime` (x4), `UdpReplyOverTheAmplificationLimitFailsFast` (unaria y stream por el cliente), `TestUdpAmplificationBudget` (suma en el cable <= 3x, aviso incluido; sin la reserva 133 > 120). `ServersTest` UDP (loopback, streams) pasa `maxAmplification: 0`; la demo `LiteSpeedLink` (Udp) cabe en 3x. LSL 205/205.
- [x] Auditoria del transporte Memory (SharedMemory, fuentes enlazadas en Client/Server; skill en modo AUDIT + HARDENING, filtrado por subagente). Modelo: secciones con nombre en el espacio de la sesion y DACL por defecto, asi que solo procesos del mismo usuario e integridad >= la del creador las abren. Descartado (FALSE_POSITIVE 9/10): ocupacion de nombres / sin autenticar el extremo (mismo usuario = sin frontera). Corregidos (aunque no crucen frontera de privilegio, un par defectuoso o malicioso no debe tumbar ni corromper al resto):
  - (10/10) `Frame.Total` del par sin validar: negativo -> `ArgumentOutOfRangeException` en el hilo lector -> proceso terminado; ~2 GB por mensaje y `_partial` sin limite ni desalojo. Ahora: `Total` en [0, 16 MB]; un mensaje en ensamblado por escritor (cada escritor envia el mensaje entero bajo su gate), otro id o longitud del mismo escritor desaloja el anterior; 64 MB en total por extremo (el exceso se descarta: su llamador agota su timeout). `ReadLoop` no deja escapar excepciones (anillo corrupto: reintenta con 1 ms); `GetPeer` captura cualquier fallo al abrir el anillo del cliente.
  - (7/10) Geometria y `Node.Offset` leidos de la memoria compartida y usados como aritmetica de punteros (escritura/lectura en cualquier direccion del otro proceso). Ahora el desplazamiento de datos se calcula del indice (ya acotado) y la geometria local; al abrir, la geometria del par debe caber en la vista mapeada (`InvalidDataException`, un `IOException`).
  - Coste: una comparacion por paquete y una multiplicacion por nodo, sin asignar; `--alloc memory` (ABBA) 433-435 -> 414-433 B (ruido).
  - Tests (SharedMemory.Tests, `InternalsVisibleTo` solo para su propio proyecto de tests): `ARingLayoutLargerThanTheSectionIsRejected`, `NodeOffsetsInSharedMemoryAreIgnored`, `MalformedFramesDoNotTakeTheHostDown`; sin el arreglo: sin excepcion, `AccessViolationException` y proceso terminado. SharedMemory 54/54, LSL 205/205.
- [x] Auditoria del generador DI (submodulo, commit 938e021, pendiente de publicar; LSL lo toma de NuGet). Confirmados 9/10:
  - Interceptores con clave: la clave solo se leia de un literal o un identificador const; `Keys.Secure`, una variable o un parametro pasaban como "" y la llamada devolvia el registro sin clave (y la plural sin clave registrada, todos los del tipo). Ahora la clave se toma con `GetConstantValue` (literal, const, miembro const, nameof); si no es constante la llamada no se intercepta y la resuelve el `switch (key)` de la API generica; la plural "todos" solo sin clave. Tests `KeyedInterceptorTests` (en ejecucion: literal, miembro const y variable dan la implementacion de su clave; clave constante no registrada -> SCDI11). Sin el arreglo el proyecto de tests ni compila (CS9144: interceptor de la llamada con clave y la sin clave fusionados).
  - Singleton construido con el primer ambito que lo pide (el getter llamaba a `__Create_x()` sobre `this`, campo estatico): retenia las dependencias scoped de ese ambito, liberadas al cerrarse y compartidas por todos. Primer intento (SCDI27, error para todo singleton -> scoped) descartado por diseno: el usuario define que un singleton vive en el ambito raiz y sus scoped son las instancias del ambito raiz (como Microsoft DI resuelve singletons; retenerlas es decision del usuario, sin validacion de ambitos). Arreglo (DI d2a6533): el camino lento de un singleton empieza con `if (!ReferenceEquals(this, Root)) return Root.__Create_x();`; el getter no cambia (lectura de campo), sin asignaciones ni grafo nuevo; solo la primera resolucion paga una llamada virtual. SCDI24 se propaga tambien a traves de consumidores Scoped (la raiz no tiene ambito del host: singleton -> scoped -> `[Hosted(Scoped)]` fallaria al construirse). Todo contenedor expone `Root` (sin ambitos = `this`). Tests `RootScopeSingletonTests` (en ejecucion: singleton resuelto primero desde un ambito recibe el `Session` del ambito raiz, directo, via transient y el propio contenedor; cerrar ese ambito no lo libera; SCDI24 via scoped). Sin el traspaso falla.
  - Validacion: DI 207/207; LSL contra un paquete local 2.26.999.2 (Directory.Build.targets temporal, ya borrado): solucion compila, LSL 206/206, `KestrelPoc` OK.
- [x] Revision final del diff `dev...security/hardening` (con submodulos; skill en modo DIFF + HARDENING): sin hallazgos >= 0,8 (comprobado: el reintento solo repite lo que el host no despacho, QUIC solo reintenta la apertura del stream, aritmetica en long, TLS falla cerrado, ningun hilo deja escapar excepciones). Dos limites por debajo del umbral:
  - Corregido: sin `XDG_RUNTIME_DIR`, `<tmp>/lsl-<usuario>` dejaba una carrera entre comprobar el enlace y el `chmod` (otro usuario es dueno de la entrada en `/tmp` y puede cambiarla entre las dos y devolverla antes de la comprobacion final, asi que recomprobar no la cierra). Ahora `~/.lsl` (nadie mas crea entradas en el home); sin `HOME` falla con un mensaje claro. Check en `LocalSocketDirectoryMustBePrivate` (Linux).
  - Sin cambio en la revision final, corregido despues: el listener QUIC no tenia tope global ni por direccion (anterior a la rama). Con mTLS + `Pinned` solo afectaba al coste del handshake, pero con `Tls = Server` (clientes sin autenticar) cualquiera podia abrir conexiones sin limite, cada una con 256 streams y su presupuesto de 4 MB: el mismo bloqueo ya cerrado en TCP. Ahora `StartQuicServerAsync` tiene `maxConnections` (1024) y `maxConnectionsPerAddress` (64, `AddressLimiter`) como TCP: lleno no acepta (MsQuic rechaza al llenarse su cola), la conexion sobrante de una direccion se cierra al aceptarla, antes de abrir streams. Coste solo al abrir/cerrar conexion (`--alloc quiccmp` Lsl_Unary 926 B, Lsl_Stream 1010 B, igual). Test `QuicHasThePerAddressLimitToo` (sin el tope falla). `TestQuicCallDoesNotThrowInternally` cuenta las `QuicException` de todo el proceso y fallaba en paralelo con el nuevo (que las provoca a proposito): pasa a su propia coleccion sin paralelismo. LSL 206/206 (x3).

## Tramas compactas (rama `perf/compact-framing`, sobre `security/hardening`)

Que capas inferiores pueden sustituir campos de LSL: QUIC no conserva limites de mensaje ni identidad de llamada, asi que en el stream multiplexado `len` y `corrId` siguen; si puede delimitar con el FIN la respuesta unaria de un stream dedicado. El resto del ahorro sale de los campos propios. Formato en el README (*Wire format*).

- [x] `opId` 8 -> 4 bytes en el cable (la API sigue en `long`): FNV-1a de 32 bits; el host rechaza en compilacion dos operaciones con el mismo id o con el de la marca del stream multiplexado QUIC (`int.MinValue`): **SCLSL026** (antes el `switch` duplicado daba CS8510 en codigo generado). `MaxMessageSize` sube 4 B. Test `ColludingOperationIdsAreRejectedAtCompileTime`: lee del host generado la firma que se hashea, busca por cumpleanos dos nombres que colisionan (~65 mil intentos) y comprueba SCLSL026.
- [x] `len` LEB128 de 1 (< 128), 2 (< 16 KB) o 4 bytes (4 = forma rellenada, aceptada al leer). Host (`FrameWriter` sobre `PipeWriter`): la trama se escribe sobre el hueco libre del segmento sin confirmarla y al cerrar se acorta la longitud desplazando el cuerpo (como mucho 16 KB); si el serializador necesita mas que ese hueco, se confirma lo escrito y queda la forma de 4 bytes. Cliente (`ArrayBufferWriter` contiguo): igual con `Framing.CompleteFrame` + `ResetWrittenCount`/`Advance`. Lectores (`TryReadLength`, trama a medias del host) y tramas de control (cierre y cancelacion: 8 -> 5 B). Tests `TheLengthTakesOneTwoOrFourBytes`, `AnInvalidOrIncompleteLengthIsDetected`, `TheHostWritesTheShortestLengthItCan`, `TheClientWritesTheShortestLengthItCan` (1/2/4 bytes por tamano de cuerpo).
- [x] QUIC dedicado: la respuesta unaria de exito va `[Success][cuerpo]` + FIN, sin longitud. Sin flag nuevo: en el canal sin correlacion, Success sin flush diferido solo lo escriben `ReturnAsync`/`ReturnRawAsync` (los items de stream siempre difieren). Errores y streams siguen enmarcados; una trama nunca empieza por 0 (su longitud es >= 1), asi que el cliente distingue por el primer byte. El FIN ya lo enviaba el host al terminar la peticion (`QuicStream.DisposeAsync` completa la escritura). Test `ADedicatedQuicUnaryResponseHasNoLength` (bytes exactos con un stream QUIC crudo + cliente generado con exito y fallo).
- Por llamada unaria (sin contar QUIC/UDP), peticion/respuesta: multiplexado 16/9 -> 9/6 B; QUIC dedicado 8/5 -> 4/1 B; UDP 16/5 -> 12/5 B.
- Negativo: con `LengthSize` fijo en 4, `Unframed` en false y sin la comprobacion de colisiones fallan los 10 casos nuevos que dependen de ello (los de 4 bytes siguen pasando).
- Encontrado al probar (anterior): `MultiplexedChannel.DisposeAsync` dejaba escapar `AuthenticationException` cuando QUIC rechazaba el certificado con escrituras pendientes (intermitente en `QuicRejectsAnUnpinnedHostAndAClientWithoutCertificate`); ahora se ignora como `IOException`.
- LSL 221/221, `KestrelPoc` OK, demo OK (incluida la descarga de 1 MB por stream dedicado, ahora sin longitud).
- [x] Medicion contra `security/hardening` (915b054), Release, worktree aparte con los mismos submodulos (los commits locales de SharedMemory/DI traidos del checkout principal):
  - Sondas exactas (`--alloc`, ABBA): identicas. cmp unary Lsl_Unary 113 -> 113 B, Lsl_UnaryRaw 114 -> 114-115; cmp Lsl_Stream 242-244 -> 243; gen Plain 198-199 -> 199, Processor 275, ClientRetry 194, ServerRetry 193 -> 193-194, ServerCacheHit 169, CacheKeyHit 266; quiccmp Lsl_Unary 985-1004 -> 977-985, Lsl_Stream 1018-1025 -> 1007-1010; quic (stream por llamada) 7082-7083 -> 7082-7084; streams items=1 tcp 356-357 -> 357-365, uds 371-402 -> 371-385, memory 429-434 -> 432-433. Ninguna asignacion nueva: acortar la longitud reescribe bytes ya escritos.
  - BDN de publicacion (mascara 6369, 3 corridas base y 2 compactas): las filas que no cambian (ASP.NET, gRPC, Memory) se mueven entre -22 % y +20 % de una corrida a otra, y la misma version varia un 17 % entre sus propias corridas (Lsl_Plain base 42,6-49,9 us); las filas Lsl compactas caen dentro de ese rango. Asignado por BDN sin diferencias fuera de su ruido de 60-130 B.
  - Latencia exacta (`--alloc cmplat`, 10 corridas alternadas): Lsl_Unary p50 base 26,5-27,1 / compacta 27,1-28,3 us en la primera tanda y base 36,9-37,2 / compacta 35,9-39,1 us en la segunda (el equipo se ralentizo un 35 % entre tandas); una corrida compacta mas rapida que las dos base: dentro de la deriva del equipo, sin cambio medible. En loopback el ancho de banda no limita, asi que el ahorro de bytes no se ve en tiempo; cuenta en redes reales (menos bytes por llamada, mas llamadas por paquete).
  - CSV y logs de BDN: carpeta de la sesion `files/bdn-compact` (no versionada).
  - Encontrado al medir: la verificacion semantica del banco (`SemanticCheck`, `Payloads.OpId`, PoC TCP) asumia opId de 8 bytes y longitud de 4 y abortaba; actualizada (opId de 32 bits, tramas con longitud LEB128 de 1 y 4 bytes).

## Skills para agentes (agentskills.io), en los repos y dentro de los paquetes

- [x] Por tarea, no por paquete: DI `sourcecrafter-dependency-injection` y `sourcecrafter-di-configuration` (submodulo, `.github/skills`, en los 4 paquetes DI); LSL `litespeedlink-services`, `litespeedlink-hosting`, `litespeedlink-security` (`.github/skills`, en los 8 paquetes via `Package.props`). En el paquete: `skills/<nombre>/SKILL.md` (convencion de dotnet-inspect / dotnet-package-skills).
- [x] Metodo (TDD de skills): tres escenarios de negocio con agentes nuevos que solo tienen los paquetes publicados (sin fuente). Linea base sin skills, luego con skills (copiadas en `.agents/skills`):
  - DI (contenedor, claves en ejecucion, ambitos, appsettings): ~45 llamadas, ~12 compilaciones, 4 clases de error (CS0012, sin generacion, CS0103 `Settings`, SCDIE00/SCDI03/SCDI08 con claves) y apaño de directorio actual -> ~8 llamadas, 2 compilaciones, 0 errores, configuracion leida desde otra carpeta.
  - LSL (contrato, validacion en host, TCP mTLS anclado): ~25 llamadas, 3 compilaciones, CS0542 (cliente `InventoryClient`) y suposicion falsa sobre la excepcion de rechazo -> ~12 llamadas, 2 compilaciones, 0 errores.
  - Hosting (Kestrel + HTTP + servicios de la app, scoped por peticion): ~30 llamadas, 4 compilaciones, CS0616/CS0246/CS0103 (namespaces), CS0542, listeners HTTP sustituidos -> ~10 llamadas, 2 compilaciones, 0 errores.
  - Refactor con lo que los agentes echaron en falta (todo ya ejecutado por ellos): ejemplo de processor, mensaje exacto del rechazo, llamadas del cliente, cliente con el mismo `Tls`, namespace de `UseLiteSpeedLink`, miembros en los ambitos, el contenedor no libera ambitos abiertos, `[assembly:]` con top-level statements.
- [x] Defectos de los paquetes encontrados por la linea base, corregidos en DI 2.26.329.330 antes de las skills:
  - `MsConfiguration.Metadata` no declaraba su dependencia de `Metadata` (`PrivateAssets=all`): CS0012 en el consumidor.
  - `MsConfiguration` no instalaba nada mas: sin el generador principal no se generaba nada (sin aviso), y el codigo emitido usaba `Microsoft.Extensions.Configuration.Json`/`.Binder` sin declararlos. Ahora depende de ellos (referencias de proyecto con alias para que sus tipos no entren en ambito; `ReferenceOutputAssembly=false` no llega al grafo de restore y pack la omite) y el principal depende de `Metadata`: instalar un solo paquete basta. README: ya no dice "en lugar del principal".
  - Interfaz registrada sin implementacion ni fabrica (`[Singleton<IPaymentGateway>("card")]`): el generador emitia `new IFoo()` (CS0144 en codigo generado) o nada; ahora SCDI06 con un mensaje que dice que hacer. Test `ConsumerMistakeTests` (4 casos, fallan sin el arreglo).
  - `appsettings` se resolvia contra el directorio actual (`Path.GetFullPath`): lanzada desde otra carpeta o como servicio, configuracion vacia sin error (el fichero es opcional). Ahora `AppContext.BaseDirectory`. Test `TheSettingsFileIsResolvedNextToTheApplication`.
  - Comprobado con un consumidor que solo referencia `SourceCrafter.DependencyInjection.MsConfiguration`: compila sin avisos y lee la configuracion ejecutado desde `%TEMP%`.
- LSL: generadores con `SourceCrafter.DependencyInjection.Metadata` `ExcludeAssets=compile;runtime` (compilan las fuentes de los atributos como internas; sin ello CS0436 al depender el generador DI de `Metadata`). DI 212/212, LSL 221/221, `KestrelPoc` y demo OK.

## Resultados vigentes (BDN `--fast`, mascara 6369, .NET 10.0.12, i9-14900HX)

Control estable (campos ~0,55-0,60 ns, alloc 3,0-3,2 ns). Tiempos orientativos; asignaciones deterministas. Las secciones anteriores conservan las cifras de su momento.

- **Comparison (TCP)**, us / B:

  | Escenario | Lsl | AspNetSlim | AspNet | gRPC / gRPC slim |
  |---|---|---|---|---|
  | Unary | 33,7 / 390 (raw 35,6 / 405) | 47,6 / 2798 | 66,0 / 5585 | 86,2 / 7140 \| 114,8 / 7114 |
  | Stream 1000 int | 60,0 / 457 (per item 60,4 / 466) | 211,8 / 4957 | 354,5 / 64299 | 417,6 / 71491 \| 411,3 / 71480 |
  | Stream 1000 int, batched | 67,0 / 947 | 81,9 / 3067 | — | — |

  Los lotes por defecto en TCP/UDS cierran el hueco del stream simple (196 -> 60 us) y bajan la asignacion de 3,3 KB a ~460 B.
- **QuicComparison (HTTP/3)**: Unary 69,9 us / 1,53 KB vs 156,8 / 30,52 KB; Stream 273,1 / 9,38 KB vs 610,4 / 32,8 KB; Batched 157,8 / 7,58 KB vs 191,9 / 9,94 KB.
- **QuicOverhead**: RawReused 65,8 us / 494 B (suelo), RawStream 91,5 / 3056, RawPipes 97,2 / 4300, Lsl 98,1 / 6875, LslUnary 100,1 / 6416, LslUnaryPooled 68,2 / 1585.
- **Streaming** (10 / 1000 items, us / B): Memory 12,8 / 453 - 46,5 / 471; UDS 27,9 / 541 - 57,6 / 563; TCP 34,1 / 559 - 65,0 / 572; QUIC 104,8 / 6906 - 270,4 / 9527.
- **MemoryConcurrency** (Memory vs UDS, us / B): c1 16,5 / 687 vs 32,2 / 749; c8 25,6 / 3559 vs 46,0 / 6291; c64 134,3 / 25959 vs 201,7 / 61641.
- **Features** (Lsl vs AspNetSlim, us / B): Plain 35,7 / 559 vs 40,8 / 2946; Processor 33,7 / 757 vs 48,1 / 6186; ClientRetry 34,3 / 667 vs 48,5 / 2951; ServerRetry 34,3 / 516 vs 46,0 / 2974; ClientCacheHit 14,6 ns / 72 vs 46,9 / 4270; ServerCacheHit 40,4 / 554 vs 48,3 / 4271; CacheKeyHit 41,0 / 623 vs 33,2 / 4411; SingleFlight 37,0 / 5455 vs 249,2 / 46342.
- **Aciertos de cache de servidor** (`--alloc lat`, intercalado, p50 us, flush diferido -> en linea en `ReturnRawAsync`): ServerCacheHit 60,7 -> 39,9; CacheKeyHit 61,0 -> 40,4; Plain 38,8 -> 39,3. Slim con OutputCache (no afectado, solo variacion): ServerCacheHit 65,0 -> 61,3; CacheKeyHit 65,0 -> 62,0. BDN: 508 / 511 B frente a 4,2-4,4 KB de slim. Detalle en `docs/PerformanceMemoryEnhancements.md` #3.
- **QuicPoolContentionPoc** (`--quicpool 64`, latencia de las pequenas, media / p50 / p99 us): sin grande 87 / 86 / 132; pool 261 / 243 / 464; stream 232 / 220 / 450. Sin cambio de conclusion (HOL relevante desde ~256 KB).
- **Streams QUIC por el pool** (Enhancements #2, reabre SourceGenStudy #11): `EnumerateAsync` usa el pool multiplexado; `[DedicatedStream]` en un stream lo saca a stream propio. Stream 1 item 135,8 us / 6873 B -> 74,6 / 1372; Streaming QUIC 10 items 78,8 / 1491, 1000 items 136,5 / 1513. Test `DedicatedStreamTest.StreamsRunOnPoolAndDedicated`.

## Fugas de memoria (`--leak`, LeakProbe) - sin fugas no intencionales
- Sonda: `dotnet run -c Release --project LiteSpeedLink.Benchmarks -- --leak [escenario] [rondas]`; GC completo por ronda, heap retenido, handles y `WeakReference` a conexiones dispuestas. `LSL_LEAK_DUMP=dir` vuelca el heap en la ronda 3 y la ultima (diff con `dotnet-dump analyze` + `dumpheap -stat`).
- 40-60 rondas: conexiones dispuestas vivas 0/8000 (UDS, TCP), 0/2000 (QUIC), 0/500 (Memory); handles estables. Pendientes residuales 1-4 KB/ronda sin tipo de LSL que crezca en cuenta (`UnarySink` 64, `RequestContext` 256, `MultiplexedChannel` 1 iguales en ronda 3 y 60): `ThreadLocal` de `ConcurrentBag`/`ArrayPool` por hilo nuevo del pool.
- Retencion intencional (acotada por el pico, no por el tiempo): pools `ConcurrentBag` de `UnarySink`/`StreamSink` por conexion, `RequestContext` y lotes de 32 KB de stream en el servidor (estaticos), `StreamEnumerator` por tipo, `_queued`/`_spare` por conexion (capacidad del mayor lote), caches con `capacity`.
estable tras la ronda 3, no crece. Resuelto: trama de cancelacion cliente -> host en TCP/UDS (ver *Pendientes*).

## Punto 8: metodo de medicion
- README rehecho con el arnes de publicacion (sin `--fast`), mascara 6305 con Control; control agrupado (0,63-0,71 ns / 2,88-2,90 ns). Unaria TCP 44 us / 354 B vs slim 58 us / 2,7 KB; stream QUIC 118 us / 1,4 KB vs 616 us / 31 KB.
- `CacheKeyHit` validado: ambos lados viajan y aciertan en host con clave solo `sku` (`[CacheKey]` vs `VaryByQuery`); GlobalSetup lo verifica con `note` distinta. `--fast` queda solo para asignaciones.

## Pendientes
- [x] Trama de cancelacion cliente -> host en TCP/UDS (ver *Observacion, no fuga* de la sonda de fugas). `DisposeAsync` de un stream sin `StreamEnd` encola `[len=4][corrId]` (8 B, sin opId: una peticion siempre lleva 8 B de opId) y la vacia best-effort. El servidor la distingue por contenido vacio y cancela el CTS del stream (enlazado al token de conexion, uno por stream en curso, reciclado con `TryReset`); el productor para y no se envia respuesta final. QUIC dedicado (sin correlacion) no la usa. Limite: si llega antes de que el handler entre en `EnumerateAsync`, se ignora y el productor sigue como antes. Test: `StreamCancellationTest` (TCP/UDS x lote/item a item, la conexion sigue sana). Tests 150/151 (Postgres).
  - Coste: A/B con y sin CTS por stream (mascara 33, 2 pares): diferencias en ambos sentidos dentro del ruido, mismas asignaciones. Comparison (129): stream TCP 53 us / 419 B vs slim 224 us / 4,4 KB; unaria 39 us / 353 B vs 49 us / 2,8 KB.
  - Abierto: Streaming (33) sale mas alto que en el README anterior en todos los transportes, Memory incluido (TCP 78-80 us vs 61, QUIC 153-191 vs 114); sin aislar (comparar contra `1b224b9` en worktree o perfilar TCP 1000).
- [ ] L2 distribuido (Redis, SQL): para despues; configuracion opt-in, nunca por defecto.
