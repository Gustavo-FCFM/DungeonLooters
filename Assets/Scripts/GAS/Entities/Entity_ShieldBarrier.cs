using UnityEngine;
using System.Collections.Generic;

// ============================================================
// Entity_ShieldBarrier
//
// La BARRERA de una habilidad de bloqueo (el escudo del Paladín, y más adelante
// la postura del Guerrero o el escudo sobre un aliado de Juramento de la
// conquista). Es un volumen —el collider de este GameObject— que se enciende
// mientras su dueño tenga el tag de bloqueo, y que reduce el daño de todo golpe
// cuya TRAYECTORIA lo atraviese.
//
// POR QUÉ UN VOLUMEN Y NO UN "¿ESTOY MIRANDO AL ATACANTE?":
// La mayoría de los ataques del juego (conos, líneas, AoE, hitscan) NO son objetos
// físicos: resuelven daño llamando ApplyGameplayEffect directo sobre la víctima,
// así que nunca chocarían con un collider. Por eso el bloqueo se resuelve del lado
// del que RECIBE, con un raycast del atacante a la víctima contra este collider
// (ver Blocks). Eso da tres cosas gratis:
//   · la geometría de bloqueo es EXACTAMENTE la que se ve, sin ángulos a ojo;
//   · protege a los aliados que estén detrás de la barrera, no solo al dueño;
//   · el mismo componente sirve para un escudo puesto sobre OTRO jugador.
// Los proyectiles físicos (GC_Projectile) sí chocan de verdad con el collider y se
// paran ahí — ver el chequeo de barrera en ese script.
//
// SE ENCIENDE SOLO: no hace falta ninguna RPC. Mira el tag ActiveTag del dueño,
// que el GameplayEffect del bloqueo otorga y que ya viaja a todos los peers por
// NetTags. En el servidor el tag está al instante (autoridad), y en los clientes
// llega con la latencia normal — que para un visual es irrelevante.
//
// SETUP: este componente va en un HIJO del PassiveBehaviorsPrefab de la clase (que
// PlayerController instancia en TODOS los peers, así que la barrera existe en
// todos). Necesita un collider marcado como TRIGGER —para no empujar a nadie ni
// pelearse con el CharacterController— en una capa que colisione con la de los
// proyectiles en la matriz de física. El mesh visual va como hijo de este objeto.
// Ajustá el tamaño y el centro del collider: ESE es el escudo. Un escudo plano es un
// BoxCollider; la cápsula de la Defensa paciente (Omnidirectional) es un CapsuleCollider.
// ============================================================
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 25 de noviembre de 2026). En este archivo: los campos del
// Inspector, la lista de escudos levantados y el ciclo de vida; lo demás, en GAS/Entities/Entity_ShieldBarrier/:
//   · Cover   levantarlo y bajarlo, y a quién cubre
//   · Block   el bloqueo del daño (IIncomingDamageModifier) y devolverlo (Guardián)
//   · Visuals lo que se ve al bloquear
public partial class Entity_ShieldBarrier : MonoBehaviour, IIncomingDamageModifier
{
    [Header("Activación")]
    [Tooltip("Mientras el dueño tenga este tag, la barrera está levantada. Lo otorga el " +
             "GameplayEffect de la habilidad de bloqueo (ver GA_ShieldBlock.BlockEffect).")]
    [Label("Tag que la activa")]
    public EGameplayTag ActiveTag = EGameplayTag.Status_Blocking;

    [Header("Bloqueo")]
    [Tooltip("Fracción del daño que la barrera frena. 1 = inmunidad total por ese lado, " +
             "0.7 = te llega el 30%. Se aplica ANTES de Resistencia/Defensa, así que las dos " +
             "cosas se acumulan.")]
    [Range(0f, 1f)]
    [Label("Daño que frena (0 a 1)")]
    public float DamageReduction = 0.7f;

    [Tooltip("Energía que cuesta frenar 1 punto de daño. Con 1, un golpe de 40 frenado al 70% " +
             "gasta 28 de energía. Si no queda energía para frenar todo, frena solo la parte " +
             "que alcance y el resto pasa.")]
    [Label("Energía por punto de daño frenado")]
    public float EnergyPerDamageBlocked = 1f;

    [Tooltip("Altura (desde los pies) a la que se mide la trayectoria del golpe. Si midiéramos " +
             "de pie a pie, el segmento atacante→víctima pasaría por DEBAJO de la barrera y no " +
             "bloquearía nunca. ~1.2 = a la altura del pecho.")]
    [Label("Altura de medición (m)")]
    public float AimHeight = 1.2f;

    [Tooltip("Energía que cuesta parar UN proyectil físico. Los proyectiles se resuelven por " +
             "colisión con este collider (no por el pipeline de daño), así que no sabemos cuánto " +
             "daño traían: se cobra un costo fijo por proyectil detenido.")]
    [Label("Energía por proyectil frenado")]
    public float EnergyPerProjectileBlocked = 10f;

    [Header("Parry (Guerrero)")]
    [Tooltip("Segundos, desde que se LEVANTA el escudo, en los que un golpe que la barrera frena " +
             "es un PARRY: se frena entero y no gasta energía. 0 = sin parry (el Paladín).")]
    [Label("Ventana de parry (s)")]
    public float ParryWindow = 0f;

    [Tooltip("Hasta qué distancia del dueño de la barrera un atacante cuenta como cuerpo a cuerpo. " +
             "A esos, el parry además los aturde (ParryStunEffect); a los de lejos solo se les " +
             "frena el golpe entero.")]
    [Label("Alcance del parry (m)")]
    public float ParryMeleeRange = 3.5f;

    [Tooltip("Lo que recibe el atacante cuerpo a cuerpo al que le hacen parry (GE_ParryStun).")]
    [Label("Efecto del parry")]
    public GameplayEffect ParryStunEffect;

    [Header("Carga de la Definitiva")]
    [Tooltip("Segundos que se le descuentan a la definitiva del dueño por cada GOLPE que frena " +
             "la barrera (la cantidad de golpes, no el daño): uno pequeño y uno enorme valen lo " +
             "mismo. Cuenta también lo que frena por un aliado, los proyectiles y el parry. 0 = no " +
             "carga nada.")]
    [Label("Definitiva por golpe frenado (s)")]
    public float UltimateSecondsPerBlock = 1f;

    // El mismo golpe frenado para varios a la vez (un barrido contra él y dos aliados detrás)
    // cuenta UNA vez: mismo atacante, mismo frame.
    private AbilitySystemComponent _lastBlockedAttacker;
    private int _lastBlockedFrame = -1;

    [Header("Devolver (Guardián)")]
    [Tooltip("Fracción del daño FRENADO que se le devuelve al atacante CUERPO A CUERPO (a " +
             "ParryMeleeRange o menos), como daño físico normal: pasa por su armadura. 1 = todo " +
             "(el Guardián). 0 = nada (el resto de los escudos). Lo devuelto no se puede volver " +
             "a devolver, así dos Guardianes no se rebotan el daño.")]
    [Range(0f, 1f)]
    [Label("Daño devuelto (0 a 1)")]
    public float ReflectMeleeFraction = 0f;

    [Tooltip("En la ventana del parry, los proyectiles no se paran: cambian de dueño (pasan a " +
             "ser del dueño de la barrera) y salen hacia donde apunta. Una sola vez por " +
             "proyectil.")]
    [Label("Devolver proyectiles en el parry")]
    public bool ReflectProjectilesOnParry = false;

    [Header("Castigar al atacante (Samurái)")]
    [Tooltip("Se le aplica al ATACANTE por cada golpe que frena esta barrera: cuerpo a cuerpo o a " +
             "distancia, también en el parry. El Samurái pone las heridas del Pícaro (GE_Wounds, " +
             "hasta 10). El mismo golpe frenado para varios a la vez cuenta una vez. Vacío = nada.")]
    [Label("Efecto al atacante")]
    public GameplayEffect OnBlockAttackerEffect;

    // Mismo criterio que la carga de la definitiva: un golpe que frena para varios a la vez
    // (un barrido contra él y dos aliados detrás) castiga una sola vez.
    private AbilitySystemComponent _lastPunishedAttacker;
    private int _lastPunishedFrame = -1;

    // Candado de la devolución: mientras se aplica un daño devuelto, ninguna barrera lo
    // vuelve a devolver (sí lo puede frenar). Todo corre en el hilo principal, de forma
    // sincrónica, así que alcanza con un estático.
    private static bool s_reflecting;
    private GameplayEffect _reflectEffect;

    [Header("Cápsula (Defensa paciente del Monje)")]
    [Tooltip("La barrera frena los golpes que le llegan al DUEÑO desde CUALQUIER lado, sin " +
             "mirar la geometría (una cápsula a su alrededor). No cubre aliados ni sigue la mira. " +
             "El collider igual hace falta (un CapsuleCollider que lo envuelva): es lo que para los proyectiles.")]
    [Label("En todas las direcciones")]
    public bool Omnidirectional = false;

    [Tooltip("Solo con Omnidirectional: a qué distancia del pecho del dueño sale el destello del golpe.")]
    [Label("Radio del destello (m)")]
    public float OmniFlashRadius = 0.8f;

    [Header("Inclinación con la Mira")]
    [Tooltip("La barrera sube y baja con la mira del jugador (mirando al cielo, el escudo apunta " +
             "arriba). Sigue la misma inclinación que el torso (UpperBodyAim), así que se ve igual " +
             "en todas las pantallas y el servidor bloquea con la misma geometría.")]
    [Label("Sigue la mira vertical")]
    public bool FollowAimPitch = true;

    [Tooltip("Altura del punto sobre el que gira la barrera, medida desde los pies (el pecho).")]
    [Label("Altura del pivote (m)")]
    public float PitchPivotHeight = 1.2f;

    [Tooltip("Cuánto de la inclinación de la mira sigue la barrera. 1 = toda.")]
    [Range(0f, 1.5f)]
    [Label("Cuánto sigue la mira (0 a 1)")]
    public float PitchFactor = 1f;

    [Header("Protección a Aliados")]
    [Tooltip("Si la barrera también protege a los aliados que queden detrás (lo que la convierte " +
             "en un escudo de equipo estilo Reinhardt). Desactivalo para un escudo puramente personal.")]
    [Label("Protege aliados")]
    public bool ProtectAllies = true;

    [Tooltip("Solo con Protect Allies: a qué distancia del dueño se buscan aliados a los que " +
             "cubrir. No define el bloqueo (eso lo decide la geometría de la barrera), solo a " +
             "quiénes se les consulta — poner un valor holgado.")]
    [Label("Alcance para aliados (m)")]
    public float AllyScanRange = 12f;

    [Tooltip("Capa de los personajes, para buscar a los aliados a cubrir.")]
    [Label("Capa de personajes")]
    public LayerMask CharacterLayer;

    [Tooltip("Cada cuánto se revisa qué aliados están cerca. No hace falta que sea rápido: un " +
             "aliado recién llegado igual queda cubierto en menos de un tick, y el bloqueo real " +
             "siempre se decide con la geometría al momento del golpe.")]
    [Label("Cada cuánto busca aliados (s)")]
    public float AllyScanInterval = 0.25f;

    // Se dispara cada vez que la barrera frena daño: cuánto frenó y en qué punto del
    // mundo. Lo usa la pasiva del Paladín para curar a su aura al bloquear ("cada vez
    // que evite daño, el jugador sanará a los aliados que estén en su aura").
    //
    // OJO: es un evento de GAMEPLAY y solo corre en el SERVIDOR (es ahí donde se
    // resuelve el daño). Para feedback visual usá OnFlash, que sí llega a todos.
    public event System.Action<float, Vector3> OnDamageBlocked;

    // Gemelo puramente VISUAL del anterior: se dispara en TODOS los peers con el punto
    // de impacto, para el destello/ripple del escudo (ver ShieldBlockFlash). Va por su
    // propio canal porque el bloqueo se resuelve solo en el servidor, y si el efecto
    // colgara de OnDamageBlocked únicamente el host lo vería.
    public event System.Action<Vector3> OnFlash;

    private AbilitySystemComponent _ownerASC;
    private Collider               _box;   // BoxCollider (escudo) o CapsuleCollider (cápsula)
    private Transform[]            _visuals;

    // ASCs en los que este modificador está registrado ahora mismo (el dueño +
    // los aliados cubiertos). Hay que llevar la cuenta para poder darse de baja
    // exactamente de los mismos al bajar la barrera o al alejarse un aliado.
    private readonly List<AbilitySystemComponent> _registeredOn = new List<AbilitySystemComponent>();
    private readonly List<AbilitySystemComponent> _scanBuffer   = new List<AbilitySystemComponent>();

    private bool  _raised;
    private float _scanTimer;

    // Cuándo se levantó por última vez (para la ventana del parry).
    private float _raisedAt = -999f;

    // Inclinación: de dónde sale el ángulo y la pose guardada del prefab, sobre la que
    // se gira (así el ajuste que se le haya dado en el prefab se respeta).
    private UpperBodyAim _aim;
    private Vector3      _baseLocalPosition;
    private Quaternion   _baseLocalRotation;

    // True mientras dura la ventana del parry.
    public bool IsParrying => _raised && ParryWindow > 0f && Time.time - _raisedAt <= ParryWindow;

    // =========================================================
    // CICLO DE VIDA
    // =========================================================

    private void Awake()
    {
        // El ASC está en el jugador: este componente vive en un nieto (hijo del
        // PassiveBehaviorsPrefab, que a su vez es hijo del jugador).
        _ownerASC = GetComponentInParent<AbilitySystemComponent>();
        _box      = GetComponent<Collider>();
        if (_box == null)
            Debug.LogWarning($"[{name}] La barrera no tiene collider: no va a frenar nada. Agregale un " +
                             $"BoxCollider (escudo) o un CapsuleCollider (cápsula) marcado como trigger.");
        _aim      = _ownerASC != null ? _ownerASC.GetComponentInChildren<UpperBodyAim>(true) : null;

        _baseLocalPosition = transform.localPosition;
        _baseLocalRotation = transform.localRotation;

        if (_box != null && !_box.isTrigger)
            Debug.LogWarning($"[{name}] El collider de la barrera NO es trigger. Siendo sólido " +
                             $"empuja a los jugadores y pelea con el CharacterController del dueño. " +
                             $"Marcalo como Is Trigger.");

        // Los visuales de la barrera son TODOS sus hijos: se apagan y prenden con
        // ella, pero este GameObject nunca se desactiva — si lo hiciéramos, el script
        // dejaría de correr y no volvería a ver el tag para levantarse.
        //
        // Se toman todos y no solo el primero para que se pueda componer el efecto con
        // varios objetos (el panel translúcido, un borde, un sistema de partículas)
        // sin que los que sobran queden encendidos para siempre.
        _visuals = new Transform[transform.childCount];
        for (int i = 0; i < transform.childCount; i++) _visuals[i] = transform.GetChild(i);

        // Estado inicial FORZADO, no vía SetRaised: ese método corta cuando el estado
        // pedido es el que ya cree tener, y _raised arranca en false — así que
        // SetRaised(false) acá no hacía nada y el escudo quedaba con el collider y los
        // visuales tal como estuvieran guardados en el prefab (o sea, encendidos).
        // Se veía como que el Paladín nace con el escudo puesto, y recién se acomodaba
        // después de levantarlo y bajarlo una vez.
        ApplyRaisedState(false);
    }

    private void OnDisable() => SetRaised(false);
    private void OnDestroy()
    {
        SetRaised(false);
        if (_reflectEffect != null) Destroy(_reflectEffect);
    }

    // Sigue el tag del dueño: levantar/bajar la barrera y, mientras está arriba,
    // refrescar cada tanto la lista de aliados cubiertos.
    private void Update()
    {
        if (_ownerASC == null) return;

        bool shouldBeRaised = _ownerASC.HasTag(ActiveTag);
        if (shouldBeRaised != _raised) SetRaised(shouldBeRaised);

        if (!_raised || !ProtectAllies || Omnidirectional) return;

        _scanTimer -= Time.deltaTime;
        if (_scanTimer > 0f) return;
        _scanTimer = Mathf.Max(0.05f, AllyScanInterval);
        RefreshCoveredAllies();
    }

    // La barrera gira alrededor del pecho siguiendo la mira. En LateUpdate, después de
    // que UpperBodyAim actualizó el ángulo del frame. Se gira sobre el eje X del PADRE
    // (el prefab de pasivas, que gira con el cuerpo), así el yaw lo sigue poniendo el
    // cuerpo y esto solo agrega el sube-y-baja.
    private void LateUpdate()
    {
        if (!FollowAimPitch || Omnidirectional || _aim == null) return;

        Quaternion pitch = Quaternion.Euler(_aim.CurrentPitch * PitchFactor, 0f, 0f);
        Vector3    pivot = new Vector3(0f, PitchPivotHeight, 0f);

        transform.localPosition = pivot + pitch * (_baseLocalPosition - pivot);
        transform.localRotation = pitch * _baseLocalRotation;
    }

    // LAS LEVANTADAS (20 de noviembre de 2026): los bots las miran para rodearlas (BotController.Instinct).
    private static readonly List<Entity_ShieldBarrier> s_raised = new List<Entity_ShieldBarrier>();
}
