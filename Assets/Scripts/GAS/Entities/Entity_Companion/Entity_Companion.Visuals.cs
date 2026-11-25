using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// Entity_Companion — LO QUE VEN TODOS
//
// Una parte de Entity_Companion (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/Entity_Companion.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class Entity_Companion
{

    // Si el Explorador muere, NO se va solo ("Si el dueño muere: se queda"): lo guarda el
    // CompanionManager, que antes anota la vida que le quedaba.

    // =========================================================
    // LO QUE VEN TODOS
    // =========================================================

    [ObserversRpc(RunLocally = true)]
    private void ObserversAttack(Vector3 hitPoint)
    {
        _lungeUntil = Time.time + 0.25f;
        if (HitVFX == null) return;
        GameObject vfx = Instantiate(HitVFX, hitPoint, Quaternion.identity);
        Destroy(vfx, 2f);
    }

    [ObserversRpc(RunLocally = true)]
    private void ObserversHowl()
    {
        // Sin animación todavía: un salto corto del modelo hacia arriba.
        _lungeUntil = Time.time + 0.4f;
    }

    // El modelo se adelanta y vuelve en cada golpe (mientras no haya animaciones).
    private void TickLunge()
    {
        if (Visual == null) return;
        float left = _lungeUntil - Time.time;
        float t = left > 0f ? Mathf.Sin(Mathf.Clamp01(left / 0.25f) * Mathf.PI) : 0f;
        Visual.localPosition = _visualRest + new Vector3(0f, 0.1f * t, 0.45f * t);
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }
}
