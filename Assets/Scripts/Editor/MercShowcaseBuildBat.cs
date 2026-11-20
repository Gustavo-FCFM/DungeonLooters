using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// ============================================================
// MercShowcaseBuildBat
//
// Cada build de Windows sale con un Showcase.bat al lado del .exe (28 de octubre de 2026, etapa
// 3 del showcase): abre tres ventanas del juego en esta PC, una por monitor.
//   · Ventana 1 (monitor 1): teclado y mouse (-kbm). Es la que suena.
//   · Ventanas 2 y 3 (monitores 2 y 3): una con cada control (-pad), sin sonido (-mute). Al
//     abrir piden "Aprieta A en tu control": clic en la ventana y A en el control que va a
//     jugar ahí (ver LocalSeat).
// Cada una escribe su propio log al lado (Showcase_1.log, …): con tres ventanas, el Player.log
// de siempre se lo pelean.
//
// No es de una vez: corre solo en cada build. Para otra combinación (dos ventanas, todas con
// control), se edita el .bat que salió, o el texto de abajo. Y desde el 20 de noviembre de 2026 sale
// también Controles.bat: X ventanas con X controles (pregunta cuántas; ver ControlsBat).
// ============================================================
public class MercShowcaseBuildBat : IPostprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPostprocessBuild(BuildReport report)
    {
        BuildTarget target = report.summary.platform;
        if (target != BuildTarget.StandaloneWindows && target != BuildTarget.StandaloneWindows64) return;

        string exePath = report.summary.outputPath;
        string folder  = Path.GetDirectoryName(exePath);
        string exe     = Path.GetFileName(exePath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(exe)) return;

        string bat =
            "@echo off\r\n" +
            "rem Mercenaries - el showcase: tres ventanas del juego en esta PC, una por monitor.\r\n" +
            "rem   Ventana 1 (monitor 1): teclado y mouse. Es la que suena.\r\n" +
            "rem   Ventanas 2 y 3 (monitores 2 y 3): una con cada control, sin sonido. Al abrir piden\r\n" +
            "rem   \"Aprieta A en tu control\": clic en la ventana y A en el control que juega ahi.\r\n" +
            "rem Cada ventana deja su log al lado (Showcase_1.log, ...).\r\n" +
            "rem Lo arma cada build (MercShowcaseBuildBat). Para otra combinacion, editar este archivo.\r\n" +
            "cd /d \"%~dp0\"\r\n" +
            $"start \"\" \"{exe}\" -kbm -monitor 1 -logFile \"%~dp0Showcase_1.log\"\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            $"start \"\" \"{exe}\" -pad -mute -monitor 2 -logFile \"%~dp0Showcase_2.log\"\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            $"start \"\" \"{exe}\" -pad -mute -monitor 3 -logFile \"%~dp0Showcase_3.log\"\r\n";

        string path = Path.Combine(folder, "Showcase.bat");
        File.WriteAllText(path, bat, new System.Text.UTF8Encoding(false));
        Debug.Log($"[Showcase] Dejé {path}: abre tres ventanas (teclado y mouse + dos controles).");

        string controls = Path.Combine(folder, "Controles.bat");
        File.WriteAllText(controls, ControlsBat(exe), new System.Text.UTF8Encoding(false));
        Debug.Log($"[Showcase] Dejé {controls}: abre X ventanas, cada una con su control.");
    }

    // CONTROLES.BAT (20 de noviembre de 2026, pedido de Gustavo): X ventanas con X controles, y si se quiere una más con
    // teclado y mouse. Pregunta cuántas al abrirlo (o se le pasan: "Controles.bat 4 s"). Reparte las ventanas entre los
    // monitores que haya; si son más ventanas que monitores, las abre en ventana chica (960×540) en vez de pantalla
    // completa, para que entren. Suena solo la primera. Cada una deja su log (Ventana_1.log, …).
    public static string ControlsBat(string exe)
    {
        return
            "@echo off\r\n" +
            "rem Mercenaries - X ventanas del juego en esta PC, cada una con su control.\r\n" +
            "rem   Al abrir, cada ventana pide \"Aprieta A en tu control\": clic en la ventana y A en el control que juega ahi.\r\n" +
            "rem   Uso: doble clic (pregunta), o \"Controles.bat 4 s\" = 4 con control + 1 con teclado y mouse.\r\n" +
            "rem Lo arma cada build (MercShowcaseBuildBat).\r\n" +
            "setlocal EnableDelayedExpansion\r\n" +
            "cd /d \"%~dp0\"\r\n" +
            "set N=%~1\r\n" +
            "if \"%N%\"==\"\" set /p N=Cuantas ventanas con control? \r\n" +
            "set /a N=%N% 2>nul\r\n" +
            "if %N% lss 1 ( echo Tiene que ser 1 o mas. & pause & exit /b 1 )\r\n" +
            "set K=%~2\r\n" +
            "if \"%K%\"==\"\" set /p K=Ademas una con teclado y mouse? (s/n): \r\n" +
            "set MON=1\r\n" +
            "for /f %%m in ('powershell -NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Screen]::AllScreens.Count\"') do set MON=%%m\r\n" +
            "set /a TOTAL=N\r\n" +
            "if /i \"%K%\"==\"s\" set /a TOTAL=N+1\r\n" +
            "set WIN=\r\n" +
            "if %TOTAL% gtr %MON% set WIN=-screen-fullscreen 0 -screen-width 960 -screen-height 540\r\n" +
            "echo Abriendo %TOTAL% ventanas en %MON% monitor(es)...\r\n" +
            "set I=0\r\n" +
            "set SOUND=\r\n" +
            "if /i \"%K%\"==\"s\" (\r\n" +
            "  set /a I+=1\r\n" +
            $"  start \"\" \"{exe}\" -kbm -monitor 1 !WIN! -logFile \"%~dp0Ventana_1.log\"\r\n" +
            "  set SOUND=-mute\r\n" +
            "  timeout /t 2 /nobreak >nul\r\n" +
            ")\r\n" +
            "for /l %%p in (1,1,%N%) do (\r\n" +
            "  set /a I+=1\r\n" +
            "  set /a \"M=(I-1) %% MON + 1\"\r\n" +
            $"  start \"\" \"{exe}\" -pad !SOUND! -monitor !M! !WIN! -logFile \"%~dp0Ventana_!I!.log\"\r\n" +
            "  set SOUND=-mute\r\n" +
            "  timeout /t 2 /nobreak >nul\r\n" +
            ")\r\n";
    }
}
