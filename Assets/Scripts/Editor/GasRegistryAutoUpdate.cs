using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// ============================================================
// GasRegistryAutoUpdate — los registros de red se ponen al día SOLOS (17 de noviembre de 2026, de las "Mejoras del GAS")
//
// Antes: cada GA_* o GE_* nuevo había que meterlo a mano en los registros (Mercenarios ▸ Actualizar los registros de
// red), y si se olvidaba, su VFX o su ícono se veía SOLO en el host, sin ningún error. Ahora Unity avisa cada vez que
// importa, borra o mueve assets, y si entre ellos hay una habilidad o un efecto, se corre la misma herramienta:
//   · CREAR o DUPLICAR uno → entra a su registro;
//   · BORRARLO → sale (si no, queda una referencia rota);
//   · RENOMBRARLO o MOVERLO → la lista se reordena (va por nombre).
// Guardar cambios de un GA o un GE que YA está en el registro no hace nada (se mira antes de recorrer el proyecto).
// La herramienta solo toca el asset si la lista cambia, y dice en la consola qué entró y qué salió.
//
// No corre en Play (cambiaría los índices de la partida en curso): espera a salir. Tampoco mientras se hace una build.
// Se apaga en Mercenarios ▸ GAS ▸ Registros de red automáticos (se guarda por máquina, EditorPrefs).
//
// Recordatorio de siempre: después de un cambio de los registros, todos juegan con el MISMO build.
// ============================================================
public class GasRegistryAutoUpdate : AssetPostprocessor
{
    private const string PrefKey = "Mercenarios.GasRegistryAutoUpdate";
    private const string MenuPath = "Mercenarios/GAS/Registros de red automáticos";

    private static bool _pending;           // ya hay una actualización en cola
    private static bool _waitingForEditMode;

    // Lo que hay que mirar en la próxima vuelta del editor.
    private static bool _fullCheck;
    private static readonly List<string> _importedToCheck = new List<string>();

    public static bool Enabled
    {
        get => EditorPrefs.GetBool(PrefKey, true);
        set => EditorPrefs.SetBool(PrefKey, value);
    }

    [MenuItem(MenuPath, false, 30)]
    private static void Toggle()
    {
        Enabled = !Enabled;
        Debug.Log(Enabled
            ? "[Registros de red] Automáticos: se ponen al día solos al crear, borrar o renombrar un GA o un GE."
            : "[Registros de red] Automáticos APAGADOS: hay que correr Mercenarios ▸ Actualizar los registros de red a mano.");
        if (Enabled) Schedule(full: true);
    }

    [MenuItem(MenuPath, true)]
    private static bool ToggleValidate()
    {
        Menu.SetChecked(MenuPath, Enabled);
        return true;
    }

    private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        if (!Enabled) return;

        // Aquí solo se MIRA (durante la importación no se tocan otros assets); lo demás va después, en delayCall.
        foreach (string p in deleted)
            if (p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) { Schedule(full: true); break; }

        foreach (string p in moved)
            if (IsGasAsset(p)) { Schedule(full: true); break; }

        foreach (string p in imported)
            if (IsGasAsset(p)) { _importedToCheck.Add(p); Schedule(full: false); }
    }

    private static bool IsGasAsset(string path)
    {
        if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return false;
        Type t = AssetDatabase.GetMainAssetTypeAtPath(path);
        return t != null && (typeof(GameplayAbility).IsAssignableFrom(t) || typeof(GameplayEffect).IsAssignableFrom(t));
    }

    private static void Schedule(bool full)
    {
        _fullCheck |= full;
        if (_pending) return;
        _pending = true;
        EditorApplication.delayCall += Run;
    }

    private static void Run()
    {
        _pending = false;

        // En Play o haciendo una build: se espera.
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            if (!_waitingForEditMode)
            {
                _waitingForEditMode = true;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
            }
            return;
        }
        if (BuildPipeline.isBuildingPlayer || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            _pending = true;
            EditorApplication.delayCall += Run;
            return;
        }

        bool full = _fullCheck || AnyImportedMissing();
        _fullCheck = false;
        _importedToCheck.Clear();
        if (!full) return;   // solo se guardaron cambios de assets que ya estaban: nada que hacer

        string changes = MercRegistryTools.SyncNetworkRegistries();
        if (!string.IsNullOrEmpty(changes))
            Debug.Log("[Registros de red] Actualizados solos. " + changes + " Reparte el mismo build a todos.");
    }

    // ¿Alguno de los importados no está en su registro? (lo normal al guardar un GA editado: todos están, y no se recorre nada)
    private static bool AnyImportedMissing()
    {
        if (_importedToCheck.Count == 0) return false;
        var ga = MercRegistryTools.FindRegistry<GameplayAbilityRegistry>();
        var ge = MercRegistryTools.FindRegistry<GameplayEffectRegistry>();
        if (ga == null || ge == null) return false;

        foreach (string p in _importedToCheck)
        {
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(p);
            if (asset is GameplayAbility a && !ga.Abilities.Contains(a)) return true;
            if (asset is GameplayEffect e && !ge.Effects.Contains(e)) return true;
            // (Un renombre no pasa por acá: Unity lo avisa como "movido", y eso ya pide la revisión completa.)
        }
        return false;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        _waitingForEditMode = false;
        _fullCheck = true;
        Schedule(full: true);
    }
}
