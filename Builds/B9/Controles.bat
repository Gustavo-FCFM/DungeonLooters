@echo off
rem Mercenaries - X ventanas del juego en esta PC, cada una con su control.
rem   Al abrir, cada ventana pide "Aprieta A en tu control": clic en la ventana y A en el control que juega ahi.
rem   Uso: doble clic (pregunta), o "Controles.bat 4 s" = 4 con control + 1 con teclado y mouse.
rem Lo arma cada build (MercShowcaseBuildBat).
setlocal EnableDelayedExpansion
cd /d "%~dp0"
set N=%~1
if "%N%"=="" set /p N=Cuantas ventanas con control? 
set /a N=%N% 2>nul
if %N% lss 1 ( echo Tiene que ser 1 o mas. & pause & exit /b 1 )
set K=%~2
if "%K%"=="" set /p K=Ademas una con teclado y mouse? (s/n): 
set MON=1
for /f %%m in ('powershell -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Screen]::AllScreens.Count"') do set MON=%%m
set /a TOTAL=N
if /i "%K%"=="s" set /a TOTAL=N+1
set WIN=
if %TOTAL% gtr %MON% set WIN=-screen-fullscreen 0 -screen-width 960 -screen-height 540
echo Abriendo %TOTAL% ventanas en %MON% monitor(es)...
set I=0
set SOUND=
if /i "%K%"=="s" (
  set /a I+=1
  start "" "GasTest.exe" -kbm -monitor 1 !WIN! -logFile "%~dp0Ventana_1.log"
  set SOUND=-mute
  timeout /t 2 /nobreak >nul
)
for /l %%p in (1,1,%N%) do (
  set /a I+=1
  set /a "M=(I-1) %% MON + 1"
  start "" "GasTest.exe" -pad !SOUND! -monitor !M! !WIN! -logFile "%~dp0Ventana_!I!.log"
  set SOUND=-mute
  timeout /t 2 /nobreak >nul
)
