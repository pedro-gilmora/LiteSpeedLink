# Estudio: ramas en runtime que puede resolver el generador

Objetivo: todo lo que se conoce al compilar (contrato, transporte, forma del metodo, tipos) debe decidirlo el generador, no un `if` por llamada.

**Premisa de red:** el objetivo es perder menos paquetes y que, si se pierden, sea un problema de la red y no de cliente/servidor. Los PoC entran primero como **opción** (opt-in), no como reemplazo; solo pasan a defecto si el benchmark lo justifica. Predecibilidad y rendimiento antes que adivinar en runtime.
Evidencia: codigo en `SourceCrafter.LiteSpeedLink.Server/Client`, salida en `LiteSpeedLink/obj/Generated/.../*.host.g.cs` y `*.client.g.cs`, y los benchmarks de `PLAN.md`.

## Qué emite hoy el generador

- **Host:** `HandleRequestsAsync(long id, ctx, token)` con `switch(id)` sobre el hash FNV-1a de la firma. Cada caso crea un scope, hace `ctx.Get<T>()`, llama al servicio y luego a `ctx.Return(...)` / `ReturnAsync` / `EnumerateAsync(() => ...)`. El transporte se pasa como delegado `RequestHandler` a `Server.StartXxx`.
- **Cliente:** proxies que llaman a `__connection.GetAsync/EnumerateAsync/SendAsync<TIn,TOut>(opId, payload, token)`, conexión genérica por transporte.

El generador ya decide la forma de cada operación (Get, Enumerate o Send), el transporte y los tipos. Sin embargo, el runtime vuelve a decidir varias de esas cosas en cada llamada.

## Inventario (ordenado por valor esperado)

| # | Rama / coste en runtime | Dónde | Dato conocido al compilar | Qué emitir | Beneficio esperado | Evidencia |
|---|---|---|---|---|---|---|
| 1 | Stream: `Func` + clausura y política de lote leída en runtime | `Server.Pipes.EnumerateAsync` | Forma del stream (`IEnumerable`/`IAsyncEnumerable`) y `[Stream(Batch, MaxDelayMs)]` | `EnumerateAsync<T, TPolicy>(___result)` con `struct __Policy_B{n}_D{ms}` (uno por configuración) o `Unbatched` | Medio | **Hecho**. POC stream −44 %, 232 B → 0; política como tipo 15,1 → 3,0 µs sin plazo (con plazo, igual: manda el reloj). Contrato `IStreams` (8 combinaciones); test `StreamPolicyTypeTest` |
| 2 | `GetAwaiter().GetResult()` en pipelines | `ServiceHandlers.Pipelines.cs` | Si el contexto es async | Proponía `await` también en ámbitos síncronos: **incorrecto** (no cabe `await`) | — | **Descartado como propuesta**: `await` ya se emitía donde el contexto es async (`canAwait`); en contextos síncronos se mantiene `GetAwaiter().GetResult()` |
| 3 | Dispatch por delegado `RequestHandler` | `Server.StartXxx` | El host concreto | `struct __Handler : IRequestHandler`; servidores genéricos | Medio-bajo: 1 llamada indirecta menos por petición
| Bytes: -6 por petición, en línea con "economía de datos"; CPU: marginal | **Descartado** por POC (`DenseDispatchPoc`, 1000 llamadas aleatorias): denso 3,94 vs FNV 2,26 µs (N=10, +74 %) y 3,88 vs 3,11 µs (N=100, +25 %); el `switch` FNV (búsqueda binaria) predice mejor que la tabla de saltos indirecta. Memoria: 0 B en ambos (el id va en un buffer ya reservado); solo ahorra 7 B de cable |
facilita el recorte de código no usado | **Diferido**: sin ganancia medible |
el JIT especializa y elimina la rama | Bajo; se ejecuta en cada trama | **Diferido**: POC −22 % (1,35 → 1,05 µs / 1000 tramas), irrelevante frente a red; solo si un perfil lo señala |
en contratos solo unarios, menos código | **Diferido**: solo si un perfil lo señala |
| **Hecho**: `UnarySink` (`ManualResetValueTaskSourceCore`) en pool por conexión; streams siguen con canal. `Lsl_Unary` 41 µs / 2,07 KB → 39 µs / 903 B (−56 % asignado) |
limpieza de API | **Descartado**: sin coste en runtime |
| Ninguno | **Descartado**: no tocar |
| 11 | `EnumerateAsync` abre un stream QUIC por llamada | `Client.QuicConnection` | Operación de stream y su perfil | Opción por operación para reutilizar stream (pool, como unarias) sin fijarlo por defecto | Pendiente de PoC: solo si mejora el tiempo sin perder aislamiento | **Pendiente** |
| Menos genéricos, menos copias, binario exacto | **Validado** por PoC (`ParamEncodingPoc`), pendiente de generar: con métodos específicos (`WriteUnmanaged`/`WriteString`, `ReadUnmanaged`/`ReadString`) y estado reutilizado, escritura 14,4 vs 36,8 ns (−61 %) y lectura 19,7 vs 23,7 ns (−17 %), mismo formato en el cable. La primera medición (bloques ≈ tupla al escribir, 37 vs 24 ns al leer) se debía a `WriteValue<T>`/`ReadValue<T>` y a alquilar el estado del pool por llamada; el alquiler pesaba más (lectura genérica con estado cacheado: 21,3 ns).
| Menos reservas y ramas | **Descartado** por PoC (`FixedSizeResponsePoc`): 8,37 vs 8,86 ns (−0,5 ns, 0 B ambos); por debajo del ruido de cualquier transporte, no justifica atributo ni rama generada |
| 14 | Pool QUIC elige stream por turno | `Client.QuicConnection.GetMuxAsync` | Forma y tamaño esperado por operación | Ruta emitida por operación: pool o stream propio (grandes/lentas) | Menos bloqueo de cabeza de línea entre unarias | **Pendiente** de PoC |

## Qué no conviene generar

- **Formato de wire con negociación:** fuera de alcance por diseño; cliente y host salen del mismo contrato.
- **Serialización propia en lugar de MemoryPack:** MemoryPack ya es código generado; duplicarlo no aporta.
- **Tamaño de lote en runtime:** puede seguir siendo un parámetro de arranque. El generador solo fija el valor por defecto de cada operación. Así el desarrollador puede ajustarlo sin recompilar el contrato.

## Plan de validación (cada punto con POC o test, según las directrices)

1. **#8 unaria sin canal.** Medir `Lsl_Unary` (asignación y tiempo) antes y después. Es el cambio más pequeño y con más asignación que quitar.
2. **#1 stream emitido.** Medir `Lsl_StreamBatched` frente a la versión emitida, con y sin atributo de lote.
3. **#2** descartado como propuesta (ver inventario).
4. **#3/#4 dispatch denso.** Micro-benchmark `switch` FNV frente a índice denso con N = 10/100 operaciones. Aplicar solo si la diferencia supera el ruido.
5. **#6/#7** solo si un perfil muestra la rama en el camino caliente; si no, no merece la pena.

## ConfigureAwait(false) en cada await

`ConfigureAwait(false)` afecta solo al `await` donde está escrito. Cada método `async` tiene su propia máquina de estados. Cuando llega a su `await`, captura el contexto actual (`SynchronizationContext` o `TaskScheduler`) salvo que ese mismo `await` diga `false`. Que el llamador use `ConfigureAwait(false)` sobre la tarea del método llamado no cambia cómo se reanudan los `await` de dentro del método llamado. Por eso, en una biblioteca va en cada `await`.

Matiz práctico:
- **Servidor:** se ejecuta en el pool de hilos, sin `SynchronizationContext`, así que es casi gratis e inofensivo. Se mantiene por coherencia y por si un host se incrusta en una app con contexto.
- **Cliente:** puede llamarse desde WPF, WinForms o MAUI. Ahí sí evita volver al hilo de UI y los bloqueos mutuos con `.Result`. Es obligatorio.
- **Alternativa:** en el código generado, el generador puede emitirlo siempre, así que no es carga manual.

## Principio

Todo lo que evite adivinar en runtime se precompila: #1-#7 pasan de 'segun perfil' a obligatorios. Orden de ejecucion: #2 (correccion), #5/#6/#7 (flags -> tipos/sobrecargas emitidas), #1 (stream emitido), #3/#4 (dispatch denso). #8 no es de generador pero va en paralelo.

