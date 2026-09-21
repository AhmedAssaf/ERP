# Claude Code PostToolUse hook (matcher: Bash). Reads the hook JSON from stdin; if the command that just ran
# contained "git push", launches the OneNote mirror detached so the session is never blocked.
$raw = [Console]::In.ReadToEnd()
try { $j = $raw | ConvertFrom-Json } catch { exit 0 }
$cmd = ''
if ($j -and $j.tool_input -and $j.tool_input.command) { $cmd = [string]$j.tool_input.command }
if ($cmd -notmatch 'git\s+push') { exit 0 }
$script = Join-Path $PSScriptRoot 'mirror-onenote.ps1'
Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$script`"") -WindowStyle Hidden | Out-Null
Write-Output '{"systemMessage": "OneNote mirror started in the background (tools/mirror-onenote.ps1)."}'
exit 0
