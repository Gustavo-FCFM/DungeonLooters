using UnityEngine;

// ============================================================
// GA_SwapPosition  (genérico — intercambiar posición; 19 de noviembre de 2026, de las "Mejoras del GAS")
//
// Tira un rayo hacia la mira, hasta su alcance, y al PRIMER personaje que toca (de los que elige "Con quién") lo cambia
// de lugar con el que la tira: cada uno aparece donde estaba el otro, mirando hacia donde miraba. Sin nadie no sale ni
// gasta nada. La primera pared corta el rayo (no se cambia con alguien que no se ve).
//
// Al elegido se le aplican los efectos "Al golpear" de la habilidad (un aturdido al enemigo, un escudo al aliado...). Los
// VFX "en el impacto" salen en los DOS lugares (los pies de cada uno, antes de moverse) y los "al golpear", sobre el otro.
//
// Un ENEMIGO Imparable (Status_Unstoppable) no se puede mover: no cuenta como blanco. A un aliado sí.
//
// EL MOVIMIENTO va por NetworkAbilitySystemComponent.ServerTeleportOwnerTo, el del Parpadeo: a un jugador lo mueve su
// dueño (el transform es suyo), a un bot su cerebro, a un NPC el servidor sobre el NavMesh. Solo se cambia con lo que
// camina (un jugador, un bot o algo con NavMeshAgent): un tótem o la campana no.
// ============================================================
[CreateAssetMenu(fileName = "GA_SwapPosition", menuName = "GAS/Generics/Swap Position")]
public class GA_SwapPosition : GameplayAbility
{
    public enum ESwapWith
    {
        [InspectorName("Enemigos")]            Enemies = 0,
        [InspectorName("Aliados")]             Allies  = 1,
        [InspectorName("Enemigos y aliados")]  Both    = 2,
    }

    private const int CharacterLayer = 7;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Con quién se puede cambiar de lugar.")]
    [Label("Con quién")]
    public ESwapWith SwapWith = ESwapWith.Enemies;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Hasta dónde llega.")]
    [Label("Alcance (m)")]
    public float MaxRange = 20f;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Qué tan grueso es el rayo contra los personajes (más = más fácil de acertarle a alguien).")]
    [Label("Grosor contra personajes (m)")]
    public float AimRadius = 0.5f;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Las capas que tapan (el mapa): no se cambia con alguien detrás de una pared.")]
    [Label("Capas que tapan")]
    public LayerMask WallLayer = 1;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Altura desde la que sale el rayo (la mano o los ojos), sobre los pies.")]
    [Label("Altura de salida (m)")]
    public float MuzzleHeight = 1.4f;

    // Busca personajes en su capa (la 7) por su cuenta: no usa la "Capa de objetivos" genérica.
    public override bool UsesTargetLayer => false;

    // =========================================================
    // ACTIVACIÓN
    // =========================================================

    // El dueño solo anticipa el gesto si hay con quién cambiarse (con su propia mira).
    public override bool CanPredictActivation()
    {
        if (OwnerASC == null) return false;
        return FindTarget(AimOrigin(), AimDirection(OwnerASC.GetComponent<PlayerController>())) != null;
    }

    public override void Activate()
    {
        if (!IsServer) return;
        if (!CanActivate()) return;

        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        AbilitySystemComponent other = FindTarget(AimOrigin(), AimDirection(pc));

        // Sin nadie: no sale ni gasta (el dueño ya se puso "atacando" por su predicción: se lo libera).
        if (other == null) { EndAbility(); return; }

        CommitAbility();

        Vector3 mine   = OwnerASC.transform.position;
        Vector3 theirs = other.transform.position;

        BroadcastImpactVFX(mine);
        BroadcastImpactVFX(theirs);

        MoveTo(OwnerASC, theirs);
        MoveTo(other, mine);

        ApplyHitEffects(other, firstHit: true);
        BroadcastHitVFX(other);

        if (pc != null) pc.PlayAnimation(this);
        EndAbility();
    }

    private Vector3 AimOrigin() => OwnerASC.transform.position + Vector3.up * MuzzleHeight;

    private Vector3 AimDirection(PlayerController pc) =>
        pc != null ? pc.GetLaunchDirection(AimOrigin(), 3f) : OwnerASC.transform.forward;

    // El primero que toca el rayo, más cerca que la primera pared, de los que elige "Con quién". null = nadie.
    private AbilitySystemComponent FindTarget(Vector3 origin, Vector3 dir)
    {
        float wallDist = Physics.Raycast(origin, dir, out RaycastHit wallHit, MaxRange, WallLayer,
                                         QueryTriggerInteraction.Ignore) ? wallHit.distance : MaxRange;

        AbilitySystemComponent best = null;
        float bestDist = float.MaxValue;
        foreach (RaycastHit h in Physics.SphereCastAll(origin, AimRadius, dir, wallDist, 1 << CharacterLayer,
                                                        QueryTriggerInteraction.Collide))
        {
            AbilitySystemComponent asc = h.collider.GetComponentInParent<AbilitySystemComponent>();
            if (!IsValidTarget(asc)) continue;
            float d = h.distance > 0f ? h.distance : Vector3.Distance(origin, asc.transform.position);
            if (d < bestDist) { bestDist = d; best = asc; }
        }
        return best;
    }

    private bool IsValidTarget(AbilitySystemComponent asc)
    {
        if (asc == null || asc == OwnerASC || asc.HasTag(EGameplayTag.State_Dead)) return false;
        if (asc.Strikeable != null || !CanBeMoved(asc)) return false;

        bool enemy = IsEnemy(asc);
        if (enemy)
        {
            if (SwapWith == ESwapWith.Allies) return false;
            if (asc.IsHiddenFromEnemies || asc.HasTag(EGameplayTag.Status_Unstoppable)) return false;
            return true;
        }
        return SwapWith != ESwapWith.Enemies && IsAlly(asc, includeSelf: false);
    }

    // Lo que camina: un jugador o un bot (PlayerController) o un NPC (NavMeshAgent). Sin red (las pruebas), cualquiera.
    private static bool CanBeMoved(AbilitySystemComponent asc)
    {
        if (asc.GetComponent<PlayerController>() != null) return true;
        if (asc.GetComponent<UnityEngine.AI.NavMeshAgent>() != null) return true;
        return asc.GetComponent<NetworkAbilitySystemComponent>() == null;
    }

    // Lo pone en 'position' mirando hacia donde miraba, por quien manda sobre su transform.
    private static void MoveTo(AbilitySystemComponent asc, Vector3 position)
    {
        Vector3 face = asc.transform.forward;
        NetworkAbilitySystemComponent net = asc.GetComponent<NetworkAbilitySystemComponent>();
        if (net != null && net.IsServerInitialized) { net.ServerTeleportOwnerTo(position, face); return; }

        PlayerController pc = asc.GetComponent<PlayerController>();
        if (pc != null) pc.TeleportTo(position, face);
        else            asc.transform.position = position;   // sin red (las pruebas)
    }

    // =========================================================
    // GIZMOS
    // =========================================================

    public override void DrawGizmos(Transform origin)
    {
        if (origin == null) return;
        Vector3 from = origin.position + Vector3.up * MuzzleHeight;
        Gizmos.color = new Color(0.6f, 0.4f, 0.9f, 0.9f);
        Gizmos.DrawLine(from, from + origin.forward * MaxRange);
        Gizmos.DrawWireSphere(from + origin.forward * MaxRange, AimRadius);
    }
}
