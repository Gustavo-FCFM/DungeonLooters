using System;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

// Fases de la partida. Warmup = todos encerrados en su base esperando; Playing =
// partida en curso; Ended = ya hay ganador (o se acabó el tiempo).
public enum EMatchState { Warmup = 0, Playing = 1, Ended = 2 }

// Avisos que el servidor manda a TODAS las pantallas. Viajan como enum + números
// (nunca como texto): el texto lo arma cada cliente en UI_MatchAnnouncer, así se
// puede cambiar la redacción sin tocar la red.
public enum EMatchAnnouncement
{
    MatchStarted,        // arrancó la partida
    ObjectiveSpawned,    // apareció el Objetivo en el centro
    ObjectiveTaken,      // un equipo lo levantó       (Team = quién)
    ObjectiveDropped,    // se cayó al piso            (Team = quién lo tenía)
    ObjectiveDelivered,  // entregado en una base      (Team = quién, Extra = puntos que lleva)
    ObjectiveReturning,  // el próximo Objetivo llega en Extra segundos
    TeamWiped,           // aniquilaron a un equipo    (Team = el equipo caído)
    TeamLevelUp,         // un equipo subió de nivel   (Team, Extra = nivel nuevo)
    MatchEnded,          // fin de partida             (Team = ganador, 0 = empate)
    ShrineActivated,     // se activó un santuario     (Extra = EShrineType)
    ShrineCaptured,      // un equipo lo capturó       (Team = quién, Extra = EShrineType)
    BellRung,            // un equipo tocó una campana (Team = quién, Extra = enemigos revelados)
    OrbsSpawned,         // aparecieron orbes de definitiva (Extra = cuántos)
    BossSpawned,         // apareció el jefe del evento
    BossSlain,           // un equipo mató al jefe del evento (Team = quién, Extra = minutos de bendición)
    BossLeft,            // el jefe del evento se fue sin que lo mataran
}

// ============================================================
// MercenariesGameMode
//
// El cerebro del modo principal "Mercenarios" (3c3c3 PvEvP). Vive en la ESCENA,
// sobre el mismo GameObject que NetworkGameManager (comparte su NetworkObject), y
// TODA su lógica corre en el servidor: acá se decide cuándo aparece el Objetivo,
// quién puntúa, cuánta experiencia gana cada equipo y cuándo se acaba la partida.
// Los clientes solo LEEN el estado sincronizado para dibujarlo (ver UI_MercenariesHUD).
//
// Lo que administra:
//   · FASES: Preparación (todos en su base) → Partida → Fin (+ reinicio automático).
//   · OBJETIVO: aparece en el centro pasado un tiempo, y vuelve a aparecer unos
//     segundos después de cada entrega (ver MercObjective).
//   · PUNTAJE: 2 entregas ganan la partida.
//   · PROGRESIÓN COMPARTIDA: cada equipo tiene UNA sola bolsa de experiencia. Todo
//     lo que gana cualquiera de sus tres jugadores va ahí, y el NIVEL sale de esa
//     bolsa — no del progreso individual. Ver la sección EXPERIENCIA COMPARTIDA.
//   · WIPES: avisa a todas las pantallas cuando un equipo queda entero en el piso.
//
// POR QUÉ LA EXPERIENCIA ES DEL EQUIPO Y NO DEL JUGADOR: el jugador puede cambiar de
// clase en su base cuando quiera, y con progreso individual eso significaba volver a
// nivel 1 (EquipCharacterClass con resetProgress reinicia los atributos). Con la
// bolsa compartida, el nivel es una propiedad del EQUIPO: al cambiar de clase el
// servidor le vuelve a subir el nivel al toque, así que cambiar no cuesta nada.
// ============================================================
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 25 de noviembre de 2026). En este archivo: los campos del
// Inspector, el estado (también el sincronizado), la lectura pública, el ciclo de vida y los avisos; lo demás, en
// GameMode/Mercenaries/MercenariesGameMode/:
//   · Phases     las fases: preparación, partida, fin, vuelta a la sala y quién va primero
//   · Objective  el Objetivo: cuándo aparece, levantarlo, soltarlo, entregarlo
//   · Experience la bolsa de experiencia del equipo, las bajas y el nivel de cada jugador
//   · Players    los jugadores: la lista, no salir de la base en la preparación, vivos y eliminados
//   · Rules      las cuentas: las reglas como funciones estáticas (las prueban las pruebas "Modo_…")
public partial class MercenariesGameMode : NetworkBehaviour
{
    public const int TeamCount = 3;

    // Acceso global (hay uno solo por escena). Lo usan MercObjective, MercTeamBase,
    // los enemigos y la UI.
    public static MercenariesGameMode Instance { get; private set; }

    // Aviso del servidor recibido en ESTE cliente. Estático porque la UI puede existir
    // antes de que el objeto de red esté listo (el HUD vive en la escena).
    public static event Action<EMatchAnnouncement, int, int> OnAnnouncement;

    // Una baja, en el SERVIDOR (quién remató, quién murió), después de repartir la experiencia.
    // La escuchan los eventos de la partida que cuentan bajas (los santuarios).
    public static event Action<AbilitySystemComponent, AbilitySystemComponent> OnServerKill;

    // =========================================================
    // CONFIGURACIÓN
    // =========================================================

    [Header("Ritmo de la partida")]
    [Tooltip("Segundos de preparación antes de que arranque. Los equipos esperan en su base.")]
    [Label("Preparación (s)")]
    public float WarmupSeconds = 30f;

    [Tooltip("Duración máxima de la partida. Si nadie llegó a los puntos necesarios, gana quien vaya arriba.")]
    [Label("Duración de la partida (s)")]
    public float MatchDurationSeconds = 900f; // 15 min

    [Tooltip("Entregas necesarias para ganar.")]
    [Label("Entregas para ganar")]
    public int PointsToWin = 2;

    [Tooltip("Segundos tras el arranque hasta que aparece el PRIMER Objetivo. Si la escena tiene el " +
             "administrador de eventos (MatchEvents) con su evento \"Objetivo\", manda el de ahí.")]
    [Label("Primer Objetivo a los (s)")]
    public float FirstObjectiveDelay = 60f;

    [Tooltip("Segundos entre una entrega y la aparición del siguiente Objetivo.")]
    [Label("Siguiente Objetivo tras (s)")]
    public float ObjectiveRespawnDelay = 30f;

    [Tooltip("Segundos que tarda un jugador en reaparecer en su base tras morir.")]
    [Label("Reaparece en (s)")]
    public float RespawnSeconds = 5f;

    [Tooltip("Al terminar, reiniciar la partida sola (cómodo para demos y pruebas).")]
    [Label("Reiniciar sola")]
    public bool AutoRestart = true;
    [Label("Reinicio tras (s)")]
    public float RestartDelay = 15f;

    [Header("Objetivo")]
    [Tooltip("Prefab del Objetivo (la Bolsa de oro). Necesita NetworkObject + MercObjective.")]
    [Label("Prefab del Objetivo")]
    public GameObject ObjectivePrefab;

    [Tooltip("Dónde aparece el Objetivo. Normalmente el centro del escenario.")]
    [Label("Dónde aparece el Objetivo")]
    public Transform ObjectiveSpawnPoint;

    [Header("Equipos")]
    [Tooltip("Las tres bases. Cada una trae su sala segura, su punto de entrega y sus puntos de aparición.")]
    [Label("Bases")]
    public MercTeamBase[] TeamBases = new MercTeamBase[TeamCount];

    [Tooltip("Color de cada equipo (1, 2, 3). Lo usan el HUD, los avisos y el marcador del Objetivo.")]
    public Color[] TeamColors =
    {
        new Color(0.90f, 0.25f, 0.25f), // 1 · rojo
        new Color(0.30f, 0.85f, 0.35f), // 2 · verde
        new Color(0.30f, 0.60f, 1.00f), // 3 · azul
    };

    [Header("Experiencia compartida (bolsa por equipo)")]
    [Tooltip("Nivel máximo. Tiene que coincidir con el MaxLevel del AbilitySystemComponent del jugador.")]
    [Label("Nivel máximo")]
    public int MaxTeamLevel = 3;

    [Tooltip("Experiencia para pasar de nivel 1 a 2, de 2 a 3, etc. Un valor por salto.")]
    [Label("Experiencia para cada nivel (exp)")]
    public float[] XpPerLevel = { 100f, 150f };

    [Tooltip("Experiencia que la bolsa del equipo gana por cada NPC derrotado.")]
    [Label("Experiencia por NPC (exp)")]
    public float XpPerNpcKill = 15f;

    [Tooltip("Experiencia por derribar a un JUGADOR enemigo. Vale bastante más que un NPC a propósito.")]
    [Label("Experiencia por jugador (exp)")]
    public float XpPerPlayerTakedown = 50f;

    [Tooltip("Minuto en el que un equipo que no hizo NADA llega igual al nivel máximo. Es la red de " +
             "seguridad para que nadie se quede atrás: la experiencia pasiva se calcula sola a partir " +
             "de esto. Matando NPCs y jugadores se llega bastante antes (alrededor del minuto 5).")]
    [Label("Nivel máximo garantizado (min)")]
    public float MinutesToGuaranteedMaxLevel = 10f;

    [Tooltip("Experiencia pasiva por segundo. En 0 (por defecto) se calcula sola con el minuto de " +
             "arriba; ponele un valor si querés fijarla a mano.")]
    [Label("Experiencia pasiva por segundo")]
    public float PassiveXpPerSecondOverride = 0f;

    [Header("Reglas")]
    [Tooltip("El cambio de clase (tecla C) solo se permite dentro de la sala segura del propio equipo.")]
    [Label("Cambiar clase solo en la sala")]
    public bool ClassChangeOnlyInSafeRoom = true;

    // =========================================================
    // ESTADO SINCRONIZADO
    // Los clientes LEEN esto; solo el servidor escribe.
    // =========================================================

    private readonly SyncVar<int> _netState  = new SyncVar<int>((int)EMatchState.Warmup);
    private readonly SyncVar<int> _netWinner = new SyncVar<int>(0);

    // Segundos que le quedan a la fase actual. Se manda cada MEDIO segundo y el
    // cliente lo descuenta solo entre paquetes (ver PhaseTimeRemaining): así el reloj
    // del HUD corre suave sin gastar red en un float por frame.
    private readonly SyncVar<float> _netPhaseTime = new SyncVar<float>(0f);
    private float _phaseTimeReceivedAt;

    // Segundos hasta el próximo Objetivo (0 = ya está en juego). Mismo criterio.
    private readonly SyncVar<float> _netObjectiveEta = new SyncVar<float>(0f);
    private float _objectiveEtaReceivedAt;

    // Un valor por equipo, SIEMPRE con TeamCount entradas (índice 0 = equipo 1).
    private readonly SyncList<int>   _netScores = new SyncList<int>();
    private readonly SyncList<int>   _netLevels = new SyncList<int>();
    private readonly SyncList<float> _netXpNorm = new SyncList<float>();

    // =========================================================
    // ESTADO SOLO-SERVIDOR
    // =========================================================

    private readonly float[] _teamXp    = new float[TeamCount];
    private readonly int[]   _teamLevel = new int[TeamCount];
    private readonly int[]   _teamScore = new int[TeamCount];
    private readonly bool[]  _teamWiped = new bool[TeamCount]; // para no repetir el aviso

    private float _phaseTimer;      // cuenta atrás de la fase actual
    private float _objectiveTimer;  // cuenta atrás hasta el próximo Objetivo
    private bool  _objectivePending;
    private float _slowTickTimer;

    private MercObjective _activeObjective;

    // Jugadores en partida (se rescanean solos, ver RefreshPlayers).
    private readonly List<PlayerController> _players = new List<PlayerController>();

    // =========================================================
    // LECTURA PÚBLICA (la usa la UI, en cualquier peer)
    // =========================================================

    public EMatchState State => (EMatchState)_netState.Value;
    public int WinnerTeam    => _netWinner.Value;

    // Reloj de la fase: el valor sincronizado menos lo que pasó desde que llegó.
    public float PhaseTimeRemaining =>
        Mathf.Max(0f, _netPhaseTime.Value - (Time.time - _phaseTimeReceivedAt));

    // Cuánto se lleva jugado desde que arrancó la partida (0 durante la preparación).
    // Lo usan los campamentos para decidir qué enemigos ya se desbloquearon.
    //
    // En el servidor sale del reloj propio y no del sincronizado: el SyncVar se publica
    // cada medio segundo y su corrección local depende de un callback que en un servidor
    // sin cliente nunca corre.
    public float MatchElapsedSeconds
    {
        get
        {
            if (State != EMatchState.Playing) return 0f;
            float remaining = IsServerInitialized ? _phaseTimer : PhaseTimeRemaining;
            return Mathf.Max(0f, MatchDurationSeconds - remaining);
        }
    }

    // Segundos hasta el próximo Objetivo. 0 = ya hay uno en juego (o todavía no aplica).
    public float ObjectiveEta =>
        Mathf.Max(0f, _netObjectiveEta.Value - (Time.time - _objectiveEtaReceivedAt));

    public int GetScore(int team) => IsValidTeam(team) && _netScores.Count == TeamCount ? _netScores[team - 1] : 0;
    public int GetLevel(int team) => IsValidTeam(team) && _netLevels.Count == TeamCount ? _netLevels[team - 1] : 1;
    public float GetXpNormalized(int team) => IsValidTeam(team) && _netXpNorm.Count == TeamCount ? _netXpNorm[team - 1] : 0f;

    public Color GetTeamColor(int team)
    {
        if (TeamColors == null || TeamColors.Length == 0) return Color.white;
        int i = Mathf.Clamp(team - 1, 0, TeamColors.Length - 1);
        return TeamColors[i];
    }

    // Nombre visible de un equipo, en el idioma de esta pantalla (Loc). Un solo lugar para
    // cambiarlo. Es para MOSTRAR: el servidor nunca lo manda escrito, manda el número.
    public static string TeamName(int team) => Loc.T("match.team", ("team", team));

    public static bool IsValidTeam(int team) => team >= 1 && team <= TeamCount;

    // Los jugadores en partida, bots incluidos (se rescanean dos veces por segundo). Solo en el servidor.
    public IReadOnlyList<PlayerController> Players => _players;

    // Cuándo aparece el primer Objetivo: el evento "Objetivo" del administrador de eventos si la
    // escena lo tiene, si no el campo del modo.
    public float FirstObjectiveDelaySeconds =>
        GameModeEventManager.TryGetStartAt<MatchEvent_Objective>(out float seconds) ? seconds : FirstObjectiveDelay;

    // El Objetivo que está en juego ahora (null si no hay). Lo usa el marcador del HUD.
    public MercObjective ActiveObjective => _activeObjective != null ? _activeObjective : MercObjective.Instance;

    // Base de un equipo (null si no está configurada).
    public MercTeamBase GetBase(int team)
    {
        if (TeamBases == null) return null;
        foreach (var b in TeamBases)
            if (b != null && b.TeamID == team) return b;
        return null;
    }

    // Dónde aparece (y reaparece) un jugador de este equipo. Lo consulta
    // NetworkGameManager en vez de repartir puntos de spawn al azar.
    public Transform GetTeamSpawnPoint(int team)
    {
        MercTeamBase b = GetBase(team);
        return b != null ? b.GetSpawnPoint() : null;
    }

    // =========================================================
    // CICLO DE VIDA
    // =========================================================

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        // Las listas tienen que existir con sus tres entradas ANTES de que un cliente
        // las lea: el HUD pregunta por el equipo 3 desde el primer frame.
        _netScores.Clear(); _netLevels.Clear(); _netXpNorm.Clear();
        for (int i = 0; i < TeamCount; i++)
        {
            _netScores.Add(0);
            _netLevels.Add(1);
            _netXpNorm.Add(0f);
            _teamLevel[i] = 1;
            _teamScore[i] = 0;
            _teamXp[i]    = 0f;
            _teamWiped[i] = false;
        }

        // Sesión nueva en el mismo Play (Desconectar → Iniciar Host): nada de la partida
        // anterior puede quedar vivo acá. Los objetos ya los destruyó FishNet al parar.
        _players.Clear();
        _activeObjective = null;
        _netWinner.Value = 0;

        ServerValidateSetup();
        ServerBeginWarmup();
    }

    // Avisa apenas arranca el servidor si falta algo que va a hacer fallar la partida
    // en silencio veinte segundos después. Es barato y ahorra sesiones enteras de
    // "¿por qué no aparece el Objetivo?" — que casi siempre es una de estas tres cosas.
    [Server]
    private void ServerValidateSetup()
    {
        if (ObjectivePrefab == null)
            Debug.LogError("[Mercenarios] SIN PREFAB DEL OBJETIVO: el modo va a correr pero el Objetivo " +
                           "nunca va a aparecer. Asignalo en el MercenariesGameMode.");
        else if (ObjectivePrefab.GetComponent<NetworkObject>() == null)
            Debug.LogError("[Mercenarios] El prefab del Objetivo no tiene NetworkObject — el servidor no " +
                           "lo va a poder crear.");

        if (ObjectiveSpawnPoint == null)
            Debug.LogWarning("[Mercenarios] Sin ObjectiveSpawnPoint: el Objetivo va a aparecer en la posición " +
                             "del NetworkGameManager.");

        int basesOk = 0;
        if (TeamBases != null)
            foreach (var b in TeamBases) if (b != null) basesOk++;

        if (basesOk < TeamCount)
            Debug.LogError($"[Mercenarios] Solo hay {basesOk} de {TeamCount} bases asignadas. Sin base, un equipo " +
                           "no tiene dónde aparecer ni dónde entregar el Objetivo.");

        Debug.Log($"[Mercenarios] Listo. El primer Objetivo aparece a los " +
                  $"{WarmupSeconds + FirstObjectiveDelaySeconds:F0} s ({WarmupSeconds:F0} de preparación + " +
                  $"{FirstObjectiveDelaySeconds:F0} de espera).");
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        // Anotamos CUÁNDO llegó cada reloj para poder descontarlo localmente.
        _netPhaseTime.OnChange    += (p, n, s) => _phaseTimeReceivedAt    = Time.time;
        _netObjectiveEta.OnChange += (p, n, s) => _objectiveEtaReceivedAt = Time.time;
        _phaseTimeReceivedAt    = Time.time;
        _objectiveEtaReceivedAt = Time.time;
    }

    private void Update()
    {
        if (!IsServerInitialized) return;

        float dt = Time.deltaTime;

        // --- gate de la sala de espera ---
        //
        // Mientras falte gente por marcar "listo", la preparación NO corre: el reloj se
        // queda congelado en su valor inicial en vez de vaciarse solo. Sin esto, los
        // 30 segundos empiezan a correr apenas levanta el servidor y el que entra
        // último se pierde la mitad de la preparación (o aparece con la partida ya
        // empezada).
        //
        // Sin LobbyManager en la escena —o con RequireAllReady apagado— AllReady da
        // true y todo se comporta como antes. La escena de pruebas no cambia en nada.
        // Ahora lo destraba el HOST con el boton Start, no el "todos listos" automatico:
        // asi el host puede esperar a alguien que todavia no llego, o arrancar igual si
        // alguien se colgo. AllReady pasa a ser solo el color del boton.
        if (State == EMatchState.Warmup && LobbyManager.Instance != null && !LobbyManager.Instance.MatchStarted)
        {
            if (_slowTickTimer + dt >= 0.5f) PublishPhaseTime();
            _slowTickTimer = (_slowTickTimer + dt) % 0.5f;
            return;
        }

        // --- reloj de la fase ---
        _phaseTimer = Mathf.Max(0f, _phaseTimer - dt);

        // --- tareas lentas (2 veces por segundo alcanza y sobra) ---
        _slowTickTimer += dt;
        bool slowTick = _slowTickTimer >= 0.5f;
        if (slowTick) _slowTickTimer = 0f;

        switch (State)
        {
            case EMatchState.Warmup:
                if (slowTick) { PublishPhaseTime(); RefreshPlayers(); ServerKeepPlayersInBase(); }
                if (_phaseTimer <= 0f) ServerStartMatch();
                break;

            case EMatchState.Playing:
                TickObjective(dt, slowTick);
                if (slowTick)
                {
                    PublishPhaseTime();
                    RefreshPlayers();
                    ServerTickSharedExperience();
                    ServerSyncPlayerLevels();
                    ServerCheckWipes();
                }
                if (_phaseTimer <= 0f) ServerEndMatch(ResolveLeader());
                break;

            case EMatchState.Ended:
                if (slowTick) PublishPhaseTime();
                if (AutoRestart && _phaseTimer <= 0f) ServerRestartMatch();
                break;
        }
    }

    private void PublishPhaseTime() => _netPhaseTime.Value = _phaseTimer;

    // SEGURO DE LA PREPARACIÓN: nadie sale de su base antes de tiempo. Las rejas frenan
    // a quien camina, pero no a un teletransporte: la Intercepción heroica del Paladín
    // sobre un aliado pegado a los barrotes lo dejaba del otro lado. En vez de tapar cada
    // habilidad que mueve (y las que vengan), se mira el resultado: quien esté fuera de
    // la planta de su sala durante la preparación vuelve a su punto de aparición.
    [Tooltip("Cuánto (en metros) puede pasarse alguien del borde de su sala durante la " +
             "preparación antes de que lo devuelvan adentro.")]
    [Label("Margen de salida en la preparación (m)")]
    public float WarmupEscapeMargin = 1.5f;

    // =========================================================
    // AVISOS A TODAS LAS PANTALLAS
    // =========================================================

    [Server]
    public void Announce(EMatchAnnouncement type, int team, int extra)
        => ObserversAnnounce(type, team, extra);

    [ObserversRpc]
    private void ObserversAnnounce(EMatchAnnouncement type, int team, int extra)
        => OnAnnouncement?.Invoke(type, team, extra);
}
