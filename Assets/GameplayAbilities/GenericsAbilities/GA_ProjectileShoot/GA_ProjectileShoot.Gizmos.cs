using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet;
using FishNet.Object;

// ============================================================
// GA_ProjectileShoot — GIZMOS (la trayectoria en el editor)
//
// Una parte de GA_ProjectileShoot (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameplayAbilities/GenericsAbilities/GA_ProjectileShoot.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class GA_ProjectileShoot
{

    // =========================================================
    // GIZMOS (la trayectoria en el editor)
    // =========================================================

    // Vista previa de la TRAYECTORIA en el Editor: sale del mismo SpawnOffset y con
    // la misma velocidad (LaunchForce) que usa el disparo real, así que la curva que
    // ves es la que va a volar. Si el Rigidbody del prefab usa gravedad, simula el
    // arco balístico paso a paso hasta tocar el suelo; si no, dibuja la línea recta.
    public override void DrawGizmos(Transform origin)
    {
        if (origin == null) return;

        Vector3 start = origin.TransformPoint(SpawnOffset);
        Vector3 velocity = origin.forward * LaunchForce;

        bool useGravity = ProjectileUsesGravity;

        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
        Gizmos.DrawWireSphere(start, 0.15f);

        if (!useGravity)
        {
            Vector3 end = start + origin.forward * 30f;
            Gizmos.DrawLine(start, end);
            Gizmos.DrawWireSphere(end, 0.3f);
            return;
        }

        // Integración simple del tiro parabólico (mismo modelo que la física).
        Vector3 point = start;
        const float step = 0.05f;
        for (int i = 0; i < 300; i++)
        {
            Vector3 next = point + velocity * step;
            velocity += Physics.gravity * step;
            Gizmos.DrawLine(point, next);
            point = next;

            // Cortamos al llegar a la altura del piso del lanzador.
            if (point.y <= origin.position.y) break;
        }
        Gizmos.DrawWireSphere(point, 0.3f);
    }

#if UNITY_EDITOR
    public override void DrawSceneHandles(Transform origin)
    {
        if (origin == null) return;
        AbilityHandles.Offset(this, "Spawn Offset", origin, ref SpawnOffset, AbilityHandles.OffsetColor);
    }
#endif

    // =========================================================
    // PRIMER IMPACTO (efectos "al primer golpe")
    // =========================================================

    // Desde cuándo se le puede volver a aplicar cada efecto a cada enemigo. Estático y
    // por (lanzador, enemigo, efecto) y no un campo de la instancia: un combo crea
    // copias NUEVAS de sus pasos en cada activación (GA_ComboSequence), así que un
    // registro guardado acá se perdería en cada ataque.
    private static readonly Dictionary<(AbilitySystemComponent, AbilitySystemComponent, GameplayEffect), float>
        _firstHitReadyAt = new Dictionary<(AbilitySystemComponent, AbilitySystemComponent, GameplayEffect), float>();

    // Los efectos "al primer golpe" (el primer enemigo que toca cada proyectil) solo se
    // aplican si ya pasó su tiempo mínimo con ese enemigo. Lo consulta ApplyHitEffects,
    // que llama GC_Projectile en el servidor.
    protected override bool CanApplyFirstHitEffect(AbilitySystemComponent target, GameplayEffect effect)
    {
        if (FirstHitCooldownPerTarget <= 0f || OwnerASC == null || target == null) return true;

        var key = (OwnerASC, target, effect);
        if (_firstHitReadyAt.TryGetValue(key, out float readyAt) && Time.time < readyAt) return false;

        _firstHitReadyAt[key] = Time.time + FirstHitCooldownPerTarget;
        return true;
    }
}
