# LiteSpeedLink samples

Proyectos independientes que consumen los paquetes publicados en nuget.org (`LiteSpeedLinkVersion` en `Directory.Build.props`). Solo necesitan el SDK de .NET 10: sin bases de datos, certificados ni navegador.

| Sample | Plantilla | Transporte | Muestra |
|---|---|---|---|
| [Chat](Chat) | Console | TCP | streams en vivo, pipeline de servidor (moderación), estado compartido entre conexiones |
| [Jobs](Jobs) | Worker Service + CLI | Local (shared memory / UDS) | contrato compartido, agente en segundo plano, CLI que habla con él |

## Chat

```powershell
cd samples/Chat
dotnet run                                   # demo: servidor + Alice y Bob en un proceso, termina con OK
dotnet run -- server 5300                    # o servidor aparte...
dotnet run -- join alice lobby localhost 5300  # ...y un cliente interactivo por terminal
```

## Jobs

```powershell
cd samples/Jobs
dotnet run --project Jobs.Agent                                 # terminal 1: agente
dotnet run --project Jobs.Cli -- hash ../../README.md other.bin # terminal 2: encola SHA-256 y muestra el progreso
dotnet run --project Jobs.Cli -- list
```

El agente es un Worker Service: se instala como servicio de Windows o unidad systemd sin cambios.
