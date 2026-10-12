@echo off
REM OpsFlow - yerel baslatici (Windows).
REM Gereksinimler: .NET 10 SDK ve localhost:5432 uzerinde calisan PostgreSQL.
REM Varsayilan baglanti appsettings.Development.json icindedir; degistirmek icin:
REM   set ConnectionStrings__Default=Host=localhost;Database=opsflow;Username=...;Password=...
REM PostgreSQL yoksa bellek ici mod:
REM   set Database__Provider=InMemory
setlocal
cd /d "%~dp0"
set ASPNETCORE_ENVIRONMENT=Development
set ASPNETCORE_URLS=http://localhost:5080
echo OpsFlow baslatiliyor: http://localhost:5080
REM Tarayici, sunucu dinlemeye basladiktan sonra acilir (ilk yukleme hata vermesin).
start "" /b cmd /c "timeout /t 8 /nobreak >nul & start http://localhost:5080"
dotnet run --project src\OpsFlow.API
endlocal
