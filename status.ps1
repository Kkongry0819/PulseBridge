[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
try { Invoke-RestMethod -Uri 'http://127.0.0.1:8976/text' -TimeoutSec 2 -ErrorAction Stop }
catch { Write-Output '♥ -- BPM · 离线' }
