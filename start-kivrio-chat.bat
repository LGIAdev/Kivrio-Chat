@echo off
setlocal
for %%I in ("%~dp0.") do set "ROOT=%%~fI"
set "APP_ID=kivrio-chat"
set "PORT_START=8020"
set "PORT_END=8029"
set "SERVER_EXE=%ROOT%\bin\kivrio-chat-server.exe"
set "SERVER_SRC=%ROOT%\server\KivrioChatServer.cs"
set "WAIT_SECONDS=30"

if not exist "%SERVER_EXE%" (
  call :compile_server
  if errorlevel 1 (
    pause
    exit /b 1
  )
)

call :is_port_busy %PORT_START%
if not errorlevel 1 (
  call :is_expected_app %PORT_START%
  if not errorlevel 1 (
    set "PORT=%PORT_START%"
    goto open_browser
  )
  call :find_free_port
  if errorlevel 1 (
    echo [ERREUR] Aucun port local disponible pour Kivrio Chat entre %PORT_START% et %PORT_END%.
    exit /b 1
  )
  goto start_server
)

set "PORT=%PORT_START%"
goto start_server

:start_server
start "" /D "%ROOT%" "%SERVER_EXE%" --root "%ROOT%" --host 127.0.0.1 --port %PORT%
for /L %%I in (1,1,%WAIT_SECONDS%) do (
  call :is_expected_app %PORT%
  if not errorlevel 1 goto open_browser
  timeout /t 1 /nobreak >nul
)
echo [ERREUR] Kivrio Chat n'a pas demarre sur le port %PORT%.
exit /b 1

:open_browser
start "" "http://127.0.0.1:%PORT%/index.html?t=%RANDOM%"
exit /b 0

:compile_server
if not exist "%SERVER_SRC%" (
  echo [ERREUR] Serveur autonome introuvable: "%SERVER_SRC%"
  exit /b 1
)
set "CSC="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not defined CSC (
  echo [ERREUR] Compilateur C# .NET Framework introuvable.
  exit /b 1
)
if not exist "%ROOT%\bin" mkdir "%ROOT%\bin"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"%SERVER_EXE%" /r:System.Web.Extensions.dll "%SERVER_SRC%"
if errorlevel 1 (
  echo [ERREUR] Compilation du serveur autonome impossible.
  exit /b 1
)
exit /b 0

:is_port_busy
netstat -ano | findstr /R /C:":%~1 .*LISTENING" >nul
exit /b %errorlevel%

:find_free_port
for /L %%P in (%PORT_START%,1,%PORT_END%) do (
  call :is_port_busy %%P
  if errorlevel 1 (
    set "PORT=%%P"
    exit /b 0
  )
)
exit /b 1

:is_expected_app
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $u = 'http://127.0.0.1:%~1/api/health'; $req = [System.Net.WebRequest]::Create($u); $req.Timeout = 300; $req.ReadWriteTimeout = 300; $res = $req.GetResponse(); $reader = [System.IO.StreamReader]::new($res.GetResponseStream()); $j = $reader.ReadToEnd() | ConvertFrom-Json; $reader.Close(); $res.Close(); if ($j.app -eq '%APP_ID%') { exit 0 } } catch { } exit 1" >nul 2>nul
exit /b %errorlevel%
