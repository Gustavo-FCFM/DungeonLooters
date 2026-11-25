using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Qué hace un bot en una pelea. Sale de la clase que tiene puesta (ver ResolveRole),
// y se puede forzar desde el inspector para probar.
public enum EBotRole
{
    Auto,      // se deduce de la clase base
    Bruiser,   // Bárbaro: se le tira encima y no lo suelta
    Assassin,  // Pícaro: espera a que esté débil y recién ahí entra
    Support,   // Paladín: se queda con los suyos y los sostiene
    Ranged,    // Mago (y todo el que pega de lejos con el básico): se queda a distancia de tiro
}

// ============================================================
// BotController
//
// Un jugador manejado por la máquina. Sirve para tener con quién probar —el modo
// espectador, una grabación, el balance— sin juntar nueve personas.
//
// CORRE SOLO EN EL SERVIDOR. El personaje de un bot es el MISMO prefab del jugador,
// spawneado sin dueño (ver NetworkGameManager.ServerSpawnBot): el servidor queda como
// autoridad y por eso puede moverlo y activarle habilidades sin pasar por ningún RPC.
// Para los clientes es un jugador más — lo ven, le pegan, les pega, suma puntos.
//
// POR QUÉ NO REUSA EL PlayerController: todo su Update arranca con "if (!IsOwner)
// return;". Un bot no tiene dueño, así que ese camino nunca corre. Lo que hace este
// script es ocupar ese lugar: mover (con NavMeshAgent, igual que MercEnemyAI), mirar,
// animar y pedir habilidades — pero pidiéndolas por ServerActivateAbility, que es el
// MISMO punto de entrada server-side que usa un jugador de verdad. Así los bots pagan
// cooldowns, energía y tags igual que todos: no hacen trampa.
//
// LAS PRIORIDADES, de más urgente a menos:
//   1. Si un ENEMIGO lleva la carga, matarlo. Todo lo demás espera.
//   2. Si la llevo YO, correr al punto de entrega.
//   3. Si la lleva un COMPAÑERO, escoltarlo.
//   4. Si está libre en el piso, ir a buscarla.
//   5. Un orbe de definitiva a mano (y la definitiva sin llenar): tomarlo.
//   6. Un santuario activo: ir a la zona y pelear dentro (12 de noviembre de 2026).
//   7. Si no, pelear con lo que haya cerca (los NPCs dan experiencia); sin nadie, ir por el
//      jefe del evento si está vivo, o al centro.
// Las campanas no las tocan: son algo del mapa (StrikeTarget), no a quién pelear.
//
// Dentro de eso, cada rol se mueve distinto: ver DecideCombatPosition.
//
// EL INSTINTO (20 de noviembre de 2026) está en BotController.Instinct.cs: esquivar proyectiles, salir de las zonas de
// daño, rodear escudos, elegir mejor a quién pegarle, retirarse bien y apuntar y reaccionar como una persona. Corrige lo
// que se decide acá; sus números están en la sección "Instinto" de abajo.
//
// LOS NÚMEROS SE AJUSTAN EN EL MOLDE (25 de noviembre de 2026): el componente se agrega al nacer el bot, así que sus
// campos no se podían dejar ajustados en el Inspector. Ahora salen del prefab Resources/BotSettings, que lleva solo un
// BotController con los números puestos a mano: al nacer, cada bot copia todos sus campos (ApplySettings). Sin ese
// prefab valen los del código. Por eso no lleva [RequireComponent(typeof(PlayerController))]: el molde no es un jugador.
// ============================================================
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 25 de noviembre de 2026). En este archivo: los campos del
// Inspector, el molde, el arranque y el rol; lo demás, en GameMode/Mercenaries/BotController/:
//   · Moves     el dash, el salto y el vuelo del Mago (lo que un jugador hace por TargetRpc)
//   · Decision  el ciclo de cada frame y la decisión de adónde ir (DecideGoal)
//   · Senses    a quién ve y a quién elige, caminar por el NavMesh y mirar
//   · Fight     la pelea: Classify, ShouldUse, apuntar y activar
//   · Wheel     la rueda de estados (el libro de hechizos del Mago)
//   · Special   revivir, trampas, marcas y el compañero del Maestro de bestias
//   · Instinct  el instinto: esquivar, salir de las zonas, rodear escudos (BotController.Instinct.cs)
public partial class BotController : MonoBehaviour
{
    // ---- El molde con los números (ver la cabecera) ----
    public const string SettingsResource = "BotSettings";
    private static BotController s_settings;
    private static bool s_settingsLoaded;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetSettings() { s_settings = null; s_settingsLoaded = false; }

    // Copia a este bot todos los campos del molde (Resources/BotSettings). La llama quien lo crea, ANTES de
    // ServerInitialize. Se lee del asset cada vez: tocar el molde en Play cambia a los bots que nazcan después.
    public void ApplySettings()
    {
        if (!s_settingsLoaded)
        {
            s_settings = Resources.Load<BotController>(SettingsResource);
            s_settingsLoaded = true;
        }
        if (s_settings != null && s_settings != this) JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(s_settings), this);
    }

    [Header("Rol")]
    [Tooltip("Auto lo deduce de la clase base que tenga puesta. Forzalo solo para probar.")]
    [Label("Rol forzado")]
    public EBotRole RoleOverride = EBotRole.Auto;

    [Header("Percepción")]
    [Tooltip("Hasta dónde ve enemigos. Es generoso a propósito: la arena mide 42 m de radio " +
             "y con poca vista los bots se quedan dando vueltas en su base.")]
    [Label("Radio de visión (m)")]
    public float VisionRadius = 35f;

    [Tooltip("La capa 7 (Character), donde viven jugadores y NPCs.")]
    [Label("Capa de personajes")]
    public LayerMask CharacterLayer = 1 << 7;

    [Tooltip("Cada cuánto vuelve a decidir a quién pegarle y adónde ir. Bajarlo los hace " +
             "más reactivos y más caros; 0.25 alcanza y sobra.")]
    [Label("Cada cuánto decide (s)")]
    public float ThinkInterval = 0.25f;

    [Header("Distancias de pelea")]
    [Tooltip("A qué distancia pega el Bárbaro. Es el alcance de un ataque cuerpo a cuerpo.")]
    [Label("Alcance cuerpo a cuerpo (m)")]
    public float MeleeRange = 2.6f;
    [Tooltip("El Pícaro y el Mago se retiran a curarse cuando su vida baja de esta fracción (el Bárbaro y el soporte, " +
             "con la suya de \"Instinto\").")]
    [Label("Pícaro y Mago: se retiran con vida bajo (0 a 1)")]
    [Range(0.05f, 0.9f)] public float AssassinRetreatBelowHealth = 0.35f;

    [Tooltip("Y vuelven a la pelea (todos, desde el 20 de noviembre de 2026) cuando se recuperaron hasta esta otra. La " +
             "diferencia entre las dos evita que entren y salgan sin parar en el borde exacto.")]
    [Label("Todos: vuelven con vida sobre (0 a 1)")]
    [Range(0.1f, 1f)] public float AssassinReturnAboveHealth = 0.75f;

    [Tooltip("A qué distancia sigue el Paladín a su compañero cuando no hay a quién pegarle.")]
    [Label("Soporte: distancia de seguimiento (m)")]
    public float SupportFollowDistance = 6f;

    [Tooltip("El que lleva el Objetivo, ya parado en la entrega: con quién pelea sin salirse de la plataforma " +
             "(el más cercano a esta distancia). Corriendo hacia la entrega solo le pega al que tiene encima.")]
    [Label("Portador: pelea en la entrega hasta (m)")]
    public float CarrierPadFightRadius = 12f;

    [Tooltip("El que pega de lejos (el Mago): a qué distancia se queda del que está peleando, " +
             "rodeándolo. Más cerca que esto se aleja.")]
    [Label("A distancia: se queda a (m)")]
    public float RangedDistance = 11f;

    [Tooltip("El que pega de lejos: hasta dónde tira el ataque básico (y solo si lo ve, sin pared " +
             "en medio).")]
    [Label("A distancia: tira el básico hasta (m)")]
    public float RangedAttackRange = 25f;

    [Tooltip("Una rueda que deja un estado (el libro de hechizos del Mago): cada cuánto, más o " +
             "menos, cambia a otra opción de las que le alcanzan. Así usa todo el libro (y la Q, que " +
             "cambia con él).")]
    [Label("Rueda de estados: cambia cada (s)")]
    public float ChoiceSeconds = 8f;

    [Tooltip("Con el maná por debajo de esta fracción vuelve a lo normal de su rueda (los misiles, " +
             "que no gastan) y puede recargar.")]
    [Label("Poco maná bajo (0 a 1)")]
    [Range(0.05f, 0.9f)] public float LowManaFraction = 0.3f;

    [Tooltip("Con la vida por debajo de esta fracción y alguien encima, usa lo que lo salva (la " +
             "Tumba helada: si hace falta, primero elige el hielo en el libro).")]
    [Label("En apuros con vida bajo (0 a 1)")]
    [Range(0.05f, 0.9f)] public float EmergencyHealth = 0.35f;

    [Tooltip("Cuánto sostiene una habilidad de MANTENER (el escudo del Paladín) antes de " +
             "soltarla. Un bot no suelta botones: sin esto se quedaría con el escudo " +
             "levantado para siempre y no podría volver a usarlo.")]
    [Label("Tiempo manteniendo (s)")]
    public float HoldSeconds = 1.6f;

    [Tooltip("Cuánto apunta un lanzamiento apuntado (el hacha, las dagas, la lanza) antes de soltarlo, " +
             "además de lo que canalice (la Flecha helada del Mago). Un bot no necesita la cámara de " +
             "apuntar: con más, solo se queda quieto con el arma atrás. Un ataque cargado (el arco, la " +
             "Mordedura de hielo) se sostiene hasta su última etapa.")]
    [Label("Apuntar un lanzamiento (s)")]
    public float ThrowAimSeconds = 0.6f;

    [Header("Colisión del salto y el dash")]
    [Tooltip("Contra qué choca un bot mientras salta o dashea. Todo MENOS la capa 7 " +
             "(Character): los personajes no se frenan entre ellos, igual que en un dash normal.")]
    [Label("Capas de colisión")]
    public LayerMask CollisionMask = ~(1 << 7);

    [Tooltip("Radio de la esfera que barre el camino. Un poco menos que el ancho del " +
             "personaje, para no engancharse en cada esquina.")]
    [Label("Radio del barrido (m)")]
    public float CollisionProbeRadius = 0.35f;

    [Tooltip("A qué altura del pie se hace el barrido, para no chocar contra el propio suelo.")]
    [Label("Altura del barrido (m)")]
    public float CollisionProbeHeight = 0.9f;

    [Header("Que no parezcan estatuas")]
    [Tooltip("Cuánto rodea al enemigo en cada decisión, en grados. En 0 se quedan de frente " +
             "como antes. Cada bot gira para su lado y cambia de sentido cada tanto.")]
    [Label("Rodeo por decisión (°)")]
    public float StrafeDegreesPerThink = 22f;

    [Tooltip("Cada cuántos segundos, más o menos, un bot cambia el sentido en que rodea.")]
    [Label("Cambia de sentido cada (s)")]
    public float StrafeFlipSeconds = 3.5f;

    [Tooltip("Cuánto se despega del enemigo MIENTRAS su ataque básico está en cooldown, en " +
             "múltiplos del alcance. Es lo que hace que peguen y se salgan en vez de " +
             "quedarse plantados comiendo golpes.")]
    [Label("Se despega (×)")]
    public float BackOffRangeMult = 2.1f;

    [Header("Ritmo")]
    [Tooltip("Mínimo entre dos ataques básicos. El cooldown real lo pone la habilidad; " +
             "esto solo evita machacar CanActivate() sesenta veces por segundo.")]
    [Label("Entre ataques básicos (s)")]
    public float PrimaryInterval = 0.6f;

    [Tooltip("Mínimo entre dos intentos de habilidad (Q/E/R). Si están en cooldown, no pasa nada.")]
    [Label("Entre habilidades (s)")]
    public float AbilityInterval = 1.2f;

    [Tooltip("El mismo mínimo, pero MIENTRAS SE ACERCAN. Más corto a propósito: es lo que " +
             "hace que tiren el hacha en el camino en vez de guardarse todo para el cuerpo " +
             "a cuerpo.")]
    [Label("Entre habilidades acercándose (s)")]
    public float ApproachAbilityInterval = 0.45f;

    [Tooltip("Mínimo entre dos INVOCACIONES (los tótems del Chamán). Mucho más largo que el " +
             "resto: el cooldown de la habilidad deja invocar seguido, pero un bot que la " +
             "usa apenas puede llena el mapa de tótems y no se ve como juega una persona.")]
    [Label("Entre invocaciones (s)")]
    public float SummonInterval = 10f;

    [Tooltip("El Maestro de bestias se queda por lo menos esto con un compañero antes de cambiarlo " +
             "por otro que convenga más (ver DesiredCompanion). Si se le muere, cambia enseguida.")]
    [Label("Mantener el compañero (s)")]
    public float CompanionKeepSeconds = 10f;

    [Header("Criterio")]
    [Tooltip("Radio con el que se cuenta si hay VARIOS enemigos juntos. Decide si vale la " +
             "pena un área, un salto encima del grupo, o la definitiva del Bárbaro.")]
    [Label("Radio para grupos (m)")]
    public float AoeRadius = 5f;

    [Tooltip("Por debajo de esta vida, el Pícaro considera que puede EJECUTAR y suelta su " +
             "definitiva.")]
    [Label("Ejecutar con vida bajo (0 a 1)")]
    [Range(0.05f, 0.9f)] public float ExecuteBelowHealth = 0.4f;

    [Tooltip("Por debajo de esta vida se considera que alguien está EN PELIGRO. Es lo que " +
             "hace que el Paladín deje de pegar y pase a curar, escudar y soltar su " +
             "definitiva.")]
    [Label("Aliado en peligro con vida bajo (0 a 1)")]
    [Range(0.05f, 0.9f)] public float AllyDangerHealth = 0.3f;

    [Tooltip("Vida a partir de la cual el Paladín SOSTIENE a alguien: escudos y curaciones " +
             "de objetivo. Más alta que AllyDangerHealth a propósito — el Escudo de fe se " +
             "pone ANTES de que la cosa se ponga fea; la definitiva se guarda para cuando ya " +
             "lo está.")]
    [Label("Sostener aliado con vida bajo (0 a 1)")]
    [Range(0.1f, 1f)] public float AllySupportHealth = 0.5f;

    [Tooltip("Cuántos segundos de combate trabado antes de gastar un buff. Sin esto se los " +
             "gastan caminando, apenas salen de cooldown, y no los tienen cuando importan.")]
    [Label("Buff tras combate de (s)")]
    public float BuffAfterSeconds = 1.5f;

    [Tooltip("La Resurrección (Clérigo de la vida): hasta dónde va a buscar el cuerpo de un aliado muerto " +
             "para revivirlo. Más lejos, lo deja.")]
    [Label("Buscar cuerpos hasta (m)")]
    public float ReviveSearchRadius = 25f;

    [Header("Mecánicas del mapa (12 de noviembre de 2026)")]
    [Tooltip("Hasta dónde va a buscar un orbe de definitiva (si la definitiva no está llena y nadie le pega de cerca).")]
    [Label("Busca orbes hasta (m)")]
    public float OrbSeekRadius = 25f;

    [Tooltip("Con un santuario activo, los bots sin nada más urgente van a la zona. Pelean con un enemigo solo si " +
             "está a esta distancia; más lejos, siguen hacia la zona (el santuario se gana estando dentro).")]
    [Label("Santuario: pelea con lo que esté a (m)")]
    public float ShrineFightRadius = 10f;

    [Tooltip("Sin santuario activo, van por el jefe del evento de mitad de partida si está vivo.")]
    [Label("Van por el jefe del evento")]
    public bool HuntEventBoss = true;

    [Header("Vuelo (el Mago)")]
    [Tooltip("A qué altura sobre el piso vuela un bot (el Vuelo del Mago y la Sobrecarga de la Evocación): " +
             "fuera del alcance cuerpo a cuerpo, a tiro de lo que pega de lejos. Al llegar a donde iba sin " +
             "nadie a quien pegarle, baja.")]
    [Label("Altura de vuelo (m)")]
    public float FlightHeight = 8f;

    [Tooltip("Sin nadie a quien pegarle (o volviendo a curarse), vuela para ir a un lugar que esté más lejos " +
             "que esto. Más cerca, camina.")]
    [Label("Vuela para ir a más de (m)")]
    public float FlyTravelDistance = 20f;

    [Header("Instinto (20 de noviembre de 2026)")]
    [Tooltip("Qué tan bueno es: cuánto tarda en reaccionar, cuánto le erra, cuánto se adelanta a lo que corre y cuántos " +
             "proyectiles ve venir. 0 = al azar al nacer (entre 0.45 y 0.85), así cada bot es distinto.")]
    [Label("Destreza (0 a 1; 0 = al azar al nacer)")]
    [Range(0f, 1f)] public float Skill = 0f;

    [Tooltip("Lo que tarda en empezar a pegarle a un enemigo nuevo: el más hábil, el mínimo; el menos, el máximo. Al que " +
             "le está pegando le reacciona más rápido.")]
    [Label("Reacción: mínimo (s)")]
    public float ReactionMin = 0.18f;

    [Label("Reacción: máximo (s)")]
    public float ReactionMax = 0.5f;

    [Tooltip("Cuánto le erra a un blanco a 10 m el menos hábil (al doble de distancia, el doble). El más hábil, casi nada.")]
    [Label("Error de puntería a 10 m (m)")]
    public float AimErrorAt10m = 1.2f;

    [Tooltip("El paso al costado con el que esquiva un proyectil que le va a pegar.")]
    [Label("Esquiva: paso al costado (m)")]
    public float DodgeStep = 2.6f;

    [Tooltip("Al salir de una zona de daño, cuánto más allá del borde se para.")]
    [Label("Zonas: sale con margen de (m)")]
    public float DangerMargin = 1.2f;

    [Tooltip("Hasta cuánto rodea a su objetivo para que un escudo enemigo no lo tape (de a 30°).")]
    [Label("Escudos: rodea hasta (°)")]
    public float FlankMaxDegrees = 135f;

    [Tooltip("El Bárbaro y el soporte, que aguantan más, se van a curar recién con la vida por debajo de esto (el Pícaro " +
             "y el que pega de lejos usan la de arriba). Vuelven con la misma de arriba.")]
    [Label("Bárbaro y soporte: se retiran con vida bajo (0 a 1)")]
    [Range(0.05f, 0.9f)] public float FrontlineRetreatBelowHealth = 0.2f;

    [Tooltip("Superado en número (dos enemigos más que los suyos alrededor) y con la vida por debajo de esto, también se " +
             "repliega.")]
    [Label("Superado en número: se repliega con vida bajo (0 a 1)")]
    [Range(0.05f, 1f)] public float OutnumberedRetreatHealth = 0.5f;

    [Tooltip("Hasta dónde va a buscar un botiquín listo al retirarse (si le queda más a mano que la base).")]
    [Label("Busca botiquín hasta (m)")]
    public float HealthPackSeekRadius = 20f;

    // --- referencias, resueltas en el servidor ---
    private PlayerController              _pc;
    private AbilitySystemComponent        _asc;
    private NetworkAbilitySystemComponent _netASC;
    private NavMeshAgent                  _agent;
    private Animator                      _anim;

    private int   _teamId;
    private float _thinkTimer;
    private float _nextPrimaryAt;
    private float _nextAbilityAt;
    private float _nextSummonAt;
    private bool  _subclassChosen;

    private AbilitySystemComponent _target;
    private bool _retreating;   // el Pícaro está volviendo a curarse
    private bool _chasing;      // persiguiendo al portador de la carga: nada de rodear
    private bool _delivering;   // llevo la carga: correr al punto de entrega
    private IHoldAbility _held;           // habilidad de MANTENER en curso (el escudo)
    private float _heldUntil;
    private Vector3 _destination;

    private float _engagedSince = -99f;   // desde cuándo estoy trabado en combate
    private bool  _targetFleeing;         // el objetivo se está alejando
    private bool  _targetApproaching;     // el objetivo se me está acercando (para la trampa)
    private float _lastTargetDistance;
    private AbilitySystemComponent _markTarget;   // a quién va la marca (Aturdir, Enemigo jurado)
    private AbilitySystemComponent _trapThreat;   // de quién huyo al dejar una trampa (sin _target)
    private readonly List<AbilitySystemComponent> _allyScratch = new List<AbilitySystemComponent>();
    private float _lastHealth = -1f;      // para saber si me están curando
    private int   _lostChecks;            // decisiones seguidas fuera de lugar

    // Cuántas decisiones seguidas hay que estar perdido antes del rescate. A cuatro por
    // segundo, seis son un segundo y medio: suficiente para descartar un tropiezo.
    private const int LostChecksBeforeRescue = 6;

    private float _strafeSign = 1f;   // hacia qué lado rodea este bot
    private float _strafeFlipAt;      // cuándo cambia de sentido

    private bool      _dashing;      // corriendo un dash: el agente no manda mientras dure
    private Coroutine _dashRoutine;
    private Coroutine _leapRoutine;
    private Coroutine _flightRoutine;

    private bool    _flying;          // volando (o cayendo después de volar): ver VUELO
    private Vector3 _airVelocity;     // la velocidad del vuelo, para el Animator
    private bool    _diveRequested;   // el Terremoto lanzado volando: cae en picada
    private float   _diveSpeed;

    private float _castingUntil;     // canalizando algo propio (la Bola de fuego, la recarga): no se corta solo
    private float _nextChoiceAt;     // cuándo puede cambiar de opción en su rueda de estados
    private int   _pendingChoice = -1;   // la opción que eligió ShouldUse para la rueda

    // El Maestro de bestias: qué compañero eligió ShouldUse, cuándo cambió por última vez, y cuándo se
    // le hizo invisible un enemigo delante (es lo que pide al Búho).
    private int   _pendingCompanion  = -1;
    private float _companionChosenAt = -99f;
    private float _lostToInvisibleAt = -99f;
    private CompanionManager _companions;

    // Cuánto pesa que el objetivo sea INMUNE: como si estuviera esto más lejos (m). Gana cualquier otro
    // a la vista; si es el único, sigue siendo él (ver UsefulAgainstImmune).
    private const float ImmuneTargetPenalty = 60f;

    // Cuánto se acuerda de un enemigo que se le hizo invisible (s): mientras, el Búho es el que conviene.
    private const float InvisibleMemorySeconds = 8f;


    // Se reusa entre búsquedas para no generar basura: la percepción corre 4 veces por
    // segundo por bot, y con nueve bots eso es mucho array descartado.
    private readonly Collider[] _hits = new Collider[32];

    // =========================================================
    // ARRANQUE
    // =========================================================

    // Hasta que el NetworkGameManager no lo inicialice, este componente no hace nada.
    // No hay OnStartServer porque NO es un NetworkBehaviour — ver la nota de arriba.
    private bool _ready;

    // La llama el NetworkGameManager justo después de spawnear el bot, y es lo único que
    // enciende este cerebro.
    //
    // POR QUÉ NO ES UN NetworkBehaviour: se agrega con AddComponent en runtime, y un
    // NetworkBehaviour agregado así NUNCA queda registrado en el NetworkObject (su lista
    // se arma en el editor). El resultado era que IsServerInitialized tiraba
    // NullReference: reventaba el bucle que spawnea los bots —de tres, entraba uno— y
    // después reventaba en cada frame. Como este script no manda ni recibe RPCs, no
    // necesita ser uno: le alcanza con correr en el servidor, que es quien lo crea.
    public void ServerInitialize(int teamId)
    {
        _teamId = teamId;

        _pc     = GetComponent<PlayerController>();
        _asc    = GetComponent<AbilitySystemComponent>();
        _netASC = GetComponent<NetworkAbilitySystemComponent>();
        _anim   = _pc != null ? _pc.characterAnimator : GetComponentInChildren<Animator>();

        if (_teamId == 0 && _netASC != null) _teamId = _netASC.NetTeamID;

        SetupAgent();
        InitInstinct();

        // Arrancan escalonados: si los nueve piensan en el mismo frame se nota el tirón.
        _thinkTimer = Random.Range(0f, ThinkInterval);

        // Y las habilidades también, con más margen. Sin esto el equipo entero saltaba EN
        // EL MISMO INSTANTE al abrirse las puertas: todos tenían todo listo y la misma
        // regla los disparaba a la vez. Un poco de desfasaje ya los hace ver distintos.
        _nextAbilityAt = Time.time + Random.Range(0f, 2f);
        _nextSummonAt  = Time.time + Random.Range(0f, SummonInterval * 0.5f);
        _ready      = true;
    }

    // El NavMeshAgent se agrega EN RUNTIME y solo en el servidor: el prefab del jugador
    // no lo trae (un jugador de verdad se mueve con el CharacterController y su input).
    // Los clientes no lo necesitan — les llega el transform ya movido.
    private void SetupAgent()
    {
        _agent = GetComponent<NavMeshAgent>();
        if (_agent == null) _agent = gameObject.AddComponent<NavMeshAgent>();

        _agent.radius           = 0.3f;   // los personajes miden ~0.6 de ancho
        _agent.height           = 2f;
        _agent.speed            = ResolveMoveSpeed();
        _agent.angularSpeed     = 720f;
        _agent.acceleration     = 30f;
        _agent.stoppingDistance = 0.4f;
        _agent.autoBraking      = false;  // frenar de golpe en cada punto los hace ver dubitativos

        // La rotación la maneja este script: un bot tiene que MIRAR a su objetivo aunque
        // se esté moviendo de costado, igual que un jugador mira hacia donde apunta.
        _agent.updateRotation = false;

        WarpToNavMesh();
    }

    private float ResolveMoveSpeed()
    {
        float s = _asc != null ? _asc.GetAttributeValue(EAttributeType.MovSpeed) : 0f;
        return s > 0f ? s : 5f;
    }

    // Pone el agente sobre el NavMesh más cercano. Hace falta al spawnear y después de
    // cada respawn: las bases están fuera del muro y el punto exacto puede caer en un
    // borde sin navegación.
    private void WarpToNavMesh()
    {
        if (_agent == null) return;

        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 6f, NavMesh.AllAreas))
            _agent.Warp(hit.position);
    }

    // La llama PlayerController.ServerTeleportBot: sin esto el agente cree que sigue en
    // el punto de muerte y vuelve caminando desde allá.
    public void OnServerTeleported() => WarpToNavMesh();

    // Corta el salto o el dash que estuviera corriendo y deja todo en un estado sano.
    //
    // StopCoroutine mata la rutina EN SU YIELD, o sea a mitad de camino: sin esto, una
    // segunda habilidad de movimiento la interrumpía con el agente apagado y las banderas
    // puestas, y nadie las volvía a limpiar.
    private void CancelMovementRoutines()
    {
        if (_dashRoutine != null) { StopCoroutine(_dashRoutine); _dashRoutine = null; }
        if (_leapRoutine != null) { StopCoroutine(_leapRoutine); _leapRoutine = null; }
        if (_flightRoutine != null) { StopCoroutine(_flightRoutine); _flightRoutine = null; }

        _dashing = false;
        _flying  = false;
        _airVelocity = Vector3.zero;
        _diveRequested = false;
        RestoreAgent();
    }

    // Deja al agente de vuelta en condiciones de mover al bot. Lo llaman el salto y el
    // dash al terminar, y el propio Update como red de seguridad: cualquier camino que
    // apague el agente y no lo encienda deja al bot congelado, y ese fue exactamente el
    // bug de "de vez en cuando se quedan parados sin hacer nada".
    private void RestoreAgent()
    {
        if (_agent == null) return;

        if (!_agent.enabled) _agent.enabled = true;
        if (_agent.isOnNavMesh) _agent.isStopped = false;
    }

    // Mueve al bot RESPETANDO las paredes.
    //
    // Mueve al bot RESPETANDO las paredes, con un barrido PROPIO.
    //
    // La primera versión usaba CharacterController.Move, que es lo que frena a un jugador.
    // No alcanzó: un bot terminó a 64 m del centro y 12 m bajo el piso en pleno salto. El
    // CharacterController tiene estado que otras partes del juego tocan —excludeLayers lo
    // manipula el dash, y TeleportTo lo apaga y lo prende—, así que no es algo en lo que
    // este script pueda confiar.
    //
    // Un SphereCast no depende de nada de eso: barre el camino que se va a recorrer y, si
    // hay algo, deja al bot justo antes. Es lo mismo que hace el CharacterController por
    // dentro, pero acá el estado lo controlamos nosotros.
    private void MoveWithCollision(Vector3 delta)
    {
        float distance = delta.magnitude;
        if (distance < 0.0001f) return;

        Vector3 dir    = delta / distance;
        Vector3 origin = transform.position + Vector3.up * CollisionProbeHeight;

        // SphereCastAll y no SphereCast: hay que poder SALTEAR lo que no me frena a mí —la
        // pared de mi propia sala segura— y quedarme con lo primero que sí. Con un solo
        // hit, toparse con la pared de casa habría dejado pasar la pared de atrás.
        using var hits = PhysicsQuery.SphereCastAll(origin, CollisionProbeRadius, dir,
                                                  distance + 0.1f, CollisionMask,
                                                  QueryTriggerInteraction.Ignore);

        float nearest = float.MaxValue;
        bool  blocked = false;

        foreach (RaycastHit hit in hits)
        {
            MercSafeRoomBarrier barrier = hit.collider.GetComponentInParent<MercSafeRoomBarrier>();
            if (barrier != null && barrier.TeamID == _teamId) continue;   // la de casa no frena

            if (hit.distance >= nearest) continue;
            nearest = hit.distance;
            blocked = true;
        }

        if (blocked)
        {
            transform.position += dir * Mathf.Max(0f, nearest - 0.1f);
            return;
        }

        transform.position += delta;
    }

    // Red de seguridad después de un salto o un dash: si el bot quedó fuera de lo
    // navegable —empujado por un impulso raro, o colado por un hueco— se lo devuelve a su
    // base. Uno perdido en el vacío no vuelve solo: se queda cayendo para siempre.
    // ¿Sigue en un lugar donde el juego quiere que esté?
    //
    // El invariante bueno es el NAVMESH: todo lo que el nivel considera transitable —la
    // arena, las rampas, los pasillos y las bases, que están afuera del muro— está
    // horneado. Estar lejos de él significa estar arriba de una pared, en el aire, o
    // afuera del mundo.
    //
    // Se suma un tope de altura porque el techo invisible tiene tres huecos hacia las
    // bases (MercArenaBounds.OpenTowardBases): por ahí se puede salir sin tocar nada.
    private bool IsWhereItShouldBe()
    {
        if (!NavMesh.SamplePosition(transform.position, out _, 3f, NavMesh.AllAreas))
            return false;

        MercArenaBounds bounds = ResolveBounds();
        if (bounds == null) return true;

        return transform.position.y <= bounds.CeilingHeight + 3f;
    }

    private MercArenaBounds _bounds;
    private bool            _boundsChecked;

    private MercArenaBounds ResolveBounds()
    {
        if (_boundsChecked) return _bounds;

        _boundsChecked = true;
        _bounds = FindFirstObjectByType<MercArenaBounds>();
        return _bounds;
    }

    private void RecoverIfLost()
    {
        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 6f, NavMesh.AllAreas))
        {
            if (_agent != null && _agent.enabled) _agent.Warp(hit.position);
            return;
        }

        MercenariesGameMode gm = MercenariesGameMode.Instance;
        Transform spawn = gm != null ? gm.GetTeamSpawnPoint(_teamId) : null;

        // El detalle importa: sin saber DÓNDE y HACIENDO QUÉ se escapan, taparlo es
        // adivinar. Si esto aparece siempre con "saltando", el agujero está en el arco.
        Debug.LogWarning($"[Bots] {name} quedó fuera del mapa en {transform.position} " +
                         $"(saltando o dasheando: {_dashing}). Lo devuelvo a su base.");
        if (_pc != null) _pc.ServerTeleportBot(spawn != null ? spawn.position : Vector3.zero,
                                               transform.forward);
    }

    // Aterrizando cerca de un lugar con techo (la sala segura): a cuántos metros de él baja.
    private const float RoofedLandingDistance = 10f;

    // ¿Lo ve? Sin pared en medio, de pecho a pecho. Lo que tapa: todo menos personajes,
    // paredes invisibles y UI (lo mismo que frena los rayos del Mago).
    private static readonly int SightMask = ~((1 << 7) | (1 << 2) | (1 << 5));

    // Qué ES una habilidad, para poder decidir CUÁNDO usarla.
    //
    // No hace falta marcar nada a mano en los assets: el TIPO ya lo dice. Las genéricas del
    // proyecto están partidas justo por comportamiento —GA_SelfBuff, GA_ProjectileShoot,
    // GA_InstantAoE, GA_Dash— así que alcanza con mirar de cuál hereda.
    private enum EAbilityKind { Melee, Ranged, Aoe, Buff, Movement }

    // Los slots que se revisan en combate, en el orden en que se proponen. El básico va
    // aparte (ver Fight): ese se usa SIEMPRE que se pueda.
    private static readonly EAbilityInput[] CombatSlots =
    {
        EAbilityInput.Action3,          // la definitiva primero: si corresponde, es la que decide
        EAbilityInput.Movement,
        EAbilityInput.SecondaryAttack,
        EAbilityInput.Action1,
        EAbilityInput.Action2,
    };

    // Reusada por ShouldUseBuff para no pedir memoria en cada decisión.
    private readonly List<GameplayEffect> _selfBuffEffects = new List<GameplayEffect>();

    // =========================================================
    // REVIVIR, TRAMPAS Y MARCAS (28 de octubre de 2026, revisión de los bots con las clases nuevas)
    // =========================================================

    // Un revivir listo (la Resurrección del Clérigo de la vida o cualquier GA_Revive, en cualquier botón;
    // desde el 19 de noviembre de 2026) y un aliado muerto a buscar: el cuerpo más cercano dentro de
    // ReviveSearchRadius. Mientras haya uno, Think lo lleva hasta ahí.
    private static readonly EAbilityInput[] s_reviveSlots =
        { EAbilityInput.Action3, EAbilityInput.Action1, EAbilityInput.Action2, EAbilityInput.Movement };

    // =========================================================
    // ROL
    // =========================================================

    // El rol sale de CUÁL DE LAS CLASES BASE desciende la clase que tiene puesta, usando
    // el orden de MainBaseClasses del prefab del jugador: 0 Bárbaro, 1 Pícaro, 2 Paladín.
    //
    // Se deduce en vez de guardarse en el asset de la clase para no obligar a tocar los
    // doce ScriptableObjects (tres base + nueve subclases) — y porque así una subclase
    // nueva hereda el rol de su rama sin que nadie se acuerde de configurarla.
    private EBotRole ResolveRole()
    {
        if (RoleOverride != EBotRole.Auto) return RoleOverride;

        // Por el ROL de la clase (EClassRole), que es lo que dice el diseño: "el rol lo
        // fija la clase". Antes se deducía de la POSICIÓN en MainBaseClasses (0 Bárbaro,
        // 1 Pícaro, 2 Paladín), y una clase nueva al final —el Clérigo— caía en Bruiser.
        switch (ResolveClassRole())
        {
            case EClassRole.Tank:    return EBotRole.Bruiser;
            // Un daño cuyo básico pega de lejos (el Mago, el arco del Explorador) pelea a
            // distancia; si no, como el Pícaro.
            case EClassRole.Damage:  return PrimaryIsRanged() ? EBotRole.Ranged : EBotRole.Assassin;
            case EClassRole.Support: return EBotRole.Support;
        }

        // Sin rol declarado (una clase base vieja sin subclases con rol): el orden de siempre.
        switch (ResolveBaseClassIndex())
        {
            case 0:  return EBotRole.Bruiser;
            case 1:  return EBotRole.Assassin;
            case 2:  return EBotRole.Support;
            default: return EBotRole.Bruiser;
        }
    }

    // El rol de la clase actual; si es una clase base sin rol propio (el Bárbaro, el
    // Pícaro y el Paladín solo lo tienen en sus subclases), el de su primera subclase.
    private EClassRole ResolveClassRole()
    {
        CharacterClassDefinition cls = _pc != null ? _pc.CurrentClassDef : null;
        if (cls == null) return EClassRole.None;
        if (cls.Role != EClassRole.None) return cls.Role;

        if (cls.AvailableSubclasses != null)
            foreach (CharacterClassDefinition sub in cls.AvailableSubclasses)
                if (sub != null && sub.Role != EClassRole.None) return sub.Role;

        // Una clase sin subclases todavía (el Mago): el rol con el que se lista en los menús.
        return cls.MenuRole;
    }

    // ¿El ataque básico pega de lejos? El de un switch se juzga por su habilidad por defecto
    // (los misiles del Mago); un cargado, por lo que suelta (la flecha del Explorador).
    private bool PrimaryIsRanged()
    {
        GameplayAbility primary = _pc != null ? _pc.PrimaryAttackAbility : null;
        if (primary is GA_TagSwitch sw && sw.DefaultAbility != null) primary = sw.DefaultAbility;
        return primary != null && Classify(primary) == EAbilityKind.Ranged;
    }

    private int ResolveBaseClassIndex()
    {
        if (_pc == null || _pc.MainBaseClasses == null || _pc.CurrentClassDef == null) return -1;

        for (int i = 0; i < _pc.MainBaseClasses.Length; i++)
        {
            if (DescendsFrom(_pc.MainBaseClasses[i], _pc.CurrentClassDef)) return i;
        }
        return -1;
    }

    // ¿"target" es "root" o alguna de sus subclases (a cualquier profundidad)?
    private static bool DescendsFrom(CharacterClassDefinition root, CharacterClassDefinition target)
    {
        if (root == null || target == null) return false;
        if (root == target) return true;

        if (root.AvailableSubclasses == null) return false;
        foreach (CharacterClassDefinition sub in root.AvailableSubclasses)
            if (DescendsFrom(sub, target)) return true;

        return false;
    }
}
