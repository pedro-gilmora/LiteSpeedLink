# Plan de mejoras — LiteSpeedLink

> **Premisa rectora**
> Clientes y hosts se **generan juntos a partir de los mismos contratos** en tiempo de compilación.
> El uso previsto es **predecible y typesafe**: no son servidores/clientes genéricos agnósticos del runtime.
> Por tanto **quedan fuera de alcance**: negociación de protocolo, handshakes de versión, descubrimiento
> dinámico de servicios, reflexión en runtime y robustez frente a usos arbitrarios del transporte.
> Todo lo que el generador puede garantizar en compile-time, **no** debe validarse en runtime.

Estado: `[ ]` pendiente · `[~]` en curso · `[x]` hecho · `[-]` descartado

---

## Fase 0 — Línea base medible

- [ ] **0.1 Proyecto de benchmarks en la solución**
  - Hoy existe `test/POC/Benchmarks` fuera de `LiteSpeedLink.slnx`.
  - Añadir `LiteSpeedLink.Benchmarks` (BenchmarkDotNet) con escenarios:
	`Greet` (string→string), `TryAuthenticate` (record struct + out), y streaming.
  - Métricas: latencia media/p99, **allocs por operación**, ops/s.
  - *Sin esto, ninguna optimización posterior es verificable.*

---

## Fase 1 — Coste evitable en la ruta caliente (máximo beneficio / mínimo riesgo)

- [ ] **1.1 Eliminar `Console.WriteLine` del camino de ejecución**
  - `MemoryConnection.cs`: 41, 44, 49, 52, 154, 157, 162, 165
  - `Server.Memory.cs`: 26, 43, 62
  - Cada llamada: interpolación + boxing + lock global de consola + I/O síncrona.
	En RPC sobre memoria compartida esto **domina el coste total**.
  - Propuesta: borrarlas. Si se quiere trazabilidad opcional, `#if LSL_TRACE` o
	`[LoggerMessage]` source-generated con guarda `IsEnabled`.
  - Riesgo: nulo.

- [ ] **1.2 Quitar `DynamicallyAccessedMembers(All)` de los genéricos**
  - `IConnection.cs` (todas las firmas), `MemoryConnection`, `TcpConnection`,
	`UdpConnection`, `QuicConnection`, `MemoryRequestContext`.
  - MemoryPack es **source-generated**: no hay reflexión que preservar.
	Estos atributos obligan al trimmer a conservar *todos* los miembros de cada tipo transmitido.
  - **Alineado directamente con la premisa**: el contrato es estático, no hace falta metadata en runtime.
  - Beneficio: tamaño de publicación y NativeAOT real. Probablemente permita simplificar
	o eliminar `TrimmerRootDescriptors.xml`.

- [ ] **1.3 Limpiar ruido del código generado**
  - `private readonly static object _lock` emitido en el cliente y nunca usado
	(`ServiceHandlers.GenerateServiceClient.cs`, ~línea 70).
  - `MemoryRequestContext.ResponseWriter` (`BufferBuilder`) nunca emitido por el generador
	(`Server.Memory.cs:54`).
  - `StartMemoryServer` recibe `onFinalize` y **lo ignora**; la variable `slave` es muerta
	(`Server.Memory.cs:21-28`, `38-45`).

---

## Fase 2 — Asignaciones por petición

- [ ] **2.1 `SerializePayload` sin asignaciones intermedias**
  - `MemoryConnection.cs:263-276` hace **3 asignaciones**:
	`Serialize(payload)` → `byte[]`, `new byte[reqLen]`, `BitConverter.GetBytes(op)`.
  - Propuesta: buffer de `ArrayPool<byte>.Shared` +
	`BinaryPrimitives.WriteInt64LittleEndian(span, op)` +
	`MemoryPackSerializer.Serialize(bufferWriter, payload)` sobre el mismo buffer.
  - Nota: la API de `RpcBuffer` exige `byte[]`; evaluar si el fork de `SharedMemory`
	(rama `migrating-to-modern-memory-management`) puede aceptar `ReadOnlySpan<byte>`.
	**Esto puede condicionar cuánto se puede optimizar aquí.**

- [ ] **2.2 `BuildRequest`: quitar `stackalloc` + `ToArray()`**
  - `TcpConnection.cs:532`, `UdpConnection.cs:76`, `QuicConnection.cs:496`
  - El `stackalloc byte[8 + payload.Length]` no está acotado (**riesgo real de StackOverflow**)
	y además acaba en `.ToArray()`, que lo copia al heap: coste doble, beneficio cero.
  - Propuesta: escribir directo en `writer.GetSpan(...)` / `Advance(...)` (TCP y QUIC ya tienen
	`PipeWriter`). Para UDP, `ArrayPool`.

- [ ] **2.3 No copiar el payload en el servidor**
  - `Server.Memory.cs:27,44`: `payload[8..]` copia el array completo en cada request.
  - Propuesta: pasar `ReadOnlyMemory<byte>` con offset a `MemoryRequestContext`.
  - Opcional: convertir `MemoryRequestContext` en `readonly ref struct` (el generador controla
	su uso, así que es seguro) → elimina otra asignación por request.

- [ ] **2.4 `BinaryPrimitives` para el `opId`**
  - `Serialize(op)` usa MemoryPack para escribir 8 bytes (TCP/UDP/QUIC);
	`BitConverter.GetBytes(op)` asigna (`MemoryConnection.cs:62,84`).
  - Ambos → `BinaryPrimitives.WriteInt64LittleEndian`.

- [ ] **2.5 `lock` por cada acceso a `MemoryRpc`**
  - `MemoryConnection.cs:23-33`: la propiedad toma el `Lock` **siempre**, y se lee varias veces
	por operación.
  - Propuesta: fast-path con `Volatile.Read`, lock solo para reconstruir, y cachear en local
	dentro de cada método.

---

## Fase 3 — Correctitud

- [ ] **3.1 `MemoryConnection.Dispose` resucita el buffer**
  - `MemoryConnection.cs:278-284`: invoca la *propiedad* `MemoryRpc`, que si el buffer ya estaba
	dispuesto **crea uno nuevo** solo para disponerlo. Debe usar el campo de respaldo.
  - Bug claro, arreglo trivial.

- [ ] **3.2 `GetServiceId` determinista**
  - `ServiceHandlers.GenerateServiceHost.cs:585-591`: usa `Encoding.Default`.
	Cliente y host compilados en máquinas con distinto codepage ANSI generan **ids distintos**.
  - Propuesta: `Encoding.UTF8` + hash estable (`XxHash64` o FNV-1a).
  - Añadir **diagnóstico del generador** si dos métodos colisionan en el mismo id.
	Encaja con la premisa: se detecta en compile-time, no en runtime.

- [ ] **3.3 Framing por longitud en TCP**
  - `TcpConnection.cs:108-112` asume que una `ReadAsync` = un mensaje completo.
	TCP es un flujo: fragmenta y coalesce **independientemente de cómo se use la API**.
	No es un problema de "uso libre", es una propiedad del transporte.
  - Propuesta: prefijo `int` LE + bucle `TryReadFrame` con `SequenceReader<byte>`.
  - *(QUIC: revisar — si cada RPC usa su propio stream, el framing puede ser innecesario.)*
  - **A discutir:** ¿es TCP un transporte de primera clase o secundario frente a memoria compartida?
	Si es secundario, esto baja de prioridad.

- [ ] **3.4 Errores del servicio**
  - El host generado no envuelve cada `case` en `try/catch`: una excepción del servicio
	tumba el handler completo.
  - El `default:` devuelve `null` en vez de `ResponseStatus.NotFound`
	(`GenerateServiceHost.cs:507-512`) — aunque con la premisa, `default` es **inalcanzable**
	si cliente y host se generan juntos, así que puede quedarse como está.
  - El cliente TCP detecta errores **capturando `MemoryPackSerializationException`**
	(`TcpConnection.cs:116-131`): control de flujo por excepciones, caro y frágil.
  - Propuesta mínima coherente con la premisa: 1 byte de estado en la respuesta
	(`Ok` / `Failed`), `try/catch` generado por caso. Sin más metadata: los tipos ya son conocidos.

- [ ] **3.5 Fire-and-forget sin observar en streaming**
  - `MemoryConnection.cs:113, 143, 228, 258`: `_ = RemoteRequestAsync(...)`.
	Excepciones perdidas y carrera entre el alta del `RpcBuffer` receptor y el envío.

---

## Fase 4 — Streaming

- [ ] **4.1 Sustituir TPL Dataflow por `System.Threading.Channels`**
  - `MemoryConnection.cs:95, 125, ...`: `BufferBlock<T>` es pesado.
  - `ToBlockingEnumerable()` (líneas 115, 145) **bloquea hilos del ThreadPool** → riesgo de starvation.
  - Propuesta: `Channel.CreateBounded<T>` (back-pressure gratis) + elimina la dependencia de Dataflow.

- [ ] **4.2 No crear un `RpcBuffer` por stream**
  - `MemoryConnection.cs:93-111, 123-141`: cada llamada a `Enumerate` crea memoria compartida
	+ eventos nombrados + un `Guid.NewGuid()` formateado a string. Setup carísimo por operación.
  - Propuesta: multiplexar el streaming sobre el canal principal con un `streamId` en la cabecera,
	o mantener un pool de sesiones reutilizables.
  - **A discutir:** ¿cuál es el patrón de uso real del streaming? Si es raro y de larga duración,
	el coste de setup es irrelevante y esto se descarta.

---

## Fase 5 — Diseño interno

- [ ] **5.1 Deduplicar transportes**
  - `Tcp/Udp/Quic` repiten `BuildRequest`, manejo de errores y flujo de lectura
	(~500 líneas casi idénticas por fichero).
  - Propuesta: base `internal` compartida. **No** un punto de extensión público — la premisa
	dice que los transportes son un conjunto cerrado y conocido.

- [ ] **5.2 Decidir el destino de `IPipeline`**
  - `Pipeline.cs`: las 4 interfaces **no se usan en ninguna parte** y
	`IPipelineAsync.ProcessAsync` devuelve `TOut`, no `Task<TOut>` (incoherente).
  - Opción A: borrar.
  - Opción B: convertirlo en interceptores **generados** (auth, logging, retry) inyectados
	en el host — encaja con la premisa si la composición se resuelve en compile-time.
  - **Decisión tuya.**

- [ ] **5.3 Firma generada del método síncrono más robusta**
  - `GenerateServiceClient.cs`: `method.GlobalNamespaced.Replace(fullTypeName + ".", "")`
	(introducido al hacer públicos los métodos sync) es frágil: si un tipo de parámetro
	contuviera ese prefijo, el `Replace` lo corrompería.
  - Propuesta: construir la firma desde los símbolos (`ReturnType` + `NameOnly` + parámetros)
	en lugar de manipular cadenas.

- [ ] **5.4 `Constants.GetDevCert`**
  - `Constants.cs:14-47`: lanza PowerShell, requiere admin, escribe en `LocalMachine`
	y lleva la contraseña hardcodeada.
  - Propuesta: `CertificateRequest.CreateSelfSigned` (puro .NET, cross-platform, sin proceso externo).
  - Solo afecta a desarrollo; prioridad baja.

- [ ] **5.5 Timeout por defecto**
  - `MemoryConnection`: `timeout = 100` ms por defecto — muy agresivo para cualquier handler
	no trivial. Además el `CancellationToken` no se propaga a `Yield`
	(`Server.Memory.cs:67-88` usa el token del contexto, no el parámetro `token`).

- [ ] **5.6 Transporte local cross-platform** *(opcional)*
  - `MemoryConnection` es `[SupportedOSPlatform("windows")]` por `RpcBuffer`.
  - Si se quiere Linux/macOS: Unix Domain Sockets como transporte "local" con latencia comparable.
  - **A discutir:** ¿Windows-only es una decisión consciente y definitiva?

---

## Descartado por la premisa

- [-] **Handshake / negociación de versión de protocolo** — el contrato es compile-time.
- [-] **Multiplexación + `correlationId` + read-loop concurrente** — asume uso libre y concurrente
	  del mismo objeto de conexión. Si el cliente generado se usa como scoped/por-hilo, sobra.
	  **Reabrir solo si** se confirma que una misma instancia se comparte entre hilos.
- [-] **Pipelining de peticiones en vuelo** — misma razón; además cambia la semántica de orden.
- [-] **Descubrimiento dinámico de servicios / catálogo en runtime.**
- [-] **Contrato de error extendido con metadata de tipos** — los tipos ya se conocen en ambos lados.
- [-] **`ITransport` como punto de extensión público** — conjunto cerrado de transportes.

---

## Preguntas abiertas para la discusión

1. ¿Una instancia de cliente generado puede usarse desde varios hilos, o es de uso exclusivo
   (scoped / por hilo)? — Determina si Fase 3.3 y los descartes sobre multiplexación se reabren.
2. ¿Memoria compartida es *el* transporte principal y TCP/UDP/QUIC son secundarios?
   — Determina la prioridad de la Fase 3.3 y de la 5.1.
3. ¿El fork de `SharedMemory` puede exponer APIs basadas en `Span`/`IBufferWriter`?
   — Techo real de la Fase 2.
4. ¿Se mantiene Windows-only a propósito? (5.6)
5. `IPipeline`: ¿borrar o convertir en interceptores generados? (5.2)
