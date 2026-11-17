using UnityEngine;
using System.Collections.Generic;
using System;

// ============================================================
// AbilitySystemComponent
//
// Núcleo del sistema de habilidades (GAS) de un personaje: guarda
// sus atributos (vida, ataque, etc.), sus tags de estado, los
// efectos activos y las habilidades otorgadas, y expone toda la
// lógica para aplicarles daño/curación/buffs. Vive en el mismo
// GameObject que NetworkAbilitySystemComponent, que se suscribe a
// sus eventos para replicar los cambios a la red — este script en
// sí no sabe nada de FishNet.
//
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 17 de noviembre de 2026). En este archivo: los
// campos del Inspector, los eventos, el estado, Unity, los tags y la afiliación. El resto, en la
// carpeta GAS/AbilitySystemComponent/:
//   · Attributes       los atributos: cargarlos, leerlos, escribirlos, recalcularlos
//   · Effects          aplicar un efecto, empuje, control, los efectos con duración y su paso por frame
//   · Damage           el camino del daño (ExecuteInstantEffect), la magnitud, robo de vida y ejecutar
//   · DamageModifiers  defensas, daño saliente (críticos), entrante (bloqueo) y por la espalda
//   · Cleanse          quitar efectos, limpiar/purgar e inmunidades
//   · Abilities        habilidades otorgadas, cooldowns y ecos
//   · Life             vida, muerte, experiencia y nivel
//   · CombatLog        quién le pegó y lo curó, el daño recibido y la cadena del depurador
// Un campo nuevo del Inspector va en ESTE archivo: Unity los guarda en el orden en que están
// escritos, y repartidos entre archivos ese orden no está garantizado.
// El archivo se llama EXACTAMENTE como la clase: con la clase repartida, Unity solo le asocia el
// componente al archivo de nombre idéntico. Hasta el 17 de noviembre se llamaba "Ability System
// Component.cs" (con espacios) y, al repartirla, los prefabs vieron el script como perdido. Se
// renombró conservando su .meta (el mismo GUID): no volver a cambiarle el nombre.
// ============================================================
public partial class AbilitySystemComponent : MonoBehaviour
{
    // =========================================================
    // CONFIGURACIÓN
    // =========================================================

    [Header("Configuración de Rol")]
    // Stats iniciales (vida, maná, etc.) que se cargan al arrancar o al
    // llamar InitializeAttributes(). Normalmente lo asigna la clase actual.
    [Label("Stats base (ASDef)")]
    public AttributeSetDefinition CharacterRoleDefinition;

    [Header("Clase y Progresión")]
    // Clase equipada actualmente; se usa para saber cuánto suben los stats
    // al subir de nivel (StatGrowthPerLevel).
    [Label("Clase actual")]
    public CharacterClassDefinition CurrentClass;

    [Header("Multijugador y Afiliación")]
    // Equipo al que pertenece. 0 = neutral/hostil para todos (ver
    // IsEnemyOf). NetworkGameManager le asigna un valor único por jugador.
    [Label("Equipo")]
    public int TeamID = 0;

    // Nivel máximo alcanzable; al llegarlo, GainExperience deja de subir
    // de nivel y dispara OnMaxLevelReached.
    [Label("Nivel máximo")]
    public int MaxLevel = 3;

    // =========================================================
    // EVENTOS — NetworkAbilitySystemComponent se suscribe a estos en
    // Awake() para saber cuándo replicar algo a la red
    // =========================================================

    // Se dispara cada vez que un atributo cambia de valor (ej: al recibir
    // daño). Parámetros: qué atributo y su nuevo valor.
    public event Action<EAttributeType, float> OnAttributeChangedCallback;

    // Se disparan al agregar/quitar un tag de estado (ej: Stunned).
    public event Action<EGameplayTag> OnTagAddedCallback;
    public event Action<EGameplayTag> OnTagRemovedCallback;

    // Se disparan al agregar/quitar un efecto CON DURACIÓN (buff/debuff) —
    // alimentan la barra de efectos activos en red. El primero manda
    // también la duración total.
    public event Action<GameplayEffect, float> OnActiveEffectAddedCallback;
    public event Action<GameplayEffect> OnActiveEffectRemovedCallback;

    // Eventos de juego: progresión, muerte y revivir.
    public event Action OnLevelUp;
    public event Action OnDeath;
    public event Action OnRevive;
    public event Action OnMaxLevelReached;

    // Se dispara cuando ESTE personaje le REPARTE un golpe de daño a alguien.
    // Parámetros: la víctima y CUÁNTO daño entró de verdad (ya pasado por el
    // bloqueo, las defensas y el escudo — o sea, lo que realmente le bajó de la
    // vida, siempre positivo). Solo en golpes directos (no ticks de DoT), y con
    // autoridad de servidor (ver ExecuteInstantEffect).
    //
    // La cantidad viaja en el evento porque hay pasivas que curan/escalan con el
    // daño hecho (Aura de protección del Paladín), y recalcularla afuera sería
    // imposible: el atacante no conoce las defensas ni el escudo de la víctima.
    // Las pasivas que no la necesitan (Cuchillas ilusorias) simplemente la ignoran.
    public event Action<AbilitySystemComponent, float> OnDealtDamage;

    // Permite que ExecuteInstantEffect (que corre sobre la VÍCTIMA) dispare el
    // evento en el ATACANTE. Los eventos solo se pueden invocar desde su clase.
    public void NotifyDealtDamage(AbilitySystemComponent victim, float damageDealt)
        => OnDealtDamage?.Invoke(victim, damageDealt);

    // Le avisa a la capa de red que hubo un golpe, para que cada uno vea lo suyo: el que
    // pegó, la X en su retícula; el que recibió, el arco rojo del lado del golpe. Si no
    // hay red (una escena de pruebas suelta) no pasa nada y el juego sigue igual.
    private void ReportCombatFeedback(AbilitySystemComponent attacker, bool critical)
    {
        NetworkAbilitySystemComponent netAsc = GetComponent<NetworkAbilitySystemComponent>();
        if (netAsc != null) netAsc.ServerReportDamage(attacker, critical);
    }

    // El número flotante sobre ESTE personaje: rojo si le pegaron, amarillo si fue
    // crítico, verde si lo curaron (ver NetworkAbilitySystemComponent → NÚMEROS
    // FLOTANTES). Server-side. Las curaciones que escriben la vida DIRECTO, sin pasar
    // por un efecto (el aura del Paladín, los botiquines), tienen que llamarlo ellas
    // con lo que curaron de verdad; lo que pasa por ExecuteInstantEffect ya lo avisa solo.
    public void ShowCombatNumber(float amount, ECombatNumber kind, bool shielded = false)
    {
        if (amount <= 0f) return;
        NetworkAbilitySystemComponent netAsc = GetComponent<NetworkAbilitySystemComponent>();
        if (netAsc != null) netAsc.ServerShowCombatNumber(amount, kind, shielded);
    }

    // Se dispara cuando ESTE personaje RECIBE un golpe de daño directo (el
    // parámetro es el atacante). Mismo criterio que OnDealtDamage: solo golpes
    // directos (no ticks de DoT), server-side. Lo usa la Copia exacta del
    // Ilusionista para reaccionar a quien la golpea (cegar + herir + explotar).
    public event Action<AbilitySystemComponent> OnTookDamage;
    public void NotifyTookDamage(AbilitySystemComponent attacker) => OnTookDamage?.Invoke(attacker);

    // Se dispara cuando ESTE personaje MATA a alguien (el parámetro es la víctima): el que
    // le dio el golpe final, o el dueño del veneno que lo terminó (LastAttacker). Cualquier
    // cosa con ASC cuenta: jugadores, NPCs, tótems. Server-side; lo dispara la víctima en
    // Die(). Lo usa la pasiva del Asesino que reinicia la Emboscada al matar.
    public event Action<AbilitySystemComponent> OnKilledTarget;

    // --- Lo que carga la definitiva según el ROL (ver EClassRole) ---
    //
    // A diferencia de los dos de arriba, estos SÍ cuentan los ticks periódicos:
    // aguantar un veneno enemigo también es aguantar, y un aura que cura también es
    // curar. Server-side, igual que todo el pipeline.

    // ESTE personaje aguantó daño de alguien más. La cantidad es lo que pasó el
    // bloqueo y las defensas, ANTES del escudo: para un tanque, que el escudo frene
    // un golpe también es aguantarlo.
    public event Action<float> OnDamageEndured;

    // ESTE personaje le curó vida a un ALIADO (nunca a sí mismo). La cantidad es la
    // vida que de verdad subió: lo que sobraba por encima de la máxima no cuenta, así
    // que curar a alguien lleno no carga nada.
    public event Action<AbilitySystemComponent, float> OnHealedAlly;
    public void NotifyHealedAlly(AbilitySystemComponent ally, float healed) => OnHealedAlly?.Invoke(ally, healed);

    // ESTE personaje se curó a SÍ MISMO con un efecto propio (la curación del Clérigo sin
    // nadie en la mira). Va aparte de OnHealedAlly a propósito: curarse no carga la
    // definitiva de soporte, pero una pasiva como la Bendición sí quiere enterarse. El
    // robo de vida y los botiquines escriben la vida directo y no pasan por acá.
    public event Action<float> OnHealedSelf;

    // ESTE personaje se curó con su ROBO DE VIDA (la vida que de verdad subió). Server-side. Solo para la tabla.
    public event Action<float> OnLifeStolen;

    // ESTE personaje le HIZO daño a alguien (13 de noviembre de 2026, para la columna "Daño" de la tabla). A
    // diferencia de OnDealtDamage, cuenta también los ticks periódicos (un veneno tuyo es daño tuyo) y lo que
    // frenó un escudo. Si el golpe vino de una invocación (tótem, compañero), el crédito es de su dueño.
    // Server-side.
    public event Action<AbilitySystemComponent, float> OnDamageDone;
    public void NotifyDamageDone(AbilitySystemComponent victim, float amount) => OnDamageDone?.Invoke(victim, amount);

    // Cuánta carga tiene la definitiva: 0 = recién usada, con el cooldown entero por
    // delante; 1 = lista. Todas las definitivas comparten el tag de cooldown
    // Ability_Cooldown_Ultimate, así que se lee de ahí. Sin cooldown puesto, está lista.
    public float GetUltimateCharge()
    {
        foreach (var e in ActiveEffects)
        {
            if (e.IsExpired || e.TotalDuration <= 0f) continue;
            if (!e.Definition.GrantedTags.Contains(EGameplayTag.Ability_Cooldown_Ultimate)) continue;
            return Mathf.Clamp01(1f - e.DurationRemaining / e.TotalDuration);
        }
        return 1f;
    }

    // =========================================================
    // ALMACENAMIENTO INTERNO
    // =========================================================

    // Valor actual de cada atributo del personaje (vida, ataque, etc.).
    protected Dictionary<EAttributeType, AttributeValue> Attributes = new Dictionary<EAttributeType, AttributeValue>();

    // Efectos con duración actualmente aplicados (buffs, debuffs,
    // cooldowns). Solo existe poblado de verdad en la copia servidor —
    // ver notas en NetworkAbilitySystemComponent.
    protected List<ActiveGameplayEffect> ActiveEffects = new List<ActiveGameplayEffect>();

    // Copia de trabajo que reutiliza ProcessActiveEffects para recorrer los efectos
    // sin iterar la lista viva (un tick puede modificarla). Es un campo y no una
    // variable local para no alocar una lista por frame y por personaje.
    private readonly List<ActiveGameplayEffect> _tickBuffer = new List<ActiveGameplayEffect>();

    // Tags de estado activos, con el CONTEO de cuántas fuentes los otorgan.
    // El tag existe mientras su conteo sea > 0.
    //
    // Es un conteo y no un simple set porque varios efectos pueden otorgar el
    // MISMO tag a la vez (ej: 3 Heridas apiladas otorgan Status_Wound 3 veces, o
    // un buff y un tótem que comparten Status_Buff_Damage). Con un set, al expirar
    // el PRIMERO se borraba el tag y los demás quedaban sin él aunque siguieran
    // activos. Ver AddTag/RemoveTag.
    protected Dictionary<EGameplayTag, int> GameplayTags = new Dictionary<EGameplayTag, int>();

    // Instancias de habilidades otorgadas a este personaje (una por cada
    // GameplayAbility de su clase). Modificar esta lista a mano rompe la
    // relación con los slots de PlayerController — usar GrantAbility().
    [Label("Habilidades dadas")]
    public List<GameplayAbility> GrantedAbilities = new List<GameplayAbility>();

    // Último ASC que le hizo daño a este personaje. Lo setea ExecuteInstantEffect
    // en cada golpe que baja la vida, y se limpia al revivir. Sirve para atribuir
    // la baja: al morir, NetworkAbilitySystemComponent.HandleDeath (server) le da
    // EXP a este atacante. Se guarda en la copia del SERVIDOR (ahí se aplica el
    // daño); en los clientes queda en null, que es lo correcto.
    [HideInInspector] public AbilitySystemComponent LastAttacker;

    // =========================================================
    // UNITY
    // =========================================================

    // Carga los atributos iniciales al crear el objeto.
    void Awake()
    {
        Strikeable = GetComponent<StrikeTarget>();
        InitializeAttributes();
    }

    // Hace avanzar el cronómetro de todos los efectos activos cada frame.
    void Update()
    {
        ProcessActiveEffects(Time.deltaTime);
    }

    // =========================================================
    // TAGS DE ESTADO
    // =========================================================

    // Consulta si el personaje tiene un tag activo ahora mismo (conteo > 0).
    public bool HasTag(EGameplayTag tag) => GameplayTags.ContainsKey(tag);

    // Cuántas fuentes están otorgando este tag (0 si no lo tiene). Sirve, por
    // ejemplo, para saber cuántas Heridas apiladas hay.
    public int GetTagCount(EGameplayTag tag) => GameplayTags.TryGetValue(tag, out int count) ? count : 0;

    // Los tags que tiene ahora (para diagnósticos: el registro del desatascarse).
    public IEnumerable<EGameplayTag> ActiveTags => GameplayTags.Keys;

    // Suma una fuente del tag. Solo notifica OnTagAddedCallback cuando pasa de
    // 0 a 1 (recién ahí el personaje "gana" el tag de verdad).
    public void AddTag(EGameplayTag tag)
    {
        if (GameplayTags.TryGetValue(tag, out int count))
        {
            GameplayTags[tag] = count + 1;
            return;
        }

        GameplayTags[tag] = 1;
        OnTagAddedCallback?.Invoke(tag);

        // Vuelo libre: enraizarlo lo baja (cae lento, ver PlayerController).
        if (tag == EGameplayTag.State_Rooted && HasTag(EGameplayTag.Status_Flying))
            RemoveEffectsWithTag(EGameplayTag.Status_Flying);
    }

    // Resta una fuente del tag. Solo lo quita (y notifica) cuando el conteo llega
    // a 0: mientras otro efecto lo siga otorgando, el tag se mantiene.
    public void RemoveTag(EGameplayTag tag)
    {
        if (!GameplayTags.TryGetValue(tag, out int count)) return;

        if (count > 1)
        {
            GameplayTags[tag] = count - 1;
            return;
        }

        GameplayTags.Remove(tag);
        OnTagRemovedCallback?.Invoke(tag);
    }

    // =========================================================
    // AFILIACIÓN / EQUIPOS
    // =========================================================

    // Algo del mapa que se golpea pero no es un personaje (la campana): ver StrikeTarget. No es enemigo
    // ni aliado de nadie, y los golpes le avisan en vez de aplicarle efectos.
    public StrikeTarget Strikeable { get; private set; }

    // True si los ENEMIGOS no deberían verlo: invisible y sin revelar. El Faro de
    // esperanza del Clérigo de la Luz revela (State_AlwaysVisible) sin sacarle la
    // invisibilidad: sus aliados lo siguen viendo fantasma y, al salir del aura, vuelve a
    // desaparecer solo. Todo lo que oculta a un invisible (modelo, barra de vida, números,
    // los NPCs que lo ignoran) pregunta ESTO y no el tag a secas.

    public bool IsHiddenFromEnemies =>
        HasTag(EGameplayTag.Status_Invisible) && !HasTag(EGameplayTag.State_AlwaysVisible);

    // True si target es un objetivo válido para atacar: TeamID distinto
    // (o cualquiera de los dos en 0 = neutral, siempre hostil).
    public bool IsEnemyOf(AbilitySystemComponent target)
    {
        if (target == null || target == this) return false;
        if (Strikeable != null || target.Strikeable != null) return false;   // algo del mapa (StrikeTarget)
        if (TeamID == 0 || target.TeamID == 0) return true;
        return TeamID != target.TeamID;
    }

    // True si target es aliado (mismo TeamID, ninguno neutral).
    // includeSelf controla si uno mismo cuenta como aliado.
    public bool IsAllyOf(AbilitySystemComponent target, bool includeSelf = true)
    {
        if (target == null) return false;
        if (target == this) return includeSelf;
        if (Strikeable != null || target.Strikeable != null) return false;   // algo del mapa (StrikeTarget)
        if (TeamID == 0 || target.TeamID == 0) return false;
        return TeamID == target.TeamID;
    }
}
