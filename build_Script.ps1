Get-Process | Where-Object { $_.ProcessName -like 'PCMode*' } | Stop-Process -Force -ErrorAction SilentlyContinue

dotnet build C:\Tools\PCMode\app\PCMode.slnx

if ($LASTEXITCODE -eq 0)
{
    Start-Process "C:\Tools\PCMode\app\src\PCMode.Tray\bin\Debug\net10.0-windows\PCMode.Tray.exe"
}
