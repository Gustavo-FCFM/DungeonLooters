using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController
//
// Controla al personaje jugador: movimiento (CharacterController
// client-authoritative), input de habilidades, cambio de clase,
// animación, cámara y HUD propios. Solo el DUEÑO (IsOwner) procesa
// input y mueve el CharacterController — las copias remotas/servidor
// se mantienen sincronizadas por NetworkTransform y por las RPCs de
// NetworkAbilitySystemComponent.
//
// IMPORTANTE EN EL PREFAB: necesita el componente NetworkTransform
// (FishNet) configurado como Component Type: Transform, Smoothing:
// Interpolation, Send Rate: 10 o más — sin él la posición del
// jugador remoto no se sincroniza y aparece estático o errático.
// ============================================================
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 25 de noviembre de 2026). En este archivo: los campos del
// Inspector, los eventos y los gizmos; los métodos, en Player/PlayerController/:
//   · Lifecycle   el ciclo de vida de red (OnStartClient…) y el Update del dueño
//   · Movement    caminar, saltar, el impulso de las habilidades y la embestida
//   · Walls       el movimiento en paredes (WallMovementProfile) y el vuelo libre
//   · Input       el input y la activación de habilidades (HandleAbilityInput)
//   · Hold        las habilidades de mantener: sus ranuras y su animación
//   · Death       morir, reaparecer y los avisos (registro de bajas, pantalla de muerte)
//   · Class       la clase y el equipamiento
//   · Ultimate    la carga de la definitiva por rol
//   · Animation   la animación, la locomoción en red, el torso, la mira en vivo, VFX al saltar, ragdoll
//   · Unstuck     el botón Desatascarse
//   · HitReaction la reacción de golpe y los pasos (sonido)
//   · Aim         el HUD, el apuntado y las utilidades públicas
//   · Spin        el giro del modelo (molinete tipo Garen)
[RequireComponent(typeof(AbilitySystemComponent))]
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInputProvider))]
public partial class PlayerController : NetworkBehaviour
{
    // =========================================================
    // COMPONENTES Y CONFIGURACIÓN
    // =========================================================

    private AbilitySystemComponent        ASC;
    private NetworkAbilitySystemComponent NetASC;
    private CharacterController           characterController;

    // Lee el input del jugador vía el Input System nuevo (acciones Move, Look,
    // PrimaryAttack, ...). Solo el dueño local lo inicializa (ver OnStartClient).
    private PlayerInputProvider           _input;

    // El jugador DUEÑO en este proceso (null en un servidor sin cliente). Lo usan
    // los sistemas que necesitan saber "de qué lado está quien mira esta pantalla",
    // como la invisibilidad (que oculta al invisible solo para sus enemigos).
    // Ver PlayerVisibility.
    public static PlayerController LocalPlayer { get; private set; }

    [Header("Cámara de Red")]
    // Prefab de cámara que se instancia solo para el dueño local (ver
    // OnStartClient).
    [Label("Prefab de la cámara")]
    public GameObject CameraPrefab;

    [Header("Clase")]
    [Label("Clase actual")]
    public CharacterClassDefinition   CurrentClassDef;
    // Todas las clases jugables posibles, usadas para sincronizar el
    // ÍNDICE de clase por red (ver ServerSetClass) en vez de la
    // referencia al asset.
    [Label("Clases base")]
    public CharacterClassDefinition[] MainBaseClasses;

    [Header("UI")]
    [Label("Ícono del personaje")]
    public Sprite CharacterIcon;

    [Header("Animación")]
    [Label("Animator")]
    public Animator characterAnimator;

    [Tooltip("Nombre del clip PLACEHOLDER que ocupa el estado genérico de acción en el controller " +
             "BASE (AC_Player). Es la 'ranura' donde se mete el clip de cada habilidad que tenga " +
             "AnimationClip asignado, así no hace falta un estado por ataque.")]
    [Label("Acción: clip placeholder")]
    public string ActionClipSlotName = "PLACEHOLDER_Action";

    [Tooltip("Valor de ActionID que lleva al estado genérico de acción (la transición desde AnyState " +
             "de ese estado se filtra por este número).")]
    [Label("Acción: ActionID")]
    public int ActionClipStateID = 99;

    [Tooltip("Trigger que dispara el estado genérico de acción.")]
    [Label("Acción: trigger")]
    public string ActionClipTrigger = "AttackTrigger";

    [Tooltip("Parámetro float que controla la VELOCIDAD del estado genérico de acción. Es propio y " +
             "NO se toca cada frame (a diferencia de AttackSpeedMult, que UpdateAnimations reescribe " +
             "siempre): se fija una vez al activar la habilidad, con la velocidad que hace que el clip " +
             "entre justo en el ritmo de ataque (ver GameplayAbility.ResolveAnimationSpeed).")]
    [Label("Acción: parámetro de velocidad")]
    public string ActionSpeedParam = "ActionSpeedMult";

    [Tooltip("Parámetro FLOAT del Animator con la velocidad vertical del personaje: positiva " +
             "subiendo, negativa cayendo.\n\n" +
             "Sirve para separar el salto de la caída, que con IsJumping solo no se puede: ese " +
             "bool dice que estás en el aire, no hacia dónde vas. Un dash hacia arriba se quedaba " +
             "con la pose de despegue durante toda la bajada.\n\n" +
             "Vacío = no se escribe (el Animator sigue funcionando con IsJumping como antes).")]
    [Label("Parámetro de velocidad vertical")]
    public string VerticalSpeedParam = "VerticalSpeed";

    // =========================================================
    // RANURAS DE "MANTENER" (habilidades IHoldAbility)
    //
    // Mismo principio que la ranura de acción de arriba, pero en TRES tiempos:
    // levantar (one-shot) → mantener (bucle) → bajar (one-shot). Los clips viven en
    // el asset de la habilidad y se meten en runtime en estos placeholders, así no
    // hace falta un estado por habilidad ni tocar el override de cada clase.
    //
    // SETUP MÍNIMO EN EL ANIMATOR BASE (AC_Player) — UN SOLO ESTADO:
    //   · Parámetros: bool "IsHolding", trigger "HoldTrigger", int "ActionID" (ya está).
    //   · 1 estado con el clip placeholder HoldLoopSlotName.
    //   · AnyState  → HoldLoop:   ActionID == HoldStateID  +  trigger HoldTrigger.
    //   · HoldLoop  → Locomotion: IsHolding == false  (SIN exit time: tiene que salir ya).
    //
    // Con eso alcanza y sobra: el escudo se levanta y se baja sin más. Las ranuras de
    // LEVANTAR y BAJAR son OPCIONALES — solo hacen falta si querés esas dos poses de
    // transición, y se agregan después sin tocar código:
    //   · HoldStart entre AnyState y HoldLoop (HoldStart → HoldLoop por Exit Time).
    //   · HoldEnd después del bucle (HoldLoop → HoldEnd con IsHolding == false, y
    //     HoldEnd → Locomotion por Exit Time).
    //
    // OPCIONAL TAMBIÉN — reacción al recibir un golpe EN el escudo:
    //   · 1 estado con el clip placeholder HoldImpactSlotName.
    //   · AnyState    → HoldImpact: ActionID == HoldImpactStateID + trigger HoldImpactTrigger.
    //   · HoldImpact  → HoldLoop:   por Exit Time, con IsHolding == true.
    //   · HoldImpact  → Locomotion: IsHolding == false (por si te bajan el escudo en pleno golpe).
    //
    // Lo único IMPRESCINDIBLE es la ranura del bucle. Si falta, se avisa una vez por
    // consola y la habilidad funciona igual, solo que sin animación.
    // =========================================================

    [Header("Animación — Mantener (escudo, canalizados)")]
    [Tooltip("Clip placeholder del estado 'mantener' (en bucle) en el controller BASE. Es el " +
             "ÚNICO obligatorio de los tres.")]
    [Label("Mantener: clip del bucle")]
    public string HoldLoopSlotName  = "PLACEHOLDER_HoldLoop";

    [Tooltip("OPCIONAL: clip placeholder del estado 'levantar'. Vacío (o sin el estado en el " +
             "Animator) = se entra directo al bucle.")]
    [Label("Mantener: clip de levantar")]
    public string HoldStartSlotName = "PLACEHOLDER_HoldStart";

    [Tooltip("OPCIONAL: clip placeholder del estado 'bajar'. Vacío (o sin el estado en el " +
             "Animator) = se sale del bucle directo a la locomoción.")]
    [Label("Mantener: clip de bajar")]
    public string HoldEndSlotName   = "PLACEHOLDER_HoldEnd";

    [Tooltip("OPCIONAL: clip placeholder de la reacción al recibir un golpe mientras bloqueás " +
             "(el escudo 'acusa' el impacto). Vacío = sin reacción.")]
    [Label("Mantener: clip del golpe")]
    public string HoldImpactSlotName = "PLACEHOLDER_HoldImpact";

    [Tooltip("Valor de ActionID que lleva al estado de mantener (la transición desde AnyState " +
             "se filtra por este número). Tiene que ser distinto de ActionClipStateID.")]
    [Label("Mantener: ActionID")]
    public int HoldStateID = 98;

    [Tooltip("Valor de ActionID que lleva al estado de reacción al golpe en el escudo. Tiene que " +
             "ser distinto de ActionClipStateID y de HoldStateID.")]
    [Label("Mantener: ActionID del golpe")]
    public int HoldImpactStateID = 97;

    [Tooltip("Trigger que dispara la entrada al mantenido.")]
    [Label("Mantener: trigger")]
    public string HoldTrigger = "HoldTrigger";

    [Tooltip("Trigger que dispara la reacción al golpe en el escudo.")]
    [Label("Mantener: trigger del golpe")]
    public string HoldImpactTrigger = "HoldImpactTrigger";

    [Tooltip("Parámetro BOOL que sostiene el bucle: mientras esté en true el personaje se queda " +
             "en el estado de mantener; al ponerse en false sale (al estado de bajar si existe, " +
             "o directo a la locomoción).")]
    [Label("Mantener: parámetro bool")]
    public string HoldingParam = "IsHolding";

    [Header("Animación — Salto de habilidad (leap)")]
    [Tooltip("OPCIONAL: clip placeholder del DESPEGUE. Se reproduce una vez y encadena al bucle.\n\n" +
             "Vacío (o sin el estado en el Animator) = se entra directo al bucle. Ojo: si el " +
             "despegue vive DENTRO del clip del bucle, el salto entero se repite mientras el " +
             "personaje está en el aire — por eso van separados.")]
    [Label("Salto: clip de despegue")]
    public string AirStartSlotName = "PLACEHOLDER_AirStart";

    [Tooltip("Clip placeholder del estado que se reproduce EN BUCLE mientras el personaje está " +
             "en el aire. Es el único obligatorio de los tres.\n\n" +
             "Tiene que ser SOLO la pose sostenida (el hacha arriba): si le metés el despegue, " +
             "el salto entero se repite en loop mientras dura el vuelo.")]
    [Label("Salto: clip del bucle")]
    public string AirLoopSlotName = "PLACEHOLDER_AirLoop";

    [Tooltip("OPCIONAL: clip placeholder del remate al aterrizar. Vacío (o sin el estado en el " +
             "Animator) = del bucle se vuelve directo a la locomoción.")]
    [Label("Salto: clip de aterrizaje")]
    public string AirLandSlotName = "PLACEHOLDER_AirLand";

    [Tooltip("Valor de ActionID que lleva al estado del bucle aéreo. Tiene que ser distinto de " +
             "ActionClipStateID, HoldStateID (98) y HoldImpactStateID (97).")]
    [Label("Salto: ActionID")]
    public int LeapStateID = 96;

    // ---------------------------------------------------------
    // REACCIÓN DE GOLPE
    //
    // Un sacudón corto al recibir daño. Va en la capa UpperBody para que el personaje
    // siga caminando mientras se sacude (como el ataque). La POSTURA la resuelve el
    // AnimatorOverrideController de cada clase: en AC_Player la ranura tiene un clip
    // placeholder, y cada AOC_* lo reemplaza por el CombatDamage de su arma (2 manos,
    // dos armas, espada y escudo). El golpe recibido CON EL ESCUDO ARRIBA no pasa por
    // acá: ese es el HoldImpact de la habilidad de bloquear.
    //
    // Para activarlo, en AC_Player (capa UpperBody):
    //   · Un estado con el clip placeholder HitClipSlotName.
    //   · Parámetro trigger HitTrigger; AnyState → ese estado con el trigger
    //     (Can Transition To Self apagado); → Empty por Exit Time.
    // Sin el parámetro en el Animator no pasa nada (ni error): se queda sin reacción.
    // ---------------------------------------------------------
    [Header("Reacción de golpe")]
    [Tooltip("Clip placeholder del estado de reacción de golpe en la capa UpperBody. Cada AOC de " +
             "clase lo reemplaza por el clip de su postura.")]
    [Label("Golpe: clip placeholder")]
    public string HitClipSlotName = "PLACEHOLDER_Hit";

    [Tooltip("Trigger del Animator que dispara la reacción.")]
    [Label("Golpe: trigger")]
    public string HitTrigger = "HitTrigger";

    [Tooltip("Segundos mínimos entre dos ANIMACIONES de reacción de golpe. Es aparte del " +
             "respiro del SONIDO (AudioLibrary.HurtCooldown, 0.45 s) a propósito: con la " +
             "velocidad de ataque de varias clases, cada golpe pasaba ese filtro y el personaje " +
             "quedaba temblando sin parar. El sonido en cada golpe se lee bien; la sacudida no.\n\n" +
             "En 0 reacciona a todos los golpes que le lleguen.")]
    [Label("Golpe: entre reacciones (s)")]
    public float HitReactionCooldown = 1f;

    [Tooltip("Los estados del Animator con esta etiqueta (Tag, en el Inspector del estado) NO se " +
             "interrumpen con la reacción de golpe: bucles como mantener el escudo, el molinete, " +
             "el vuelo del salto o el aturdido. Se mira en todas las capas.")]
    [Label("Golpe: bloqueado por estado")]
    public string HitBlockedByStateTag = "Loop";

    private bool _hasHitTrigger;

    // ---------------------------------------------------------
    // MUERTE Y ATURDIMIENTO
    //
    // Dos bools del Animator, IsDead e IsStunned, para dos estados de la capa Base:
    //   · PLACEHOLDER_Death: AnyState → Death con IsDead == true (sin Exit Time, Can
    //     Transition To Self apagado); Death → Movement con IsDead == false. El clip se
    //     elige al azar de DeathClips y se mete en la ranura al morir, así hay variedad
    //     sin un estado por muerte.
    //   · PLACEHOLDER_Stun: AnyState → Stun con IsStunned == true; Stun → Movement con
    //     IsStunned == false. El clip (Stun01 del pack) tiene que estar en BUCLE (Loop
    //     Time en el import): dura lo que dure el tag State_Stunned.
    //
    // Si el prefab tiene un RagdollController armado, la muerte NO usa la animación:
    // suelta el cuerpo y lo empuja en la dirección del golpe. La animación queda de
    // respaldo para el prefab sin ragdoll.
    // ---------------------------------------------------------
    [Header("Muerte y aturdimiento")]
    [Tooltip("Clip placeholder del estado de muerte (capa Base). Al morir se reemplaza por uno " +
             "de DeathClips al azar.")]
    [Label("Muerte: clip placeholder")]
    public string DeathClipSlotName = "PLACEHOLDER_Death";

    [Tooltip("Animaciones de muerte posibles. Se elige una al azar por muerte.")]
    [Label("Muerte: animaciones")]
    public AnimationClip[] DeathClips;

    [Tooltip("Bool del Animator que lleva al estado de muerte.")]
    [Label("Muerte: parámetro")]
    public string DeadParam = "IsDead";

    [Tooltip("Bool del Animator que lleva al bucle de aturdido. Se alimenta del tag State_Stunned " +
             "en todas las copias.")]
    [Label("Aturdido: parámetro")]
    public string StunnedParam = "IsStunned";

    [Tooltip("Con ragdoll: la cámara sigue al cuerpo mientras cae, en vez de quedarse mirando el " +
             "punto donde murió. Apagalo para probar la otra sensación.")]
    [Label("Cámara sigue al ragdoll")]
    public bool CameraFollowsRagdoll = true;

    private string _deathSlotKey;
    private bool   _hasDeadParam, _hasStunnedParam;
    private RagdollController _ragdoll;
    private ThirdPersonOrbitCam _orbitCam;

    // Copia en RUNTIME del controller (base + overrides de la clase). Es la que
    // permite intercambiar el clip de la ranura de acción sin tocar los assets.
    private AnimatorOverrideController _runtimeAnimator;
    // Clip que está puesto ahora en la ranura, para no re-asignarlo si no cambió
    // (asignar un override provoca un rebind del Animator).
    private AnimationClip _currentActionClip;
    // Nombre EXACTO del clip placeholder encontrado en el controller (la "llave" de la
    // ranura). null = el estado genérico todavía no existe, así que las habilidades
    // con AnimationClip igual usan el esquema viejo.
    private string _actionSlotKey;
    private bool _warnedNoActionSlot;

    // Llaves reales de las ranuras de mantener (mismo criterio que _actionSlotKey).
    private string _holdStartKey, _holdLoopKey, _holdEndKey, _holdImpactKey;
    // Qué clip está puesto ahora en cada una, para no provocar rebinds al pedo.
    private AnimationClip _currentHoldStart, _currentHoldLoop, _currentHoldEnd, _currentHoldImpact;
    private bool _warnedNoHoldSlots;
    private bool _warnedNoHoldImpact;

    // Llaves reales de las ranuras del salto de habilidad (mismo criterio que las de
    // mantener) y qué clip hay puesto en cada una, para no provocar rebinds al pedo.
    private string _airStartKey, _airLoopKey, _airLandKey;
    private AnimationClip _currentAirStart, _currentAirLoop, _currentAirLand;
    private bool _warnedNoAirSlots;

    // Retención del despegue: entre que la habilidad dispara la animación y que el
    // impulso realmente levanta al personaje pasan uno o más frames (el impulso viaja
    // por TargetRpc). Mientras tanto se fuerza IsJumping en true para que el bucle
    // aéreo no se corte solo. Ver UpdateAnimations.
    private bool  _leapTakeoffPending;
    private float _leapTakeoffDeadline;

    [Header("Huesos")]
    [Label("Socket mano principal")]
    public Transform MainHandSocket;
    [Label("Socket mano secundaria")]
    public Transform OffHandSocket;

    private GameObject currentMainWeapon;
    private GameObject currentOffWeapon;
    private GameObject currentWeaponTrail;

    // Instancia del prefab de comportamientos de pasiva de la clase actual (ver
    // CharacterClassDefinition.PassiveBehaviorsPrefab). Se destruye al cambiar de clase.
    private GameObject currentPassiveBehaviors;

    // True mientras se está ejecutando una habilidad — bloquea input de
    // otras habilidades y el giro por movimiento normal.
    private bool isAttacking = false;

    // Cuándo se puso isAttacking=true, para el watchdog de abajo. Si una
    // habilidad no llama a FinishAttack (su corutina se interrumpió, etc.),
    // el jugador quedaría sin poder actuar; el watchdog lo resetea tras un
    // máximo razonable. No aplica al menú radial (que mantiene isAttacking a
    // propósito hasta soltar el botón).
    private float _attackStartTime;
    private const float MaxAttackSeconds = 5f;

    // Instancias de habilidad otorgadas, una por slot (ver
    // EquipCharacterClass). [HideInInspector] porque se llenan en
    // runtime, no se configuran a mano.
    [HideInInspector] public GameplayAbility MovementAbility;
    [HideInInspector] public GameplayAbility AbilityQ;
    [HideInInspector] public GameplayAbility AbilityE;
    [HideInInspector] public GameplayAbility AbilityR;
    [HideInInspector] public GameplayAbility PrimaryAttackAbility;
    [HideInInspector] public GameplayAbility AimAbility;

    [Header("Físicas")]
    [Label("Fuerza de salto (m/s)")]
    public float jumpForce  = 8f;
    [Label("Gravedad (m/s²)")]
    public float gravity    = -9.8f;

    [Header("Caída de Pluma / Aleteo")]
    [Tooltip("Multiplicador de gravedad mientras el personaje tenga el tag Status_FeatherFall. " +
             "0.35 = cae a poco más de un tercio de la velocidad normal. 1 = gravedad normal.")]
    [Range(0f, 1f)]
    [Label("Caída de pluma: gravedad (×)")]
    public float featherFallGravityScale = 0.35f;

    [Tooltip("Impulso de cada ALETEO. Mientras dure la caída de pluma se puede volver a saltar en " +
             "el aire las veces que se quiera, y cada aleteo vale esto. Conviene por debajo de " +
             "jumpForce, porque se encadenan uno atrás del otro.")]
    [Label("Caída de pluma: aleteo (m/s)")]
    public float flapForce = 3f;

    [Tooltip("Tope de velocidad de caída mientras dura (valor positivo). Sin esto, la gravedad se " +
             "sigue acumulando entre aleteo y aleteo y caer desde muy alto termina en picada. " +
             "0 = sin tope.")]
    [Label("Caída de pluma: velocidad máx. (m/s)")]
    public float featherFallMaxSpeed = 6f;

    [Header("Apuntado (Third Person Shooter)")]
    // Qué tan rápido el cuerpo gira para alinearse con el frente de la cámara.
    // Más alto = casi instantáneo; más bajo = giro más suave/perezoso.
    // Si algún día querés que el cuerpo SOLO gire mientras te movés o apuntás
    // (y no mientras estás quieto girando la cámara), ver FaceCameraForward.
    [Label("Rapidez del giro al apuntar (más = más rápido)")]
    public float aimTurnSpeed = 15f;

    [Tooltip("Cuánto control conserva un jugador mientras lo repelen o lo atraen: su movimiento normal, " +
             "multiplicado por esto, se SUMA al empuje (y el cuerpo sigue a la cámara). 0 = ninguno, " +
             "1 = camina normal encima del empuje. Aturdido o enraizado no tiene control igual.")]
    [Label("Control al ser empujado (0 a 1)")]
    [Range(0f, 1f)] public float knockbackSteerControl = 0.6f;

    private float   verticalVelocity;
    private Vector3 spawnPosition;

    // Impulso temporal de movimiento genérico: cualquier habilidad
    // (salto, dash, empujón...) puede tomar control del desplazamiento
    // horizontal + un impulso vertical sin que este script tenga que
    // conocerla por nombre — ver ApplyAbilityVelocity()/ClearAbilityVelocity()
    // más abajo. El único que sabe qué habilidad la usó y qué hacer al
    // aterrizar es quien la disparó (NetworkAbilitySystemComponent).
    private Vector3 _abilityVelocity;
    private bool    _abilityVelocityActive;

    // Impulso de DASH en 3D: a diferencia de _abilityVelocity (horizontal +
    // gravedad, para el salto), este es un vector completo en cualquier
    // dirección —incluye arriba/abajo— y durante su vigencia NO se aplica
    // gravedad, para que el dash sea una línea recta hacia donde apunta el
    // jugador. Se atenúa solo (inercia). Ver ApplyDashVelocity/HandleMovementInput.
    private Vector3 _dashVelocity;
    private bool    _dashActive;

    // VUELO LIBRE (Status_Flying, lo da el GE de un GA_Flight). Mientras esté el tag: sin
    // gravedad, WASD mueve en 3D hacia donde mira la cámara (más rápido si solo va hacia
    // adelante), Espacio sube y Ctrl baja, y no aterriza aunque toque el piso. Los números
    // llegan del GA_Flight por StartFlight. Al terminar en el aire, o aturdido en pleno
    // vuelo, cae lento (la gravedad de la caída de pluma, sin aleteos).
    private float _flightSpeedMultiplier = 1.5f;
    private float _flightForwardBoost    = 1.5f;
    private float _flightVerticalSpeed   = 6f;
    private float _flightIdleSink        = 1.5f;  // hasta cuánto cae (m/s) si no se mueve
    private float _flightSinkNow;         // lo que está cayendo ahora por quedarse quieto
    private Vector3 _flightLaunch;        // impulso de despegue que se disipa solo (hacia arriba o hacia la mira)
    private float _flightVerticalNow;     // velocidad vertical del vuelo, para el Animator
    private bool  _wasFlying;
    private bool  _flightFalling;         // terminó el vuelo en el aire: cae lento hasta el piso

    // Embestida (GA_RushAttack, la Patada voladora del Monje): avanza sola a velocidad
    // constante; izquierda/derecha la tuercen y volver a apretar su botón la corta. Ver
    // StartRush / TickRush.
    private bool          _rushActive;
    private Vector3       _rushVelocity;
    private float         _rushEndsAt;
    private float         _rushStrafeSpeed;
    private int           _rushExcludeMask;
    private bool          _rushCancelable;
    private EAbilityInput _rushSlot;

    // Repelido / atraído (GameplayEffect.KnockbackDistance): un recorrido horizontal que
    // NO depende del input y corre aunque esté aturdido o enraizado — es control, lo
    // mueve quien empuja. Ver ApplyKnockback / TickKnockback.
    private bool    _knockbackActive;
    private Vector3 _knockbackDisplacement;
    private float   _knockbackDuration;
    private float   _knockbackElapsed;
    private float   _knockbackProgress;

    private const int InvisibleWallsMask = 1 << 2;   // Ignore Raycast: límites de la arena y de las salas

    private bool     _wallActive;
    private Vector3  _wallNormal;          // hacia AFUERA de la pared, siempre horizontal
    private Collider _wallCollider;
    private Vector3  _wallPoint;
    private float    _wallStartTime;
    private float    _wallVertical;        // velocidad vertical mientras está enganchado
    private Vector3  _wallAlongVelocity;   // lo que corre a lo largo (Monje): lo conserva al soltarse

    // La última pared de la que se soltó: a ESA no se vuelve a pegar hasta tocar el piso
    // (salvo que haya SALTADO de ella: WallMovementProfile.SameWallAfterJump).
    private bool     _hasLastWall;
    private Collider _lastWallCollider;
    private Vector3  _lastWallNormal;
    private Vector3  _lastWallPoint;

    // Después de saltar de una pared, el WASD no puede empujar de vuelta contra ella un rato.
    private Vector3  _wallJumpAwayNormal;
    private float    _wallJumpLockUntil;

    // Recién teletransportado (TeleportTo): un instante en el que se engancha solo a la
    // pared que tiene enfrente, sin empujar (el Paso de las sombras del Shinobi llega así).
    private float    _wallGraceUntil;
    private Vector3  _wallGraceDir;

    // El último roce de COSTADO que avisó el CharacterController (ver OnControllerColliderHit).
    private int      _sideHitFrame = -1;
    private Vector3  _sideHitNormal;
    private Vector3  _sideHitPoint;
    private Collider _sideHitCollider;

    // Inclinación del modelo hacia afuera de la pared: hacia dónde apunta el "arriba" del
    // modelo, en el espacio de la raíz. La calcula el dueño y viaja con la locomoción.
    private Vector3 _leanTargetLocal = Vector3.up;
    private Vector3 _leanUpLocal     = Vector3.up;
    private bool    _modelLeaned;

    // Resto de velocidad que deja un dash al TERMINAR (ver ClearDashVelocity). No es
    // lo mismo que _abilityVelocity: acá el jugador conserva el control normal —gira,
    // frena y SALTA— y esto solo se suma a su movimiento mientras se disipa. Es lo que
    // evita que un dash termine en una frenada seca.
    private Vector3 _inertiaVelocity;
    private float   _inertiaDamping = 3f;

    // La inercia de ahora la dejó una PARED (el salto desde ella, o lo que corría a lo
    // largo): al tocar el piso se corta. Es un impulso de aire; sin esto, al aterrizar
    // seguía resbalando en la dirección del salto (8 de octubre, reporte de Gustavo).
    private bool    _inertiaFromWall;

    // Último punto de mira que el dueño calculó con SU cámara y envió al
    // servidor junto con el input de habilidad. El servidor (y por lo tanto
    // las Abilities, que corren ahí) NO tiene una cámara de juego válida
    // propia, así que GetAimPoint() cae a este valor cuando IsOwner es falso.
    [HideInInspector] public Vector3 NetworkAimPoint;

    // DESDE DÓNDE mira: la posición de la cámara del dueño al activar. Va junto con
    // NetworkAimPoint porque el punto solo no alcanza para saber a quién está apuntando.
    //
    // POR QUÉ: la cámara está detrás y al hombro del personaje, así que la línea
    // cámara→retícula y la línea personaje→retícula son distintas, y cuanto más cerca
    // está el objetivo, más se abren entre sí. Eligiendo con la segunda, apuntar a un
    // enemigo del fondo te elegía al que tenías pegado. Con el origen a mano se puede
    // medir contra el rayo de verdad, que es lo que el jugador ve en la retícula.
    [HideInInspector] public Vector3 NetworkAimOrigin;

    // Dirección de movimiento (WASD/stick en espacio de mundo, plano horizontal)
    // que el dueño tenía al activar la última habilidad. La usa la esquiva
    // direccional del dash. Igual que NetworkAimPoint: el servidor no tiene input
    // del dueño, así que este se la envía con el pedido (ver GetMoveDirection y
    // NetworkAbilitySystemComponent.ServerActivateAbility). Cero = quieto.
    [HideInInspector] public Vector3 NetworkMoveDirection;

    // El giro (en grados, sobre "atravesado a la mira") que el dueño le dio con la rueda a lo que
    // pone: el Muro de fuego del Mago (IWallTargetAbility). Viaja con el pedido, igual que la mira.
    [HideInInspector] public float NetworkPlacementYaw;

    // Dónde estaba el dueño al pedir la habilidad (en el servidor). Lo que se apunta en el piso y
    // canaliza antes de salir (el Muro, el Levantamiento, la Tormenta del Mago: 2 s) cae donde soltó
    // aunque se haya movido: el alcance y el giro se miden desde acá. Cero = no vino ningún pedido.
    [HideInInspector] public Vector3 NetworkRequestPosition;

    // Índice de clase sincronizado a los observadores remotos (ver
    // EquipCharacterClass/ServerSetClass) — mandamos el índice y no el
    // asset porque FishNet no serializa referencias a ScriptableObjects.
    private readonly SyncVar<int> _netClassIndex = new SyncVar<int>(-1);

    // Clase con la que tiene que nacer este personaje cuando lo creó el DESATASCARSE
    // (ver esa sección): el dueño la equipa al recibirla. -1 = nació normal, por la sala.
    private readonly SyncVar<int> _netRespawnClassIndex = new SyncVar<int>(-1);

    // El KDA de la partida (lo cuenta el servidor, ServerCountKda). Vive en el personaje: al
    // terminar la partida todos se despawnean, así que la siguiente arranca en 0 sola.
    private readonly SyncVar<int> _netKills   = new SyncVar<int>(0);
    private readonly SyncVar<int> _netDeaths  = new SyncVar<int>(0);
    private readonly SyncVar<int> _netAssists = new SyncVar<int>(0);

    // Daño hecho y curación hecha en la partida (13 de noviembre de 2026, columnas de la tabla). Los cuenta el
    // servidor (ServerCountDamage / ServerCountHealing) solo con la partida en juego. El daño incluye venenos y lo
    // que frenó un escudo, a jugadores y a NPCs; la curación, la de habilidades a aliados Y a uno mismo y el robo de
    // vida (desde el 13 de noviembre de 2026), pero no la de un botiquín.
    private readonly SyncVar<int> _netDamageDone  = new SyncVar<int>(0);
    private readonly SyncVar<int> _netHealingDone = new SyncVar<int>(0);
    private float _damageDoneExact, _healingDoneExact;

    // Cuánto tiene cargada la definitiva, para que la vean los ALIADOS sobre su barra de vida (13 de noviembre de
    // 2026, UI_NameplateUltimate). El cooldown de la definitiva solo existe completo en el servidor y en el dueño:
    // el servidor manda el porcentaje entero, 4 veces por segundo y solo si cambió. 255 = sin definitiva (clase base).
    private readonly SyncVar<byte> _netUltimatePercent = new SyncVar<byte>(255);
    private float _nextUltimatePercentAt;

    [Tooltip("Para la asistencia: hasta cuántos segundos antes de la baja cuenta haberle pegado a la " +
             "víctima, o haber curado al que hizo la baja.")]
    [Label("Asistencia: ventana (s)")]
    public float AssistWindowSeconds = 10f;

    [HideInInspector] public bool isRadialMenuOpen = false;
    private GameplayAbility currentRadialAbility;

    // Menú ÚNICO de clases del dueño local (UI_ClassMenu). Se resuelve al spawnear la
    // cámara (OnStartClient) y atiende las dos teclas: C abre las clases base y V las
    // subclases de la clase actual. Solo el dueño lo tiene (null en las copias
    // remotas/servidor).
    private UI_ClassMenu _classMenu;

    // La camara que este jugador instancio para si mismo (solo el dueno tiene una).
    // Se guarda para poder destruirla al despawnear: ver OnStopClient.
    private GameObject _ownerCamera;

    // La camara de la escena (la del lobby) que se apaga al spawnear. Se guarda para
    // poder PRENDERLA de vuelta al desconectarse: Camera.main solo devuelve camaras
    // activas, asi que una vez apagada no hay como volver a encontrarla.
    private GameObject _lobbyCamera;

    // Habilidad de zona (IGroundTargetAbility) que se está apuntando ahora mismo,
    // mientras se mantiene su botón. Ver UI_GroundTargetIndicator.
    private GameplayAbility _groundTargetAbility;
    private EAbilityInput   _groundTargetSlot;

    // El giro del muro que se está apuntando (IWallTargetAbility): la rueda lo cambia de a pasos
    // y arranca en 0 (atravesado a la mira) cada vez que se aprieta. Va con el pedido.
    private float _placementYaw;

    // Lo apuntado que canaliza antes de salir: hasta cuándo se deja el marcador a la vista (lo
    // esconde antes el fin de la habilidad, FinishAttack). 0 = no hay.
    private float _castMarkerUntil;

    // El marcador del piso: vive en el prefab de la cámara (solo el dueño la tiene) y al arrancar
    // se suelta de ella, así que se lo pide por su Instance.
    private bool _warnedNoGroundIndicator;

    // Habilidad de MANTENER (IHoldAbility) que el dueño está sosteniendo ahora
    // mismo, y en qué slot está. null = no hay ninguna. Ver ProcessHoldRelease.
    private GameplayAbility _holdAbility;
    private EAbilityInput   _holdSlot;

    // Bloquea el input del dueño (movimiento y habilidades) mientras un menú
    // modal está abierto — ej. la selección de clase (UI_ClassMenu).
    private bool _inputLocked;

    // Cámara del dueño, cacheada. Camera.main busca por TAG cada vez que se llama, y
    // acá se usa varias veces por frame (movimiento, giro del cuerpo, punto de mira).
    // Se re-resuelve sola si la cámara actual se destruyó o desactivó (respawn,
    // cambio de escena).
    private Camera _mainCamera;

    // PICADA (el Terremoto del Mago lanzado volando): el servidor cortó el vuelo y le pide al
    // DUEÑO que caiga rápido hasta el piso, sin la caída lenta del fin del vuelo; al tocarlo, el
    // servidor larga la grieta. Lo llama NetworkAbilitySystemComponent.TargetStartDive.
    private bool  _diving;
    private float _diveSpeed;

    // =========================================================
    // INPUT Y ACTIVACIÓN DE HABILIDADES
    // =========================================================

    // Revisa cada botón/tecla de habilidad y dispara su activación o
    // liberación (para las de tipo menú radial).
    // Soltó antes de que se llenara la canalización de un lanzamiento apuntado: se suelta
    // ahora, si ya se llenó (ver ProcessHoldRelease). Si el servidor la cortó, ya no hay
    // mantenido y no pasa nada.
    private bool _holdReleaseQueued;

    // True mientras lo que corre se puede cortar (el ataque básico siempre; un
    // lanzamiento si su asset lo marca).
    private bool _attackInterruptible;

    // QUÉ es lo que está corriendo. Hace falta para una sola cosa: dejar que el ataque
    // básico corte a otra habilidad sin cortarse a sí mismo.
    private EAbilityInput _runningSlot = EAbilityInput.None;

    // Detecta presionar/soltar la ACCIÓN (Input System) asignada a una
    // habilidad. Al presionar la activa; al soltar cierra el menú radial (o la
    // habilidad de zona) si esta la tenía abierta. WasPressedThisFrame /
    // WasReleasedThisFrame son el equivalente del viejo GetButtonDown/GetButtonUp.
    // ATAQUE BÁSICO SOSTENIDO: con el botón (o el gatillo) apretado, cada vez que el
    // ataque principal vuelve a estar disponible se dispara solo, sin tener que soltar
    // y volver a apretar. Con control, apretar el gatillo por cada golpe cansa; con
    // mouse es cómodo igual. Solo para el LMB, y solo para un ataque "normal": las
    // habilidades de rueda, de mantener o de zona tienen su propio ciclo con el botón.
    [Header("Ataque básico")]
    [Tooltip("Mantener apretado el ataque principal lo repite solo cada vez que vuelve a estar disponible.")]
    [Label("Repetir ataque al mantener")]
    public bool AutoRepeatPrimaryAttack = true;

    // Pequeño respiro entre disparos automáticos: si el servidor rechazara uno, esto
    // evita reintentar cada frame.
    private float _nextAutoRepeatAt;

    // =========================================================
    // AVISOS DE UNA MUERTE: registro de bajas y pantalla de muerte
    //
    // Solo el servidor sabe quién dio el golpe final (ASC.LastAttacker), así que él
    // manda los datos: a TODOS la línea del registro de bajas, y al que murió su
    // pantalla con la cuenta regresiva.
    //
    // Manda QUÉ es cada uno y no un texto ya escrito (8 de octubre de 2026, el juego en
    // dos idiomas): el nombre del jugador (un nombre no se traduce), el índice de su
    // clase y, si es un NPC, qué NPC es. Cada pantalla lo escribe en su idioma
    // (ResolveCharacterName / ResolveClassName).
    // =========================================================

    // Qué NPC es, para nombrarlo en el idioma de cada pantalla. 0 = un personaje (jugador
    // o bot: va su nombre). Se manda por número: se amplía SOLO al final.
    private enum ENpcLabel
    {
        None  = 0,
        Boss  = 1,   // la Banshee de curación
        Mage  = 2,
        Totem = 3,
        Ghost = 4,
        IceMage        = 5,
        RockMage       = 6,
        DamageBanshee  = 7,
        SilenceBanshee = 8,
        BloatedZombie  = 9,   // también su veneno ("Summon_ZombiePoison")
        ShieldSkeleton = 10,
        Necromancer    = 11,
    }

    // --- El resumen de muerte (13 de noviembre de 2026, pedido de Gustavo) ---
    //
    // "Te mató el Mago: 45 de Bola de fuego, 30 de Misiles, 12 del zombi": el daño recibido en los últimos segundos,
    // junto por quién y con qué (AbilitySystemComponent.CollectRecentDamage), de mayor a menor. Viaja como QUÉ es
    // (nombre o NPC, equipo, índice de la habilidad en el registro) y cada pantalla lo escribe en su idioma.
    [Tooltip("El resumen de muerte junta el daño recibido en estos últimos segundos.")]
    [Label("Resumen de muerte: últimos (s)")]
    public float DeathRecapSeconds = 10f;

    [Tooltip("Cuántas líneas muestra como mucho (las de más daño).")]
    [Label("Resumen de muerte: líneas")]
    public int DeathRecapLines = 5;

    public const int RecapOverTime = 1;   // banderas de cada línea
    public const int RecapSelf     = 2;

    // =========================================================
    // CLASE Y EQUIPAMIENTO
    // =========================================================

    // ¿Este personaje lo maneja un BOT? Lo marca el NetworkGameManager al spawnearlo.
    // Un bot NO tiene dueño: lo spawnea el servidor para sí mismo, así que todo lo que
    // el flujo normal delega en el dueño (equipar la clase por ServerRpc, teletransportar
    // por TargetRpc) acá tiene que hacerlo el servidor a mano.
    [System.NonSerialized] public bool IsBot;

    // =========================================================
    // CARGA DE LA DEFINITIVA
    //
    // Todas las definitivas comparten un cooldown (GE_Cooldown_Ultimate, 180 s) y
    // "cargar" es adelantarlo. Tres fuentes:
    //
    //   1. Cada golpe que conecta: UltimateChargeAmount de cada habilidad (lo de
    //      siempre, ver GameplayAbility.ChargeUltimate).
    //   2. HACER EL ROL de la clase (ver EClassRole): el tanque carga aguantando, el
    //      de daño matando personajes, el soporte curando aliados. Es el incentivo
    //      para jugar lo que la clase es, y lo que hace que la definitiva llegue a
    //      tiempo: con solo los golpes, tardaba demasiado.
    //   3. El tiempo, que corre solo.
    //
    // Y dos reglas al cambiar de clase:
    //
    //   · Una subclase ARRANCA EN 0. Antes arrancaba llena, porque equipar una clase
    //     borra todos los efectos —el cooldown de la definitiva incluido— y sin
    //     cooldown puesto la definitiva está lista.
    //   · Cambiar de clase a mitad de partida CONSERVA LA MITAD de la carga que se
    //     tenía, y esa mitad es con la que arranca la próxima subclase. Sin esto se
    //     podía cargar la definitiva con una clase y gastarla con otra; con el
    //     reinicio a 0 a secas, cambiar de clase castigaba de más.
    //
    // Todo del lado del servidor: los efectos (y por lo tanto el cooldown) solo
    // existen ahí. El HUD de cada jugador lo recibe por la resincronización de
    // cooldowns del NetworkASC, sin nada nuevo por la red.
    // =========================================================

    [Header("Carga de la definitiva")]
    [Tooltip("PRUEBAS: al elegir subclase, la definitiva arranca LISTA en vez de en 0. " +
             "Para probar clases rápido; apagado en una partida de verdad.")]
    [Label("PRUEBAS: definitiva llena al evolucionar")]
    public bool StartSubclassWithFullUltimate = false;

    [Tooltip("Qué parte de la carga se conserva al cambiar de clase a mitad de partida. " +
             "0.5 = la mitad. Existe para que no se pueda cargar la definitiva con una " +
             "clase y gastarla con otra.")]
    [Range(0f, 1f)]
    [Label("Definitiva conservada al cambiar (0 a 1)")]
    public float UltimateKeptOnClassChange = 0.5f;

    [Tooltip("TANQUE: segundos de definitiva por cada punto de daño que aguanta de un " +
             "enemigo (después de las defensas, antes del escudo). Con 0.1, aguantar " +
             "100 de daño adelanta 10 s de los 180.")]
    [Label("Tanque: carga por punto de daño (s)")]
    public float TankChargePerDamage = 0.1f;

    [Tooltip("DAÑO: segundos de definitiva por cada personaje enemigo que mata (jugador " +
             "o bot; los monstruos no cuentan).")]
    [Label("Daño: carga por baja (s)")]
    public float DamageChargePerKill = 20f;

    [Tooltip("SOPORTE: segundos de definitiva por cada punto de vida que le cura a un " +
             "ALIADO. No cuenta curarse a sí mismo ni lo que se pasa de la vida máxima. " +
             "Con 0.15, curar 100 adelanta 15 s.")]
    [Label("Soporte: carga por punto curado (s)")]
    public float SupportChargePerHeal = 0.15f;

    // Carga guardada al dejar una clase con definitiva, para la próxima subclase.
    // -1 = no hay nada guardado (la próxima subclase arranca en 0).
    private float _carriedUltimateCharge = -1f;

    // Lista plana de TODAS las clases (base + subclases, recursivo) usada para
    // sincronizar el cambio de clase por índice. Se construye igual en todos
    // los peers (mismos assets, mismo MainBaseClasses), así que el índice
    // significa lo mismo para todos. Antes se usaba MainBaseClasses directo,
    // que NO incluye las subclases → al evolucionar, GetClassIndex daba -1 y el
    // cambio (arma incluida) nunca se sincronizaba.
    private List<CharacterClassDefinition> _allClasses;

    private const float LocomotionSendRate = 15f;

    private float _nextLocomotionSend;
    private bool  _locomotionSent;
    private float _sentSpeed, _sentMoveX, _sentMoveY, _sentVertical;
    private bool  _sentJumping;
    private sbyte _sentLeanX, _sentLeanZ;

    private float _netSpeed, _netMoveX, _netMoveY, _netVertical;
    private bool  _netJumping;
    private sbyte _netLeanX, _netLeanZ;

    // =========================================================
    // MIRA EN VIVO (canalizados que apuntan mientras duran)
    //
    // El servidor conoce la mira del dueño solo en el instante de activar: NetworkAimPoint
    // viaja con el pedido de la habilidad. Un canalizado que apunta MIENTRAS dura (el
    // Aliento del dragón rojo deja una zona de fuego donde mirás en cada tick) necesita la
    // mira de AHORA. El servidor le pide al dueño que se la mande un rato
    // (ServerStreamAimFor), y el dueño la manda ~10 veces por segundo por canal no
    // confiable: un punto perdido lo reemplaza el siguiente. En el host no hace falta:
    // ahí GetAimPoint lee su cámara en vivo.
    // =========================================================

    private const float AimStreamRate = 10f;
    private float _aimStreamUntil;
    private float _nextAimStreamAt;

    // =========================================================
    // VFX AL SALTAR (GameplayEffect.JumpVFX)
    //
    // Un efecto puede dejar algo cada vez que su portador salta: el humo del Manto de
    // oscuridad del Shinobi, del piso o de una pared. El salto lo hace el DUEÑO (el
    // movimiento es suyo), y el dueño también sabe qué efectos tiene: le llegan por
    // NetworkASC.NetActiveEffectDuration, el diccionario de la barra de buffs. Así que el
    // dueño decide, lo dibuja en el acto y le pide al servidor que lo repita en las demás
    // pantallas. Solo viaja algo si de verdad tiene puesto un efecto con humo.
    // =========================================================

    private const float JumpVFXLifetime = 4f;

    private const string UpperBodyLayerName = "UpperBody";
    private bool _stunHidUpperBody;

    // =========================================================
    // DESATASCARSE
    //
    // Arreglo de emergencia para el jugador que quedó sin control: no se mueve, no
    // ataca, o quedó atrapado en la geometría. Lo dispara el botón Desatascarse del menú
    // de pausa (UI_PauseMenu), que abren ESC o Start — y se leen de la copia del input de
    // los MENÚS (MenuInput), no de la del jugador, así que llegan aunque el mapa de input
    // del jugador esté apagado. Que es, justamente, uno de los estados de los que hay que
    // poder salir. (En la escena de pruebas, el botón Unstuck del recuadro de red.)
    //
    // QUÉ HACE: no intenta arreglar el personaje trabado — le da uno NUEVO, recién
    // salido del prefab, en su base, con la misma clase. Mientras no sepamos qué es lo
    // que se traba, cualquier arreglo "en el lugar" es adivinar; un personaje nuevo no
    // arrastra nada de lo anterior (input, tags, mantenidos, estado de combate).
    //
    // Y ANTES DE REEMPLAZARLO, DEJA DICHO QUÉ ENCONTRÓ TRABADO. Cada uso es una pista: si
    // el log dice siempre lo mismo, ahí está la causa. Desde el 19 de octubre de 2026 eso
    // queda GUARDADO en un archivo, no solo en la consola (que se pierde al cerrar): el
    // cliente le manda al servidor una foto de su lado (input, menús, movimiento, tags) y el
    // HOST escribe las dos partes juntas en Unstuck.log — en el editor, en la carpeta Logs
    // del proyecto; en una build, junto al Player.log. También los pedidos rechazados: un
    // "quédate quieto" de alguien que jura estar trabado también dice algo.
    //
    // CONDICIONES (las valida el servidor): un botón que te manda a tu base con la vida
    // llena y sin estados es un atajo si se puede usar en cualquier momento — con la bolsa
    // quedabas al lado de la entrega, y aturdido quedabas libre. Por eso solo se acepta si:
    //   · llevas UnstuckStillSeconds quieto (el atascado real no se está moviendo),
    //   · no te pegaron en los últimos UnstuckCombatLockSeconds,
    //   · no tienes un control LEGÍTIMO encima (un aturdido de verdad espera a que se
    //     le pase; un tag pegado SIN efecto que lo dé sí es el bug, y no frena),
    //   · no estás muerto,
    //   · pasó el cooldown (UnstuckCooldownSeconds, por jugador).
    // Si llevabas la bolsa, se te cae donde estabas (el Objetivo ve que su portador
    // desapareció y la suelta ahí).
    // =========================================================

    [Header("Desatascarse (botón Unstuck del recuadro de red)")]
    [Tooltip("Segundos que hay que llevar quieto para que el servidor acepte el pedido.")]
    [Label("Desatascarse: quieto (s)")]
    public float UnstuckStillSeconds = 5f;
    [Tooltip("Cuánto te puedes mover (en metros) y seguir contando como quieto.")]
    [Label("Desatascarse: radio quieto (m)")]
    public float UnstuckStillRadius = 0.75f;
    [Tooltip("Si te pegaron hace menos que esto, no se puede usar: no es un escape de peleas.")]
    [Label("Desatascarse: bloqueo en combate (s)")]
    public float UnstuckCombatLockSeconds = 5f;
    [Tooltip("Cada cuánto lo puede usar un mismo jugador.")]
    [Label("Desatascarse: espera (s)")]
    public float UnstuckCooldownSeconds = 60f;

    // Cooldown por CONEXIÓN y no por personaje: el personaje se reemplaza al usarlo.
    private static readonly Dictionary<int, float> _unstuckReadyAt = new Dictionary<int, float>();

    private Vector3 _stillAnchor = new Vector3(float.MaxValue, 0f, 0f);
    private float   _stillSince;
    private float   _lastDamagedAt = -999f;

    // Agrega una entrada al Unstuck.log de ESTA máquina (el servidor la llama con las dos
    // partes). En el editor va a la carpeta Logs del proyecto; en una build, junto al
    // Player.log (Application.persistentDataPath). Nunca rompe el juego si no puede escribir.
    private static bool _unstuckPathAnnounced;

    private static readonly EGameplayTag[] ControlTags =
    {
        EGameplayTag.State_Stunned, EGameplayTag.State_Rooted, EGameplayTag.State_Silenced,
    };

    // El personaje nuevo del desatascarse equipa la clase del anterior apenas le llega.
    private bool _respawnClassEquipped;

    // Cuándo se reprodujo la última reacción de golpe (ver HitReactionCooldown).
    private float _lastHitReactionAt = -10f;

    // PASOS por distancia recorrida, no por Animation Events: así suenan igual en las
    // copias remotas (que se mueven por NetworkTransform, sin pasar por Move()) y no
    // hay que meter eventos en cada clip de caminar del pack. Y el aterrizaje es el
    // cambio aire → piso, que también vale para todas las copias.
    private Vector3 _lastStepPos;
    private float   _stepAccum;
    private bool    _wasGrounded = true;
    private float   _spawnedAt;

    // Busca en los overrides del controller el clip placeholder que se llame
    // 'slotName' y devuelve su nombre EXACTO (la llave de esa ranura), o null si no
    // existe.
    //
    // La búsqueda ignora mayúsculas/minúsculas por comodidad, pero se guarda el
    // nombre exacto: el indexador del AnimatorOverrideController sí las distingue y,
    // si no coincide al carácter, la asignación no hace nada EN SILENCIO (la
    // habilidad se quedaría con el clip placeholder).
    // ---------------------------------------------------------
    // POSE DE LA MANO SECUNDARIA (el mandoble a dos manos del Maestro de batalla)
    //
    // Una capa "OffHandPose" en el Animator, con la máscara del brazo izquierdo y un solo
    // estado con el clip PLACEHOLDER_OffHandPose. Si la clase trae una pose (y tiene el
    // tag que pide, ver OffHandPoseRequiredTag), se pone en esa ranura y la capa va a peso
    // 1; si no, a peso 0 y el brazo se anima normal. Desde el 1 de octubre va DEBAJO de
    // UpperBody (antes era la última, para el libro del Clérigo): así los ataques a dos
    // manos mueven también ese brazo, y la pose solo se ve caminando o quieto.
    // ---------------------------------------------------------
    public const string OffHandPoseSlotName  = "PLACEHOLDER_OffHandPose";
    public const string OffHandPoseLayerName = "OffHandPose";

    // La clase con la que se armó el Animator: al revivir se vuelve a prender su pose.
    private CharacterClassDefinition _offHandPoseClass;

    // ---------------------------------------------------------
    // POSTURA A LA VISTA: armas a la espalda y pose de dos manos según un tag
    //
    // Corre en TODAS las copias (desde LateUpdate): los tags viajan sincronizados, así
    // que cada pantalla decide sola qué mostrar, sin RPC. Solo hace algo cuando el
    // estado cambia.
    // ---------------------------------------------------------
    private bool _offHandStowed;
    private bool _mainHandStowed;
    private bool _offHandPoseOn;
    private Transform _backBone;

    // ---------------------------------------------------------
    // ANIMACIONES DE LA POSTURA
    //
    // Al armar el Animator se anotan, clip por clip, qué pone el override de la postura y
    // qué tenía la clase en ese lugar; cambiar de postura es aplicar una lista o la otra
    // sobre el mismo controller de runtime (no se rearma, así las ranuras de las
    // habilidades siguen valiendo). Las ranuras PLACEHOLDER_ se saltean: son de las
    // habilidades, y pisarlas cortaría un ataque a la mitad.
    // ---------------------------------------------------------
    private List<KeyValuePair<AnimationClip, AnimationClip>> _stanceClipsOn;
    private List<KeyValuePair<AnimationClip, AnimationClip>> _stanceClipsOff;
    private bool _stanceClipsApplied;

    // ---------------------------------------------------------
    // CRECER (el Avatar del Guardián, ver GrowTag en la clase)
    //
    // Solo el MODELO (el hijo con el Animator): la cápsula, la cámara y la barrera no
    // cambian. Corre en todas las copias, leyendo el tag. Crece y se achica de a poco.
    // ---------------------------------------------------------
    private Vector3 _modelBaseScale;
    private bool    _modelBaseScaleKnown;
    private const float GrowPerSecond = 3f;

    // =========================================================
    // APUNTADO
    // =========================================================

    // Calcula hacia dónde apunta el dueño (centro de su cámara) hasta
    // maxRange, ignorando triggers y su propio cuerpo. Si no sos el
    // dueño (ej: esta copia corre en el servidor o es un observador
    // remoto), no hay Camera.main válida para este proceso — se usa el
    // último punto que el dueño calculó y envió (NetworkAimPoint).
    // La mira FIJA de lo apuntado que canaliza antes de salir (el Muro, el Levantamiento y la
    // Tormenta del Mago: 2 s): mientras dura, la mira es el punto donde soltó, TAMBIÉN en el host
    // (que si no lee su cámara en vivo y lo apuntado salía hacia donde miraba al terminar). La
    // pone y la saca GameplayAbility.CastRoutine; vence sola por las dudas.
    private Vector3 _lockedAimPoint;
    private float   _aimLockedUntil;

    // Altura aproximada de los ojos sobre el transform (que está en los pies).
    private const float EyeHeight = 1.6f;

    // =========================================================
    // UTILIDADES PÚBLICAS
    // =========================================================

    // Devuelve el GameObject del arma principal actualmente equipada
    // (lo usan los proyectiles para clonar su visual).
    // =========================================================
    // GIRO DEL MODELO (molinete tipo Garen)
    //
    // Rota el MODELO, no la raíz. Es a propósito: la raíz la maneja FaceCameraForward
    // para que el cuerpo mire donde apunta la cámara, y el movimiento WASD se calcula
    // relativo a la cámara — girar la raíz rompería las dos cosas. El área del
    // whirlwind es un radio alrededor del jugador, así que no tiene dirección y no le
    // importa hacia dónde "mire" el modelo.
    //
    // Al ser puramente cosmético cada peer lo corre sobre su propia copia, sin
    // autoridad ni sincronización de transform.
    // =========================================================

    private float _modelSpinSpeed;   // grados por segundo; 0 = sin giro
    private float _modelSpinAngle;   // acumulado, para poder devolverlo a cero al terminar

    // La curva del proyectil mientras se mantiene un lanzamiento (TrajectoryPreview): el arco
    // del Explorador (la fase que saldría si soltara ahora) y los lanzamientos apuntados (el
    // hacha, las dagas, la lanza, el Guiding Bolt). Desde la mano y hacia la mira, como el
    // disparo de verdad. La curva vive en el prefab de la cámara.
    private TrajectoryPreview _trajectory;
    private bool _trajectorySearched;

    // Muestra u oculta el arma que el personaje tiene en la mano, sin desactivar el
    // GameObject: se apagan los Renderer y nada más.
    //
    // POR QUÉ EXISTE: el rig de Kevin Iglesias tiene un hueso de prop (B-handProp) y
    // sus animaciones de lanzamiento LO ANIMAN — el hueso sale volando para simular
    // que el arma se suelta, y vuelve al final del clip. Como el arma cuelga de ese
    // hueso, la animación "lanza" el arma real del jugador además del proyectil del
    // GA: se ven dos. Ocultándola mientras dura el lanzamiento, lo único que vuela es
    // el proyectil.
    //
    // Se apagan Renderer y no el GameObject a propósito: desactivarlo también apagaría
    // el hijo WeaponTrail y perdería el estado que manejan los AnimationEvents.
    // El DUEÑO esconde y devuelve el arma con su propio reloj, arrancado al apretar.
    //
    // POR QUÉ NO ALCANZA CON EL AVISO DEL SERVIDOR: el servidor esconde el arma cuando
    // SU cuenta llega al frame de soltar, y ese aviso vuelve al dueño dos viajes
    // después de que él ya vio su animación lanzarla. En el host se ve perfecto (viaje
    // cero), pero un cliente conectado veía el hacha todavía en la mano mientras el
    // proyectil ya salía — dos hachas al mismo tiempo. Acá la escondemos en el frame
    // en que el dueño la ve salir.
    //
    // Los tiempos los calcula la habilidad con los mismos datos que el servidor (ver
    // GA_ProjectileShoot.ResolveThrowTiming), así que las dos puntas coinciden. Y la
    // rutina siempre devuelve el arma, aunque el servidor rechace la habilidad: no hay
    // forma de quedarse sin arma en la mano.
    private Coroutine _weaponHideRoutine;

    // =========================================================
    // GIZMOS — vista previa de las áreas de habilidad en el Editor
    //
    // Lee CurrentClassDef directo (no las instancias otorgadas en runtime),
    // así funciona con el jugador seleccionado en la Scene view SIN
    // necesidad de darle Play — ajustás Range/AbilityRadius/ConeAngle/etc.
    // en el asset de la habilidad y ves el área real actualizarse ahí mismo.
    // =========================================================
    private void OnDrawGizmosSelected()
    {
        if (CurrentClassDef == null || CurrentClassDef.Abilities == null) return;

        foreach (var assignment in CurrentClassDef.Abilities)
            assignment.Ability?.DrawGizmos(transform);
    }
}
