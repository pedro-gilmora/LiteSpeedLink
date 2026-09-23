# Copilot Instructions

## Directrices del proyecto
- En LiteSpeedLink, los clientes y servidores generados están pensados para usarse en contextos predecibles y typesafe, donde cliente y host se generan a partir de los mismos contratos en tiempo de compilación. NO deben diseñarse como clientes/servidores genéricos agnósticos del runtime: hay que evitar proponer negociación de protocolo, handshakes de versión, descubrimiento dinámico o robustez para usos arbitrarios fuera del flujo generado.