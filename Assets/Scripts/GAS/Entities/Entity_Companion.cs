using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// Entity_Companion  (las mascotas del Maestro de bestias — 14 de octubre de 2026)
//
// Un compañero animal (Lobo, Oso, Búho) que pelea junto a su Explorador. Toda la decisión
// corre en el SERVIDOR, como los fantasmas (MercEnemyAI); los demás lo ven por el
// NetworkTransform y su vida por el NetworkAbilitySystemComponent. Lo crea y lo saca el
// CompanionManager del dueño.
//
// CÓMO ELIGE A QUIÉN PEGAR (pedido de Gustavo):
//   · PASIVO: cada AttackInterval segundos ataca solo a un enemigo dentro de su rango
//     (PassiveRange, medido desde el DUEÑO): primero jugadores, y el más cercano al dueño.
//     El Búho busca primero a los invisibles. Pegarle lo pone en cooldown.
//   · ORDEN: cuando el Explorador le pega a alguien, el compañero se olvida del cooldown y
//     corre al doble de velocidad a pegarle a ese, aunque esté fuera de su rango. Si estaba
//     fuera, después de ese golpe lo suelta y vuelve a merodear.
//   · DEFENDER: el que le pega al Explorador pasa a ser su objetivo (como una orden).
//   · Se queda con su objetivo mientras siga en rango; si se va, merodea y busca otro.
//   · No ataca invisibles (salvo el Búho, que los prioriza), ni a nadie en su sala segura.
//
// CÓMO SE MUEVE:
//   · Lobo y Oso por el piso (NavMeshAgent). Si el objetivo está en una pared o saltando
//     alto, se quedan debajo esperando; si el Explorador le pega a otro, van a ese.
//   · El Oso se pone DELANTE del Explorador, entre él y el enemigo más cercano (sin
//     taparle la vista), y los fantasmas lo prefieren si cerca no hay otro jugador.
//   · El Búho no busca camino: flota sobre el Explorador, APARECE junto a su objetivo
//     para el picotazo y vuelve.
//   · Si queda lejos del dueño o trabado, reaparece a su lado.
//
// IRA PRIMAL (la definitiva del Maestro de bestias, 15 de octubre de 2026): mientras el dueño
// tiene Status_PrimalFury, el CompanionManager saca a los tres, ataca cada uno el doble de
// rápido (Ira primal: entre ataques) y su presencia suma su versión extra (la misma otra vez:
// la Presencia bestial vale doble).
//
// SU DAÑO ES DEL EXPLORADOR: sus efectos se aplican con el dueño como autor (escalan con
// su ataque, la baja es suya y le carga la definitiva). El CompanionManager ignora esos
// golpes al escuchar "el Explorador pegó", si no la mascota se daría órdenes sola.
//
// Desde el 20 de octubre de 2026 es una invocación (Entity_Summon): el equipo, la regla de
// enemigo, irse si el Explorador se va o cambia de clase, y su propia muerte (resuelta fuera
// del daño, en Update) los resuelve la base. Su "Modelo" es el de la base. Lo de cuál está
// afuera, la Ira primal, la vida guardada y las órdenes sigue en el CompanionManager.
//
// El prefab: NetworkObject, NetworkTransform, AbilitySystemComponent,
// NetworkAbilitySystemComponent, este script y (Lobo y Oso) NavMeshAgent y un collider en
// la capa Character. El Búho, sin collider: no se le puede pegar.
// ============================================================
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 25 de noviembre de 2026). En este archivo: los campos del
// Inspector, la lista de compañeros y el arranque; lo demás, en GAS/Entities/Entity_Companion/:
//   · Brain     las órdenes del CompanionManager, qué hace en cada momento (servidor) y la muerte
//   · Movement  moverse por el piso y saltar a otro piso o a una plataforma
//   · Skills    el búho (flota y aparece junto al objetivo), la presencia bestial y el aullido
//   · Visuals   lo que ven todos: animación y efectos
[RequireComponent(typeof(AbilitySystemComponent))]
public partial class Entity_Companion : Entity_Summon
{
    [Header("Vida")]
    [Tooltip("Su vida máxima, como fracción de la del Explorador al aparecer (0.5 = la mitad).")]
    [Label("Vida (0 a 1 de la del dueño)")]
    public float HealthFraction = 0.5f;

    [Tooltip("No se le puede hacer daño (el Búho). Igual conviene que su prefab no tenga collider.")]
    [Label("Indestructible")]
    public bool Indestructible;

    [Header("Moverse")]
    [Tooltip("Metros por segundo caminando.")]
    [Label("Velocidad (m/s)")]
    public float MoveSpeed = 6f;

    [Tooltip("Velocidad mientras va a cumplir una orden (el Explorador le pegó a alguien) o a defenderlo.")]
    [Label("Velocidad al ir a una orden (×)")]
    public float ChaseMultiplier = 2f;

    [Tooltip("Dónde se queda junto al dueño cuando no pelea, en el espacio del dueño (X = derecha, " +
             "Y = arriba, Z = adelante). El Búho flota: su Y es la altura.")]
    [Label("Lugar junto al dueño (m)")]
    public Vector3 FollowOffset = new Vector3(1.5f, 0f, -2.5f);

    [Tooltip("Se pone DELANTE del dueño, entre él y el enemigo más cercano (el Oso).")]
    [Label("Se pone delante del dueño")]
    public bool GuardOwner;

    [Tooltip("No camina: flota junto al dueño y para atacar APARECE junto al objetivo (el Búho).")]
    [Label("Ataca apareciendo")]
    public bool BlinkAttack;

    [Tooltip("Si queda más lejos que esto del dueño (sin estar persiguiendo a nadie), reaparece a su lado.")]
    [Label("Reaparece junto al dueño a (m)")]
    public float TeleportDistance = 25f;

    [Tooltip("Hasta qué distancia del dueño acepta una orden. Más lejos, la ignora: no llegaría nunca.")]
    [Label("Alcance de las órdenes (m)")]
    public float CommandRange = 40f;

    [Header("Merodear (sin pelear)")]
    [Tooltip("Sin nadie a quien pegar, da vueltas por la zona del dueño en vez de quedarse pegado " +
             "detrás de él (el Lobo). No vale para el que se pone delante (Oso) ni para el que flota (Búho).")]
    [Label("Merodea alrededor del dueño")]
    public bool Roam = true;

    [Tooltip("Hasta qué distancia del dueño elige los puntos a donde ir.")]
    [Label("Merodear: radio (m)")]
    public float RoamRadius = 6f;

    [Tooltip("Los puntos quedan al menos a esta distancia del dueño: no se le mete entre los pies.")]
    [Label("Merodear: distancia mínima (m)")]
    public float RoamMinRadius = 2.5f;

    [Tooltip("Velocidad al merodear, como fracción de la normal (camina, no corre).")]
    [Label("Merodear: velocidad (×)")]
    public float RoamSpeedFactor = 0.6f;

    [Tooltip("Segundos que se queda quieto al llegar a cada punto (al azar entre los dos).")]
    [Label("Merodear: pausa mínima (s)")]
    public float RoamPauseMin = 1f;

    [Label("Merodear: pausa máxima (s)")]
    public float RoamPauseMax = 3f;

    [Header("Saltar entre pisos")]
    [Tooltip("Si el camino por el piso da mucha vuelta o no llega (otro piso, una plataforma), SALTA " +
             "derecho al punto, si está a esta distancia horizontal o menos.")]
    [Label("Salto: distancia máxima (m)")]
    public float JumpRange = 14f;

    [Tooltip("Cuánto más arriba o abajo puede saltar.")]
    [Label("Salto: altura máxima (m)")]
    public float JumpHeight = 9f;

    [Tooltip("Salta si el camino por el piso es más largo que la línea recta × esto (más unos metros).")]
    [Label("Salto: si el camino da más vuelta que (×)")]
    public float JumpDetourFactor = 1.8f;

    [Label("Salto: espera entre saltos (s)")]
    public float JumpCooldown = 1.5f;

    [Header("Atacar")]
    [Tooltip("Hasta dónde ataca solo, medido desde el DUEÑO (Lobo 20, Oso 12, Búho 30).")]
    [Label("Rango pasivo (m)")]
    public float PassiveRange = 20f;

    [Tooltip("Segundos entre un golpe y el siguiente.")]
    [Label("Entre ataques (s)")]
    public float AttackInterval = 1.2f;

    [Tooltip("A qué distancia (horizontal) del objetivo puede pegarle.")]
    [Label("Alcance del golpe (m)")]
    public float AttackReach = 2.2f;

    [Tooltip("Cuánto más arriba o abajo que él puede estar el objetivo para alcanzarlo. Uno en " +
             "una pared o saltando más alto no lo alcanza: lo espera abajo.")]
    [Label("Alto que alcanza (m)")]
    public float ReachHeight = 2.5f;

    [Tooltip("Lo que le aplica al golpeado, con el Explorador como autor (daño, heridas, " +
             "aturdido...). Cada entrada puede tener su espera por enemigo.")]
    [Label("Efectos del golpe")]
    public List<AbilityEffect> AttackEffects = new List<AbilityEffect>();

    [Tooltip("Ataca a los invisibles, y PRIMERO a ellos (el Búho).")]
    [Label("Caza invisibles")]
    public bool HuntsInvisible;

    [Tooltip("Segundos que el equipo del dueño ve al golpeado a través de las paredes. 0 = no lo revela.")]
    [Label("Revela al golpeado (s)")]
    public float RevealSeconds;

    [Tooltip("Cuánto se acuerda de quien le pegó al dueño para defenderlo.")]
    [Label("Defiende durante (s)")]
    public float DefendMemory = 4f;

    [Tooltip("VFX en el golpeado (opcional).")]
    [Label("VFX del golpe")]
    public GameObject HitVFX;

    [Header("Aullido (Lobo)")]
    [Tooltip("Si muere un enemigo al que mordió hace poco, los aliados cerca reciben esto (se acumula).")]
    [Label("Efecto del aullido")]
    public GameplayEffect HowlEffect;

    [Label("Radio del aullido (m)")]
    public float HowlRadius = 10f;

    [Tooltip("Cuánto tiempo después de morderlo cuenta su muerte para aullar.")]
    [Label("Mordido hace menos de (s)")]
    public float HowlMemory = 5f;

    [Header("Presencia bestial")]
    [Tooltip("Lo que reciben los aliados cerca del compañero O del Explorador (los dos dan lo " +
             "mismo y no se suma: es un GE que se refresca).")]
    [Label("Efecto de presencia")]
    public GameplayEffect PresenceEffect;

    [Label("Radio de presencia (m)")]
    public float PresenceRadius = 8f;

    [Header("Ira primal (la definitiva)")]
    [Tooltip("Durante la Ira primal, el tiempo entre ataques se multiplica por esto. 0.5 = ataca " +
             "el doble de rápido.")]
    [Label("Ira primal: entre ataques (×)")]
    public float FuryAttackIntervalMultiplier = 0.5f;

    [Tooltip("Durante la Ira primal, además de su presencia, los mismos aliados reciben este efecto " +
             "(una copia oculta de la presencia: así vale doble). Vacío = la presencia no sube.")]
    [Label("Ira primal: presencia extra")]
    public GameplayEffect FuryPresenceEffect;

    [Header("Provocar (Oso)")]
    [Tooltip("Los fantasmas lo atacan a él si cerca no hay otro jugador que su dueño.")]
    [Label("Los NPCs lo prefieren")]
    public bool TauntsNpcs;

    [Header("Referencias")]
    [Label("Capa de personajes")]
    public LayerMask CharacterLayer = 1 << 7;

    // Quién es su dueño, para todas las pantallas (el HUD del dueño busca a su compañero). Lo
    // sincroniza la base. Su "Modelo" (Visual, de la base) se adelanta un poco en cada golpe;
    // vacío = el primer hijo.
    public NetworkObject OwnerObject => SummonerObject;

    // Qué opción de la rueda es (0 = la primera), para todas las pantallas: con la Ira primal hay
    // tres del mismo dueño y el HUD tiene que encontrar al elegido.
    private readonly SyncVar<int> _optionIndex = new SyncVar<int>(-1);
    public int OptionIndex => _optionIndex.Value;

    // El Explorador (solo servidor). Es el dueño de la invocación.
    public AbilitySystemComponent OwnerASC => Summoner;
    public AbilitySystemComponent ASC => _asc;

    private AbilitySystemComponent _asc;
    private NavMeshAgent           _agent;
    private CompanionManager       _manager;

    // --- servidor ---
    private AbilitySystemComponent _target;
    private bool  _commanded;            // va a cumplir una orden (corre más rápido)
    private float _attackReadyAt;
    private AbilitySystemComponent _defendFrom;
    private float _defendUntil;
    private float _blinkReturnAt;        // el Búho vuelve junto al dueño a esta hora
    private float _presenceTimer;
    private float _stuckTimer;
    private Vector3 _lastPosition;
    private bool  _dead;
    private bool  _leaping;
    private Vector3 _leapFrom, _leapTo;
    private float _leapStart, _leapDuration, _leapHeight;
    private float _nextPathCheck, _nextLeapAt;
    private bool    _hasRoamPoint;
    private Vector3 _roamPoint;
    private float   _roamWaitUntil;
    private readonly Dictionary<AbilitySystemComponent, float> _bitten = new Dictionary<AbilitySystemComponent, float>();
    private readonly Collider[] _hits = new Collider[48];
    private readonly HashSet<AbilitySystemComponent> _seen = new HashSet<AbilitySystemComponent>();

    // --- todas las pantallas: el tirón del golpe ---
    private Vector3 _visualRest;
    private float   _lungeUntil;

    // Los compañeros prendidos en este proceso, en lugar de FindObjectsByType (25 de
    // noviembre de 2026; los busca CompanionManager). Se lee con un foreach directo.
    public static IReadOnlyList<Entity_Companion> All => s_all;
    private static readonly List<Entity_Companion> s_all = new List<Entity_Companion>();

    private void OnEnable()  { if (!s_all.Contains(this)) s_all.Add(this); }
    private void OnDisable() => s_all.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetAllList() => s_all.Clear();

    protected override void Awake()
    {
        base.Awake();
        _asc   = GetComponent<AbilitySystemComponent>();
        _agent = GetComponent<NavMeshAgent>();
        if (Visual == null && transform.childCount > 0) Visual = transform.GetChild(0);
        if (Visual != null) _visualRest = Visual.localPosition;
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        // La posición llega por el NetworkTransform: un agente activo pelearía contra ella.
        if (!IsServerInitialized && _agent != null) _agent.enabled = false;
    }

    // Lo llama el CompanionManager en el servidor, justo después de crearlo con
    // Entity_Summon.Spawn (que ya le dio el dueño y el equipo). healthFraction: con cuánta vida
    // sale (la que tenía guardada si se lo cambió herido; 1 = llena). optionIndex: qué opción de
    // la rueda es.
    [Server]
    public void Begin(CompanionManager manager, float healthFraction = 1f, int optionIndex = -1)
    {
        AbilitySystemComponent owner = OwnerASC;
        if (owner == null) return;
        _optionIndex.Value = optionIndex;
        _manager = manager;

        NetworkAbilitySystemComponent net = GetComponent<NetworkAbilitySystemComponent>();

        float max = Indestructible ? 100f
                  : Mathf.Max(1f, owner.GetAttributeValue(EAttributeType.MaxHealth) * HealthFraction);
        float delta = max - _asc.GetAttributeValue(EAttributeType.MaxHealth);
        if (!Mathf.Approximately(delta, 0f)) _asc.UpgradeAttribute(EAttributeType.MaxHealth, delta);
        _asc.SetCurrentAttributeValue(EAttributeType.Health, Mathf.Max(1f, max * Mathf.Clamp01(healthFraction)));

        if (Indestructible) _asc.AddTag(EGameplayTag.Status_Immunity);
        if (TauntsNpcs)     _asc.AddTag(EGameplayTag.Status_Taunting);

        // La vida nueva (la de su dueño), a todas las pantallas.
        if (net != null) net.SyncAllAttributesToNet();

        if (_agent != null && !BlinkAttack)
        {
            _agent.enabled          = true;
            _agent.stoppingDistance = 0f;
            _agent.acceleration     = 40f;
            _agent.angularSpeed     = 720f;
            _agent.autoBraking      = true;
        }
        _lastPosition  = transform.position;
        _attackReadyAt = Time.time + 0.5f;
    }

    // Murió alguien que mordió hace poco: aúlla y los aliados cerca pegan más.
    private readonly List<AbilitySystemComponent> _expired = new List<AbilitySystemComponent>();
}
