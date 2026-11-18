using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility
//
// Clase base abstracta de toda habilidad del juego (ScriptableObject,
// una instancia por personaje que la tiene otorgada — ver
// AbilitySystemComponent.GrantAbility). Define el ciclo de vida común
// (costo, cooldown, activación, fin) y utilidades compartidas
// (afiliación, VFX, gizmos de editor); cada subclase concreta
// (GA_ConeAttack, GA_LeapAttack, etc.) implementa Activate() con su
// propia lógica de detección/daño.
//
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 17 de noviembre de 2026). En este archivo: TODOS
// los campos del Inspector (con sus secciones, también los de canalizar y las cargas) y el estado
// común. El resto, en la carpeta GAS/GameplayAbility/:
//   · Lifecycle    otorgar, poder activarse, comprometer, cobrar y terminar; el cooldown y la definitiva
//   · HitTiming    cuándo pega: los frames de golpe del clip y la velocidad de la animación
//   · Cast         canalizar (cómo corre) y la barra del dueño
//   · Charges      las cargas (cómo corren)
//   · Echoes       los ecos (la Proyección arcana)
//   · Effects      aplicar la lista Effects: por afiliación, al golpear, al activarse, al matar
//   · Stacks       acumulaciones, "al terminar de golpear" y daño mágico con un tag
//   · Targeting    dirección del golpe, alcance cuerpo a cuerpo, afiliación y objetivo apuntado
//   · Visuals      los VFX (Visuals) y su envío por red
//   · ClientHooks  lo que pregunta el cliente: animación de los pasos, predicción, cámara, ícono
//   · Legacy       datos viejos → formato nuevo (al cargar el asset)
//   · Gizmos       gizmos y manijas del editor
//   · Tasks        las tareas: ¿corto? (ShouldStop / Guard con EStopWhen) y las esperas (WaitSeconds, WaitUntil, Repeat)
// Un campo nuevo del Inspector va en ESTE archivo: Unity los guarda en el orden en que están
// escritos, y repartidos entre archivos ese orden no está garantizado.
// No renombrar este archivo: con la clase repartida, Unity solo le asocia la clase al archivo que se
// llama EXACTAMENTE como ella (los assets de las habilidades apuntan a su GUID).
// ============================================================
public abstract partial class GameplayAbility : ScriptableObject, IChargedAbility
{
    // =========================================================
    // CONFIGURACIÓN GENERAL
    // =========================================================

    [Section(AbilitySection.General)]
    [Tooltip("El nombre en inglés (lo ve el jugador con el juego en inglés: la barra de casteo y la de carga).")]
    [Label("Nombre (inglés)")]
    public string AbilityName = "New Ability";

    [Tooltip("El nombre en español. Vacío = sale el inglés. Se cargan todos de una con " +
             "Mercenarios ▸ Idiomas ▸ Importar nombres traducidos.")]
    [Label("Nombre (español)")]
    public string AbilityNameEs;

    [Label("Ícono")]
    public Sprite AbilityIcon;

    // El nombre que ve el jugador, en su idioma.
    public string DisplayName => Loc.Pick(AbilityName, AbilityNameEs);

    [Tooltip("Qué hace, en inglés (la ventana de ayuda).")]
    [Label("Descripción (inglés)")]
    [TextArea] public string Description;

    [Tooltip("Qué hace, en español. Vacío = sale la inglesa.")]
    [Label("Descripción (español)")]
    [TextArea] public string DescriptionEs;

    public string DisplayDescription => Loc.Pick(Description, DescriptionEs);

    [Section(AbilitySection.CostCooldown)]
    [Tooltip("Efecto instantáneo que se descuenta al activar (ej: -20 de maná).")]
    [Label("Efecto de costo")]
    public GameplayEffect CostEffect;

    // Con qué "fuente" se cobra el costo. Un costo que escala con un stat (el 50 % del maná MÁXIMO
    // del corte avanzando del Filo danzante) necesita al dueño, que es de quien se leen los stats:
    // con la habilidad de fuente el escalado daba 0 y no cobraba nada. Los demás, como siempre.
    protected object CostSource => CostEffect != null && CostEffect.ScalesWithSource ? (object)OwnerASC : this;

    // Efecto CON duración que bloquea reactivar la habilidad mientras esté
    // activo (ver CanActivate). Lo IMPORTANTE del GE acá es su primer GrantedTag:
    // es la "identidad" del cooldown para la UI, la carga de ultimate y el
    // bloqueo por slot. La DURACIÓN normalmente la define CooldownDuration (abajo),
    // así podés REUSAR un mismo GE de cooldown para muchas habilidades (uno por
    // slot/tag) en vez de crear uno por cada una.
    [Tooltip("GE con duración que bloquea volver a usarla. Lo que importa es su PRIMER " +
             "GrantedTag (la identidad del cooldown); la duración normalmente sale de " +
             "CooldownDuration, así un mismo GE sirve para muchas habilidades.")]
    [Label("Efecto de cooldown")]
    public GameplayEffect CooldownEffect;

    [Tooltip("Duración del cooldown en segundos, configurada acá en el GA. Si es > 0, " +
             "pisa el Duration del CooldownEffect (reusá un mismo GE de cooldown y ajustá " +
             "el tiempo por habilidad). 0 = usar el Duration del GE. Lo ignora " +
             "UseAttackSpeedAsCooldown (ese tiene prioridad).")]
    [Label("Cooldown (s)")]
    public float CooldownDuration = 0f;

    [Tooltip("Marcá esto en los ATAQUES BÁSICOS: el cooldown sale del stat AtkSpeed del dueño " +
             "(ignorando CooldownDuration y el Duration del CooldownEffect), o sea que el cooldown " +
             "ES el ritmo de ataque.\n\n" +
             "También controla la ANIMACIÓN: al ser un ritmo, el clip se estira/comprime para durar " +
             "exactamente ese tiempo y el swing entra justo entre golpe y golpe. Las habilidades " +
             "normales (cooldown = espera, no ritmo) reproducen su clip a velocidad natural.")]
    [Label("Cooldown = ritmo de ataque")]
    public bool UseAttackSpeedAsCooldown = false;

    // Cuánto adelanta el cooldown de la ultimate cada vez que esta
    // habilidad conecta un golpe (ver ChargeUltimate).
    [Tooltip("Segundos que le resta al cooldown de la DEFINITIVA cada golpe que conecta. 0 = nada.")]
    [Label("Carga de la definitiva (s)")]
    public float UltimateChargeAmount = 0f;

    // Si el dueño tiene cualquiera de estos tags, CanActivate() falla
    // (ej: no se puede atacar si está Silenciado).
    [Section(AbilitySection.Rules, startCollapsed: true)]
    [Tooltip("Si el lanzador tiene CUALQUIERA de estos tags, no la puede usar. Por tipo de acción: " +
             "State_Stunned en todas; State_Disarmed en las de arma; State_Silenced en las de " +
             "magia; State_Rooted en las de movimiento.")]
    [Label("Bloqueada por tags")]
    public List<EGameplayTag> ActivationBlockedTags;

    [Tooltip("Al revés que ActivationBlockedTags: el dueño DEBE tener TODOS estos tags para " +
             "poder activarla (ej: 'Marcado para morir' del Asesino solo se lanza estando invisible). " +
             "Vacío = sin requisitos.")]
    [Label("Requiere tags")]
    public List<EGameplayTag> ActivationRequiredTags;

    [Tooltip("Esta habilidad SÍ se puede usar mientras el personaje está canalizando otra (el " +
             "molinete del bárbaro).\n\n" +
             "Un canalizado con BlockOtherAbilities bloquea TODO lo demás: la lista de lo " +
             "permitido se arma marcando esta casilla en las pocas que sí, no enumerando las " +
             "muchas que no. Así una habilidad nueva nace bloqueada por defecto, que es lo " +
             "seguro — si fuera al revés habría que acordarse de agregarla a una lista.")]
    [Label("Usable canalizando")]
    public bool UsableWhileChanneling = false;

    [Tooltip("Esta habilidad SÍ se puede usar mientras se sostiene una habilidad de MANTENER " +
             "(el escudo). Lo normal es que el escudo ocupe las manos y bloquee todo; la Carga " +
             "defensiva del Guerrero es la excepción: carga con el escudo arriba.\n\n" +
             "El escudo SIGUE arriba durante y después de esta habilidad: se baja soltando el " +
             "botón, como siempre. Mientras se sostiene, esta habilidad no reproduce su " +
             "animación, para no romper la pose del escudo.")]
    [Label("Usable manteniendo")]
    public bool UsableWhileHolding = false;

    // Si se puede usar con un mantenido arriba. Virtual porque un GA_TagSwitch lo
    // resuelve según la variante que se dispararía (la Carga defensiva sí, la ofensiva no).
    public virtual bool CanUseWhileHolding => UsableWhileHolding;

    [Section(AbilitySection.Animation)]
    [Tooltip("FORMA RECOMENDADA: arrastrá acá el clip de esta habilidad y listo — no hace falta " +
             "crear un estado en el Animator, ni reservar un AnimationID, ni mapear el clip en el " +
             "override de cada clase. El clip se mete en runtime en la ranura genérica de acción " +
             "(ver PlayerController.ActionClipSlotName). Como el clip vive en ESTE asset, cada peer " +
             "reproduce el mismo sin sincronizar nada extra.\n\n" +
             "Si lo dejás vacío se usa el esquema viejo de AnimationTriggerName + AnimationID.")]
    [Label("Clip de animación")]
    public AnimationClip AnimationClip;

    [Tooltip("Esquema viejo (solo se usa si AnimationClip está vacío): nombre del trigger del Animator.")]
    [Label("Trigger (viejo)")]
    public string AnimationTriggerName = "AttackTrigger";

    [Tooltip("Esquema viejo (solo se usa si AnimationClip está vacío). 1=Melee, 2=Proyectil, 3=Salto, 4=Extra")]
    [Label("ID de animación (viejo)")]
    public int AnimationID = 1;

    [Section(AbilitySection.Sound)]
    [Tooltip("Al LANZAR: suena en el personaje junto con la animación. Lo oyen todos los peers " +
             "por el mismo camino que la animación. Vacío = silencio.")]
    [Label("Sonido al lanzar")]
    public SfxCue CastSound;

    [Tooltip("Al IMPACTAR: suena con los VFX de golpe e impacto (en cada objetivo alcanzado, o " +
             "donde cae la habilidad), en todos los peers. Vacío = silencio.")]
    [Label("Sonido al impactar")]
    public SfxCue ImpactSound;

    // =========================================================
    // EFECTOS Y VFX (listas con condiciones)
    //
    // Las DOS listas que reemplazan a los campos sueltos de cada GA (DamageEffect,
    // AdditionalEffects, TargetEffects, FirstHitEffects, HitVFX, ImpactVFX...). Viven en la
    // base porque las reglas son las mismas para todas: cada entrada dice a quién y cuándo.
    // Cada GA decide en qué momentos llama a ApplyHitEffects / BroadcastHitVFX /
    // BroadcastImpactVFX; "al activarse" y "al lanzar" los resuelve CommitAbility para todas.
    // =========================================================

    [Section(AbilitySection.Stacks, startCollapsed: true)]
    [Tooltip("Acumulaciones que esta habilidad LEE al activarse, contadas por este tag (cada " +
             "acumulación lo da una vez: las Artes marciales dan Status_MartialArts). Las entradas " +
             "de Efectos que escalan con acumulaciones usan cuántas había. None = no lee ninguna.")]
    [Label("Tag de las acumulaciones")]
    public EGameplayTag StacksTag = EGameplayTag.None;

    [ShowIf(nameof(StacksTag), EGameplayTag.None, true)]
    [Tooltip("Si gasta las acumulaciones que leyó, y cuándo. 'Al terminar' conserva lo que dan " +
             "mientras dura la habilidad: el +1 de ataque por acumulación del Samurái, con el que " +
             "curan los Cortes devastadores.")]
    [Label("Gasta las acumulaciones")]
    public EStackConsume ConsumeStacks = EStackConsume.Never;

    // Cuántas acumulaciones había al activarse (las lee CommitAbility). -1 = no leyó. Un
    // combo se la pasa a sus pasos, así un golpe del combo escala con lo que leyó el combo.
    [System.NonSerialized] public int StackSnapshot = -1;
    [System.NonSerialized] private bool _consumeStacksOnEnd;

    [Section(AbilitySection.Effects)]
    [Tooltip("Los GameplayEffect de la habilidad. Cada entrada dice CUÁNDO (al golpear, al " +
             "primer golpe, al activarse, al matar), A QUIÉN (enemigos, aliados, el lanzador, " +
             "todos) y, si querés, una CONDICIÓN (que el objetivo tenga un tag).\n\n" +
             "Se aplican en el orden de la lista. El primero 'al golpear, a enemigos' sin condición " +
             "es el DAÑO PRINCIPAL: el que mide una barrera al frenar un proyectil.")]
    [Label("Efectos")]
    public List<AbilityEffect> Effects = new List<AbilityEffect>();

    [Section(AbilitySection.Visuals)]
    [Tooltip("Los VFX de la habilidad. Cada entrada dice CUÁNDO aparece (al lanzar, al golpear, " +
             "en el impacto), dónde, de qué tamaño y cuánto dura. Se ven en todas las pantallas.")]
    [UnityEngine.Serialization.FormerlySerializedAs("VisualsSequence")]
    [Label("VFX")]
    public List<AbilityVisual> Visuals = new List<AbilityVisual>();

    // ---------------------------------------------------------
    // INTERRUPCIÓN DEL ATAQUE BÁSICO
    //
    // El ataque principal se puede cortar con otra habilidad (un pícaro a mitad del
    // swing que se escapa con el dash). Solo las instancias marcadas IsInterruptible (la
    // del slot PrimaryAttack, y los pasos de combo que clona) miran el contador de
    // cancelación del ASC: cuando otra habilidad activa lo incrementa
    // (AbilitySystemComponent.CancelInterruptibleAbilities), el timing en curso se corta
    // sin pegar y sin llamar EndAbility — el "fin" lo va a mandar la habilidad nueva.
    // El cooldown ya pagado NO se devuelve: cancelar tiene ese costo.
    //
    // SE PUEDE MARCAR EN EL ASSET. El slot PrimaryAttack lo recibe siempre (lo fuerza
    // EquipCharacterClass) y sus pasos de combo lo heredan; cualquier otra habilidad que
    // quiera poder cortarse lo pide acá. Los lanzamientos (hacha, dagas) lo usan para
    // que se pueda cancelar el tiro antes de soltar, o encadenar otra cosa apenas soltó
    // sin esperar el remate de la animación.
    // ---------------------------------------------------------
    [Section(AbilitySection.Rules)]
    [Tooltip("Otra habilidad puede CORTAR esta a mitad de camino. Lo que ya pegó (o ya " +
             "salió) queda; lo que faltaba no pasa, y el cooldown ya pagado NO se " +
             "devuelve. El ataque principal lo trae puesto de fábrica.")]
    [Label("Se puede interrumpir")]
    public bool IsInterruptible;

    // Capas de física que puede golpear esta habilidad. Es un FILTRO DE FÍSICA:
    // la detección (Physics.OverlapSphere/Box/Capsule) solo considera colliders
    // en estas capas. La afiliación amigo/enemigo se resuelve aparte en código
    // (IsEnemyOf), no acá — por eso normalmente esto apunta a la capa de
    // personajes (jugadores + NPCs) y el filtro de equipo lo hace la habilidad.
    // La geometría de cada ataque (radio/largo/ángulo) la define cada habilidad
    // concreta con sus propios campos.
    [Section(AbilitySection.Targeting)]
    [Tooltip("Capas de física que puede alcanzar (normalmente 'Character', la 7). Es solo el " +
             "filtro de física: quién es amigo o enemigo lo decide el código.")]
    [Label("Capas alcanzables")]
    public LayerMask TargetLayer;

    // ¿Esta habilidad busca personajes con TargetLayer? Las que no (un buff propio, un
    // teletransporte, los envoltorios que delegan en otra) lo apagan y el Inspector
    // esconde el campo: un campo que no hace nada invita a tocarlo y esperar un resultado.
    public virtual bool UsesTargetLayer => true;

    // ¿Esta habilidad GOLPEA a alguien (llama a ApplyHitEffects)? Las que no solo aceptan
    // efectos "al activarse": el Inspector marca en rojo una entrada "al golpear" en un
    // buff propio, que nunca se aplicaría.
    public virtual bool UsesHitEffects => true;

    // Personaje dueño de esta instancia de habilidad. Lo asigna
    // Initialize() al otorgarla; el resto de la clase asume que nunca es
    // null salvo mientras la habilidad todavía no fue otorgada.
    protected AbilitySystemComponent OwnerASC;

    // Cuando esta instancia es un CLON (GrantAbility y los pasos de combo la
    // crean con Instantiate), apunta al asset-template original. Lo usa
    // GameplayAbilityRegistry para resolver el índice de red de un clon.
    // NonSerialized a propósito: es estado de runtime, no se guarda en el asset
    // ni se copia al clonar (se setea a mano justo después del Instantiate).
    [System.NonSerialized] public GameplayAbility SourceTemplate;

    // Qué habilidad "es" para la espera por enemigo de sus efectos (AbilityEffect.
    // PerTargetCooldown). Un combo se la pone a sus pasos: los 4 golpes de una ráfaga
    // comparten la espera. null = ella misma (su asset, así la espera sigue entre usos).
    [System.NonSerialized] public Object EffectCooldownKey;

    public Object CooldownKey => EffectCooldownKey != null ? EffectCooldownKey
                               : SourceTemplate != null ? (Object)SourceTemplate : this;

    // True si este código está corriendo en el servidor (o si no hay red,
    // ej. un NPC). Cada Activate() concreto debe empezar con
    // "if (!IsServer) return;" para que la lógica de juego (daño,
    // detección) tenga autoridad única en el servidor.
    protected bool IsServer
    {
        get
        {
            if (OwnerASC == null) return true;
            NetworkAbilitySystemComponent netASC =
                OwnerASC.GetComponent<NetworkAbilitySystemComponent>();
            if (netASC == null) return true; // sin red (singleplayer/NPC): siempre "servidor"
            return netASC.IsServerInitialized;
        }
    }

    // =========================================================
    // QUÉ ES EN EL KIT DE SU DUEÑO (lo marca PlayerController al equipar; no se guarda en el asset)
    // =========================================================

    // True en la instancia que ocupa el slot de la DEFINITIVA (R). La marca
    // PlayerController al equipar; el respawn la usa para no resetearle el cooldown.
    [System.NonSerialized] public bool IsUltimate;

    // True si esta instancia es el ATAQUE BÁSICO (clic izquierdo) de su dueño. La marca
    // PlayerController al equipar, igual que IsUltimate. (Hasta el 1 de octubre la usaba
    // el desarme; ahora el desarme va por el ActivationBlockedTags de cada habilidad de
    // arma. Se deja la marca por si otra regla necesita saber cuál es el básico.)
    [System.NonSerialized] public bool IsBasicAttack;

    // Con qué nombre avisa su fin (ver EndAbility). Lo pone GA_TagSwitch en sus variantes.
    [System.NonSerialized] public GameplayAbility ReportEndAs;

    // ¿Es el ataque básico del dueño? Ella misma, o el switch que la lanzó (las variantes del
    // clic izquierdo del Mago: los misiles, la bola, la flecha...).
    public bool CountsAsBasicAttack => IsBasicAttack || (ReportEndAs != null && ReportEndAs.IsBasicAttack);

    // =========================================================
    // CANALIZAR (cualquier habilidad; 19 de octubre de 2026, pedido de Gustavo)
    //
    // Antes de salir, la habilidad se sostiene un rato: la barra de canalizar del dueño, una
    // pose en bucle en todas las pantallas y, si la cortan, CUENTA COMO LANZADA (cobra costo
    // y cooldown). Dos formas:
    //   · Se lanza al terminar: canaliza "Dura" segundos y recién ahí corre la habilidad de
    //     siempre, con su animación (el Haz guía del Clérigo: 1 s y sale el rayo).
    //   · Continuo: la habilidad se repite cada "Hace efecto cada" mientras dura. El costo y
    //     el cooldown se cobran una vez, en la primera.
    // La corta lo mismo que la bloquea (sus "Bloqueada por tags": el aturdido siempre; el
    // silencio si es magia), morir, otra habilidad si es interrumpible y, si se marca,
    // recibir daño. Mientras dura, la mira del dueño le llega al servidor en vivo: lo que
    // sale, sale hacia donde apunta AHORA, no hacia donde apuntaba al apretar.
    //
    // Lo arranca el servidor: NetworkAbilitySystemComponent.ServerActivateAbility llama a
    // BeginCast en vez de Activate. No vale para las de mantener ni las de rueda (tienen su
    // propio ciclo con el botón) ni para las que ya canalizan solas (IChanneledAbility: el
    // molinete, el Aliento del dragón, el Golpe final).
    // =========================================================

    [Section(AbilitySection.Channel)]
    [Tooltip("Si se sostiene un rato antes de salir.\n\n" +
             "· Canaliza y se lanza al terminar: barra de canalizar y pose en bucle; al terminar, " +
             "sale la habilidad de siempre con su animación (la de la sección Animación es la de " +
             "soltar).\n" +
             "· Continuo: la habilidad se repite cada tanto mientras dura (un rayo que quema " +
             "mientras se sostiene). El costo y el cooldown, una vez.\n\n" +
             "Si la cortan (aturdido, el silencio si es magia, morir...), cuenta como lanzada: " +
             "cobra costo y cooldown.\n\n" +
             "En un LANZAMIENTO APUNTADO (mantener y soltar, como el Haz guía): mantener canaliza; " +
             "si se suelta antes de que se llene la barra, sale apenas se llena. No vale en las " +
             "de rueda ni en los demás mantenidos (el escudo).")]
    [Label("Canalizar")]
    public ECastMode CastMode = ECastMode.None;

    [ShowIf(nameof(CastMode), ECastMode.None, true)]
    [Tooltip("Cuánto dura la canalización.")]
    [Label("Dura (s)")]
    public float CastSeconds = 1f;

    [ShowIf(nameof(CastMode), ECastMode.Continuous)]
    [Tooltip("Cada cuánto se repite la habilidad mientras canaliza. La primera sale al empezar.")]
    [Label("Hace efecto cada (s)")]
    public float CastTickSeconds = 0.5f;

    [ShowIf(nameof(CastMode), ECastMode.None, true)]
    [Tooltip("Mientras canaliza no puede usar otras habilidades (salvo las marcadas 'Usable " +
             "canalizando'). Moverse, sí.")]
    [Label("Bloquea las demás habilidades")]
    public bool CastBlocksOthers = true;

    [ShowIf(nameof(CastMode), ECastMode.None, true)]
    [Tooltip("Cualquier golpe que reciba la corta (y cuenta como lanzada).")]
    [Label("Se corta al recibir daño")]
    public bool CastBreaksOnDamage = false;

    [ShowIf(nameof(CastMode), ECastMode.None, true)]
    [Tooltip("OPCIONAL: el gesto de empezar, una vez, antes del bucle.")]
    [Label("Clip: empezar a canalizar")]
    public AnimationClip CastStartAnimation;

    [ShowIf(nameof(CastMode), ECastMode.None, true)]
    [Tooltip("La pose EN BUCLE mientras canaliza. Vacío = sin pose (solo la barra). Usa las " +
             "ranuras del mantenido en el Animator, como el escudo: no hay estados que crear.")]
    [Label("Clip: canalizando (bucle)")]
    public AnimationClip CastLoopAnimation;

    [ShowIf(nameof(CastMode), ECastMode.Continuous)]
    [Tooltip("OPCIONAL: el remate al terminar de canalizar. (Al lanzar al terminar no hace falta: " +
             "el de soltar es el clip de la sección Animación.)")]
    [Label("Clip: soltar")]
    public AnimationClip CastReleaseAnimation;

    // =========================================================
    // CARGAS  (varios usos antes de tener que esperar la recarga)
    //
    // Vive acá y no en cada habilidad porque el esquema es SIEMPRE el mismo y estaba
    // duplicado idéntico en el dash, la copia exacta y la intercepción heroica. Ahora
    // cualquier GA tiene cargas con solo subir MaxCharges — incluido un GA_SelfBuff
    // como el Castigo divino del Paladín, que era el caso que faltaba.
    //
    // CÓMO SE BLOQUEA (la parte no obvia): el estado de cargas es SERVER-ONLY, porque
    // Activate() es server-only y la instancia del dueño nunca lo tocaría. Si el gate
    // dependiera del contador, la predicción del dueño no coincidiría con el servidor.
    // Por eso el bloqueo de cara al jugador se hace con el TAG del cooldown, que sí se
    // sincroniza (NetTags): el CooldownEffect se aplica únicamente al gastar la ÚLTIMA
    // carga, así que mientras queden cargas no hay tag y el jugador puede encadenarlas.
    // Al recuperar una carga se limpia el tag a mano, para no esperar a que el GE
    // expire solo (si no, quedaría un desfase entre "tengo carga" y "puedo usarla").
    //
    // COOLDOWN = RECARGA POR CARGA: un solo valor (ResolveCooldownDuration) define
    // cuánto dura el cooldown Y cuánto tarda en volver cada carga.
    // =========================================================

    [Section(AbilitySection.CostCooldown)]
    [Tooltip("Usos disponibles antes de tener que esperar la recarga. 1 (o 0) = sin sistema de " +
             "cargas: cooldown normal en cada uso, como cualquier habilidad.\n\n" +
             "Con 2 o más, el cooldown se aplica solo al gastar la ÚLTIMA carga, y cada carga " +
             "tarda en volver lo que dure ese mismo cooldown.")]
    [Label("Cargas")]
    public int MaxCharges = 1;

    // =========================================================
    // SE ACTIVA SOLA (18 de noviembre de 2026, los eventos de juego)
    //
    // Además de (o en vez de) con su botón, la habilidad sale sola cuando a su dueño le pasa algo: al recibir daño, al
    // bloquear un golpe, al matar... (ver GameplayEvents.cs). Valen su costo, su cooldown (que es la "espera") y lo que
    // la bloquea, como siempre. Si el evento tiene "otro" (el que te pegó, el que mataste) y "Apunta al otro" está
    // prendido, la habilidad apunta a él: un contraataque le pega a quien te pegó. Lo resuelve
    // AbilitySystemComponent.SendGameplayEvent → NetworkAbilitySystemComponent.ServerActivateTriggered, en el servidor.
    // Puede no tener botón (InputSlot None en la clase). Mejor si es instantánea: no espera a que termine lo que hacías.
    // =========================================================

    [Section(AbilitySection.Trigger, startCollapsed: true)]
    [Tooltip("El evento que la activa sola. (ninguno) = solo con su botón.")]
    [Label("Se activa sola cuando")]
    public EGameplayEvent TriggerEvent = EGameplayEvent.None;

    [Section(AbilitySection.Trigger)]
    [ShowIf(nameof(TriggerEvent), EGameplayEvent.None, true)]
    [Tooltip("Solo si su dueño tiene este tag (una postura, un buff). None = siempre.")]
    [Label("Solo si quien la tiene tiene")]
    public EGameplayTag TriggerOnlyIfOwnerHas = EGameplayTag.None;

    [Section(AbilitySection.Trigger)]
    [ShowIf(nameof(TriggerEvent), EGameplayEvent.None, true)]
    [Tooltip("Solo si el daño, la curación o lo bloqueado llega a esto. 0 = cualquiera.")]
    [Label("Mínimo (vida; 0 = cualquiera)")]
    public float TriggerMinMagnitude = 0f;

    [Section(AbilitySection.Trigger)]
    [ShowIf(nameof(TriggerEvent), EGameplayEvent.None, true)]
    [Tooltip("Si también la activan los ticks (un veneno, una zona). Apagado: solo los golpes directos.")]
    [Label("También con ticks")]
    public bool TriggerIncludePeriodic = false;

    [Section(AbilitySection.Trigger)]
    [ShowIf(nameof(TriggerEvent), EGameplayEvent.None, true)]
    [Tooltip("Probabilidad de que salga cada vez que pasa el evento (1 = siempre).")]
    [Label("Probabilidad (0 a 1)")]
    [Range(0f, 1f)] public float TriggerChance = 1f;

    [Section(AbilitySection.Trigger)]
    [ShowIf(nameof(TriggerEvent), EGameplayEvent.None, true)]
    [Tooltip("Si el evento tiene otro (el que te pegó, el que mataste...), la habilidad apunta a él. Apagado: apunta a donde " +
             "miraba el dueño.")]
    [Label("Apunta al otro")]
    public bool TriggerAimsAtOther = true;

    // Mientras corre una activación SOLA: qué evento la disparó (con el otro y el cuánto). La habilidad lo puede leer en
    // su Activate() (WasTriggered = true). No se guarda en el asset.
    [System.NonSerialized] public bool WasTriggered;
    [System.NonSerialized] public GameplayEventData TriggeredBy;
}
