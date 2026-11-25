using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Connection;

// ============================================================
// NetworkAbilitySystemComponent
//
// Capa de red de un personaje: vive en el mismo GameObject que
// AbilitySystemComponent (que no sabe nada de FishNet) y traduce sus
// cambios (atributos, tags, efectos activos) a SyncVars/RPCs para que
// todos los clientes los vean. También recibe las peticiones de
// activar habilidades desde PlayerController y actúa de puente para
// todo lo que necesita autoridad de servidor + ejecución en el
// cliente dueño (salto, VFX, secuencias visuales).
//
// PREFAB DEL JUGADOR: necesita AMBOS componentes en el mismo
// GameObject — AbilitySystemComponent (lógica de juego) y este script
// (sincronización) — más NetworkObject y NetworkTransform de FishNet.
// ============================================================
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 25 de noviembre de 2026). En este archivo: los campos, las
// SyncVars, el ciclo de vida y las utilidades; las RPC y lo demás, en Network/NetworkAbilitySystemComponent/:
//   · Sync      lo que se espeja: atributos, tags, cooldowns, cargas, efectos y su VFX, la cuerda, equipos
//   · Abilities activar habilidades por red, mantener, canalizar, combos y su animación, el fin
//   · Feedback  lo que se ve del combate: destellos, sensación, números, barra de canalizar
//   · Movement  mover desde el servidor: teletransporte, empuje, vuelo, embestida, dash, salto
//   · Vfx       los VFX de habilidad replicados a todos
//   · Life      muerte, revivir y experiencia
[RequireComponent(typeof(AbilitySystemComponent))]
public partial class NetworkAbilitySystemComponent : NetworkBehaviour
{
    // =========================================================
    // CAMPOS Y SYNCVARS
    // =========================================================

    [Header("Recompensas")]
    [Tooltip("EXP que gana el jugador que consigue una baja. Provisional para pruebas; " +
             "es lo que reemplaza al cheat de subir de nivel a mano una vez que haya varios jugadores.")]
    [Label("Experiencia por baja (exp)")]
    public float ExperiencePerKill = 50f;

    // Referencia al ASC local (mismo GameObject).
    private AbilitySystemComponent _asc;

    // Instancia de GA_LeapAttack cuyo impacto falta resolver — la guarda el
    // servidor entre ServerStartLeap() y ServerResolveLeapImpact() (que
    // llama el dueño al aterrizar). Ver esos métodos en la sección SALTO.
    private GA_LeapAttack _pendingLeapImpact;

    // Atributos numéricos sincronizados. El servidor escribe, todos los
    // clientes leen (ver HandleAttributeChanged / OnNet*Changed).
    private readonly SyncVar<float> _netHealth    = new SyncVar<float>();
    private readonly SyncVar<float> _netMaxHealth = new SyncVar<float>();
    private readonly SyncVar<float> _netMana      = new SyncVar<float>();
    private readonly SyncVar<float> _netMaxMana   = new SyncVar<float>();
    private readonly SyncVar<float> _netEnergy    = new SyncVar<float>();
    private readonly SyncVar<float> _netShield    = new SyncVar<float>();
    private readonly SyncVar<int>   _netTeamID    = new SyncVar<int>();
    private readonly SyncVar<float> _netLevel     = new SyncVar<float>();
    private readonly SyncVar<float> _netExp       = new SyncVar<float>();

    // Tags de estado activos, sincronizados para UI remota y efectos visuales.
    public readonly SyncHashSet<EGameplayTag> NetTags = new SyncHashSet<EGameplayTag>();

    // Feedback del Crítico mejorado del Asesino: el estado "listo" (pasó la
    // reutilización) vive en el servidor, así que lo sincronizamos para que el HUD
    // del dueño (host o cliente remoto) lo muestre. SyncVar solo manda al cambiar.
    private readonly SyncVar<bool> _netFirstStrikeReady = new SyncVar<bool>();
    public bool NetFirstStrikeReady => _netFirstStrikeReady.Value;

    // =========================================================
    // COOLDOWNS EN RED
    //
    // ASC.ActiveEffects (de donde sale GetCooldownStatus) SOLO existe en la
    // copia del servidor — cada GameplayEffect es un ScriptableObject clonado
    // por proceso, así que ni siquiera tiene sentido "sincronizar la lista".
    //
    // En vez de eso mandamos, por slot de habilidad: el TICK en que empezó
    // el cooldown "vigente" y cuánto dura desde ese tick. El cliente calcula
    // el tiempo restante localmente con TimeManager.Tick (que aproxima el
    // tick del servidor), sin necesitar un paquete de red por frame.
    //
    // Se re-sincroniza cada CooldownSyncInterval segundos, no solo al activar
    // la habilidad — así, si algo reduce el cooldown a mitad de camino (carga
    // de ultimate por ChargeUltimate/ReduceCooldownByTag), el próximo barrido
    // lo corrige sin tener que enganchar cada lugar que podría modificarlo.
    // =========================================================

    public readonly SyncDictionary<EAbilityInput, uint>  NetCooldownStartTick = new SyncDictionary<EAbilityInput, uint>();
    public readonly SyncDictionary<EAbilityInput, float> NetCooldownDuration  = new SyncDictionary<EAbilityInput, float>();

    // Cargas disponibles por slot, para las habilidades que usan sistema de
    // cargas (ej: el dash). El estado real vive en la habilidad (server-side);
    // esta la reporta con ServerReportCharges cada vez que cambia, y la UI la
    // lee con TryGetNetCharges. Un slot sin entrada = habilidad sin cargas.
    public readonly SyncDictionary<EAbilityInput, int> NetCharges = new SyncDictionary<EAbilityInput, int>();

    // Slots que se revisan en cada barrido de cooldowns.
    private static readonly EAbilityInput[] _cooldownSlots =
    {
        EAbilityInput.PrimaryAttack, EAbilityInput.SecondaryAttack,
        EAbilityInput.Action1, EAbilityInput.Action2, EAbilityInput.Action3,
        EAbilityInput.Movement
    };

    // Cada cuánto se re-sincronizan los cooldowns (ver Update).
    private const float CooldownSyncInterval = 0.25f;
    private float _cooldownSyncTimer;

    // =========================================================
    // EFECTOS ACTIVOS EN RED (buffs/debuffs con duración, para la UI)
    //
    // Mismo problema que con los cooldowns: ActiveEffects del ASC solo
    // existe de verdad en la copia del servidor. Acá no podemos mandar la
    // referencia al ScriptableObject GameplayEffect por SyncVar (FishNet no
    // sabe serializar assets arbitrarios), así que sincronizamos el ÍNDICE
    // del efecto dentro de GameplayEffectRegistry — el cliente lo resuelve
    // de vuelta al mismo asset, que ya tiene cargado localmente porque es
    // el mismo build. Junto al índice mandamos el TICK en que empezó y
    // cuánto dura, igual que con los cooldowns, para no necesitar un
    // paquete de red por frame.
    //
    // A diferencia de los cooldowns (que se barren cada CooldownSyncInterval
    // segundos), acá actualizamos on-demand desde los hooks del ASC
    // (OnActiveEffectAddedCallback/Removed) porque los buffs/debuffs pueden
    // ser mucho más cortos y un barrido periódico se los podría perder.
    // =========================================================

    public readonly SyncDictionary<int, uint>  NetActiveEffectStartTick = new SyncDictionary<int, uint>();
    public readonly SyncDictionary<int, float> NetActiveEffectDuration  = new SyncDictionary<int, float>();

    // Cuántas instancias (stacks) de ese efecto están activas. Un efecto con
    // StackingPolicy = Stack puede tener varias a la vez (ej: las Heridas del
    // Pícaro), y todas comparten esta única entrada porque el diccionario está
    // indexado por EFECTO, no por instancia — ver SyncActiveEffect.
    public readonly SyncDictionary<int, int> NetActiveEffectStacks = new SyncDictionary<int, int>();

    // El otro extremo de cada VÍNCULO con cuerda que tiene este personaje (GameplayEffect "Cuerda"; 20 de noviembre de
    // 2026): índice del efecto en el registro → el objeto de red de quien se lo puso. Con eso cada pantalla dibuja la cuerda
    // (LinkTether). Va aparte de los de arriba porque un vínculo puede ser "Hidden" (sin ícono) y esos no se sincronizan.
    public readonly SyncDictionary<int, NetworkObject> NetLinkPartner = new SyncDictionary<int, NetworkObject>();

    // =========================================================
    // STATS DERIVADOS EN RED (AtkSpeed, MovSpeed, Atq, Def, etc.)
    //
    // Los atributos "pool" (Health, Mana...) y los Max tienen su SyncVar
    // dedicado arriba. Pero los demás stats (los que RecalculateAllAttributes
    // del ASC computa como Base*Modificadores) los cambian los buffs vía el
    // sistema de modificadores, y no tenían dónde sincronizarse. Este
    // diccionario genérico replica CUALQUIER stat que no tenga SyncVar
    // propio, así un buff nuevo sobre cualquier atributo llega a los clientes
    // sin tener que agregar un SyncVar por cada uno. Sin esto, el jugador 2
    // nunca recibía el buff de velocidad de ataque/movimiento de su Rage.
    // =========================================================

    public readonly SyncDictionary<EAttributeType, float> NetStats = new SyncDictionary<EAttributeType, float>();

    // =========================================================
    // REVELADO A UN EQUIPO (ver a través de paredes)
    //
    // Equipo → tick del servidor hasta el que ese equipo ve a este personaje a través de
    // las paredes (la silueta de TeamReveal). Lo usa la Marca del cazador del Explorador:
    // el que lo marcó y sus aliados lo ven; el resto, no. No es un tag porque un tag no
    // dice QUIÉN lo puso, y en un 3c3c3 el tercer equipo no tiene por qué verlo.
    //
    // Las entradas vencidas no se borran al instante: cada pantalla compara contra su
    // tick, y la próxima marca pisa la entrada de ese equipo.
    // =========================================================

    public readonly SyncDictionary<int, uint> NetRevealedUntilTick = new SyncDictionary<int, uint>();

    // Revela a este personaje al equipo 'team' por 'seconds' segundos (o lo que le quede,
    // si ya estaba revelado por más tiempo).
    [Server]
    public void ServerRevealToTeam(int team, float seconds)
    {
        if (team <= 0 || seconds <= 0f) return;
        uint until = TimeManager.Tick + TimeManager.TimeToTicks(seconds, FishNet.Managing.Timing.TickRounding.RoundUp);
        if (NetRevealedUntilTick.TryGetValue(team, out uint current) && current >= until) return;
        NetRevealedUntilTick[team] = until;
    }

    // ¿Lo ve ahora el equipo 'team' a través de las paredes?
    public bool IsRevealedToTeam(int team)
        => team > 0 && NetRevealedUntilTick.TryGetValue(team, out uint until) && TimeManager != null &&
           TimeManager.Tick < until;

    // =========================================================
    // PROPIEDADES PÚBLICAS DE LECTURA (para la UI y otros scripts)
    // =========================================================

    public float NetHealth    => _netHealth.Value;
    public float NetMaxHealth => _netMaxHealth.Value;
    public float NetMana      => _netMana.Value;
    public float NetMaxMana   => _netMaxMana.Value;
    public float NetEnergy    => _netEnergy.Value;
    public float NetShield    => _netShield.Value;
    public int   NetTeamID    => _netTeamID.Value;
    public float NetLevel     => _netLevel.Value;
    public float NetExp       => _netExp.Value;

    // =========================================================
    // CICLO DE VIDA
    // =========================================================

    // Obtiene la referencia al ASC local y se suscribe a todos sus hooks
    // (atributos, tags, muerte/revivir, efectos activos).
    private void Awake()
    {
        _asc = GetComponent<AbilitySystemComponent>();

        if (_asc == null)
        {
            Debug.LogError("[NetworkASC] No se encontró AbilitySystemComponent en el mismo GameObject.");
            return;
        }

        _asc.OnAttributeChangedCallback += HandleAttributeChanged;
        _asc.OnTagAddedCallback         += HandleTagAdded;
        _asc.OnTagRemovedCallback       += HandleTagRemoved;

        _asc.OnDeath  += HandleDeath;
        _asc.OnRevive += HandleRevive;
        _asc.OnMaxLevelReached += HandleMaxLevelReached;

        _asc.OnActiveEffectAddedCallback   += HandleActiveEffectAdded;
        _asc.OnActiveEffectRemovedCallback += HandleActiveEffectRemoved;
    }

    // Desuscribe todos los hooks/callbacks para no dejar referencias
    // colgando cuando este componente se destruye.
    private void OnDestroy()
    {
        if (_asc == null) return;

        _asc.OnAttributeChangedCallback -= HandleAttributeChanged;
        _asc.OnTagAddedCallback         -= HandleTagAdded;
        _asc.OnTagRemovedCallback       -= HandleTagRemoved;
        _asc.OnDeath                    -= HandleDeath;
        _asc.OnRevive                   -= HandleRevive;
        _asc.OnMaxLevelReached          -= HandleMaxLevelReached;
        _asc.OnActiveEffectAddedCallback   -= HandleActiveEffectAdded;
        _asc.OnActiveEffectRemovedCallback -= HandleActiveEffectRemoved;

        _netHealth.OnChange    -= OnNetHealthChanged;
        _netMaxHealth.OnChange -= OnNetMaxHealthChanged;
        _netMana.OnChange      -= OnNetManaChanged;
        _netMaxMana.OnChange   -= OnNetMaxManaChanged;
        _netEnergy.OnChange    -= OnNetEnergyChanged;
        _netShield.OnChange    -= OnNetShieldChanged;
        _netTeamID.OnChange    -= OnNetTeamIDChanged;
        _netLevel.OnChange     -= OnNetLevelChanged;
        _netExp.OnChange       -= OnNetExpChanged;
        NetTags.OnChange       -= OnNetTagsChanged;
        NetStats.OnChange      -= OnNetStatChanged;
        NetActiveEffectDuration.OnChange -= OnNetActiveEffectVfxChanged;
        NetLinkPartner.OnChange -= OnNetLinkPartnerChanged;

        DespawnAllEffectVfx();
        DespawnAllTethers();
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        // ============================================================
        // CORRECCIÓN BUG TeamID = 0:
        //
        // Antes aquí se hacía "_asc.TeamID = base.OwnerId + 1", pero
        // OwnerId todavía no está poblado de forma confiable en este
        // punto del ciclo de vida de FishNet (justo al spawnear),
        // por lo que devuelve -1 y el resultado siempre era TeamID = 0.
        //
        // El NetworkGameManager YA llama AssignTeam(uniqueTeamID)
        // inmediatamente después de hacer Spawn() del jugador, con
        // el número de jugador correcto (1, 2, 3, ...). Esa es la
        // única fuente de verdad para el TeamID — no la dupliques aquí.
        //
        // Dejamos esto vacío (más allá del base.OnStartServer()) para
        // no pisar el valor que el GameManager va a asignar a continuación.
        // ============================================================
    }

    // Suscribe los callbacks de SyncVar para recibir los cambios que
    // manda el servidor.
    public override void OnStartClient()
    {
        base.OnStartClient();

        // Punto de partida para la reacción de golpe: la vida con la que llegó el objeto
        // (al que entra tarde no le llega un OnChange por el valor inicial).
        _healthSeen     = true;
        _lastSeenHealth = _netHealth.Value;

        _netHealth.OnChange    += OnNetHealthChanged;
        _netMaxHealth.OnChange += OnNetMaxHealthChanged;
        _netMana.OnChange      += OnNetManaChanged;
        _netMaxMana.OnChange   += OnNetMaxManaChanged;
        _netEnergy.OnChange    += OnNetEnergyChanged;
        _netShield.OnChange    += OnNetShieldChanged;
        _netTeamID.OnChange    += OnNetTeamIDChanged;
        _netLevel.OnChange     += OnNetLevelChanged;
        _netExp.OnChange       += OnNetExpChanged;
        NetTags.OnChange       += OnNetTagsChanged;
        NetStats.OnChange      += OnNetStatChanged;
        NetActiveEffectDuration.OnChange += OnNetActiveEffectVfxChanged;
        NetLinkPartner.OnChange += OnNetLinkPartnerChanged;
        foreach (var kvp in NetLinkPartner) SpawnTether(kvp.Key, kvp.Value);   // el que entra tarde ve las que ya estaban

        // OnChange solo dispara para cambios FUTUROS — aplicamos de una los
        // stats que ya venían sincronizados al momento de conectarnos (ej: un
        // jugador que ya tenía un buff activo cuando entramos a la partida).
        if (!IsServerInitialized && _asc != null)
            foreach (var kvp in NetStats)
                _asc.SetCurrentAttributeValue(kvp.Key, kvp.Value);

        // Lo mismo para los VFX: si entramos a la partida con un debuff ya puesto
        // encima de este personaje, su VFX tiene que aparecer igual.
        foreach (int index in NetActiveEffectDuration.Keys) SpawnEffectVfx(index);

        // La silueta a través de paredes (Marca del cazador) la dibuja cada pantalla: se
        // agrega sola a todo personaje en red, así no hay que tocar ningún prefab.
        if (GetComponent<TeamReveal>() == null) gameObject.AddComponent<TeamReveal>();
    }

    // Solo en el servidor: re-sincroniza los cooldowns cada
    // CooldownSyncInterval segundos (ver sección COOLDOWNS).
    private void Update()
    {
        if (!IsServerInitialized || _asc == null) return;

        // Feedback del Crítico mejorado: barato de chequear cada frame, y el SyncVar
        // solo manda cuando el bool cambia (no hay tráfico si no se mueve).
        _netFirstStrikeReady.Value = _asc.IsFirstStrikeReady;

        FlushPendingHealNumber();

        _cooldownSyncTimer += Time.deltaTime;
        if (_cooldownSyncTimer < CooldownSyncInterval) return;
        _cooldownSyncTimer = 0f;

        foreach (EAbilityInput slot in _cooldownSlots)
            SyncCooldownForSlot(slot);
    }

    private bool  _healthSeen;
    private float _lastSeenHealth;
    private float _lastHurtAt = -10f;

    // Las cuerdas vivas en esta pantalla (índice del efecto → la cuerda).
    private readonly Dictionary<int, LinkTether> _tethers = new Dictionary<int, LinkTether>();

    // =========================================================
    // VFX DE EFECTOS ACTIVOS (GameplayEffect.TargetVFX)
    //
    // Un debuff importante tiene que VERSE encima de quien lo sufre, y eso la
    // VisualsSequence de la habilidad no lo puede resolver: esa secuencia solo conoce
    // al que LANZA, y arranca en CommitAbility() —antes de que la habilidad sepa
    // siquiera a quién le pegó— (ver GameplayAbility.PlayVisualsSequence).
    //
    // Se cuelga de NetActiveEffectDuration, que YA replica a todos los peers qué
    // efectos están activos en este personaje, indexados por GameplayEffectRegistry.
    // O sea que el VFX aparece igual en el dueño, en los observadores y en el host sin
    // un solo RPC nuevo: ese diccionario ya viajaba para la barra de buffs.
    //
    // Corre solo del lado CLIENTE (asServer = false): en un servidor dedicado no hay
    // nada que dibujar, y en host es la copia cliente del callback la que instancia.
    // =========================================================

    // VFX vivos ahora mismo, uno por efecto (índice del registro → instancia).
    private readonly Dictionary<int, GameObject> _activeEffectVfx = new Dictionary<int, GameObject>();

    // Deja de emitir y borra el VFX cuando se apagó la última partícula, en vez de
    // cortarlo de golpe. Un VFX sin partículas (un modelo, una malla) se va al instante.
    private const float MaxEffectVfxFade = 3f;

    // Nombre elegido por el jugador en el menú de entrada. Se sincroniza a todos
    // para mostrarlo sobre su barra de vida (ver UI_WorldHealthbar). Vive acá y no
    // en el ASC porque es puramente identidad de red, no un stat de juego.
    private readonly SyncVar<string> _netPlayerName = new SyncVar<string>(string.Empty);

    // Qué le pedimos a los observadores respecto de la animación de mantenido.
    public enum EHoldAnimationPhase
    {
        Start,   // levantar → entrar al bucle
        Stop,    // salir del bucle (bajar)
        Impact   // reacción one-shot al recibir un golpe en el escudo, sin salir del mantenido
    }

    // =========================================================
    // NÚMEROS FLOTANTES (daño y curación)
    //
    // El número sale sobre el que RECIBE: rojo el daño, amarillo el crítico, verde
    // la curación. Lo ven TODOS (ObserversRpc), porque lo que dice —a quién le están
    // pegando y cuánto, a quién están curando— sirve tanto al que juega como al que
    // mira. Va por el canal no confiable: es decoración, y si se pierde uno no pasa
    // nada.
    //
    // Las CURACIONES se juntan: el aura del Paladín cura a todo el equipo en cada golpe,
    // el robo de vida cura en cada golpe, y un número por cada una llenaría la pantalla
    // de verde. Se suman durante HealNumberBatchSeconds y sale un solo número.
    // =========================================================

    [Tooltip("Cada cuánto se juntan las curaciones de este personaje en un solo número verde.")]
    [Label("Juntar curaciones cada (s)")]
    public float HealNumberBatchSeconds = 0.35f;

    private float _pendingHeal;
    private float _pendingHealFlushAt;

    // =========================================================
    // LEVANTAR A UN BOT O A UN NPC (el impulso hacia arriba del empuje)
    //
    // Un tiro vertical con la gravedad del jugador, que el servidor mueve. Con NavMeshAgent sube
    // el CUERPO por su baseOffset: el agente sigue en su lugar del NavMesh (no se sale del mapa
    // ni queda arriba de una pared) y el collider sube con él (se le puede pegar en el aire).
    // Sin agente, el transform. Otro golpe en el aire lo vuelve a lanzar desde donde está.
    // =========================================================

    private const float LaunchGravity = 9.8f;   // la de PlayerController.gravity

    private Coroutine _launch;
    private float     _launchHeight;       // cuánto está levantado ahora
    private float     _launchRestOffset;   // el baseOffset del agente en el piso

    // =========================================================
    // EMBESTIDA (GA_RushAttack) — la Patada voladora del Monje. El dueño se mueve (el
    // transform es suyo); el servidor golpea a los que alcanza. Cualquiera de los dos la
    // puede cortar: el dueño al volver a apretar o al chocar (ServerRequestStopRush), el
    // servidor al frenar en un enemigo o si lo aturden (ServerStopRush).
    // =========================================================

    private bool _rushStopRequested;

    // =========================================================
    // UTILIDADES INTERNAS
    // =========================================================

    // Resuelve, a partir de un slot, la instancia de habilidad otorgada a
    // este personaje (buscando en CurrentClass.Abilities qué template
    // corresponde a ese slot, y matcheándolo por nombre contra
    // GrantedAbilities).
    private GameplayAbility FindAbilityBySlot(EAbilityInput slot)
    {
        if (_asc == null || _asc.CurrentClass == null) return null;

        foreach (var assignment in _asc.CurrentClass.Abilities)
        {
            // Ability puede ser null: un slot sin habilidad asignada (ej. el ult del
            // Ilusionista aún sin implementar). Sin este guard, acceder a
            // assignment.Ability.AbilityName tira NullReference desde el sync de
            // cooldowns, que recorre TODOS los slots cada 0.25s.
            if (assignment.InputSlot != slot || assignment.Ability == null) continue;

            foreach (var granted in _asc.GrantedAbilities)
            {
                if (granted != null && granted.AbilityName == assignment.Ability.AbilityName)
                    return granted;
            }
        }
        return null;
    }
}
