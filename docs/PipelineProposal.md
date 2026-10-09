# Propuesta: pipelines guard + wrapper

Estado: **cerrada**. Implementado: processors `[ClientProcessor<T>]`/`[ServerProcessor<T>]` (seccion 1) y retry como bucle inline generado por `[ClientRetry]`/`[ServerRetry]` (2.6). Los wrappers genericos (2.1-2.5) no se implementaron: el unico caso real (retry) sale mas barato inline, sin structs ni delegados. Se conserva el analisis como referencia.
POC medido: `test/POC/PipelineWrapperPoc` (`dotnet run -c Release -- --check` para el self-check; sin argumentos, BenchmarkDotNet).

## 1. Qué hay hoy

`[ClientProcessor<T>]`/`[ServerProcessor<T>]` sobre un parámetro (ida, antes de la llamada) o el retorno (vuelta, después).
Cada etapa recibe un valor y devuelve `(ResponseStatus, TOut)`; distinto de `Success` corta la llamada (host: `Fail`, cliente: `PipelineRejectedException`).
El generador los compone en línea (`if (etapa is not (Success, TOut x)) ...`), sin delegados ni asignaciones.

Esto cubre autenticación, validación y transformación (POC `AuthRetryPoc`: `[ServerProcessor<Authenticate>] Principal caller`).
**No** cubre lo que tiene que envolver la llamada entera: retry, timing, logging de excepción, circuit breaker. Un guard se ejecuta antes o después, nunca *alrededor*.

### ¿Los pipelines async aceptan `CancellationToken`?

**No.** `IAsyncPipeline<…>.ProcessAsync(T input)` e `IAsyncValuePipeline<…>.ProcessAsync(T input)` solo reciben el valor.
El token existe en el método RPC generado, en el transporte (`MemoryConnection` → `MemoryRpc.CallAsync(…, token)`) y en el handler del host (`MemoryRequestHandler(…, CancellationToken token)`), pero el generador no lo pasa a las etapas.
Consecuencia: un pipeline async con E/S (p. ej. `PostgresAuthorizationPipeline`) no se cancela con la llamada.

**Propuesta G1**: añadir el token a las firmas async (cambio incompatible, aceptable: hay 3 implementaciones en el repo, `Exclaim`, `Bracket`, `PostgresAuthorizationPipeline`):

```csharp
public interface IAsyncPipeline<T>                { Task<(ResponseStatus, T)>         ProcessAsync(T input, CancellationToken token); }
public interface IAsyncPipeline<TIn, TOut>        { Task<(ResponseStatus, TOut)>      ProcessAsync(TIn input, CancellationToken token); }
public interface IAsyncValuePipeline<T>           { ValueTask<(ResponseStatus, T)>    ProcessAsync(T input, CancellationToken token); }
public interface IAsyncValuePipeline<TIn, TOut>   { ValueTask<(ResponseStatus, TOut)> ProcessAsync(TIn input, CancellationToken token); }
```

El generador ya tiene `token` en ámbito en ambos lados; solo cambia `ServiceHandlers.Pipelines.cs` (líneas ~210-211) para emitir `.ProcessAsync(expr, token)`.
En el camino sync (`.GetAwaiter().GetResult()`) se pasa el token del método sync si existe, o `default`.
Los sync (`IPipeline`) no cambian.

### Guards a nivel de contrato

Hoy un guard que no transforma (p. ej. "rol admin") tiene que colgar de un parámetro.
**Propuesta G2**: permitir `ClientProcessor<T>`/`ServerProcessor<T>` también en `Method | Interface` cuando `T : IPipeline<TReq>`/`IAsyncValuePipeline<TReq>` y `TReq` es la tupla de parámetros de la operación. Es azúcar sobre lo que ya existe: el generador lo emite igual que un processor de parámetro. Decidir solo si aparece un caso real (YAGNI).

## 2. Wrappers alrededor de la llamada

### 2.1 Forma: processors fuera, wrappers dentro

Los processors se quedan como están (sentencias planas, una vez). Los wrappers envuelven **solo** la llamada (RPC en el cliente, implementación en el host):

```
if (!pre1) throw/Fail            // processors de entrada: una vez, aunque haya reintentos
if (!pre2) throw/Fail

result = W1(→ W2(→ CallRemoteOrServerImplementation()))   // wrappers: el 1º declarado es el más externo

if (!post1) throw/Fail           // processors de salida: una vez, sobre el resultado final

return result                    // host: bytes serializados; cliente: valor tipado
```

Consecuencias:

- Un reintento **no** repite autenticación/validación/transformación: solo repite la llamada.
- El wrapper **no ve la petición**: recibe "la llamada" ya con sus argumentos procesados. Por eso no necesita `TReq`, ni tuplas de parámetros, ni `Unit` para la entrada; solo `TRes`.
- En el cliente, si la serialización va dentro de la llamada, cada reintento re-serializa. Optimización posible: serializar antes de los wrappers y que la llamada solo envíe el buffer (lo decide el struct de la llamada; el wrapper no cambia).

### 2.2 Contrato

```csharp
namespace SourceCrafter.LiteSpeedLink;

// "Lo siguiente": el generador lo emite como readonly struct por operación y capa.
public interface ICall<TRes>      { TRes Invoke(); }
public interface IAsyncCall<TRes> { ValueTask<TRes> InvokeAsync(); }

// Wrapper agnóstico de la operación: un mismo Retry sirve para todas.
public interface IWrapperPipeline
{
	TRes Invoke<TRes, TCall>(TCall call, CancellationToken token) where TCall : struct, ICall<TRes>;
}

public interface IAsyncWrapperPipeline
{
	ValueTask<TRes> InvokeAsync<TRes, TCall>(TCall call, CancellationToken token) where TCall : struct, IAsyncCall<TRes>;
}

// Declaración: orden declarado = de fuera hacia dentro. Interfaz antes que método.
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class WrapperAttribute<T> : Attribute;        // ambos lados
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ClientWrapperAttribute<T> : Attribute;
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ServerWrapperAttribute<T> : Attribute;
```

El token se pasa al wrapper (para no reintentar si el llamante canceló) y además va capturado en el struct de la llamada, que es quien lo entrega al transporte.

### 2.3 Código generado (cliente, `[ClientWrapper<Timing>][ClientWrapper<Retry>] int Hit([ClientProcessor<Check>] int n)`)

Wrappers obligatoriamente `readonly struct`: el generador solo emite el struct de la llamada real; cada capa interior es `AsyncWrappedCall<TWrapper, TNext, TRes>` de librería (todo argumento genérico es struct ⇒ el JIT especializa, sin dispatch).

```csharp
private readonly struct __Hit_Call(SecureClient c, int n, CancellationToken token) : IAsyncCall<int>
{
	public ValueTask<int> InvokeAsync() => c.__HitRpcAsync(n, token);
}

public async ValueTask<int> HitAsync(int n, CancellationToken token = default)
{
	if (__check.Process(n) is not (ResponseStatus.Success, int __n0)) throw new PipelineRejectedException(...);

	var __r = await __timing.InvokeAsync<int, AsyncWrappedCall<RetryPolicy, __Hit_Call, int>>(new(__retry, new(this, __n0, token), token), token).ConfigureAwait(false);

	// processors de retorno sobre __r
	return __r;
}
```

En el host es igual dentro del `case`: processors de parámetros → wrappers alrededor de `___scope.Secure.Hit(n)` → processors de retorno → serializar respuesta.

Por qué `AsyncWrappedCall<>` de librería y no lambdas ni structs intermedios generados (medido, 2.5):

- Lambda: 64-104 B/llamada (delegate + closure) y llamada indirecta.
- `AsyncWrappedCall<TWrapper, TNext, TRes>` con wrapper `readonly struct`: 0 B, sin dispatch, mismo coste que un struct por capa (4,7-4,9 ns vs 4,7). Con wrapper `class` el JIT compartiría código (+~3 ns/capa); por eso el wrapper debe ser `readonly struct` y sobran los structs intermedios generados.

### 2.4 Wrapper de usuario y reglas

```csharp
// Implementado en Abstractions como RetryPolicy (Attempts, Interval; sync + async); test: RetryPolicyTest.
public readonly struct Retry() : IAsyncWrapperPipeline
{
	public TimeSpan Interval { get; init; }

	[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]   // obligatorio: es async
	public async ValueTask<TRes> InvokeAsync<TRes, TCall>(TCall call, CancellationToken token) where TCall : struct, IAsyncCall<TRes>
	{
		for (var attempt = 1; ; attempt++)
		{
			// Solo TimeoutException es transitoria: cualquier otra excepción sale en el primer intento.
			try { return await call.InvokeAsync().ConfigureAwait(false); }
			catch (TimeoutException) when (attempt < 3 && !token.IsCancellationRequested) { }

			if (Interval > TimeSpan.Zero) await Task.Delay(Interval, token).ConfigureAwait(false);
		}
	}
}

// Paso directo sin await: no es async, no necesita builder.
public readonly struct Timing(Metrics metrics) : IAsyncWrapperPipeline
{
	public ValueTask<TRes> InvokeAsync<TRes, TCall>(TCall call, CancellationToken token) where TCall : struct, IAsyncCall<TRes>
	{
		metrics.Calls++;
		return call.InvokeAsync();
	}
}
```

Reglas, comprobadas por el generador (ya inspecciona el tipo del atributo) como **errores**:

| Regla | Por qué |
|---|---|
| `InvokeAsync` con `async` debe llevar `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]` | sin él, +125-133 B por llamada que suspende |
| El wrapper debe ser `readonly struct` | se copia en cada capa; con `class` como argumento genérico el JIT comparte código (+~3 ns/capa, 2.5) |
| Wrapper resuelto de DI como los processors; sin registro → error (como SCLSL013) | mismo modelo que los processors |
| Wrapper sync en operación async o viceversa → error | no se mezcla sync-over-async en silencio |

El builder pooled implica que el `ValueTask` devuelto solo puede esperarse una vez (regla general de `ValueTask`; el código generado la cumple).

### 2.5 Medido

`test/POC/PipelineWrapperPoc`, .NET 10, Release, BenchmarkDotNet `[MemoryDiagnoser]`. Retry de 3 intentos (+ un contador como 2ª capa) alrededor de una llamada `int → int`.
Síncrono: el transporte termina síncrono (camino rápido de Memory). Suspende: el transporte suspende de verdad (`Task.Yield`).

| Variante | Síncrono: B | Suspende: B | Síncrono: ns |
|---|---:|---:|---:|
| Sin wrapper | 0 | 112 | 0,7 |
| **Forma 2.3: 2 wrappers `class`, struct por capa, pooled** (`Final2LayersClass`) | **0** | **112** | **4,7** |
| 2 wrappers `class`, struct por capa, sin pooled | 0 | 236-246 | 4,4 |
| 2 wrappers `struct` + `Chain<>` de librería, pooled | 0 | 112 | 4,7-4,9 |
| 2 wrappers, interno `class` en `Chain<>` | 0 | 112 | 7,8-8,0 |
| Lambda `static` + estado explícito | 0 | 243-255 | 5,2-5,3 |
| Lambda que captura `this` | 64 | 300-309 | 7,8 |
| Lambda con closure | 96 | 331-341 | 9,9-10,5 |
| 2 capas de lambdas con closure | 104 | 347-349 | 10,8-11,8 |

Asignaciones deterministas en todas las pasadas; los tiempos con transporte que suspende (~3-4 µs) tienen demasiado ruido para comparar variantes.
Coste de 2 capas: ~4 ns, invisible frente a una llamada RPC (~11 µs en Memory).

Comparación: el retry manual de `AuthRetryPoc` (lambda + `CreateLinkedTokenSource` + `CancelAfter` por intento) cuesta **+432 B/llamada**, casi todo por el CTS: el plazo por intento debe venir del transporte (2.6).

### 2.6 Retry: semántica y plazo

- Plazo por intento: **el del transporte** (`MemoryConnection` ya tiene timeout por llamada, 5000 ms por defecto; en UDP, reenvío con el mismo corrId desde un temporizador por conexión). Nada de `CreateLinkedTokenSource` + `CancelAfter` por intento.
- Se reintenta con `TimeoutException`, nunca si el token del llamante está cancelado.
- Exige handlers idempotentes (POC `AuthRetryPoc`: respuesta "perdida" → el handler se ejecuta 2 veces). La respuesta tardía del intento abandonado la descarta `MemClient` (id con generación).
- Útil sobre todo en UDP; en TCP/QUIC/Pipes el transporte ya retransmite.

## 3. Guard vs wrapper: cuándo usar cada uno

| Necesidad | Usar |
|---|---|
| Autenticar, validar, transformar un parámetro o el retorno | Processor (guard) |
| Rechazar con `Failed` sin ejecutar | Processor (guard) |
| Ejecutar 0..N veces la llamada (retry, circuit breaker, caché) | Wrapper |
| Medir/registrar la llamada entera, incluida la excepción | Wrapper |
| Necesita el token de la llamada | Ambos (processor tras G1) |

## 4. Abierto

1. Re-serialización en reintentos del cliente (2.1): serializar antes de los wrappers si el perfil lo justifica.
2. Streams (`IEnumerable`/`IAsyncEnumerable`): fuera de alcance. Processors de retorno → SCLSL012 (procesar = desempaquetar y reempaquetar cada elemento). Retry → SCLSL016 y se ignora: el fallo llega a mitad de la enumeración y repetir reenviaría lo ya entregado; reanudar exigiría un checkpoint en el contrato.
3. Cortocircuito desde un wrapper (p. ej. circuit breaker abierto): lanzar `PipelineRejectedException` → `Failed` en el catch del handler (ya existe). No se añade un canal de estado propio.
4. G1 y G2 son independientes de los wrappers; G1 merece la pena ya (el token se pierde hoy).

## 5. Plan de implementación (cada paso con test)

1. G1: token en `ProcessAsync` + test de cancelación de una etapa async en el host y en el cliente.
2. `ICall`/`IAsyncCall`/`I(Async)WrapperPipeline` + `[(Client|Server)?Wrapper<T>]` en `Pipeline.cs`.
3. Generador cliente: struct `__Op_Call` + uno por wrapper interior, processors fuera; test de generación (`GeneratorHarness`) y test E2E de retry sobre Memory (fallos inyectados como en `AuthRetryPoc`) que compruebe que los processors de parámetros se ejecutan una vez con 2 intentos.
4. Generador host: igual dentro del `case`; test E2E de un wrapper de timing.
5. Diagnósticos (errores): `async` sin builder pooled, struct no `readonly`, wrapper no registrado, wrapper sync en camino async y viceversa.
6. Benchmark en `LiteSpeedLink.Benchmarks`: llamada Memory con y sin `[ClientWrapper<Retry>]`; criterio: +0 B.
