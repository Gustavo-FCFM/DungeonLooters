using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// ============================================================
// MercRegistryTools
//
// Pone al día los dos registros de red: el de habilidades y el de efectos.
//
// POR QUE HACE FALTA: FishNet no puede mandar la referencia a un ScriptableObject por
// RPC. El servidor manda el INDICE dentro de estas listas y cada peer lo resuelve contra
// su propia copia (cargada por Resources.Load). Si una habilidad o un efecto NO esta en
// su registro, el indice viaja en -1 y su VFX —o el icono del buff— se ve SOLO en el host.
//
// Desde el 17 de noviembre de 2026 esto corre SOLO al crear, borrar, duplicar o renombrar un
// GA_* o un GE_* (GasRegistryAutoUpdate). El menu queda para forzarlo, y las herramientas de
// una vez lo siguen llamando al terminar.
//
// EL ORDEN es alfabetico y deterministico (la regla vive en CollectFromProject de cada
// registro), asi que el indice significa lo mismo en todos lados MIENTRAS el conjunto de
// assets sea el mismo. Despues de un cambio hay que repartir el mismo build a todos: un
// cliente viejo resolveria los indices contra su lista vieja y veria el VFX equivocado.
//
// Si la lista ya esta como tiene que estar, NO se toca el asset (no aparece como cambiado en git).
// ============================================================
public static class MercRegistryTools
{
    [MenuItem("Mercenarios/Actualizar los registros de red (habilidades y efectos)", false, 1)]
    public static void RefreshNetworkRegistries()
    {
        string changes = SyncNetworkRegistries();
        if (changes == null) return;   // falta un registro: ya se avisó

        var ga = FindRegistry<GameplayAbilityRegistry>();
        var ge = FindRegistry<GameplayEffectRegistry>();
        Debug.Log($"[Mercenarios] Registros de red: {ga.Abilities.Count} habilidades y {ge.Effects.Count} efectos. " +
                  (changes.Length == 0 ? "Ya estaban al día." : changes + " Reparte el mismo build a todos."));
    }

    // Pone los dos registros como tienen que estar y guarda solo lo que cambió.
    // Devuelve qué cambió ("" = nada) o null si falta algún registro.
    public static string SyncNetworkRegistries()
    {
        var ga = FindRegistry<GameplayAbilityRegistry>();
        var ge = FindRegistry<GameplayEffectRegistry>();
        if (ga == null || ge == null) return null;

        var parts = new List<string>();
        string a = Sync(ga, ga.Abilities, GameplayAbilityRegistry.CollectFromProject());
        string e = Sync(ge, ge.Effects, GameplayEffectRegistry.CollectFromProject());
        if (a.Length > 0) parts.Add("Habilidades: " + a + ".");
        if (e.Length > 0) parts.Add("Efectos: " + e + ".");
        return string.Join(" ", parts);
    }

    // Reemplaza la lista si es distinta de la que tiene que ser y dice qué cambió.
    private static string Sync<T>(Object registry, List<T> current, List<T> wanted) where T : Object
    {
        if (current.Count == wanted.Count && current.SequenceEqual(wanted)) return "";

        // Lo que entra y lo que sale; si es el mismo conjunto, solo cambió el orden (un renombre).
        // Un asset borrado queda en la lista como referencia rota: cuenta como "salió".
        var wantedSet = new HashSet<T>(wanted);
        var currentSet = new HashSet<T>(current.Where(x => x != null));
        List<string> added = wanted.Where(x => !currentSet.Contains(x)).Select(x => x.name).ToList();
        int removed = current.Count(x => x == null || !wantedSet.Contains(x));

        var sb = new List<string>();
        if (added.Count > 0) sb.Add("entra " + Names(added));
        if (removed > 0) sb.Add($"sale{(removed > 1 ? "n" : "")} {removed} (borrado{(removed > 1 ? "s" : "")} o movido{(removed > 1 ? "s" : "")} fuera del proyecto)");
        if (sb.Count == 0) sb.Add("cambió el orden (un renombre)");

        // Sin Undo a propósito: un Ctrl+Z después de crear un asset no tiene que desarmar el registro.
        current.Clear();
        current.AddRange(wanted);
        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssetIfDirty(registry);
        return string.Join(", ", sb);
    }

    private static string Names(List<string> names) =>
        names.Count <= 6 ? string.Join(", ", names) : string.Join(", ", names.Take(6)) + $" y {names.Count - 6} más";

    // El registro que vale es el de Resources: es el unico que Resources.Load encuentra en
    // runtime. Se busca POR TIPO, no por ruta: el asset se puede mover dentro de Resources.
    public static T FindRegistry<T>() where T : ScriptableObject
    {
        string[] guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
        if (guids.Length == 0)
        {
            Debug.LogError($"[Mercenarios] No encontré ningún {typeof(T).Name} en el proyecto. " +
                           $"Crea uno en Assets/Resources/{typeof(T).Name}.asset (menú Create ▸ GAS).");
            return null;
        }

        T target = null;
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            T candidate = AssetDatabase.LoadAssetAtPath<T>(path);
            if (candidate == null) continue;
            if (path.Contains("/Resources/")) { target = candidate; break; }
            if (target == null) target = candidate;
        }

        if (target != null && !AssetDatabase.GetAssetPath(target).Contains("/Resources/"))
            Debug.LogWarning($"[Mercenarios] '{AssetDatabase.GetAssetPath(target)}' no está en una carpeta Resources: " +
                             "Resources.Load no lo va a encontrar y el juego se queda sin registro.");
        return target;
    }
}
