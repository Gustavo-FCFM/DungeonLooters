using UnityEngine;

// ============================================================
// GA_Hook  (genérico — el gancho; 19 de noviembre de 2026, de las "Mejoras del GAS")
//
// Tira un rayo hacia la mira, hasta su alcance, y engancha lo PRIMERO que toca: un enemigo, o una pared / el piso
// (si "Engancha paredes y piso" está prendido). Sin enganche no sale ni gasta nada.
//
//   · "Te lleva": vas vos hacia lo enganchado (y frenás un poco antes).
//   · "Trae al enemigo": al enemigo enganchado se le aplican los efectos "Al golpear" de la habilidad. Para TRAERLO, uno
//     de esos efectos es un GE con "Atraer (hacia él)" (el desplazamiento del GE, que frena a su "Distancia a la que
//     frena"). Si engancha una pared, te lleva a vos.
// En los dos modos, al enemigo enganchado se le aplican los efectos "Al golpear" (un daño, una lentitud...).
//
// EL MOVIMIENTO del que tira va por el mismo camino que el dash (NetworkAbilitySystemComponent.ServerStartDash): lo
// ejecuta el dueño (el transform es suyo) o, en un bot, su cerebro. Al terminar el viaje se libera solo, como el dash.
// ============================================================
[CreateAssetMenu(fileName = "GA_Hook", menuName = "GAS/Generics/Hook")]
public class GA_Hook : GameplayAbility
{
    public enum EHookMode
    {
        [InspectorName("Te lleva (a la pared o al enemigo)")]      PullSelf   = 0,
        [InspectorName("Trae al enemigo (a la pared, te lleva)")]  PullTarget = 1,
    }

    private const int CharacterLayer = 7;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Qué hace al enganchar. \"Trae al enemigo\" necesita un GE con \"Atraer (hacia él)\" en sus efectos " +
             "\"Al golpear\".")]
    [Label("Modo")]
    public EHookMode Mode = EHookMode.PullSelf;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Hasta dónde llega el gancho.")]
    [Label("Alcance (m)")]
    public float MaxRange = 20f;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Qué tan grueso es el gancho contra los personajes (más = más fácil de enganchar a alguien).")]
    [Label("Grosor contra personajes (m)")]
    public float HookRadius = 0.4f;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Si engancha paredes y piso (para moverse). Apagado: solo personajes.")]
    [Label("Engancha paredes y piso")]
    public bool HooksTerrain = true;

    [Section(AbilitySection.Targeting)]
    [ShowIf(nameof(HooksTerrain))]
    [Tooltip("Las capas que cuentan como pared o piso (el mapa).")]
    [Label("Capas de pared y piso")]
    public LayerMask WallLayer = 1;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Altura desde la que sale el gancho (la mano), sobre los pies.")]
    [Label("Altura de salida (m)")]
    public float MuzzleHeight = 1.4f;

    [Section(AbilitySection.Movement)]
    [Tooltip("Velocidad del viaje hacia lo enganchado.")]
    [Label("Velocidad del viaje (m/s)")]
    public float TravelSpeed = 35f;

    [Section(AbilitySection.Movement)]
    [Tooltip("Cuánto antes de lo enganchado frena (para no meterse en la pared ni encima del enemigo).")]
    [Label("Frena antes (m)")]
    public float StopShort = 1.2f;

    [Section(AbilitySection.Movement)]
    [Tooltip("Tope del viaje: aunque no haya llegado, a este tiempo suelta.")]
    [Label("Viaje máximo (s)")]
    public float MaxTravelTime = 0.8f;

    [Section(AbilitySection.Movement)]
    [Tooltip("Al llegar no frena en seco: conserva esta parte de la velocidad, que se disipa sola.")]
    [Label("Velocidad al soltar (0 a 1)")]
    [Range(0f, 1f)] public float ExitSpeedPercent = 0.2f;

    [Section(AbilitySection.Movement)]
    [Tooltip("Las capas que atraviesa durante el viaje (los otros jugadores), como el dash.")]
    [Label("Atraviesa (capas)")]
    public LayerMask ExcludePlayerLayer = 1 << CharacterLayer;

    // Busca personajes en su capa (la 7) por su cuenta: no usa la "Capa de objetivos" genérica.
    public override bool UsesTargetLayer => false;

    // =========================================================
    // ACTIVACIÓN
    // =========================================================

    // El dueño solo anticipa el gesto si el gancho va a enganchar algo (con su propia mira).
    public override bool CanPredictActivation()
    {
        if (OwnerASC == null) return false;
        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        Vector3 origin = OwnerASC.transform.position + Vector3.up * MuzzleHeight;
        Vector3 dir = pc != null ? pc.GetLaunchDirection(origin, 3f) : OwnerASC.transform.forward;
        return TryHook(origin, dir, out _, out _);
    }

    public override void Activate()
    {
        if (!IsServer) return;
        if (!CanActivate()) return;

        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        NetworkAbilitySystemComponent netAsc = OwnerASC.GetComponent<NetworkAbilitySystemComponent>();

        Vector3 origin = OwnerASC.transform.position + Vector3.up * MuzzleHeight;
        Vector3 dir = pc != null ? pc.GetLaunchDirection(origin, 3f) : OwnerASC.transform.forward;

        // Sin enganche: no sale ni gasta (el dueño ya se puso "atacando" por su predicción: se lo libera).
        if (!TryHook(origin, dir, out AbilitySystemComponent enemy, out Vector3 point)) { EndAbility(); return; }

        CommitAbility();

        if (enemy != null)
        {
            if (StrikeIfScenery(enemy)) { EndAbility(); return; }
            ApplyHitEffects(enemy, firstHit: true);
            BroadcastHitVFX(enemy);
        }
        else BroadcastImpactVFX(point);

        if (pc != null) pc.PlayAnimation(this);

        // ¿Viaja el que tira? Siempre al enganchar una pared; al enganchar un enemigo, solo en "Te lleva".
        bool travel = enemy == null || Mode == EHookMode.PullSelf;
        if (!travel || !TravelTo(enemy != null ? enemy.transform.position + Vector3.up : point, pc, netAsc))
            EndAbility();
        // Si viaja, el fin lo maneja el viaje (como el dash: al terminar libera al dueño).
    }

    // Lo primero que toca el gancho: el enemigo más cercano en el camino (más cerca que la primera pared), o la pared.
    private bool TryHook(Vector3 origin, Vector3 dir, out AbilitySystemComponent enemy, out Vector3 point)
    {
        enemy = null;
        point = Vector3.zero;

        // La primera pared corta el alcance (no se engancha a alguien detrás de ella) y, si engancha terreno, es el punto.
        bool wallInPath = Physics.Raycast(origin, dir, out RaycastHit wallHit, MaxRange, WallLayer,
                                          QueryTriggerInteraction.Ignore);
        float wallDist = wallInPath ? wallHit.distance : MaxRange;
        bool wall = HooksTerrain && wallInPath;

        float best = float.MaxValue;
        foreach (RaycastHit h in Physics.SphereCastAll(origin, HookRadius, dir, wallDist, 1 << CharacterLayer,
                                                        QueryTriggerInteraction.Collide))
        {
            AbilitySystemComponent asc = h.collider.GetComponentInParent<AbilitySystemComponent>();
            if (asc == null || asc == OwnerASC || asc.HasTag(EGameplayTag.State_Dead)) continue;
            if (!IsEnemy(asc) && asc.Strikeable == null) continue;
            if (asc.IsHiddenFromEnemies) continue;
            float d = h.distance > 0f ? h.distance : Vector3.Distance(origin, asc.transform.position);
            if (d < best) { best = d; enemy = asc; }
        }

        if (enemy != null) { point = enemy.transform.position; return true; }
        if (wall) { point = wallHit.point; return true; }
        return false;
    }

    // El viaje del que tira hacia 'target', frenando antes. false = está tan cerca que no hay viaje.
    private bool TravelTo(Vector3 target, PlayerController pc, NetworkAbilitySystemComponent netAsc)
    {
        Vector3 from = OwnerASC.transform.position + Vector3.up;
        Vector3 toTarget = target - from;
        float dist = toTarget.magnitude - StopShort;
        if (dist <= 0.2f) return false;

        Vector3 dir = toTarget.normalized;
        float duration = Mathf.Clamp(dist / Mathf.Max(1f, TravelSpeed), 0.1f, Mathf.Max(0.1f, MaxTravelTime));
        float speed = dist / duration;

        if (netAsc != null)
        {
            netAsc.ServerStartDash(dir * speed, duration, ExcludePlayerLayer.value, true, ExitSpeedPercent, 3f);
            return true;
        }
        if (pc != null) pc.ApplyDashVelocity(dir * speed, true);   // sin red: el impulso (no gestiona el fin)
        return false;
    }

    // =========================================================
    // GIZMOS
    // =========================================================

    public override void DrawGizmos(Transform origin)
    {
        if (origin == null) return;
        Vector3 from = origin.position + Vector3.up * MuzzleHeight;
        Gizmos.color = new Color(0.9f, 0.8f, 0.3f, 0.9f);
        Gizmos.DrawLine(from, from + origin.forward * MaxRange);
        Gizmos.DrawWireSphere(from + origin.forward * MaxRange, HookRadius);
    }
}
