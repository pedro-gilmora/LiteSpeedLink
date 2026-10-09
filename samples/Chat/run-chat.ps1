# Lanza el servidor y un cliente por usuario, cada uno en su propia terminal.
# .\run-chat.ps1                       -> server + alice + bob
# .\run-chat.ps1 -Users alice,bob,carol -Room dev -Name my-chat
param([string[]]$Users = @('alice', 'bob'), [string]$Room = 'lobby', [string]$Name = 'my-chat-test')

$ErrorActionPreference = 'Stop'
$Users = $Users -split ','

function Test-Host { try { [IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting("${Name}_Host").Dispose(); $true } catch { $false } }

if (Test-Host) { throw "A chat server '$Name' is already running: close it or use -Name." }

dotnet build $PSScriptRoot -nologo -v q -clp:ErrorsOnly
if ($LASTEXITCODE) { exit $LASTEXITCODE }

function Open([string]$title, [string]$runArgs) {
	Start-Process powershell -WorkingDirectory $PSScriptRoot -ArgumentList '-NoExit', '-Command', "`$Host.UI.RawUI.WindowTitle = '$title'; dotnet run --no-build -- $runArgs"
}

Open 'chat server' "server $Name"

# Los clientes fallan si el host aun no creo su anillo: esperar a que exista.
$deadline = (Get-Date).AddSeconds(15)
while (-not (Test-Host)) {
	if ((Get-Date) -gt $deadline) { throw "Chat server '$Name' did not start." }
	Start-Sleep -Milliseconds 200
}

foreach ($user in $Users) { Open "chat $user" "join $user $Room $Name" }
