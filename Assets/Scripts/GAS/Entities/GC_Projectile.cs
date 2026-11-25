using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;

// ============================================================
// GC_Projectile
//
// Proyectil físico en red: vuela con un Rigidbody (simulado en el
// servidor, cinemático en los clientes) y, al chocar, resuelve daño
// con autoridad de servidor. El modelo visual real (arma del que
// disparó) y el VFX de impacto se resuelven por separado en cada
// peer a partir de quién disparó — ver comentarios en cada sección.
// ============================================================
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 25 de noviembre de 2026). En este archivo: los campos y el
// ciclo de vida; lo demás, en GAS/Entities/GC_Projectile/:
//   · Sweep    el choque por barrido del servidor (Sweep), con el rebote y la vuelta
//   · Impact   el impacto: daño, VFX, deshacerse
//   · Flying   la lista de los que vuelan (para los bots) y el lanzamiento
//   · Parry    potenciado por un Summon_ProjectileBooster y devuelto por un parry
//   · Visuals  los visuales: el modelo del arma y el de relleno
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public partial class GC_Projectile : NetworkBehaviour
{
    private AbilitySystemComponent sourceASC; // Quién disparó (solo poblado en el servidor)
    // Cuánto vive un proyectil si la habilidad no dice otra cosa (su "Tiempo de vuelo" en 0).
    public const float DefaultLifeTime = 5f;
    private float lifeTime = DefaultLifeTime;
    private float ultChargeAmount = 0f;
    // Instancia de GA_ProjectileShoot que disparó. Lo que le hace el proyectil a quien
    // toca es SU lista de efectos (ApplyHitEffects: daño a enemigos, curación a aliados
    // si la trae, lo de primer golpe), y sus VFX de impacto se replican desde ella.
    private GameplayAbility sourceAbility;

    // El daño principal de la habilidad (con el que la barrera mide cuánto frenó). Devuelto
    // por un parry, su copia congelada (ver Reflect).
    private GameplayEffect PrimaryDamage => Frozen(sourceAbility != null ? sourceAbility.PrimaryDamageEffect : null);

    // Enemigos ya golpeados, para no repetir daño en el mismo frame si el
    // proyectil atraviesa varios colliders del mismo objetivo.
    private HashSet<AbilitySystemComponent> enemiesHit = new HashSet<AbilitySystemComponent>();

    // Lo mismo del lado aliado: un proyectil que cura y atraviesa no debe curar
    // varias veces al mismo aliado mientras lo recorre.
    private HashSet<AbilitySystemComponent> alliesHit = new HashSet<AbilitySystemComponent>();

    // Quién disparó, sincronizado a TODOS los peers. sourceASC (arriba) solo
    // se llena en el servidor porque Initialize() solo corre ahí — pero el
    // swap de visuales (cubo -> arma real) es puramente cosmético y tiene
    // que verse igual en cualquier cliente, no solo en el servidor. Un
    // NetworkObject sí lo sabe serializar FishNet de forma nativa (a
    // diferencia de un ScriptableObject o un GameObject local), así que cada
    // peer resuelve SU PROPIA copia local del arma del dueño a partir de esto.
    private readonly SyncVar<NetworkObject> _shooterNob = new SyncVar<NetworkObject>();
    private bool _weaponVisualsApplied = false;

    // Los Renderer que trae el prefab (su modelo por defecto). Se anotan en Awake para
    // poder apagarlos y volver a prenderlos sin tocar el arma clonada.
    private Renderer[] _defaultRenderers;

    [Header("Modelo")]
    [Tooltip("Lo que se ve volar es una copia del ARMA de la mano del que lo tiró (el hacha, la " +
             "daga, el kunai). Apagado = se ve el modelo propio de este prefab: la flecha del " +
             "Explorador, que no es el arco que tiene en la mano.")]
    [Label("Usar el arma del que lo tira")]
    public bool UseShooterWeapon = true;

    [Tooltip("La punta sigue la curva del vuelo: con gravedad, sube mirando arriba y cae de " +
             "punta (la flecha). Apagado = conserva el giro con el que salió (el hacha que gira).")]
    [Label("Apunta hacia donde vuela")]
    public bool AlignToVelocity;

    private Rigidbody _body;

    [Header("Choque")]
    [Tooltip("La caja que choca con paredes y escudos toma el tamaño del ARMA que se ve volar (el " +
             "hacha, la daga). Con la caja grande de antes, un tiro que pasaba cerca de una pared " +
             "'pegaba' en ella aunque el arma no la tocara. Solo con 'Usar el arma del que lo tira' " +
             "y un BoxCollider; con el modelo propio (la flecha) vale la caja del prefab.")]
    [Label("Caja al tamaño del arma")]
    public bool FitColliderToWeapon = true;

    [Tooltip("Lo más fina que puede quedar la caja en cada lado.")]
    [Label("Caja: grosor mínimo (m)")]
    public float MinColliderSize = 0.1f;

    [Tooltip("Con qué radio le pega a los PERSONAJES, aparte de la caja: puede ser más generoso " +
             "que el arma para que no cueste acertar (la caja solo decide contra paredes y " +
             "escudos). 0 = la misma caja.")]
    [Label("Radio contra personajes (m; 0 = la caja)")]
    public float CharacterHitRadius = 0.5f;

    [Header("Blur de giro (opcional)")]
    [Tooltip("Material translucido del efecto (el paquete Simple Spin Blur trae uno listo: " +
             "'Spin Blur Material').\n\nVACIO = sin blur; el proyectil vuela como siempre.\n\n" +
             "Con material puesto, cada malla del ARMA CLONADA recibe SimpleSpinBlur, que " +
             "dibuja copias fantasma entre los frames de rotacion. Por eso el efecto sale solo " +
             "cuando el proyectil gira de verdad (hacha con AddSpin) y no en un tiro recto.")]
    [Label("Blur: material")]
    public Material SpinBlurMaterial;

    [Range(1, 128)]
    [Tooltip("Cuantos frames de retraso cubre el blur. Mas alto = estela de giro mas larga.")]
    [Label("Blur: frames de estela")]
    public int SpinBlurShutterSpeed = 4;

    [Range(1, 50)]
    [Tooltip("Copias fantasma dibujadas entre cada par de rotaciones. Mas alto = mas suave y " +
             "mas caro.")]
    [Label("Blur: copias")]
    public int SpinBlurSamples = 4;

    [Range(-0.1f, 0.1f)]
    [Tooltip("Opacidad de las copias fantasma. El rango es chico a proposito: es el del paquete.")]
    [Label("Blur: opacidad (0 a 1)")]
    public float SpinBlurAlpha = 0f;

    [Tooltip("Por debajo de esta velocidad angular no se dibuja nada. Es lo que hace que el " +
             "blur aparezca solo mientras el arma gira y desaparezca sola si se frena.")]
    [Label("Blur: giro mínimo")]
    public float SpinBlurAngularCutoff = 5f;

    [Range(1, 8)]
    [Tooltip("A cuantas mallas del arma se les monta el blur, empezando por las MAS GRANDES.\n\n" +
             "Un arma esta hecha de varias piezas (el hacha tiene siete: hojas, mango y adornos) " +
             "y el blur en un adorno chiquito no se distingue, pero cuesta igual. Con 2 alcanza " +
             "para las hojas, que es lo unico que se nota girando.\n\n" +
             "El criterio es el VOLUMEN de la malla, no el nombre: sigue funcionando aunque " +
             "renombres las piezas.")]
    [Label("Blur: máximo de mallas")]
    public int SpinBlurMaxMeshes = 2;

    // =========================================================
    // CICLO DE VIDA
    // =========================================================

    // Vuela derecho (sin gravedad) 'seconds' y después cae. Lo pide GA_ProjectileShoot con su
    // Vuelo recto, en el servidor, justo después de darle la velocidad.
    public void HoldGravity(float seconds)
    {
        if (_body == null) _body = GetComponent<Rigidbody>();
        if (_body == null || !_body.useGravity) return;
        _body.useGravity = false;
        StartCoroutine(RestoreGravity(seconds));
    }

    private IEnumerator RestoreGravity(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (_body != null && !_body.isKinematic) _body.useGravity = true;
    }

    // Solo en el servidor: el choque (ver CHOQUE POR BARRIDO) y, la flecha, mirar hacia donde
    // va (el NetworkTransform lo lleva a todos).
    private void FixedUpdate()
    {
        if (!IsServerInitialized) return;

        Sweep();
        if (!_gone) UpdateBounceAndReturn();

        if (!AlignToVelocity) return;
        if (_body == null) _body = GetComponent<Rigidbody>();
        if (_body == null || _body.isKinematic) return;

        Vector3 v = _body.linearVelocity;
        if (v.sqrMagnitude > 0.25f) _body.MoveRotation(Quaternion.LookRotation(v));
    }

    // Apaga el placeholder visual (el cubo del prefab) apenas el objeto
    // existe.
    private void Awake()
    {
        // Se anotan los renderers PROPIOS del prefab antes de tocar nada: son los que
        // se apagan y se vuelven a prender. Guardarlos evita que, más adelante, un
        // "prender todo" también encienda el arma clonada y se vean las dos cosas.
        _defaultRenderers = GetComponentsInChildren<Renderer>(true);

        // Con modelo propio (la flecha) no hay nada que reemplazar: se ve tal cual viene.
        if (!UseShooterWeapon)
        {
            _weaponVisualsApplied = true;
            return;
        }

        // Apagamos el modelo propio ACÁ y no en OnStartClient(). Awake() corre síncrono
        // como parte del propio Instantiate(), antes de que el objeto se dibuje por
        // primera vez. OnStartClient() en cambio es un callback de FishNet que puede
        // disparar uno o más frames después — en el servidor/host el objeto ya existe
        // (y se renderiza) desde el Instantiate(), así que si esperáramos a
        // OnStartClient() para ocultarlo, el host llegaría a ver el modelo por defecto
        // durante esos frames antes del swap al arma.
        SetDefaultVisuals(false);

        // ...pero apagarlo es una APUESTA a que va a venir un arma a reemplazarlo, y esa
        // apuesta solo la gana un jugador con arma equipada. Un NPC no tiene
        // PlayerController ni arma, así que su proyectil quedaba apagado para siempre:
        // volaba INVISIBLE, hacía daño, explotaba, y no se veía nada.
        //
        // Este temporizador es la red de seguridad: si en un par de frames nadie aplicó
        // un arma, se vuelve a mostrar el modelo del prefab. Cubre al NPC, al jugador
        // desarmado y a cualquier proyectil que se spawnee sin dueño.
        Invoke(nameof(ShowDefaultVisualsIfNoSwap), 0.2f);
    }

    // En los clientes, apaga la física local (el servidor es quien
    // simula el vuelo real) y se suscribe/aplica el swap de arma según
    // quién disparó.
    public override void OnStartClient()
    {
        base.OnStartClient();

        // Este proyectil no tiene dueño (se spawnea sin conexión asociada),
        // así que el servidor es quien simula su física — la velocidad se le
        // pone en Initialize()/SpawnProjectile() del lado servidor. Si el
        // Rigidbody sigue no-kinemático en los clientes, cae por gravedad
        // local y pelea contra el NetworkTransform (mismo problema que
        // tuvimos con el jugador). En clientes lo apagamos y dejamos que
        // NetworkTransform mueva el objeto.
        if (!IsServerInitialized)
        {
            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity  = false;
            }
        }

        _shooterNob.OnChange += OnShooterChanged;
        if (_shooterNob.Value != null) TryApplyShooterWeaponVisuals(_shooterNob.Value);

        // Potenciado (la Gran bola de fuego): crece en todas las pantallas.
        _boostScale.OnChange += OnBoostScaleChanged;
        if (!IsServerInitialized && _boostScale.Value != 1f) ApplyBoostScale(_boostScale.Value);
    }

    // La llama GA_ProjectileShoot (siempre en el servidor) justo después
    // de spawnear el proyectil, con todos los datos que necesita para
    // resolver el impacto y publicar quién disparó.
    // Nota: la velocidad NO se pasa acá — la setea GA_ProjectileShoot.SpawnProjectile
    // directo sobre el Rigidbody. Antes había un parámetro 'speed' que no se usaba.
    // Los efectos ya no se pasan sueltos: los lee de 'ability' al chocar. 'lifeTimeSeconds'
    // es opcional: sin él vive 5 segundos.
    public void Initialize(AbilitySystemComponent source, float ultCharge, GameplayAbility ability, float lifeTimeSeconds = 0f)
    {
        sourceASC            = source;
        ultChargeAmount      = ultCharge;
        sourceAbility        = ability;

        // Alcance del proyectil expresado como tiempo de vuelo: con velocidad
        // constante, "vive 2s" es "llega hasta 2s × velocidad". 0 = dejar el default.
        if (lifeTimeSeconds > 0f) lifeTime = lifeTimeSeconds;

        // Publicamos quién disparó para que cada cliente pueda resolver su
        // propia arma local (ver comentario en _shooterNob más arriba).
        if (source != null)
            _shooterNob.Value = source.GetComponent<NetworkObject>();

        // Solo el servidor decide cuándo se destruye/despawnea el proyectil.
        if (IsServerInitialized)
        {
            // El arma también en el servidor, YA: la caja que choca toma su tamaño (ver
            // FitColliderToModel). En el host ya venía por el SyncVar; en un servidor sin
            // pantalla no llegaría nunca.
            if (_shooterNob.Value != null) TryApplyShooterWeaponVisuals(_shooterNob.Value);

            StartSweep(source);
            SetupBounceAndReturn();
            if (_body == null) _body = GetComponent<Rigidbody>();
            if (!s_flying.Contains(this)) s_flying.Add(this);
            StartCoroutine(DespawnAfterLifetime());
        }
    }

    // Despawnea el proyectil solo tras lifeTime segundos, si nadie lo
    // destruyó antes por un impacto.
    private IEnumerator DespawnAfterLifetime()
    {
        yield return new WaitForSeconds(lifeTime);
        // Un bumerán no se pierde por tiempo: da la vuelta (la vuelta tiene su propio tope).
        if (CanReturn) { StartReturn(); yield break; }
        if (_returning) yield break;
        DespawnSelf();
    }

    // =========================================================
    // CHOQUE POR BARRIDO — autoridad de servidor (19 de octubre de 2026)
    //
    // Antes el choque era OnTriggerEnter: el collider del proyectil tenía que TOCAR algo en
    // un paso de física. Eso tenía dos problemas:
    //   · A velocidad alta salta lo que hay en el medio: a 100 m/s (el Haz guía) avanza 2 m
    //     por paso, y una pared o un escudo más finos que eso se los podía saltar.
    //   · La caja tenía que ser grande (1 m) para acertarle a la gente, y con esa caja un
    //     tiro que pasaba cerca de una pared "pegaba" en ella (pedido de Gustavo).
    // Ahora, en cada paso, el servidor BARRE el tramo que recorrió el proyectil: su forma
    // (la caja, del tamaño del arma; ver FitColliderToModel) contra paredes, escudos y
    // gente, y además una esfera de "Radio contra personajes" solo contra personajes. Lo
    // que encuentra lo procesa en ORDEN de distancia, así un escudo frena antes que el que
    // está detrás. Cada collider cuenta una vez (como un OnTriggerEnter). El primer tramo
    // sale del cuerpo del que lo tira: tirar pegado a una pared pega en la pared, no la cruza.
    // =========================================================

    private Collider _shape;
    private int      _mask;
    private Vector3  _lastCenter;
    private bool     _sweeping;
    private bool     _gone;          // ya chocó con algo que lo despawneó
    private int      _trajectory;    // sube en cada Reflect: lo del tramo viejo ya no cuenta
    private readonly HashSet<Collider> _touched = new HashSet<Collider>();
    private readonly HashSet<AbilitySystemComponent> _impactShown = new HashSet<AbilitySystemComponent>();

    private static readonly RaycastHit[] s_hits     = new RaycastHit[32];
    private static readonly Collider[]   s_overlaps = new Collider[32];
    private static readonly List<KeyValuePair<float, Collider>> s_found = new List<KeyValuePair<float, Collider>>();

    // Donde choca y se deshace: la explosión y la zona de su habilidad, si tiene (la Bola de
    // fuego del Mago; ver GA_ProjectileShoot "Al chocar"). Devuelto por un parry, no: el autor
    // ya no es el que la tiró. Una sola vez.
    private bool _impacted;

    // =========================================================
    // REBOTE Y VUELTA (19 de noviembre de 2026, de las "Mejoras del GAS") — servidor
    //
    // Lo pide la sección "Rebote y vuelta" de GA_ProjectileShoot; todo apagado = lo de siempre.
    //   · Rebota en paredes: en vez de deshacerse, sale reflejado contra la cara que tocó.
    //   · Salta entre enemigos: al pegarle a uno, va hacia el enemigo más cercano que no tocó
    //     (persiguiéndolo un poco, para no errarle si se mueve).
    //   · Vuelve al lanzador: a su distancia (o a su tiempo de vuelo, o al chocar) da la vuelta y
    //     persigue al dueño hasta llegarle; a la vuelta atraviesa paredes y enemigos.
    // Un parry (Reflect) apaga todo: el devuelto sigue derecho.
    // =========================================================

    private const int   CharacterLayer      = 7;
    private const float ArriveDistance      = 1.2f;   // a esto del pecho del dueño, llegó
    private const float MaxReturnSeconds    = 4f;     // tope de la vuelta (un dueño que huye)
    private const float MaxChaseSeconds     = 1.5f;   // tope de la persecución de un salto
    private const float BounceIgnoreSeconds = 0.1f;   // no vuelve a chocar con la pared de la que rebotó

    private int       _wallBouncesLeft;
    private int       _enemyBouncesLeft;
    private bool      _returning;
    private float     _returnStartedAt;
    private Vector3   _launchOrigin;
    private float     _speed;
    private Collider  _bouncedOff;
    private float     _bouncedOffUntil;
    private AbilitySystemComponent _chase;
    private float     _chaseStartedAt;

    private static readonly Collider[] s_near = new Collider[32];

    // =========================================================
    // LOS QUE ESTÁN VOLANDO (20 de noviembre de 2026): los bots los ven venir y se corren (BotController.Instinct). Solo
    // en el servidor, que es donde piensan.
    // =========================================================

    private static readonly List<GC_Projectile> s_flying = new List<GC_Projectile>();

    // Avisa UNA vez a la habilidad que lo tiró que este proyectil se fue (el arma escondida vuelve con el último bumerán).
    // Al que lo tiró, no al que lo devolvió con un parry: el arma es la suya.
    private GA_ProjectileShoot _thrownBy;

    // =========================================================
    // POTENCIADO (al cruzar un Summon_ProjectileBooster: la Gran bola de fuego de la Evocación)
    // =========================================================

    // Servidor: ya se potenció (una sola vez). El tamaño viaja a todas las pantallas.
    private bool _boosted;
    private readonly SyncVar<float> _boostScale = new SyncVar<float>(1f);
    private Vector3 _baseScale;
    private bool    _baseScaleKnown;

    // True si un parry ya lo devolvió: no se vuelve a devolver, y lo del primer impacto
    // (el aturdido del Orden) era de su lanzador original, no viaja con él.
    private bool _reflected;

    // EL DAÑO ES EL QUE TRAÍA. Al devolverlo, cada efecto de la habilidad que escala con un
    // stat del que lo tiró se cambia por una copia CONGELADA con esos stats (original →
    // copia; ver GameplayEffect.CreateFrozenCopy). Con el efecto original, el daño se
    // recalcularía con los stats del Guardián: un rayo mágico devuelto no haría nada, y la
    // quemadura del Clérigo de la Luz tampoco. La copia conserva además el tipo: un daño
    // mágico devuelto sigue ignorando la armadura. Lo que no escala va tal cual (la
    // ralentización del hacha, las heridas).
    private readonly Dictionary<GameplayEffect, GameplayEffect> _frozen = new Dictionary<GameplayEffect, GameplayEffect>();
}
