# Estudio: ramas en runtime que puede resolver el generador

Objetivo: todo lo que se conoce al compilar (contrato, transporte, forma del metodo, tipos) debe decidirlo el generador, no un `if` por llamada.

**Premisa de red:** el objetivo es perder menos paquetes y que, si se pierden, sea un problema de la red y no de cliente/servidor. Los PoC entran primero como **opción** (opt-in), no como reemplazo; solo pasan a defecto si el benchmark lo justifica. Predecibilidad y rendimiento antes que adivinar en runtime.
Evidencia: codigo en `SourceCrafter.LiteSpeedLink.Server/Client`, salida en `LiteSpeedLink/obj/gen/SourceCrafter.DependencyInjection/ServiceProviders/*.host.g.cs` y `*.client.g.cs`, y los benchmarks de `PLAN.md`.

## Generadores

- Dos ensamblados parciales, descubiertos por el DI por prefijo `SourceCrafter.DependencyInjection.Partial`: `...LiteSpeedLink.Server` (`ServiceHostGenerator`, proyecto `ServerGenerator`) y `...LiteSpeedLink.Client` (`ServiceClientGenerator`, proyecto `ClientGenerator`, `CLIENT_PARTIAL`).
- Ambos heredan de la base abstracta `ServiceHandlersGenerator` (`AnalyzeContainer`, `GetServiceId`); lo común (raw, pipelines, helpers) se enlaza desde `Helpers/Generator.props`. Cada lado solo compila su emisión (`GenerateServiceHost.cs` / `GenerateServiceClient.cs`).
- Nombres de clase distintos: el registro de parciales desduplica por nombre de tipo. Test: `GeneratorHarness` carga los dos.

## Qué emite hoy el generador

- **Host:** `HandleRequestsAsync(long id, ctx, token)` con `switch(id)` sobre el hash FNV-1a de la firma. Cada caso crea un scope, lee los argumentos y llama al servicio.
  - **Argumentos:** en operaciones unarias los lee con un lector raw emitido (`__ReqM{id}(ctx.Body.Span)`); en las demás, con `ctx.Get<T>()`.
  - **Respuesta:** `ctx.Return(...)`, `ReturnAsync`, o `EnumerateAsync<T, TPolicy>(___result)` con la política de lote como tipo (#1).
  - **Transporte:** TCP, UDS y QUIC reciben `new __Handler(provider)`, un `readonly struct : IRequestHandler` (#3); Memory y UDP siguen con el delegado `RequestHandler`.
  - **Memory:** `MemChannel` (fuentes del fork SharedMemory enlazadas `internal` con `SG_CONTEXT`, sin `RpcBuffer`); la respuesta se escribe con `IBufferWriter<byte>` directo al nodo compartido y los streams del cliente van por `MemPull` (sin `Channel`).
- **Cliente:** clase por interfaz atada a la conexión concreta (`TcpConnection`, `QuicConnection`...).
  - **Operaciones unarias, sin retorno (void o `Task`) y con pipelines:** van por raw (#12), con `GetRaw`/`GetRawAsync(opId, __Req{n}(...), token).ConfigureAwait(false)` y el lector `__Res{n}`.
  - **Streams:** siguen por la API tipada (`EnumerateAsync<TIn,TOut>`): raw diferido por POC (#12, 6d).
  - **Miembros sync en transportes de red:** son extensiones sync-over-async de `ClientExtensions`; solo Memory es sync nativo.
- **Pipelines:** una etapa por sentencia, `if (etapa is not (Success, TOut x)) throw new PipelineRejectedException(...)`, de dentro afuera; `var` solo si `TOut` es `Nullable<T>` (CS8116).

El generador decide la forma de cada operación (Get, Enumerate o Send), el transporte, los tipos, el lector y el escritor de parámetros, y la política de lote. Lo que aún se decide en runtime está en las filas pendientes o diferidas del inventario.

## Inventario (ordenado por valor esperado)

| # | Rama / coste en runtime | Dónde | Dato conocido al compilar | Qué emitir | Beneficio esperado | Evidencia |
|---|---|---|---|---|---|---|
| 1 | Stream: `Func` + clausura y política de lote leída en runtime | `Server.Pipes.EnumerateAsync` | Forma del stream (`IEnumerable`/`IAsyncEnumerable`) y `[Stream(Batch, MaxDelayMs)]` | `EnumerateAsync<T, TPolicy>(___result)` con `struct __Policy_B{n}_D{ms}` (uno por configuración) o `Unbatched` | Medio | **Hecho**. POC stream −44 %, 232 B → 0; política como tipo 15,1 → 3,0 µs sin plazo (con plazo, igual: manda el reloj). Contrato `IStreams` (8 combinaciones); test `StreamPolicyTypeTest` |
| 2 | `GetAwaiter().GetResult()` en pipelines | `ServiceHandlers.Pipelines.cs` | Si el contexto es async | Proponía `await` también en ámbitos síncronos: **incorrecto** (no cabe `await`) | — | **Descartado como propuesta**: `await` ya se emitía donde el contexto es async (`canAwait`); en contextos síncronos se mantiene `GetAwaiter().GetResult()` |
| 3 | Dispatch por delegado `RequestHandler` | `Server.StartXxx` | El host concreto | `readonly struct __Handler : IRequestHandler`; servidores genéricos `where THandler : struct` | Medio-bajo: 1 llamada indirecta menos por petición | **Hecho** en TCP, UDS y QUIC (`DelegateRequestHandler` adapta el delegado para llamadas manuales). Memory y UDP siguen con delegado. Test `UdsTest` (struct `Doubler`) |
| 4 | `switch` FNV sobre `long` | Host generado | Conjunto finito de operaciones | Índice denso 0..N-1 en lugar del hash | Bytes: −6 por petición (economía de datos); CPU: marginal | **Descartado** por POC (`DenseDispatchPoc`, 1000 llamadas aleatorias): denso 3,94 vs FNV 2,26 µs (N=10, +74 %) y 3,88 vs 3,11 µs (N=100, +25 %). El `switch` FNV (búsqueda binaria) predice mejor que la tabla de saltos indirecta. Memoria: 0 B en ambos; solo ahorra 7 B de cable |
| 5 | Opciones del servidor leídas en runtime (TLS, lote por defecto) aunque el host no las use | `Server.StartXxx` | Transporte y opciones del host | Sobrecargas emitidas por combinación | Bajo; facilita el recorte de código no usado | **Diferido**: sin ganancia medible |
| 6 | Framing: `bool correlated` por trama | `Internals/Framing.cs` | Transporte (con o sin corrId) | Estrategia estática (`Framing<Correlated>`); el JIT especializa y elimina la rama | Bajo; se ejecuta en cada trama | **Diferido**: POC `SourceGenPocBenchmarks.Framing_*` −22 % (1,35 → 1,05 µs / 1000 tramas), irrelevante frente a la red; solo si un perfil lo señala |
| 7 | Canal del cliente preparado para streams aunque el contrato sea solo unario | `MultiplexedChannel` | Contrato sin `Enumerate*` | Canal solo unario | Bajo; en contratos solo unarios, menos código | **Diferido**: #8 ya quitó el `Channel` de las unarias; solo si un perfil lo señala |
| 8 | Unaria entregada por `Channel` | `MultiplexedChannel` | Forma unaria | `IValueTaskSource` reutilizable | Alto en asignación | **Hecho**: `UnarySink` (`ManualResetValueTaskSourceCore`) en pool por conexión; streams siguen con canal. `Lsl_Unary` 41 µs / 2,07 KB → 39 µs / 903 B (−56 % asignado) |
| 9 | `[CallerMemberName] name` en `IConnection`/`IAsyncConnection` | Client | Nombre del método | Quitar el parámetro | Limpieza de API | **Descartado**: constante de compilación, sin coste en runtime |
| 10 | `DynamicallyAccessedMembers` en genéricos | Client | — | Quitarlo | Ninguno | **Descartado**: lo impone MemoryPack (trimming, PLAN 1.2); no tocar |
| 11 | `EnumerateAsync` abre un stream QUIC por llamada | `Client.QuicConnection` | Operación de stream | Reutilizar stream (pool, como unarias) | Menos coste fijo por stream (suelo medido: stream crudo 86 µs frente a RPC 110 µs tras el arreglo del FIN) | **Descartado como defecto**: se mantiene stream propio por `EnumerateAsync` para aislar el bloqueo de cabeza de línea. El lote por política (#1) ya aplica en QUIC (−35 %). Reabrir solo si QUIC pasa a transporte principal |
| 12 | Parámetros y respuesta por `ctx.Get<T>()`/`Serialize` genéricos | Client/Host | Tipos de cada parámetro y del retorno | Escritor `__Req{n}` y lector `__Res{n}`/`__ReqM{id}` por operación (raw) | Medio en unarias | **Hecho** en unarias, `void`/`Task` y pipelines (`ParamEncodingPoc`, `RawCapacityTest`, POC `IAuth.TouchAsync`); test `UdsTest.TestRawRoundtrip`. Streams **diferidos** por POC (`StreamRawDecodePoc`): un `MemoryPackReader` con métodos específicos frente a `Deserialize(span, ref item)` por item cuesta +125 % con 1 item (alquiler del estado, ~15 ns) y ahorra −29 % (strings) / −17 % (packables) con 64, ~3,6 ns/item: ~2 % de `Lsl_Stream` (196 µs), a cambio de una API `EnumerateRawAsync` en 5 transportes. Reabrir si un perfil de stream señala la decodificación |
| 13 | Buffer de respuesta de tamaño variable | Host | Respuesta de tamaño fijo | Buffer exacto | Menos reservas y ramas | **Descartado** por POC (`FixedSizeResponsePoc`): 8,37 vs 8,86 ns (−0,5 ns, 0 B ambos); por debajo del ruido de cualquier transporte |
| 14 | Unaria grande en el pool QUIC bloquea a las pequeñas (HOL) | `Client.QuicConnection` | Operación marcada por el desarrollador | Ruta a stream propio por operación | Latencia de las pequeñas con cargas grandes | POC `QuicPoolContentionPoc`: el stream propio gana desde ~1 MB (p99 de las pequeñas −66 %); ≤64 KB empate. **Hecho** como opt-in: `[DedicatedStream]` → `QuicConnection.Dedicated`; ignorado fuera de QUIC. Test `DedicatedStreamTest` |
| 15 | Coalescing de streams en el cliente | `StreamConnection`/`MultiplexedChannel` | — | No aplica: depende de lo que traiga cada `ReadAsync` | — | **Hecho como defecto** (`coalesceStreams = true` en TCP/UDS), sin cambio de cable. `Lsl_Stream` 274 → 196 µs (AspNetSlim 229 µs). Tests `StreamBatchingTest`, `StreamBatchConcurrencyTest` |

Las columnas 2 a 5 de las filas 5, 7, 9, 10, 12 y 14 se perdieron en ediciones anteriores y aquí están **inferidas** del código y de las conclusiones que sí quedaron (beneficio y estado). Las filas 4, 6, 8 y 13 se reconstruyeron a partir de sus POCs. Revisar las inferidas si se retoman.

## Qué no conviene generar

- **Formato de wire con negociación:** fuera de alcance por diseño; cliente y host salen del mismo contrato.
- **Serialización propia en lugar de MemoryPack:** MemoryPack ya es código generado; duplicarlo no aporta.
- **Tamaño de lote en runtime:** puede seguir siendo un parámetro de arranque. El generador solo fija el valor por defecto de cada operación. Así el desarrollador puede ajustarlo sin recompilar el contrato.

## Plan de validación (cada punto con POC o test, según las directrices)

| # | Estado | Evidencia |
|---|---|---|
| 8 | Hecho | `Lsl_Unary` 41 → 39 µs, 2,07 KB → 903 B |
| 1 | Hecho | `SourceGenPocBenchmarks.Policy_*`, `StreamPolicyTypeTest` |
| 3 | Hecho (TCP/UDS/QUIC) | `UdsTest` |
| 4 | Descartado | `DenseDispatchPoc` |
| 12 | Hecho (salvo streams) | `ParamEncodingPoc`, `UdsTest.TestRawRoundtrip`, `RawCapacityTest`, POC `IAuth.TouchAsync` |
| 15 | Hecho (defecto TCP/UDS) | `Comparison`: `Lsl_Stream` 196 µs vs `AspNetSlim_Stream` 229 µs |
| 2, 9, 10, 13 | Descartados | Ver inventario |
| 5, 6, 7 | Diferidos | Solo si un perfil muestra la rama en el camino caliente |
| 14 | Hecho (opt-in) | `QuicPoolContentionPoc`, `DedicatedStreamTest` |
| — | Hecho: generador dividido host/cliente | `GeneratorHarness`, suite 94/94 |

**Siguiente en el generador:** nada abierto con ganancia medida. Candidatos solo con perfil: raw en streams (#12), #5/#6/#7, y modos de stream de `MemChannel` (`Take`/`Window`/`IdleTimeoutMs` como atributos, ver PLAN *Modos de stream en MemChannel*) cuando haya un caso real.

## ConfigureAwait(false) en cada await

`ConfigureAwait(false)` afecta solo al `await` donde está escrito. Cada método `async` tiene su propia máquina de estados. Cuando llega a su `await`, captura el contexto actual (`SynchronizationContext` o `TaskScheduler`) salvo que ese mismo `await` diga `false`. Que el llamador use `ConfigureAwait(false)` sobre la tarea del método llamado no cambia cómo se reanudan los `await` de dentro del método llamado. Por eso, en una biblioteca va en cada `await`.

Matiz práctico:
- **Servidor:** se ejecuta en el pool de hilos, sin `SynchronizationContext`, así que es casi gratis e inofensivo. Se mantiene por coherencia y por si un host se incrusta en una app con contexto.
- **Cliente:** puede llamarse desde WPF, WinForms o MAUI. Ahí sí evita volver al hilo de UI y los bloqueos mutuos con `.Result`. Es obligatorio.
- **Alternativa:** en el código generado, el generador puede emitirlo siempre, así que no es carga manual.

**Estado:** emitido en host, etapas de pipeline, cliente tipado y cliente raw (paso 5).

## Principio

Si el dato se conoce al compilar, lo decide el generador; si no hay medida que lo justifique, se queda como está.

