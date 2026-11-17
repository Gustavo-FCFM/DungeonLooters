using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — GIZMOS Y MANIJAS DEL EDITOR
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // GIZMOS — vista previa del área real de la habilidad en el Editor
    // =========================================================

    // No hace nada por defecto. PlayerController.OnDrawGizmosSelected()
    // la llama para cada habilidad de la clase equipada (CurrentClassDef),
    // tanto en modo Play como fuera de él — así se puede ajustar
    // Range/AbilityRadius/etc. en el Inspector del asset y ver el área
    // real actualizarse en la Scene view sin tener que jugar para
    // probarlo. Cada habilidad concreta con área de golpe la sobreescribe
    // dibujando EXACTAMENTE los mismos valores que usa en su propio
    // Physics.Overlap...(), para que el gizmo nunca se desincronice de lo
    // que realmente golpea.
    public virtual void DrawGizmos(Transform origin) { }

    // El gizmo de las habilidades de OBJETIVO ÚNICO: el alcance (esfera) y el cono de
    // selección alrededor de la mira (selectionAngle es la MITAD: "hasta X° de la mira").
    // La mira sale de la cámara y no del cuerpo, así que es una aproximación: sirve para
    // ver la escala, no para medir al centímetro.
    protected static void DrawSelectionGizmo(Transform origin, float range, float selectionAngle, Color color)
    {
        if (origin == null) return;

        Gizmos.color = color;
        Gizmos.DrawWireSphere(origin.position, range);

        Vector3 chest = origin.position + Vector3.up;
        Gizmos.color = new Color(color.r, color.g, color.b, color.a * 0.7f);
        for (int i = 0; i < 4; i++)
        {
            Quaternion around = Quaternion.AngleAxis(i * 90f, origin.forward);
            Vector3 edge = around * (Quaternion.AngleAxis(selectionAngle, origin.up) * origin.forward);
            Gizmos.DrawLine(chest, chest + edge * range);
        }
    }

#if UNITY_EDITOR
    // MANIJAS en la Scene view para ajustar los números de la forma arrastrando (alcance,
    // radio, ángulo, desde dónde sale). Las dibuja el muñeco de prueba (AbilityPreview)
    // con esta habilidad elegida; cada cambio tiene Deshacer y queda guardado en el asset.
    // Cada GA con forma la sobreescribe usando las ayudas de AbilityHandles.
    public virtual void DrawSceneHandles(Transform origin) { }
#endif
}
