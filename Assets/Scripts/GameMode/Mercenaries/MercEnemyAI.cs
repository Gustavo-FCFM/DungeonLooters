using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// MercEnemyAI
//
// Los NPCs que plagan el escenario del modo Mercenarios (los fantasmas de la game
// jam). Son el "PvE" del PvEvP: no son una amenaza real, están para que haya algo que
// hacer entre pelea y pelea y para repartir experiencia a las bolsas de los equipos.
//
// Es la versión EN RED del EnemyAI de la jam, que era de un jugador solo (buscaba al
// PlayerController con FindFirstObjectByType y corría en todas las máquinas). Acá:
//
//   · TODA la decisión (a quién persigo, cuándo pego) corre SOLO en el servidor. Los
//     clientes ven el resultado por el NetworkTransform del prefab y la vida por el
//     NetworkAbilitySystemComponent.
//   · La detección es CORTA a propósito (el diseño pide que no salten encima de los
//     jugadores que pasan cerca): fuera de ese radio ni se enteran, y si los sacás de
//     su zona vuelven solos a su puesto.
//   · La experiencia de matarlo NO se reparte acá: la baja viaja por el camino normal
//     del core (LastAttacker → NetworkASC.AwardKillExperience), que se la deja a la
//     bolsa del EQUIPO del que remató.
//
// El prefab necesita: NetworkObject, NetworkTransform, AbilitySystemComponent,
// NetworkAbilitySystemComponent, NavMeshAgent y este script.
// ============================================================
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 25 de noviembre de 2026). En este archivo: el equipo de los
// monstruos, los campos del Inspector y el arranque; lo demás, en GameMode/Mercenaries/MercEnemyAI/:
//   · Brain   la cabeza (servidor): a quién ve, perseguir, atacar y volver a su puesto
//   · Combat  el bloqueo después de atacar y la rotación de habilidades
//   · Death   morir, dar la experiencia y deshacerse
//   · Visuals lo que ven todos: animación, barra de vida y efectos
[RequireComponent(typeof(AbilitySystemComponent))]
public partial class MercEnemyAI : NetworkBehaviour
{
    // ============================================================
    // EL EQUIPO DE LOS MONSTRUOS
    //
    // Los NPCs son un CUARTO bando, no "neutrales". Antes iban con TeamID 0, que en el
    // core significa "hostil para todos y aliado de nadie", y eso traía dos problemas
    // que solo se ven cuando el enemigo hace algo más que pegar:
    //
    //   · Un aura de buff para los suyos NO alcanzaba a nadie. IsAllyOf devuelve false
    //     apenas uno de los dos es 0, así que el jefe se buffeaba solo.
    //   · Un AoE de daño lastimaba a sus propios fantasmas, porque IsEnemyOf devuelve
    //     true contra cualquiera si uno de los dos es 0.
    //
    // Con un equipo propio (4), los monstruos son aliados entre ellos y enemigos de los
    // tres equipos de jugadores, sin tocar una línea del core. El 4 queda fuera del
    // rango 1..3 del modo, así que ni puntúa, ni suma experiencia, ni aparece en el
    // marcador.
    // ============================================================
    public const int MonsterTeamId = 4;

    // Una habilidad de la rotación, con su propio reloj y su propio alcance. Un jefe con
    // una sola habilidad cada diez segundos es un fantasma lento; lo que lo hace pelea es
    // tener dos o tres cosas que hace en momentos distintos.
    [System.Serializable]
    public class ExtraAbility
    {
        [Label("Habilidad")]
        public GameplayAbility Ability;

        [Tooltip("Segundos entre usos de ESTA habilidad. Es independiente de la cadencia " +
                 "general del enemigo y del cooldown propio del asset.")]
        [Label("Espera (s)")]
        public float Cooldown = 10f;

        [Tooltip("Alcance propio. En 0 usa el Attack Range del enemigo. Sirve para el golpe " +
                 "de área que solo tiene sentido cuando lo tenés encima.")]
        [Label("Alcance (m; 0 = el de ataque)")]
        public float Range = 0f;

        [Tooltip("Usarla solo cuando al enemigo le quede ESTA fracción de vida o menos " +
                 "(0.5 = de la mitad para abajo). En 1 se puede usar siempre. Es la forma " +
                 "barata de que un jefe tenga 'segunda fase'.")]
        [Label("Usar con vida bajo (0 a 1)")]
        [Range(0.05f, 1f)] public float UseBelowHealthPercent = 1f;

        [Tooltip("Esperar este tanto desde que aparece antes de usarla por primera vez.")]
        [Label("Espera inicial (s)")]
        public float InitialDelay = 3f;
    }

    [Header("Percepción")]
    [Tooltip("A qué distancia detecta a un jugador. Corto a propósito: no queremos que " +
             "todo el mapa se le venga encima al que pasa corriendo.")]
    [Label("Radio de detección (m)")]
    public float DetectionRadius = 9f;

    [Tooltip("Cuánto se puede alejar de su puesto persiguiendo. Pasado eso, vuelve.")]
    [Label("Radio de persecución (m)")]
    public float LeashRadius = 18f;

    [Tooltip("Cada cuánto vuelve a buscar objetivo (segundos). No hace falta cada frame.")]
    [Label("Cada cuánto busca objetivo (s)")]
    public float RetargetInterval = 0.4f;

    [Tooltip("Capa donde viven los personajes. En este proyecto es 'Character' (7).")]
    [Label("Capa de personajes")]
    public LayerMask CharacterLayer = 1 << 7;

    [Header("Combate")]
    [Tooltip("Desde qué distancia puede atacar. Un fantasma a melee: 2. Un mago: 10.")]
    [Label("Alcance de ataque (m)")]
    public float AttackRange = 2.2f;

    [Label("Entre ataques (s)")]
    public float AttackCooldown = 1.6f;

    [Tooltip("Distancia que trata de MANTENER con su objetivo. En 0 se le tira encima (melee); " +
             "con un valor, retrocede si lo tiene más cerca que eso (magos y jefes a distancia).")]
    [Label("Distancia a mantener (m)")]
    public float KeepDistance = 0f;

    [Tooltip("Qué tan rápido gira para encarar. Importa más de lo que parece en los enemigos a " +
             "distancia: el proyectil sale hacia ADELANTE, así que si todavía no terminó de girar, " +
             "el tiro se va a cualquier lado.")]
    [Label("Rapidez de giro (más = más rápido)")]
    public float TurnSpeed = 10f;

    [Tooltip("Cuánto puede estar desalineado y aun así disparar, en grados. Hasta que no encara, " +
             "no ataca.")]
    [Label("Tolerancia de puntería (°)")]
    public float AimTolerance = 15f;

    [Tooltip("No atacar si hay una pared en el medio. Con la arena a tres alturas, sin esto los " +
             "magos disparan a través de la meseta y de los tablados.")]
    [Label("Necesita línea de tiro")]
    public bool RequireLineOfSight = true;

    [Tooltip("Qué cuenta como pared para la línea de tiro. Por defecto todo MENOS los personajes " +
             "(si no, un fantasma delante de otro le taparía el tiro).")]
    [Label("Capas que tapan")]
    public LayerMask SightBlockers = ~(1 << 7);

    [Tooltip("Altura de los 'ojos' desde donde se traza la línea de tiro.")]
    [Label("Altura de los ojos (m)")]
    public float EyeHeight = 1.4f;

    [Tooltip("Efecto de daño que aplica al golpear. Podés reusar el GE_EnemyDamage de la jam.")]
    [Label("Efecto de daño")]
    public GameplayEffect DamageEffect;

    [Tooltip("Daño plano si no asignaste un GameplayEffect arriba.")]
    [Label("Daño sin efecto (vida)")]
    public float FallbackDamage = 8f;

    [Tooltip("Opcional: en vez de pegar a melee, activa esta habilidad (enemigos a distancia). " +
             "Es su ataque BÁSICO, el que repite todo el tiempo.")]
    [Label("Habilidad a usar")]
    public GameplayAbility AbilityToUse;

    [Tooltip("Habilidades extra con su propio tiempo de espera: la rotación de un jefe. Cuando " +
             "una está lista y en rango, se usa EN LUGAR del ataque básico de ese turno. Se " +
             "revisan en orden, así que poné arriba la más importante.")]
    [Label("Habilidades extra")]
    public List<ExtraAbility> ExtraAbilities = new List<ExtraAbility>();

    [Tooltip("VFX en el jugador al recibir el golpe (opcional).")]
    [Label("VFX al golpear")]
    public GameObject HitVFX;

    [Header("Bloqueo después de atacar (el esqueleto)")]
    [Tooltip("Después de cada ataque se cubre: se le aplica este efecto (tiene que dar Status_Blocking, que levanta " +
             "su Entity_ShieldBarrier) y se queda quieto mirando hacia quien lo atacaba, sin girar, por \"Se cubre por\". " +
             "Vacío = no se cubre.")]
    [Label("Efecto de bloqueo")]
    public GameplayEffect BlockAfterAttack;

    [Tooltip("Cuánto se queda cubriéndose hacia el mismo lado. Por la espalda o los costados se le puede pegar.")]
    [Label("Se cubre por (s)")]
    public float BlockSeconds = 3f;

    [Header("Recompensa")]
    [Tooltip("Experiencia que le da a la bolsa del equipo que lo mate. En 0 usa el valor general " +
             "del modo de juego (XpPerNpcKill). Se pone un número acá cuando este enemigo vale " +
             "más que el resto — un jefe no puede dar lo mismo que un fantasma.")]
    [Label("Experiencia que da (exp; 0 = la del modo)")]
    public float ExperienceReward = 0f;

    [Header("Muerte")]
    [Tooltip("Segundos entre que muere y desaparece (deja ver la animación de muerte).")]
    [Label("Desaparece tras (s)")]
    public float DespawnDelay = 2f;

    [Header("Al morir (el zombi hinchado)")]
    [Tooltip("Revienta al morir: a los enemigos a esta distancia les aplica \"Efecto del estallido\". 0 = no revienta. " +
             "No revienta si lo retira un santuario (ServerDismiss).")]
    [Label("Estallido: radio (m; 0 = no)")]
    public float DeathBurstRadius = 0f;

    [Tooltip("El daño del estallido (su autor es este NPC: puede escalar con su ataque).")]
    [Label("Efecto del estallido")]
    public GameplayEffect DeathBurstEffect;

    [Tooltip("Lo que se ve al reventar, en todas las pantallas. Se borra solo a los 3 s.")]
    [Label("VFX del estallido")]
    public GameObject DeathBurstVFX;

    [Tooltip("Una invocación que deja donde murió (Entity_Summon con \"Sigue sin su dueño\": la nube de veneno).")]
    [Label("Deja al morir")]
    public GameObject DeathSummon;

    [Header("Animación")]
    [Tooltip("Parámetro float del Animator con la velocidad de movimiento. Vacío = no se toca.")]
    [Label("Parámetro de velocidad")]
    public string SpeedParameter = "Speed";
    [Tooltip("Trigger del Animator al atacar. Vacío = no se toca.")]
    [Label("Trigger de ataque")]
    public string AttackTrigger = "Attack";

    // --- referencias ---
    private AbilitySystemComponent _asc;
    private NavMeshAgent _agent;
    private Animator _animator;

    // --- estado servidor ---
    private Vector3 _homePosition;
    private AbilitySystemComponent _target;
    private GameplayAbility _runtimeAbility;
    private GameplayAbility[] _extraRuntime;   // instancias otorgadas de la rotación
    private float[] _extraNextUse;             // cuándo vuelve a estar lista cada una
    private GameplayEffect _runtimeDamage;
    private float _retargetTimer;
    private float _lastAttackTime;
    private bool _dead;

    // --- estado de presentación (todos los peers) ---
    private Vector3 _lastVisualPos;
    private float _visualSpeed;

    // El spawner que lo creó, para que sepa que tiene que reponerlo.
    [System.NonSerialized] public MercEnemySpawner Spawner;

    // Todos los NPCs en juego, solo en el servidor. Los santuarios buscan acá los que están dentro
    // de su zona (MatchEvent_Shrines).
    public static readonly List<MercEnemyAI> ServerAll = new List<MercEnemyAI>();

    public AbilitySystemComponent Asc => _asc;
    public bool IsDead => _dead;

    // ¿Está peleando? (tiene a quién atacar; solo en el servidor). Lo usa su campamento de invocados.
    public bool HasTarget => _target != null;

    [Header("Invocar (el Nigromante)")]
    [Tooltip("Lo que se ve donde aparece cada invocado de su campamento (MercEnemySpawner hijo con \"Solo mientras " +
             "su dueño viva\"). Se borra solo a los 2.5 s.")]
    [Label("VFX al invocar")]
    public GameObject SummonVFX;

    // Su campamento invocó a alguien en 'point': el VFX ahí y el gesto de ataque, en todas las pantallas.
    [Server]
    public void ServerShowSummon(Vector3 point)
    {
        if (SummonVFX != null) ObserversSummonVfx(point);
        if (!string.IsNullOrEmpty(AttackTrigger)) ObserversPlayAttack();
    }

    [ObserversRpc(RunLocally = true)]
    private void ObserversSummonVfx(Vector3 point)
    {
        if (SummonVFX == null) return;
        GameObject vfx = Instantiate(SummonVFX, point + Vector3.up * 0.05f, Quaternion.identity);
        Destroy(vfx, 2.5f);
    }

    private void Awake()
    {
        _asc      = GetComponent<AbilitySystemComponent>();
        _agent    = GetComponent<NavMeshAgent>();
        _animator = GetComponentInChildren<Animator>();
        _lastVisualPos = transform.position;
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        _homePosition = transform.position;
        if (!ServerAll.Contains(this)) ServerAll.Add(this);

        if (_asc != null)
        {
            _asc.TeamID = MonsterTeamId;
            _asc.OnDeath += HandleDeath;
            _asc.OnTookDamage += HandleTookDamage;

            if (AbilityToUse != null) _runtimeAbility = _asc.GrantAbility(AbilityToUse);
            GrantExtraAbilities();

            // El ASC carga sus atributos en Awake, o sea ANTES de que este objeto
            // exista en la red: esos primeros valores no dispararon ninguna
            // sincronización y los clientes verían al fantasma con la barra de vida en
            // CERO hasta el primer golpe. Un volcado completo acá lo arregla. (Al
            // jugador no le pasa porque su clase se equipa después de spawnear, y ese
            // camino ya reinicializa los atributos.)
            NetworkAbilitySystemComponent netASC = GetComponent<NetworkAbilitySystemComponent>();
            if (netASC != null) netASC.SyncAllAttributesToNet();
        }

        if (_agent != null)
        {
            _agent.enabled = true;

            // La distancia la manejamos NOSOTROS (ver ServerTick), así que el agente no
            // tiene que frenar por su cuenta. Con un stoppingDistance grande —lo que
            // pedía el alcance de un mago— el agente daba por "llegado" cualquier destino
            // cercano y la retirada para mantener distancia no se movía ni un metro.
            _agent.stoppingDistance = 0f;
        }
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        // En los clientes la posición llega por el NetworkTransform: un NavMeshAgent
        // activo pelearía contra ella (y encima cada cliente calcularía su propio
        // camino). Solo el servidor navega.
        if (!IsServerInitialized && _agent != null) _agent.enabled = false;
    }

    public override void OnStopServer()
    {
        base.OnStopServer();
        ServerAll.Remove(this);
        if (_asc != null) { _asc.OnDeath -= HandleDeath; _asc.OnTookDamage -= HandleTookDamage; }
        if (_runtimeDamage != null) Destroy(_runtimeDamage);
    }

    private void OnDestroy()
    {
        ServerAll.Remove(this);
    }

    private void Update()
    {
        UpdateVisuals();
        if (!IsServerInitialized || _dead) return;
        ServerTick();
    }

    // =========================================================
    // BLOQUEO DESPUÉS DE ATACAR
    // =========================================================

    private float _blockUntil;
    private Vector3 _blockDirection;
    private AbilitySystemComponent _lastHitter;
    private float _lastHitAt = -100f;

    private bool _dismissed;
    private Vector3 _deathPoint;
    private static readonly Collider[] s_burstHits = new Collider[32];
}
