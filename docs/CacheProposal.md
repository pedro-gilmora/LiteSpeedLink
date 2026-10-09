# Propuesta: `[ClientCache]` / `[ServerCache]`

Estado: propuesta (revisión 3: clave de caché).

## Objetivo

Evitar viajes (cliente) y trabajo repetido (servidor) en operaciones unarias de lectura. El generador decide
todo en compilación: qué se cachea, con qué clave, en qué punto se consulta y con qué tipos. La caché no añade
bytes al cable.

## API

```csharp
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ClientCacheAttribute(int durationMs, int capacity = 1024) : Attribute
{
	public int DurationMs { get; } = durationMs;
	public int Capacity { get; } = capacity;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ServerCacheAttribute(int durationMs, int capacity = 1024) : Attribute
{
	public int DurationMs { get; } = durationMs;
	public int Capacity { get; } = capacity;
}

/// Qué forma la clave (ver «Clave»). En un parámetro: el parámetro entero o, con miembros, rutas relativas a su
/// tipo. En el método: rutas con raíz en el nombre del parámetro. Sin [CacheKey], todos los de payload.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class CacheKeyAttribute(params string[] members) : Attribute
{
	public string[] Members { get; } = members;
}
```

| Parámetro    | Obligatorio | Efecto |
|--------------|-------------|--------|
| `durationMs` | sí          | TTL absoluto desde que se guarda. Obligatorio: evita cachés eternas por descuido. |
| `capacity`   | no (1024)   | Máximo de entradas por operación. Sin parámetros de clave se ignora (una entrada). |
| `[CacheKey]` | no          | Cliente y servidor. Seleccionar parámetros o miembros declara que el resto no afecta al resultado (responsabilidad del desarrollador, como el emparejamiento de extremos en P2). |

Se leen desde `AttributeData.ConstructorArguments` (posicional; Roslyn normaliza argumentos con nombre y defaults).

Reglas comunes: solo se guardan respuestas `Success` (nunca `Failed`, `NotFound`, timeout ni cancelación);
TTL con `Environment.TickCount64`; una caché por método (la operación va implícita).

## Servidor: bytes → bytes

`ServerCacheManager` guarda **la respuesta serializada, ya pasada por los server return processors**. Un acierto
se salta la lectura de argumentos, la resolución por DI, el handler, los return processors y la serialización.

### `ConcurrentDictionary<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>`

`Span<T>` es un `ref struct` y no puede ser argumento genérico ni vivir en el heap; `ReadOnlyMemory<byte>` sí, y es
lo que ya usan el transporte (`Body`) y la escritura (`WriteRawAsync`). Pero su `Equals` compara referencia,
offset y longitud, no contenido: hace falta un comparador propio.

```csharp
public sealed class ServerCacheManager(int durationMs, int capacity)
{
	// Clave y valor copiados al guardar (Body vive en un buffer del pool); el valor sale tal cual a WriteRawAsync.
	private readonly ConcurrentDictionary<ReadOnlyMemory<byte>, (ReadOnlyMemory<byte> Response, long Expires)> _entries =
		new(ByteContentComparer.Instance);

	// .NET 9+: consulta por el span del Body sin materializar la clave.
	private readonly ConcurrentDictionary<ReadOnlyMemory<byte>, (ReadOnlyMemory<byte>, long)>.AlternateLookup<ReadOnlySpan<byte>> _lookup;

	public bool TryGet(ReadOnlyMemory<byte> request, out ReadOnlyMemory<byte> response);
	public void Set(ReadOnlyMemory<byte> request, ReadOnlyMemory<byte> response);
}

// IEqualityComparer<ReadOnlyMemory<byte>> + IAlternateEqualityComparer<ReadOnlySpan<byte>, ReadOnlyMemory<byte>>:
// hash con System.HashCode.AddBytes(span), igualdad con span.SequenceEqual.
```

- Acierto: 0 asignaciones (lookup por `request.Span`, `WriteRawAsync` del `ReadOnlyMemory<byte>` guardado).
- Fallo: una copia de la clave y otra de la respuesta al guardar (`ToArray()`: nunca se guarda memoria del pool).
- Sin parámetros de payload: el generador emite un campo `Entry?` (clase inmutable `(Response, Expires)`) en lugar del
  manager; se publica con `Volatile.Write` (una tupla de structs podría leerse a medias).
- Sin `[CacheKey]` la clave son los bytes de la petición: igualdad por contenido gratis, **ningún requisito sobre
  los tipos** (complejos incluidos) y el acierto se salta incluso la deserialización.
- Con `[CacheKey]` la clave es tipada (ver «Clave») y se construye tras leer los argumentos: un acierto sigue
  saltándose DI, handler, return processors y serialización. El valor sigue siendo bytes.

### Generado

```text
sin server param processors:  Body -[TryGet]-hit-> WriteRaw
								   -miss-> __ReqN -> retry{handler} -> return processors -> __ResN(bytes) -> Set -> WriteRaw
con server param processors:  Body -> __ReqN -> param processors -[TryGet(Body)]-hit-> WriteRaw
																  -miss-> retry{handler} -> ...
```

Con processors de parámetro (p.ej. autorización) la consulta va **después** de ellos: un acierto no puede
saltarse la autorización. El generador elige el punto en compilación. Requiere:

- `ReturnRaw`/`ReturnRawAsync(ReadOnlyMemory<byte>)` en `RequestContext`, `UdpRequestContext` y
  `MemoryRequestContext` (Pipes ya tiene `ResponseChannel.WriteRawAsync`).
- Escritor `__ResN` generado (como `__ReqN`) solo en métodos con `[ServerCache]`.
- Caché global por host, compartida entre conexiones: lo que dependa del llamante va en un param processor o no se cachea.
- Un manager por método cacheado, creado al primer uso: `__cacheN ?? Interlocked.CompareExchange(ref __cacheN, new(d, c), null) ?? __cacheN`.
  Sin `lock`: si dos hilos compiten, uno descarta su instancia vacía.

## Cliente: clave tipada → resultado tipado

### Sobre `ConcurrentDictionary<int, object>`

Dos problemas:

1. **Colisiones**: con el hash como clave, dos argumentos distintos con el mismo `int` devuelven el resultado del
   otro, en silencio. Con 32 bits y 1024 entradas por operación es improbable pero no imposible, y el error es
   un dato incorrecto. La clave debe conservar los valores para compararlos; el hash solo elige el bucket.
2. **Boxing**: `object` empaqueta cada resultado de tipo valor y obliga a un cast en cada acierto.

El generador conoce en compilación el tipo de la clave y del resultado, así que no hace falta `object`:

```csharp
public sealed class ClientCacheManager<TKey, TValue>(int durationMs, int capacity) where TKey : IEquatable<TKey>
{
	// El valor va inline en el nodo del diccionario: sin boxing ni asignación extra.
	private readonly ConcurrentDictionary<TKey, (TValue Value, long Expires)> _entries = new();

	public bool TryGet(in TKey key, out TValue value);
	public void Set(in TKey key, TValue value);
}
```

La forma de `TKey` la decide el generador (ver «Clave»).

### Generado

```text
args -[TryGet(key)]-hit-> resultado
	 -miss-> param processors -> __ReqN -> retry{transporte} -> __ResN -> return processors -> Set(key, resultado) -> resultado
```

- La clave son los argumentos **originales** (antes de los processors) y el valor es el resultado **final**
  (después de los return processors): un acierto no ejecuta nada más.
- La consulta va fuera del bucle de `[ClientRetry]`.
- Una caché por instancia de cliente.
- **Instancia compartida**: un acierto devuelve la misma referencia a todos los llamantes. Con resultados de tipo
  referencia mutables, mutarlos altera la caché. Se documenta; si hace falta, una opción `copy` reutilizaría
  `__ResN` guardando bytes (coste: deserializar en cada acierto).

## Clave

Regla: la clave nunca puede dar un falso acierto. Un fallo de más es aceptable; un dato de otro argumento, no.

### Igualdad por valor

Un tipo de hoja tiene igualdad por valor si (comprobación recursiva en el generador):

- es primitivo, `string`, `enum`, o un struct que implementa `IEquatable<T>` (`Guid`, `DateTime`, `decimal`...);
- es `Nullable<T>` de uno de ellos;
- es `record`/`record struct` y todos sus campos de instancia la tienen;
- declara `IEquatable<T>` explícitamente (el contrato es del desarrollador, como en P2).

No la tienen: clases sin `IEquatable<T>` (igualdad por referencia: nunca acertaría y llenaría la caché), structs sin
`IEquatable<T>` (`ValueType.Equals` empaqueta y usa reflexión), arrays y colecciones (por referencia; también
dentro de un record).

### Forma de la clave (decidida en compilación)

| Caso | Clave | Coste de un acierto |
|------|-------|---------------------|
| Sin parámetros de clave | campo único `Entry?` (inmutable, `Volatile`) | ninguno |
| Todas las hojas con igualdad por valor | 1 hoja: su tipo tal cual (`int` → `ConcurrentDictionary<int, …>`, sin `HashCode`); 2+: `ValueTuple` de las hojas | hash tipado, 0 asignaciones |
| Sin `[CacheKey]` y algún parámetro sin igualdad por valor | bytes serializados de los parámetros (`ByteContentComparer`) + `SCLSL018` (info) | serializar + hashear bytes |
| `[CacheKey]` con una hoja sin igualdad por valor | — | `SCLSL019` (error) |

El hash solo elige el bucket: un `int` de `HashCode.Combine` como clave daría falsos aciertos en colisiones, así que
la clave conserva los valores. `ValueTuple` ya lo hace (`IEquatable<>`, `EqualityComparer<T>.Default` por elemento,
hash combinado): sin struct generado.

La alternativa por bytes sirve para cualquier tipo serializable con MemoryPack sin trabajo del desarrollador:

- Cliente sin param processors: la clave **es** la salida de `__ReqN`; en un fallo esos bytes se envían tal cual,
  sin trabajo extra. Con param processors la clave se serializa aparte, desde los argumentos originales.
- Si dos valores iguales serializan distinto (`HashSet`/`Dictionary` sin orden, `-0.0`/`0.0`), es un fallo, nunca un
  falso acierto.

**Forzar la igualdad por valor** (la variante estricta): elevar el aviso en `.editorconfig`, sin API nueva:

```ini
dotnet_diagnostic.SCLSL018.severity = error
```

Con eso, todo parámetro complejo de un método cacheado debe ser record, implementar `IEquatable<T>` o reducirse a
miembros con `[CacheKey]`.

### `[CacheKey(params string[] members)]`

```csharp
// Parámetro entero (debe tener igualdad por valor); traceId queda fuera de la clave.
Task<Price> GetPrice([CacheKey] string sku, Guid traceId);

// Miembros de un parámetro complejo, relativos a su tipo (C# 12: nameof de miembros de instancia vía tipo).
Task<Order> Get([CacheKey(nameof(OrderQuery.Id), nameof(OrderQuery.Customer.Region))] OrderQuery query);

// En el método, con raíz en el nombre del parámetro (C# 11: parámetros en ámbito en atributos del método).
[CacheKey(nameof(query.Id), nameof(query.Customer.Region), nameof(currency))]
Task<Order> Get(OrderQuery query, string currency, Guid traceId);
```

`nameof(a.b.c)` evalúa a `"c"`: `AttributeData` pierde la ruta. El generador lee la sintaxis del atributo
(`ApplicationSyntaxReference`, como `GetAttributeParamsMap` en el parser de DI: posicional y, si no, por nombre):

- `nameof(...)`: recorre la cadena de acceso con el `SemanticModel`. Cada segmento ya es un símbolo validado por el
  compilador, así que la ruta no puede estar mal escrita y se renombra al refactorizar.
- Literal `"a.b.c"`: el generador resuelve cada segmento; si no existe, `SCLSL019`.

**Contrato en otro ensamblado** (lo habitual: `Application.Contracts` es una referencia, no código fuente del
proyecto generado). Sin sintaxis solo queda `AttributeData`: cada `nameof` ya es la cadena final, es decir, el último
segmento (`nameof(query.Customer.Region)` → `"Region"`). Resolución de cada cadena, siempre **ordinal (sensible a
mayúsculas)**, igual que el nombre del miembro declarado:

1. Ruta con puntos desde la raíz (el tipo del parámetro marcado, o los parámetros si `[CacheKey]` está en el método):
   `"Customer.Region"`, `"query.Id"`, `"currency"`.
2. Si no resuelve y es un solo segmento (un `nameof` anidado colapsado): búsqueda del miembro con ese nombre exacto
   en el grafo de propiedades/campos públicos de la raíz. Si hay uno solo, esa es la ruta; si hay varios, `SCLSL019`
   (ambiguo: usar la ruta literal `"Customer.Region"`).

`ponytail:` la búsqueda en anchura recorre como mucho 4 niveles y no entra en tipos con igualdad por valor
(son hojas) ni en colecciones; ampliar si un contrato real lo necesita.

Con sintaxis disponible (contrato en el mismo proyecto) se usa la ruta completa del `nameof` y el paso 2 nunca hace
falta. Así el mismo contrato produce la misma clave en cliente y servidor, se compile cada uno donde se compile.

Reglas de las rutas:

- Acepta elementos sueltos (`params`), `new[] { ... }` y expresión de colección `[ ... ]`.

- Solo propiedades y campos públicos de instancia: ni métodos ni indexadores.
- Los tramos de tipo referencia se recorren con `?.`; la hoja pasa a ser anulable.
- La hoja puede ser a su vez un record con igualdad por valor.
- `[CacheKey]` en el método y en parámetros a la vez: `SCLSL019`.
- Con algún `[CacheKey]`, los parámetros sin marcar quedan fuera de la clave. El `CancellationToken` nunca entra.

Generado (cliente, `Get(OrderQuery query, string currency, Guid traceId)` del ejemplo):

```csharp
// ClientCacheManager<(int?, string?, string), Order> __cache3
// OrderQuery y Customer son tipos referencia: cada tramo se recorre con ?. y la hoja pasa a ser anulable
var __key = (query?.Id, query?.Customer?.Region, currency);
if (__cache3.TryGet(__key, out var __cached)) return __cached;
```

## Expulsión y concurrencia

- Llena → purga de expiradas; si sigue llena, no se guarda.
  `ponytail:` sin LRU; cambiar a LRU/segmentos si la tasa de aciertos con caché llena lo justifica.
- Contador propio con `Interlocked` para la capacidad: `ConcurrentDictionary.Count` toma todos los locks.
- Ningún lock abarca un `await`: `TryGet` y `Set` son síncronos y cortos (lectura sin locks; escritura con el lock de
  un segmento del diccionario). El handler async corre entre ambos sin retener nada.
- **Single-flight**: fallos simultáneos con la misma clave ejecutan el handler una sola vez. Cada manager tiene una
  segunda tabla `ConcurrentDictionary<TKey, Task<TValue>>` de llamadas en curso, separada de los resultados:
  1. `TryGet` acierta → resultado.
  2. `TryAdd(key, tcs.Task)` gana → este llamador ejecuta el handler; al terminar publica el resultado (si es `Success`)
     con `Set`, completa el `TaskCompletionSource` y quita la entrada en curso (`finally`).
  3. Pierde → `await` de la tarea del ganador.
  - Excepción/cancelación/no `Success`: nada se guarda; los que esperaban reciben el mismo fallo y la siguiente
    llamada vuelve a intentarlo. Nunca se cachean errores.
  - `TaskCreationOptions.RunContinuationsAsynchronously`: los que esperan no corren en el hilo del ganador.
  - La cancelación de un seguidor solo cancela su espera (`WaitAsync(token)`), no la llamada del ganador.
  - Servidor: la clave en curso es la copia de los bytes de la petición (el buffer vuelve al pool tras el dispatch).

## Diagnósticos

`SCLSL017` (warning, «Cache ignored»):

- stream (no hay una respuesta única; reproducirlo duplicaría el coste de memoria);
- sin resultado (`void`/`Task`/`ValueTask`): es un comando y cachearlo suprimiría efectos;
- parámetros `out`/`ref`: el resultado no es solo el retorno;
- `durationMs <= 0` o `capacity <= 0`;
- `[CacheKey]` en un método sin `[ClientCache]`/`[ServerCache]`, o sobre el `CancellationToken`.

`SCLSL018` (info, «Cache key falls back to bytes»): sin `[CacheKey]`, el parámetro `{1}` de `{0}` no tiene igualdad
por valor y la clave serán sus bytes. Se puede elevar a error en `.editorconfig` para exigir igualdad por valor.

`SCLSL019` (error, «Invalid cache key»): ruta inexistente o no pública, método o indexador en la ruta, hoja sin
igualdad por valor, o `[CacheKey]` en el método y en parámetros a la vez.

`[ClientCache]` + `[ServerCache]` juntos es legítimo; la antigüedad máxima es la suma de ambos TTL.

## Pruebas

- `ServerCacheManager`/`ClientCacheManager`: acierto, expiración, capacidad, igualdad por contenido.
- Generador: acierto sin transporte ni handler (contadores), `Failed` sin cachear, `[CacheKey]` con subconjunto,
  rutas `nameof` (parámetro y método) y literales, `?.` en tramos de referencia, alternativa por bytes con `SCLSL018`,
  `SCLSL019` por ruta inválida u hoja sin igualdad, colisión de hash sin falso acierto,
  slot único sin parámetros, consulta fuera de `ClientRetry`, consulta tras los server param processors,
  `SCLSL017` en cada caso, argumentos con nombre (`[ClientCache(capacity: 8, durationMs: 100)]`).
- POC en `Program.cs`: contador de invocaciones del handler con caché en cliente y servidor.

## Pasos de implementación

1. Atributos en Abstractions; `ServerCacheManager` (Server) y `ClientCacheManager<TKey, TValue>` (Client) + tests.
2. Host: `ReturnRaw*` en los tres contextos, escritor `__ResN`, consulta/guardado; `SCLSL017`.
3. Clave: análisis de igualdad por valor, rutas de `[CacheKey]` desde la sintaxis, `__KeyN`, alternativa por
   bytes (`ClientCacheManager` con comparador; `ByteContentComparer` accesible desde cliente y servidor).
   `SCLSL018`/`SCLSL019`.
4. Cliente: consulta/guardado en la ruta raw y en la de processors.
5. Tests del generador + POC.
