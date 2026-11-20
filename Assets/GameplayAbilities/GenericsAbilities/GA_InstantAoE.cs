using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GA_InstantAoE
//
// Golpe de área de UNA sola aplicación: revisa quién está dentro del radio y le
// aplica la lista de efectos una vez. Es la versión instantánea de
// GA_ContinuousAoE (misma configuración: área, efectos, dónde se despliega), sin
// duración ni ticks. Pensada para explosiones, ondas expansivas, bombas, etc.
//
// DÓNDE SE DESPLIEGA (DeployMode): sobre el propio dueño, o en la ZONA APUNTADA con
// la retícula (mantener el botón → marcador en el suelo → soltar, ver
// IGroundTargetAbility).
//
// StartDelay sirve para sincronizar el impacto con la animación (el golpe cae recién
// después de ese tiempo). El punto de la zona se resuelve al ACTIVAR, así que apuntar
// a otro lado durante el delay no cambia dónde cae.
//
// A QUIÉN AFECTA lo dice cada entrada de la lista (enemigos, aliados, todos). El VFX
// del estallido es una entrada "En el impacto" de la lista de VFX, con "Calzar con el
// área" para que mida lo mismo que el radio.
//
// GOLPES EXTRA POR ACUMULACIÓN (ExtraHitsPerStack): con las acumulaciones que leyó al
// activarse (sección Acumulaciones), repite el golpe — los Cortes devastadores del Samurái
// cortan una vez más por cada acumulación de Artes marciales.
//
// TODO EL MAPA (19 de noviembre de 2026, la definitiva global de las "Mejoras del GAS"): con la casilla, el golpe no mira
// el radio: alcanza a todos los personajes de la partida, estén donde estén (por defecto solo jugadores y bots: un NPC
// del otro lado del mapa no se entera). A quién le hace qué lo sigue diciendo la lista de efectos. Los VFX "en el
// impacto" salen sobre el que la usa y los "al golpear", sobre cada uno.
//
// Si en cambio querés una zona que PERSISTA aplicando efectos cada tanto (charco de
// veneno, aura, la zona de los Cañones del Pirata), usá GA_ContinuousAoE.
// ============================================================
[CreateAssetMenu(fileName = "GA_InstantAoE", menuName = "GAS/Generics/Instant AoE")]
public class GA_InstantAoE : GameplayAbility, IGroundTargetAbility
{
    [Section(AbilitySection.Shape)]
    [Tooltip("Radio del área, en metros.")]
    [Label("Radio (m)")]
    public float Radius = 4f;

    [Tooltip("AtOwner: el área estalla sobre el dueño. AtReticle: se apunta con el marcador " +
             "en el suelo (mantener → apuntar → soltar) y cae ahí.")]
    [Label("Dónde aparece")]
    public GA_ContinuousAoE.EAoEDeploy DeployMode = GA_ContinuousAoE.EAoEDeploy.AtOwner;

    [Tooltip("Alcanza a TODOS los personajes de la partida, estén donde estén (una definitiva global): no mira el " +
             "radio ni dónde aparece. A quién le hace qué lo sigue diciendo la lista de efectos.")]
    [Label("Todo el mapa")]
    public bool Global = false;

    [ShowIf(nameof(Global))]
    [Tooltip("Solo jugadores y bots: los NPC (fantasmas, el jefe) y las invocaciones no la sienten. Apagado = todos " +
             "(menos lo que se golpea del mapa, como la campana).")]
    [Label("Solo jugadores (sin NPCs)")]
    public bool GlobalPlayersOnly = true;

    [ShowIf(nameof(Global))]
    [Tooltip("A los enemigos invisibles no les llega. Apagado = también a ellos.")]
    [Label("No a los invisibles")]
    public bool GlobalSkipsHidden = false;

    [ShowIf(nameof(DeployMode), GA_ContinuousAoE.EAoEDeploy.AtReticle)]
    [Tooltip("Alcance máximo al que se puede lanzar la zona.")]
    [Label("Alcance máximo (m)")]
    public float MaxRange = 15f;

    // IGroundTargetAbility: valores del marcador del suelo (solo se muestra si
    // DeployMode es AtReticle).
    public float MaxTargetRange   => MaxRange;
    public float TargetRadius     => Radius;
    public bool  UsesGroundTarget => !Global && DeployMode == GA_ContinuousAoE.EAoEDeploy.AtReticle;

    [Section(AbilitySection.Timing)]
    [Tooltip("Espera antes de que el golpe caiga (para acompañar la animación). Se ajusta " +
             "por la velocidad de ataque, igual que en las demás habilidades.")]
    [Label("Espera inicial (s)")]
    public float StartDelay = 0f;

    [Tooltip("Golpes EXTRA por cada acumulación leída (sección Acumulaciones). Con 1: sin " +
             "acumulaciones, un golpe; con 4, cinco (los Cortes devastadores del Samurái). Cada " +
             "golpe extra repite la animación y vuelve a aplicar los efectos 'al activarse' (la " +
             "curación por corte). 0 = siempre un solo golpe.")]
    [Label("Golpes extra por acumulación")]
    public int ExtraHitsPerStack = 0;

    [ShowIf(nameof(ExtraHitsPerStack), ShowIfAttribute.Positive)]
    [Tooltip("Segundos entre un golpe y el siguiente (se ajusta con la velocidad de ataque).")]
    [Label("Entre golpes (s)")]
    public float RepeatInterval = 0.3f;

    public override float VisualAreaRadius => Radius;
    public override bool SupportsVisualTiming(EVisualWhen when) => true;

    // Valida, cobra costo/cooldown y programa el impacto.
    public override void Activate()
    {
        if (!IsServer) return;
        if (!CanActivate()) return;

        CommitAbility();

        if (OwnerASC == null) return;

        PlayerController pc = OwnerASC.GetComponent<PlayerController>();

        // El centro se resuelve ACÁ, al activar (ver nota de cabecera sobre StartDelay).
        Vector3 center = ResolveCenter(pc);

        if (pc != null) pc.PlayAnimation(this);

        // Un golpe, más los extra por las acumulaciones que leyó CommitAbility.
        int hits = 1 + Mathf.Max(0, StackSnapshot) * Mathf.Max(0, ExtraHitsPerStack);

        if (StartDelay > 0f || hits > 1) OwnerASC.StartAbilityCoroutine(ImpactRoutine(center, hits));
        else                             { Detonate(center); EndAbility(); }
    }

    // Punto donde cae el área: el dueño, o la zona apuntada (recortada a MaxRange
    // para que coincida con la vista previa del marcador).
    private Vector3 ResolveCenter(PlayerController pc)
    {
        Vector3 origin = OwnerASC.transform.position;
        if (Global || DeployMode == GA_ContinuousAoE.EAoEDeploy.AtOwner) return origin;

        // Apuntada: el alcance se mide desde donde estaba al pedirla (si canalizó antes de salir,
        // cae donde soltó aunque se haya movido).
        if (pc != null) origin = pc.AimFromPosition;

        Vector3 center = pc != null ? pc.GetAimPoint(MaxRange)
                                    : origin + OwnerASC.transform.forward * MaxRange;

        Vector3 toZone = center - origin;
        if (toZone.magnitude > MaxRange) center = origin + toZone.normalized * MaxRange;
        return center;
    }

    // Espera StartDelay (ajustado por velocidad de ataque) y detona; si hay golpes extra
    // (ExtraHitsPerStack), los encadena cada RepeatInterval. Sobre el dueño, cada golpe sale
    // de donde está AHORA (se puede mover entre corte y corte). Aturdido o muerto, corta.
    private IEnumerator ImpactRoutine(Vector3 center, int hits)
    {
        float speedMultiplier = 1f;
        float atkSpeedStat = OwnerASC.GetAttributeValue(EAttributeType.AtkSpeed);
        if (atkSpeedStat > 0) speedMultiplier = 1f / atkSpeedStat;

        // Lo cortan morir y el aturdido, también en el amago (antes el golpe caía igual).
        AbilityGuard guard = Guard(EStopWhen.Dies | EStopWhen.Stunned);
        if (StartDelay > 0f)
        {
            yield return WaitSeconds(StartDelay / speedMultiplier, guard);
            if (guard.Stopped) { EndAbility(); yield break; }
        }

        NetworkAbilitySystemComponent netAsc = OwnerASC.GetComponent<NetworkAbilitySystemComponent>();

        for (int i = 0; i < hits; i++)
        {
            if (i > 0)
            {
                yield return WaitSeconds(RepeatInterval / speedMultiplier, guard);

                if (OwnerASC == null) yield break;
                if (ShouldStop(EStopWhen.Dies | EStopWhen.Stunned)) break;

                if (Global || DeployMode == GA_ContinuousAoE.EAoEDeploy.AtOwner) center = OwnerASC.transform.position;

                // Cada golpe extra: su animación (en todas las pantallas, también el dueño:
                // no lo pudo anticipar) y sus efectos "al activarse" (la curación por corte).
                if (netAsc != null) netAsc.ServerPlayAbilityAnimationOnAll(this);
                ApplyActivationEffects();
            }

            Detonate(center);
        }

        EndAbility();
    }

    // Aplica los efectos UNA vez a cada objetivo válido dentro del radio y reproduce
    // el VFX de impacto en todos los peers.
    private void Detonate(Vector3 center)
    {
        BroadcastImpactVFX(center);

        var seen = new HashSet<AbilitySystemComponent>();
        bool firstEnemy = true;

        foreach (AbilitySystemComponent targetASC in Global ? GlobalTargets() : TargetsInRadius(center))
        {
            // Un mismo personaje puede tener varios colliders: sin este filtro le
            // aplicaríamos los efectos más de una vez (y esto es de UNA sola aplicación).
            if (targetASC == null || !seen.Add(targetASC)) continue;

            bool enemy = IsEnemy(targetASC);
            if (!ApplyHitEffects(targetASC, firstHit: enemy && firstEnemy)) continue;

            // El sonido ya sonó una vez en el estallido: acá solo los VFX de golpe.
            BroadcastHitVFX(targetASC, withSound: false);

            if (!enemy) continue;
            firstEnemy = false;

            OnTargetHit(targetASC);
            if (OwnerASC.CompareTag("Player")) ChargeUltimate();
        }

        // Las entradas "al terminar de golpear" (el Baile final del Filo danzante: sus beneficios
        // con los puntos de todos los golpeados).
        ApplyAfterHitsEffects();
    }

    protected override bool SupportsAfterHits => true;

    // Los de adentro del radio (por cada collider: Detonate descarta los repetidos).
    private IEnumerable<AbilitySystemComponent> TargetsInRadius(Vector3 center)
    {
        foreach (Collider hit in Physics.OverlapSphere(center, Radius, TargetLayer))
            yield return hit.GetComponentInParent<AbilitySystemComponent>();
    }

    // "Todo el mapa": cada personaje vivo de la partida, menos el que la usa (que recibe lo suyo por "al activarse") y lo
    // que se golpea del mapa (la campana: desde la otra punta no tiene sentido). Con "Solo jugadores", sin NPCs ni
    // invocaciones; con "No a los invisibles", sin enemigos ocultos. Se busca una vez por golpe, no cada frame.
    private IEnumerable<AbilitySystemComponent> GlobalTargets()
    {
        foreach (AbilitySystemComponent asc in Object.FindObjectsByType<AbilitySystemComponent>(FindObjectsSortMode.None))
        {
            if (asc == null || ReferenceEquals(asc, OwnerASC) || asc.Strikeable != null) continue;
            if (asc.HasTag(EGameplayTag.State_Dead)) continue;
            if (GlobalPlayersOnly && !IsPlayerCharacter(asc)) continue;
            if (GlobalSkipsHidden && IsEnemy(asc) && asc.IsHiddenFromEnemies) continue;
            yield return asc;
        }
    }

    // Un jugador o un bot (con PlayerController). Sin red (las pruebas), cualquiera que no sea una invocación.
    private static bool IsPlayerCharacter(AbilitySystemComponent asc)
    {
        if (asc.GetComponent<PlayerController>() != null) return true;
        return asc.GetComponent<NetworkAbilitySystemComponent>() == null && asc.GetComponent<Entity_Summon>() == null;
    }

    // =========================================================
    // ESTALLIDO QUE PIDE OTRA HABILIDAD (la Bola de fuego del Mago, al chocar)
    // =========================================================

    // Solo el estallido en 'center' (sus efectos y su VFX), sin el ciclo de vida de una
    // habilidad: ni costo, ni cooldown, ni animación, ni EndAbility. Servidor.
    public void DetonateAt(Vector3 center)
    {
        if (!IsServer || OwnerASC == null) return;
        Detonate(center);
    }

    // Una copia de 'template' con este dueño, lista para DetonateAt. Devuelve 'cached' si ya era
    // eso (quien la usa la guarda y la reusa en cada lanzamiento).
    public static GA_InstantAoE Deployable(GA_InstantAoE template, AbilitySystemComponent owner, GA_InstantAoE cached)
    {
        if (cached != null && cached.SourceTemplate == template && cached.OwnerASC == owner) return cached;

        GA_InstantAoE area = Instantiate(template);
        area.Initialize(owner);
        area.SourceTemplate = template;   // su identidad en la red (el VFX del estallido)
        area.CooldownEffect = null;
        area.CostEffect     = null;
        area.DisableCharges();
        return area;
    }

    // La copia de 'template' de 'owner' para las explosiones que no son de una habilidad del dueño (la marca que estalla al
    // detonarla, GameplayEffect.ExplosionOnRemoved): una por plantilla y dueño, guardada acá. null = sin dueño.
    private static readonly Dictionary<(GA_InstantAoE, AbilitySystemComponent), GA_InstantAoE> s_deployed =
        new Dictionary<(GA_InstantAoE, AbilitySystemComponent), GA_InstantAoE>();

    public static GA_InstantAoE DeployableFor(GA_InstantAoE template, AbilitySystemComponent owner)
    {
        if (template == null || owner == null) return null;
        if (s_deployed.Count > 64) s_deployed.Clear();   // dueños que ya no están (cambios de partida): se rehacen solas
        s_deployed.TryGetValue((template, owner), out GA_InstantAoE cached);
        if (cached == null || cached.OwnerASC != owner) cached = Deployable(template, owner, null);
        s_deployed[(template, owner)] = cached;
        return cached;
    }

    // Gancho para que una habilidad concreta reaccione a cada enemigo alcanzado
    // (ej: los Cañones del Pirata, que además le apuestan a quien golpean).
    protected virtual void OnTargetHit(AbilitySystemComponent target) { }

    // Vista previa del área en el Editor.
    public override void DrawGizmos(Transform origin)
    {
        if (origin == null || Global) return;   // todo el mapa: no hay área que dibujar

        Vector3 center = DeployMode == GA_ContinuousAoE.EAoEDeploy.AtReticle
            ? origin.position + origin.forward * MaxRange
            : origin.position;

        Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.35f);
        Gizmos.DrawSphere(center, Radius);
    }

#if UNITY_EDITOR
    public override void DrawSceneHandles(Transform origin)
    {
        if (origin == null || Global) return;   // todo el mapa: no hay área que dibujar

        Vector3 center = origin.position;
        if (DeployMode == GA_ContinuousAoE.EAoEDeploy.AtReticle)
        {
            AbilityHandles.Distance(this, "Max Range", origin.position, origin.forward, ref MaxRange,
                                    AbilityHandles.DistanceColor);
            center = origin.position + origin.forward * MaxRange;
        }
        AbilityHandles.Radius(this, "Radius", center, ref Radius, AbilityHandles.RadiusColor, origin.right);
    }
#endif

    // =========================================================
    // DATOS VIEJOS (solo para pasarlos a las listas; ver GameplayAbility.UpgradeLegacyData)
    // =========================================================

    [SerializeField, HideInInspector] private GA_ContinuousAoE.EAoETarget Targets = GA_ContinuousAoE.EAoETarget.Enemies;
    [SerializeField, HideInInspector] private List<GameplayEffect> EffectsToApply;
    [SerializeField, HideInInspector] private GameObject VisualPrefab;
    [SerializeField, HideInInspector] private float VisualScaleMultiplier = 2f;
    [SerializeField, HideInInspector] private float VisualLifetime = 2f;

    protected override void OnUpgradeLegacyData(ref bool changed)
    {
        base.OnUpgradeLegacyData(ref changed);

        UpgradeEffects(EffectsToApply, EEffectWhen.OnHit, GA_ContinuousAoE.ToEffectTarget(Targets), ref changed);
        UpgradeVisual(ref VisualPrefab, new AbilityVisual
        {
            When = EVisualWhen.OnImpact, MatchAreaSize = true,
            AreaSizeMultiplier = VisualScaleMultiplier, DestroyTime = VisualLifetime,
        }, ref changed);
    }
}
