using System.Collections.Generic;
using UnityEngine;

// ============================================================
// GA_Revive  (genérico — revivir; 19 de noviembre de 2026, de las "Mejoras del GAS")
//
// Devuelve a la vida a aliados muertos, con "Vida al volver" de su vida máxima y sus cooldowns como estaban. Salió de la
// Resurrección del Clérigo de la vida (GA_Resurrection, que ahora es esta misma habilidad con su asset de siempre):
//
//   · "Al cuerpo de la mira": el aliado muerto más centrado en la mira, a su alcance (la Resurrección).
//   · "Todos los cuerpos a su alcance": todos los aliados muertos a "Alcance" del que la usa (un revivir en área).
//
// LEVANTAR CANALIZANDO (un revivir que se carga quieto al lado del cuerpo) no es código aparte: la sección "Canalizar" de
// cualquier GA, en "Se lanza al terminar" con sus segundos. Al terminar vuelve a buscar el cuerpo: si ya no está (reapareció
// en la base), no revive.
//
// POR QUÉ RECORRE LOS PERSONAJES Y NO USA FÍSICA: un muerto con ragdoll apaga su cápsula y pasa los huesos a otra capa (para
// que los ataques no le peguen al cadáver), así que un OverlapSphere por la capa de personajes no lo encuentra. Se apunta al
// CUERPO: la cadera del ragdoll si está suelto, o el transform si murió con la animación.
//
// El muerto vuelve DONDE CAYÓ (su transform; al revivir, el modelo vuelve a su raíz). Su reaparición en la base se cancela
// (NetworkASC.ServerResurrect). La ventana es la de la reaparición: pasados esos segundos ya no hay cuerpo. Se revive solo
// a jugadores y bots (un NPC muerto se va solo).
//
// Sin ningún cuerpo no se lanza ni gasta. A cada revivido, la lista de efectos "Al golpear → Aliados" (un escudo para que no
// lo vuelvan a tumbar al instante) y los VFX "al golpear".
//
// Los bots la usan sola en cualquier botón (BotController.TryRevive) y la pantalla del dueño pinta los cuerpos de verde o rojo
// si la tiene (DeadAllyHighlighter, en el prefab de pasivas de la subclase).
// ============================================================
[CreateAssetMenu(fileName = "GA_Revive", menuName = "GAS/Generics/Revive")]
public class GA_Revive : TargetImpactAbility
{
    public enum EReviveMode
    {
        [InspectorName("Al cuerpo de la mira")]            Aimed = 0,
        [InspectorName("Todos los cuerpos a su alcance")]  Area  = 1,
    }

    [Section(AbilitySection.Targeting)]
    [Tooltip("A quién revive: al cuerpo que apunta, o a todos los que estén a su alcance.")]
    [Label("Revive a")]
    public EReviveMode Mode = EReviveMode.Aimed;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Distancia máxima desde el que la usa hasta el cuerpo.")]
    [Label("Alcance máximo (m)")]
    public float MaxRange = 8f;

    [Section(AbilitySection.Targeting)]
    [ShowIf(nameof(Mode), EReviveMode.Aimed)]
    [Tooltip("Ángulo máximo (grados) entre la mira y el cuerpo. Un cuerpo en el piso queda " +
             "debajo de la retícula, así que conviene más generoso que el de GA_Target.")]
    [Label("Ángulo de selección (°)")]
    public float SelectionAngle = 35f;

    [Section("Resurrección")]
    [Tooltip("Con cuánta vida vuelve, como fracción de su vida máxima. 0.5 = la mitad.")]
    [Range(0.05f, 1f)]
    [Label("Vida al volver (0 a 1)")]
    public float HealthFraction = 0.5f;

    private readonly List<AbilitySystemComponent> _bodies = new List<AbilitySystemComponent>();

    // =========================================================
    // ACTIVACIÓN
    // =========================================================

    public override bool CanActivate() => base.CanActivate() && FindBodies(_bodies) > 0;

    // Lista para usarse (sin cooldown, sin aturdir ni silenciar), SIN pedir un cuerpo. La usan el contorno de los aliados
    // muertos (DeadAllyHighlighter) para pintarlos de verde o de rojo y los bots. Funciona en el dueño: los tags viajan.
    public bool IsReady => base.CanActivate();

    // Sin cuerpo el dueño no anticipa la animación: el servidor la va a descartar igual.
    public override bool CanPredictActivation() => FindBodies(_bodies) > 0;

    public override void Activate()
    {
        if (!IsServer) return;

        if (!CanActivate()) { EndAbility(); return; }   // CanActivate deja los cuerpos en _bodies

        CommitAbility();

        foreach (AbilitySystemComponent ally in _bodies)
        {
            NetworkAbilitySystemComponent allyNet = ally.GetComponent<NetworkAbilitySystemComponent>();
            if (allyNet != null) allyNet.ServerResurrect(HealthFraction);
            else                 ally.Revive(resetAbilities: false, healthFraction: HealthFraction); // sin red

            if (ally.HasTag(EGameplayTag.State_Dead)) continue;
            ApplyHitEffects(ally, firstHit: true);
            PlayImpactVFXOnTarget(ally);
        }

        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        if (pc != null)
        {
            if (Mode == EReviveMode.Aimed) pc.RotateToAim();
            pc.PlayAnimation(this);
        }

        EndAbility();
    }

    // =========================================================
    // BUSCAR LOS CUERPOS
    // =========================================================

    // Llena 'into' con los cuerpos que revive ahora (uno en "de la mira", todos en "a su alcance") y dice cuántos.
    private int FindBodies(List<AbilitySystemComponent> into)
    {
        into.Clear();
        if (OwnerASC == null) return 0;

        Vector3 origin = OwnerASC.transform.position;
        Vector3 aimOrigin = origin, aimDir = OwnerASC.transform.forward;
        if (Mode == EReviveMode.Aimed)
        {
            // El rayo de la retícula, no la línea desde los pies (como FindBestTargetInAim).
            PlayerController ownerPc = OwnerASC.GetComponent<PlayerController>();
            aimOrigin = ownerPc != null ? ownerPc.GetAimOrigin() : origin + Vector3.up * 1.6f;
            Vector3 aimPoint = ownerPc != null ? ownerPc.GetAimPoint(200f) : origin + OwnerASC.transform.forward * MaxRange;
            aimDir = aimPoint - aimOrigin;
            if (aimDir.sqrMagnitude < 0.0001f) aimDir = OwnerASC.transform.forward;
            aimDir.Normalize();
        }

        AbilitySystemComponent best = null;
        float bestAlign = Mathf.Cos(SelectionAngle * Mathf.Deg2Rad);

        // Se busca al usarla, en la lista de personajes prendidos (AbilitySystemComponent.All).
        foreach (AbilitySystemComponent asc in AbilitySystemComponent.All)
        {
            if (asc == null || ReferenceEquals(asc, OwnerASC) || !IsRevivable(asc)) continue;
            if (!asc.HasTag(EGameplayTag.State_Dead) || !IsAlly(asc, includeSelf: false)) continue;

            Vector3 body = BodyPosition(asc);
            if ((body - origin).sqrMagnitude > MaxRange * MaxRange) continue;

            if (Mode == EReviveMode.Area) { into.Add(asc); continue; }

            Vector3 toBody = body - aimOrigin;
            if (toBody.sqrMagnitude < 0.0001f) continue;
            float align = Vector3.Dot(aimDir, toBody.normalized);
            if (align > bestAlign) { bestAlign = align; best = asc; }
        }

        if (best != null) into.Add(best);
        return into.Count;
    }

    // Se revive a jugadores y bots (los que reaparecen). Sin red (las pruebas), a cualquiera.
    private static bool IsRevivable(AbilitySystemComponent asc) =>
        asc.GetComponent<PlayerController>() != null || asc.GetComponent<NetworkAbilitySystemComponent>() == null;

    // Dónde está el cuerpo que se ve: la cadera del ragdoll si está suelto, o un poco arriba de los pies si murió con la
    // animación (queda tirado en el piso).
    public static Vector3 BodyPosition(PlayerController pc) => BodyPosition(pc.GetComponent<AbilitySystemComponent>(), pc);

    public static Vector3 BodyPosition(AbilitySystemComponent asc, PlayerController pc = null)
    {
        Transform t = pc != null ? pc.transform : asc.transform;
        RagdollController ragdoll = t.GetComponent<RagdollController>();
        if (ragdoll != null && ragdoll.IsActive && ragdoll.Hips != null) return ragdoll.Hips.position;
        return t.position + Vector3.up * 0.3f;
    }

    // =========================================================
    // VISUALES Y GIZMOS
    // =========================================================

    public override void DrawGizmos(Transform origin)
    {
        if (Mode == EReviveMode.Area)
        {
            if (origin == null) return;
            Gizmos.color = new Color(1f, 0.9f, 0.4f, 0.9f);
            Gizmos.DrawWireSphere(origin.position, MaxRange);
            return;
        }
        DrawSelectionGizmo(origin, MaxRange, SelectionAngle, new Color(1f, 0.9f, 0.4f, 0.9f));
    }

#if UNITY_EDITOR
    public override void DrawSceneHandles(Transform origin)
    {
        if (Mode == EReviveMode.Aimed) AbilityHandles.Selection(this, origin, ref MaxRange, ref SelectionAngle);
    }
#endif

    // Busca los cuerpos entre los personajes, no con física (un ragdoll no está en la capa).
    public override bool UsesTargetLayer => false;
}
