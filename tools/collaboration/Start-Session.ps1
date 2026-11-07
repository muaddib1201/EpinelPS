param([string]$SessionId = $env:CODEX_THREAD_ID)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SessionId)) { throw 'Provide the current chat id with -SessionId. Each chat must use its own id.' }
& python (Join-Path $PSScriptRoot 'nikke_workflow.py') start --session $SessionId
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize an isolated session. Do not edit the shared source checkout.' }
