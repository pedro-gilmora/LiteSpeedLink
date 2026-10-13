# Performance + memory enhancements

Lo que más rinde, de mayor a menor ganancia esperada. Todas las cifras son de la corrida `--fast`, salvo el resultado del punto 3; los tiempos aproximados y las asignaciones son las del informe.

### 1. Lotes por defecto también en QUIC (hecho)
- **Dato:** en QUIC, `Lsl_Stream` tarda 273 µs / 9,4 KB y `Lsl_StreamBatched` 158 µs / 7,6 KB. En TCP, activar los lotes por defecto bajó el stream de 196 a 60 µs.
- **Acción:** aplicar a QUIC el mismo lote por defecto que TCP/UDS (`DefaultStreamBatch`), decidido por el generador según el transporte.
- **Ganancia esperada:** alrededor de −40 % en streams QUIC. Es el cambio más barato.
- **Cambio:** `StartQuicServerAsync` usa `streamBatch = DefaultStreamBatch`, igual que `StartTcpServer` y `StartUdsServer`. El host generado no pasa el parámetro, así que hereda el valor por defecto sin tocar el generador. `streamBatch: 0` sigue enviando item a item.
- **Test:** `StreamBatchPolicyTest.EveryStreamTransportBatchesByDefault` comprueba que los tres transportes tienen el mismo valor por defecto, y `QuicStreamsMatchBatched` cubre ahora también `DefaultStreamBatch` (mismo orden y mismos items con 0, 1, 7 y 1000).
- **Resultado** (BDN de publicación, `QuicComparisonBenchmarks`, mismo binario salvo el cambio):

  | | Antes | Ahora |
  |---|---|---|
  | Lsl_Stream (por defecto) | 620,6 ± 37,0 µs / 8,11 KB | **276,6 ± 70,6 µs / 6,57 KB** |
  | Lsl_StreamBatched (lote 256 explícito) | 440,3 ± 41,8 µs / 6,96 KB | 326,5 ± 93,1 µs / 7,09 KB |
  | Slim H3 Stream | 1 425,0 ± 119,8 µs / 24,81 KB | 1 342,9 ± 199,8 µs / 24,67 KB |

  - −55 % de latencia y −1,5 KB por stream en la fila por defecto. La desviación es alta (QUIC sobre loopback), pero la diferencia es muy superior al ruido.
  - Con el valor por defecto, el stream QUIC ya rinde igual o mejor que el lote de 256 explícito: refuerza el punto 7.

### 2. Cada stream QUIC paga ~5 KB fijos (hecho)
- **Dato:**
  - Una unaria con stream propio cuesta 6,4 KB; con el pool, 1,6 KB.
  - El stream QUIC de 10 items cuesta 105 µs / 6,9 KB, frente a 34 µs / 559 B en TCP.
  - `RawPipes` asigna 1,2 KB más que `RawStream`, solo por el `PipeReader`/`PipeWriter`.
- **Acción:** reutilizar del pool los streams cortos (punto #11 de SourceGenStudy). La alternativa es cachear o poolear los pipes por stream. `[DedicatedStream]` quedaría solo para respuestas grandes, como ya decidió el PoC #14.
- **Ganancia esperada:** −60 a −70 % de latencia y memoria en streams cortos de QUIC.
- **Cambio:** `QuicConnection.EnumerateAsync` usa el mismo pool de `MultiplexedChannel` que las unarias (sin estado propio: el `StreamEnumerator` del canal ya va del pool). Sin pool (`unaryStreams: 0` o `Dedicated`) sigue abriendo un stream por llamada. `[DedicatedStream]` ahora también se aplica a operaciones de stream: el generador emite `__connection.Dedicated.EnumerateAsync` para streams largos o suscripciones que no deben compartir stream (HOL). El servidor no cambia (el pool ya sirve tramas de stream con `ServePipeAsync`).
- **Test:** `DedicatedStreamTest.StreamsRunOnPoolAndDedicated` (40 streams concurrentes, intercalando pool y dedicada, items y orden exactos) y `RoutesOnlyQuicToDedicated` (el stream marcado va por `Dedicated` solo en QUIC).
- **Resultado** (BDN de publicación, misma corrida):

  | | Stream propio | Pool |
  |---|---|---|
  | `QuicOverhead` stream de 1 item (`Lsl` / `LslPooled`) | 135,8 µs / 6 873 B | **74,6 µs / 1 372 B** |
  | `Streaming` QUIC 10 items | 104,8 µs / 6 906 B (corrida previa) | **78,8 µs / 1 491 B** |
  | `Streaming` QUIC 1000 items | 270,4 µs / 9 527 B (corrida previa) | **136,5 µs / 1 513 B** |

  - −45 % de latencia y −80 % de memoria en el stream corto; el stream por pool cuesta lo mismo que la unaria por pool (81,9 µs / 1 438 B).
  - Abandonar un stream del pool descarta sus tramas en el cliente (como TCP/UDS); antes, el `DisposeAsync` del stream propio drenaba el resto hasta el FIN.

### 3. Un hit de caché de servidor salía más caro que no tener caché (resuelto)
- **Dato original (`--fast`):** `ServerCacheHit` tardaba 40,4 µs / 554 B, frente a 35,7 µs / 559 B de `Plain`. `CacheKeyHit` tardaba 41,0 µs y era la única fila de `FeatureBenchmarks` donde LiteSpeedLink perdía en tiempo frente a slim.
- **Causa:** no estaba en `ServerCacheManager` (la búsqueda por bytes no copia ni bloquea). `ReturnRawAsync` llamaba a `WriteRawAsync` con flush **diferido**, pensado para los lotes de stream: cada acierto escribía la trama y esperaba a que el `ThreadPool` la enviara.
- **Cambio:** `WriteRawAsync` recibe `deferFlush`. La respuesta final de `ReturnRawAsync` hace flush en línea, igual que `ReturnAsync`; los lotes siguen diferidos (`Server.Pipes.cs`).
- **Cómo se midió:**
  - BDN de publicación no sirve para comparar estas filas entre sí: en tres corridas, el mismo `Lsl_Plain` dio 38,9, 41,9 y 62,1 µs, con avisos de distribución bimodal.
  - Se usa `--alloc lat`: todas las filas en el mismo proceso, intercaladas por rondas (40 rondas × 2 000 llamadas por fila), con afinidad a P-cores. Así la deriva de loopback afecta a todas las filas por igual.
  - Se compara el código con flush diferido ("Antes") y con flush en línea ("Ahora") en dos ejecuciones separadas. Las filas que no pasan por `ReturnRawAsync` (Plain y slim) sirven de control: su diferencia entre columnas es solo variación entre ejecuciones (±0,5 a 3 µs).
- **Resultado** (latencia por llamada en µs, p50 / p99; p50 es la mediana y p99 la cola del 1 % más lento):

  | | Antes | Ahora |
  |---|---|---|
  | Lsl Plain | 38,8 / 89 | 39,3 / 86 |
  | Lsl ServerCacheHit | 60,7 / 109 | **39,9 / 84** |
  | Lsl CacheKeyHit | 61,0 / 110 | **40,4 / 86** |
  | Slim Plain | 62,0 / 119 | 59,8 / 119 |
  | Slim ServerCacheHit (OutputCache) | 65,0 / 132 | 61,3 / 137 |
  | Slim CacheKeyHit (OutputCache) | 65,0 / 125 | 62,0 / 125 |

  - Un acierto cuesta ahora lo mismo que Plain (antes, ~21 µs más) y unos 21 µs menos que slim en la mediana.
  - Asignaciones (BDN de publicación, después del cambio): ServerCacheHit 508 B frente a 4249 B de slim; CacheKeyHit 511 B frente a 4391 B.
- **Queda:**
  - Domina el viaje por loopback. El handler de prueba es trivial, así que la caché solo ahorra tiempo con handlers caros.
  - Los costes locales del acierto (crear el scope y, en CacheKeyHit, deserializar la petición para construir la clave) quedan por debajo del ruido de esta medición.

### 4. Concurrencia en UDS/TCP: `PendingSlots` + escritura agrupada (hecho)
- **Dato:** en UDS la asignación por llamada crece de 749 B (c1) a ~963 B (c64). En Memory baja hasta ~405 B. La POC ya midió `ConcurrentDictionary` en 40 ns / 40 B frente a 15 ns / 0 B con slots.
- **Acción:** llevar `PendingSlots` a `MultiplexedChannel._pending`. Después, escribir sin suspender en `SendRequestAsync`, porque la caja se agota en `_gate.WaitAsync`.
- **Ganancia esperada:** menos asignaciones y menor latencia a c ≥ 8.
- **Cambio:** `_pending` pasa a ser un `object?[4096]` indexado con `id & SlotMask`. `Register` arma el sink y lo publica con CAS; si el slot está ocupado (por ejemplo, por un stream largo), salta al id siguiente. `Unregister` libera con CAS solo si el ocupante es ese sink. `Deliver` lee el slot y el sink compara su id armado, así que una respuesta tardía o de otro id con los mismos bits bajos se descarta como antes. `Fail` recorre la tabla. Límite: 4096 llamadas en vuelo por conexión (`InvalidOperationException` al llenarse), marcado con `ponytail:`. El servidor ya limita a 256 por conexión.
- **Test:** `UdsTest.TestPendingSlotWrapSkipsLiveStream` deja un stream abierto ocupando su slot, lanza 5000 unarias (el contador da la vuelta a la tabla) y comprueba que todas devuelven lo suyo y que el stream recibe después exactamente sus 100 items.
- **Resultado** (BDN de publicación, `MemoryConcurrencyBenchmarks.Uds`, control correcto):

  | Concurrencia | Antes | Ahora |
  |---|---|---|
  | 1 | 57,3 ± 9,8 µs / 769 B | 59,9 ± 3,3 µs / **686 B** |
  | 8 | 70,5 ± 4,3 µs / 6 418 B | 69,9 ± 4,4 µs / **6 110 B** |
  | 64 | 278,7 ± 10,9 µs / 61 833 B | 291,7 ± 16,0 µs / **59 599 B** |

  - Memoria: −83 B por llamada en c1 y unos −36 B por llamada en c8/c64 (el nodo del diccionario).
  - Latencia: sin cambio apreciable (dentro de la desviación). El coste dominante está en otro sitio.
- **Perfil** (`dotnet-trace`, `dotnet-sampled-thread-time`, `--alloc conc` sobre TCP): `SendRequestAsync` asigna 107/151/174 B por llamada a c8/c64/c256 (su caja, porque espera en `_gate.WaitAsync` o en el flush), la mitad del total por llamada; con c1 no asigna. En tiempo de hilo, el envío del cliente (`StreamPipeWriter.FlushAsyncInternal` → `Socket.SendAsync`) es el mayor bloque inclusivo, por delante del bucle de lectura. Cada llamada hace su propio `send`, en serie detrás de la puerta.
- **Escritura agrupada:** `SendRequestAsync` ya no espera. Bajo un `Lock` escribe su trama en un `ArrayBufferWriter` de cola (cabecera rellenada por posición, porque el buffer puede realojarse al serializar). Solo la llamada que encuentra la cola sin vaciador la vuelca al `PipeWriter` y hace flush; las que llegan mientras tanto vuelven sin suspender y salen en el siguiente `send` (doble buffer). Un fallo de envío se reparte a todas las llamadas en curso (`Fail`).
  - Coste: una copia de la trama (cola → pipe).
  - Cambio de comportamiento: si la serialización de un parámetro falla, esa llamada recibe `ArgumentException("Invalid parameters")` como antes y la conexión se cierra igual, pero el cierre lo hace el vaciador.
- **Test:** `UdsTest.TestBatchedSendKeepsFramesIntact` lanza 2000 llamadas concurrentes con cuerpos de 0 B a ~22 KB (fuerza realojos de la cola) y exige el eco exacto en cada una.
- **Resultado** (BDN de publicación, `MemoryConcurrencyBenchmarks`, control correcto; se añadió `Tcp` al escenario para medir el mismo cliente). La base es `PendingSlots` con la puerta:

  | Transporte / c | Puerta | Agrupada |
  |---|---|---|
  | UDS 1 | 53,3 ± 2,1 µs / 686 B | 33,6 ± 5,8 µs / 664 B |
  | UDS 8 | 68,5 ± 3,7 µs / 6 086 B | 60,1 ± 4,3 µs / **5 021 B** |
  | UDS 64 | 286,3 ± 17,0 µs / 58 352 B | **186,0 ± 10,5 µs** / **44 494 B** |
  | TCP 1 | 60,8 ± 4,5 µs / 654 B | 53,3 ± 2,3 µs / 654 B |
  | TCP 8 | 189,2 ± 14,1 µs / 6 518 B | **131,1 ± 17,5 µs** / **5 067 B** |
  | TCP 64 | 978,4 ± 36,0 µs / 60 069 B | **648,9 ± 20,9 µs** / **45 221 B** |

  - c64: −35 % de tiempo en UDS y −34 % en TCP; −24 % de memoria en ambos (sin la caja de `SendRequestAsync`).
  - c1 queda igual o mejor (la ruta sin contención solo añade un `lock` y una copia pequeña).

### 5. Asignaciones inesperadas en el camino feliz del cliente (no reproduce)
- **Dato:** respecto a `Plain` (559 B), `ClientRetry` asigna 108 B más y `Processor` 198 B más. El README dice que retry no asigna nada.
- **Acción:** localizarlo con `--alloc gen` (`AllocProbe`). Puede ser un CTS por intento o la caja de un processor async.
- **Ganancia esperada:** cumplir lo que promete el README y bajar a ~559 B en ambos casos.
- **Resultado** (`--alloc gen`, 200 000 llamadas por fila, cliente y servidor en el mismo proceso):
  - `Lsl_Plain` 232 B/op, `Lsl_ClientRetry` 226 B/op, `Lsl_ServerRetry` 226 B/op. Retry no asigna nada: lo que prometía el README se cumple. La diferencia del informe `--fast` era ruido de esa corrida.
  - `Lsl_Processor` 308 B/op (+76 B). El desglose lo atribuye a `System.String` (~79 B/op): el token `"tok-admin"` que lee el servidor y la respuesta `"pedro"` que lee el cliente. `Plain` devuelve un `int`. Es el coste de la propia operación, no del processor.
- **Visto de paso:** cada llamada genera en el servidor un `Scoped` (~60 B) y un `FeaturesImpl` (~27 B), porque el host crea un scope de DI por petición y la implementación es scoped. Es por diseño; solo merecería la pena tocarlo si una implementación singleton pudiera saltarse el scope, y eso no es parte de este punto.

### 6. Hueco entre el Memory generado y MemChannel puro (hecho: no hay capas sobrantes)
- **Dato:** una unaria Memory c1 tarda 16,5 µs en `MemoryConcurrency`, frente a 6,2 µs en la POC de MemChannel.
- **Diagnóstico:** los arneses no eran equivalentes. La POC mantiene `c` workers en bucle y divide entre 20k llamadas. `MemoryConcurrency` crea `c` tareas con `Task.Run` y un `WhenAll` en cada operación. La copia de `MemChannel.cs` de la POC solo difiere en los atributos `[SupportedOSPlatform]`.
- **Medición con el mismo arnés** (`--alloc memc`, misma máquina, 3 corridas):
  - `MemoryConnection`: c1 7,5–11,3 µs; c8 13,7–23 µs; c64 97–144 µs; 41 B/llamada.
  - POC `MemClient.CallAsync`: c1 8,5 µs; c8 11 µs; c64 91 µs; 0 B.
  - El arnés BDN de la misma ejecución: c1 18,1 µs/629 B; c8 40,8 µs/3517 B; c64 266 µs/26 KB.
- **Conclusión:** la latencia queda dentro del ruido de la POC. Los 41 B son el `MemoryRequestContext` que el servidor crea por petición. Los ~600 B por operación de BDN son las tareas y el array de `Run`, no la ruta Memory. No se cambia código de producción.
- **Pendiente opcional:** convertir `MemoryRequestContext` en un objeto reutilizable (41 B/llamada). Cambiaría un tipo público y solo ahorraría esos bytes, así que no se hace sin un caso real.

### 7. Rutas que se pueden borrar (hecho)
- **Dato:**
  - `Lsl_StreamBatched` (67 µs / 947 B) ya es peor que el stream por defecto (60 µs / 457 B) en TCP.
  - `Lsl_UnaryRaw` (35,6 µs / 405 B) no mejora a la unaria tipada (33,7 µs / 390 B).
- **Acción:** retirar el batched opt-in de TCP/UDS, o dejarlo solo para QUIC hasta hacer el punto 1. Revisar si el raw unario sigue justificado.
- **Ganancia esperada:** menos código y menos ramas en el generador.
- **Cómo se midió:** `--alloc cmplat` (filas intercaladas en el mismo proceso, p50 µs / B por llamada del proceso):

  | | p50 | p99 | B/op |
  |---|---|---|---|
  | Lsl_Unary | 31,3 | 77,1 | 113 |
  | Lsl_UnaryRaw | 31,6 | 73,8 | 121 |
  | Lsl_Stream (por defecto) | 67,0 | 166,5 | 241 |
  | Lsl_StreamPerItem (`coalesceStreams: false`) | 66,7 | 165,7 | 240 |
  | Lsl_StreamBatched (lote 256 runtime, antes de quitarlo) | 82,5 | — | 703 |

- **Cambio:**
  - Se quitan `streamBatch` y `streamBatchMaxDelay` de `StartTcpServer`, `StartUdsServer` y `StartQuicServerAsync`, junto con `DefaultStreamBatch`, `BatchPolicy` y su paso por `ResponseChannel`. El lote por defecto (32 KB o hasta que el productor va a esperar) es ahora el tipo interno `DefaultBatched : IStreamPolicy`, y los `EnumerateAsync` sin política lo usan, así que el JIT pliega las ramas igual que con las políticas generadas.
  - Un lote distinto se fija solo en el contrato: `[Stream(Batch, MaxDelayMs)]`, que el generador ya emite como política de tipo.
  - Se conserva `UnaryRaw`: rinde igual, pero no es una ruta solo del benchmark. El generador la usa para out/ref, `[ServerCache]`/`[ClientCache]` y pipelines.
  - Se conserva `coalesceStreams`: no cuesta nada (rinde igual que el defecto) y sirve de control.
- **Test:** `StreamBatchPolicyTest.NoRuntimeBatchKnob` comprueba que ningún `Start*Server` expone `streamBatch*`. `StreamBatchingTest`, `StreamBatchConcurrencyTest`, `StreamPolicyTypeTest` y `QuicStreamsMatchBatched` recorren `Unbatched`, el valor por defecto y lotes explícitos por tipo.

#### Decisión cerrada: lote de streams
El lote por defecto agrupa siempre y solo el contrato lo cambia con `[Stream(Batch, MaxDelayMs)]`. El host no tiene opción para ello. No se reabre sin una medición intercalada que lo contradiga.

Por qué:
1. **No cambia lo que recibe el cliente.** Llegan los mismos items en el mismo orden; solo cambia cuántas tramas viajan. Es una mejora interna, así que va por defecto (regla de la tabla de abajo).
2. **Agrupar gana a no agrupar.** En TCP el stream pasó de 196 a 60 µs al agrupar por defecto (punto 1).
3. **El defecto se adapta y un tamaño fijo no.** Se envía al llegar a 32 KB o cuando el productor va a esperar: con un productor rápido salen pocas tramas grandes, y con uno lento cada item sale enseguida. Un tamaño fijo solo puede enviar antes de tiempo (más tramas) o retener datos (más latencia y un temporizador). Medido: 67,0 µs / 241 B frente a 82,5 µs / 703 B con lote de 256.
4. **Se decide por operación.** Una opción del servidor vale para todas sus operaciones, pero una suscripción quiere cada item al momento y un volcado quiere lotes grandes. Eso solo se sabe en la operación, así que va en `[Stream]`.
5. **Se decide al compilar.** Cliente y host salen de los mismos contratos. La política es un tipo (`DefaultBatched`, `Unbatched` o el struct generado) y el JIT quita las ramas, en vez de leer una opción en cada item.
6. **Una sola forma de hacerlo.** La opción del servidor y `[Stream]` hacían lo mismo; con una sola no hay dudas sobre cuál manda.

Por qué hubo idas y vueltas: las mediciones anteriores se hicieron en ejecuciones separadas y el ruido entre ellas tapaba la diferencia. `--alloc cmplat` intercala las filas en el mismo proceso.

Límites: solo está medido en loopback y con un tamaño de item. Si una operación necesitara otro lote según el despliegue, se añade a `[Stream]`; no se recupera la opción del servidor.

### 8. Método de medición
- Las cifras del README salen de `--fast` y no son publicables. Antes de publicar hay que repetirlas con el arnés completo, incluyendo el control.
- La fila `CacheKeyHit` compara la caché de servidor de LiteSpeedLink con OutputCache de slim. Conviene confirmar que miden lo mismo antes de sacar conclusiones del punto 3.
- **Hecho.** README rehecho con el arnés de publicación (`HarnessConfig.Instance`: afinidad P-cores, 10 warmup / 15 iteraciones, `MemoryRandomization`) y máscara 6305 = Control + Streaming + Comparison + QuicComparison + Features. Control agrupado: lectura 0,63–0,71 ns, alloc 2,88–2,90 ns, así que la corrida vale.
- Cambios frente a las cifras `--fast`: unaria TCP 34 → 44 µs (slim 48 → 58, misma proporción ~1,3x); stream QUIC 273 µs / 9,4 KB → 118 µs / 1,4 KB (puntos 1-2); la fila "batched" de LSL desaparece (punto 7) y queda slim con flush por lote frente al stream por defecto.
- `CacheKeyHit` mide lo mismo: los dos lados viajan por loopback, aciertan en el host y no ejecutan el handler. LSL usa `[ServerCache]` + `[CacheKey(Sku)]`; slim usa `CacheOutput(VaryByQuery("sku"))`. En los dos la clave es solo `sku` y `note` se ignora. El `GlobalSetup` lo comprueba: con `note=x{i}` ambos devuelven `"7:a"` y los contadores de handler suben como mucho 1. Diferencia que queda: LSL lleva el record MemoryPack en el cuerpo y slim lo lleva en la query de un GET. Resultado: 44 µs / 519 B frente a 58 µs / 4,3 KB.
- Regla: `--fast` solo vale para asignaciones; los tiempos que se publiquen necesitan el arnés completo con el bit 1. `Program.cs` ya avisa si falta.

**Orden sugerido:** 1 → 3 → 5 → 4 → 2 → 6. El 7 se puede hacer en cualquier momento. Cada punto llevaría su test o POC.

### ¿Por defecto o por configuración?
Regla: lo que es solo una mejora interna, sin coste para nadie y sin cambiar el comportamiento observable, va **por defecto** y no se expone. Solo es **configuración** lo que exige infraestructura externa o cambia la semántica, y lo decide el generador o el host en tiempo de compilación o de arranque, nunca una rama en caliente.

| Punto | Decisión | Motivo |
|---|---|---|
| 1. Lotes en QUIC | **Por defecto** (hecho). `streamBatch` de QUIC pasa de `0` a `DefaultStreamBatch`, como TCP/UDS. Desde el punto 7 ya no hay parámetro de host: se cambia con `[Stream(Batch, MaxDelayMs)]`. | El lote no cambia lo que recibe el cliente, solo cuántas tramas viajan. TCP ya lo hace por defecto y ganó 196 → 60 µs. |
| 2. Streams cortos de QUIC del pool | **Por defecto** (hecho). `[DedicatedStream]` es el opt-in para respuestas grandes y, ahora también, streams largos o suscripciones. | Reutilizar el stream es interno. La excepción (stream propio) ya existe como atributo y la decide el generador. |
| 3. Flush en línea de `ReturnRawAsync` | **Por defecto** (ya aplicado). Sin configuración. | Es la corrección de un bug: diferir el flush de una respuesta final no aporta nada. |
| 4. `PendingSlots` + escritura agrupada en `MultiplexedChannel` | **Por defecto** (hecho). Sin configuración. | Estructuras internas más baratas y un flush por tanda; el cable no cambia. |
| 5. Asignaciones de `ClientRetry`/`Processor` | No hace falta cambio: no reproduce (retry 226 B frente a 232 B de Plain; Processor +76 B por las cadenas de la operación). | Lo prometido por el README ya se cumple. |
| 6. Hueco Memory generado vs MemChannel | Sin cambio (hecho): el hueco era del arnés. Con el mismo arnés, `MemoryConnection` iguala a `MemClient`. | No hay capas que quitar. |
| 7. Rutas que se pueden borrar | **Quita configuración** (hecho). Fuera `streamBatch`/`streamBatchMaxDelay` del host; el lote solo se cambia con `[Stream(Batch, MaxDelayMs)]`. `UnaryRaw` y `coalesceStreams` se quedan. | El lote runtime era peor que el defecto y duplicaba una decisión que ya toma el generador. Raw es la base de la caché y de out/ref. |
| 8. Método de medición | Hecho. README con arnés de publicación y control; `CacheKeyHit` validado. | `--fast` solo para asignaciones. |
| Antes el productor seguía hasta el final y los lotes huérfanos inflaban el pool del servidor (~7,5 MB). Solo cuesta 8 B cuando se abandona; A/B sin diferencia medible en Streaming. |
| L2 distribuido (Redis, SQL; `PLAN.md`) | **Configuración, nunca por defecto.** Opt-in por atributo y backend por DI; el host falla al arrancar si falta. | Exige un almacén externo, añade un viaje de red por cada fallo de L1 y solo compensa con varias instancias. |
