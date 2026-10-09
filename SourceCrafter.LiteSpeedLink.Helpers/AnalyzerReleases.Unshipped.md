; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SCLSL010 | LiteSpeedLink | Error | Processor type is not a pipeline
SCLSL011 | LiteSpeedLink | Error | Pipeline chain type mismatch
SCLSL012 | LiteSpeedLink | Error | Pipeline not supported here
SCLSL013 | LiteSpeedLink | Error | Pipeline is not registered
SCLSL014 | LiteSpeedLink | Error | Pipeline must be implemented implicitly
SCLSL015 | LiteSpeedLink | Warning | Retry on both client and server
SCLSL016 | LiteSpeedLink | Warning | Retry ignored on streams
SCLSL017 | LiteSpeedLink | Warning | Cache ignored
SCLSL018 | LiteSpeedLink | Info | Cache key falls back to bytes
SCLSL019 | LiteSpeedLink | Error | Invalid cache key
