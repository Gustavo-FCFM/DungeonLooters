using UnityEngine;
using System.Collections.Generic;

// ============================================================
// GameplayEffect
//
// Asset que describe UN efecto que se le puede aplicar a un
// personaje: daño/curación instantánea, un buff temporal, un
// debuff periódico, un cooldown, etc. Es la "receta" — el estado
// runtime de una aplicación concreta vive en ActiveGameplayEffect.
// AbilitySystemComponent.ApplyGameplayEffect() es quien lo procesa.
// ============================================================
[CreateAssetMenu(fileName = "GE_Base", menuName = "GAS/Gameplay Effect")]
public class GameplayEffect : ScriptableObject
{
    // Cómo se comporta este efecto si se vuelve a aplicar mientras ya
    // está activo.
    public enum EStackingType
    {
        [InspectorName("Se refresca (uno solo)")] Refresh,  // Reinicia la duración (Veneno + Veneno = 1 Veneno con el tiempo reiniciado)
        [InspectorName("Se acumula")]             Stack,    // Se acumula (Veneno + Veneno = 2 Venenos haciendo daño)
        [InspectorName("Se reemplaza")]           Override  // El nuevo reemplaza al viejo (ej: un buff que cambia de nivel)
    }

    // Hacia dónde mueve el desplazamiento de este efecto (ver KnockbackDistance).
    public enum EKnockbackDirection
    {
        [InspectorName("Repeler (lejos)")]           AwayFromSource,  // Repeler: lejos de quien lo aplicó (el dash del Monje)
        [InspectorName("Atraer (hacia él)")]         TowardSource,    // Atraer: hacia quien lo aplicó (un gancho); frena a PullStopDistance
        [InspectorName("Hacia donde mira el que lo aplica")] SourceForward    // Hacia donde mira quien lo aplicó (una patada en línea)
    }

    // Qué efectos quita este al aplicarse (ver "Quitar efectos e inmunidad").
    public enum ERemoveKind
    {
        [InspectorName("Nada")]                           Nothing = 0,
        [InspectorName("Los debuffs (limpiar)")]          Debuffs = 1,
        [InspectorName("Los buffs (purgar)")]             Buffs   = 2,
        [InspectorName("Buffs y debuffs")]                Both    = 3,
    }

    // Lo que hace que un efecto con duración se vaya SOLO (ver "Se va solo", en "Quitar efectos e inmunidad").
    // Se pueden marcar varios. Solo se amplía al final (se guarda como número).
    [System.Flags]
    public enum ERemoveOnEvent
    {
        [InspectorName("Nunca")]                    Nothing    = 0,
        [InspectorName("Al recibir daño")]          TakeDamage = 1,
        [InspectorName("Al hacer daño")]            DealDamage = 2,
        [InspectorName("Al moverse")]               Move       = 4,
        [InspectorName("Al usar una habilidad")]    UseAbility = 8,
    }

    // Si es Buff/Debuff/Hidden — controla si aparece en la barra de
    // efectos activos y de qué color (ver UI_EffectSlot).
    public enum EEffectType
    {
        [InspectorName("Buff (verde)")]   Buff,   // Verde, beneficioso
        [InspectorName("Debuff (rojo)")]  Debuff, // Rojo, dañino
        [InspectorName("Oculto")]         Hidden  // No se muestra en UI (ej: el propio cooldown de una habilidad)
    }

    // Si es 0, el efecto se aplica una sola vez y desaparece (daño,
    // curación). Si es mayor a 0, queda activo ese tiempo (buffs,
    // debuffs, cooldowns).
    [Section(EffectSection.Duration)]
    [ShowIf(nameof(Infinite), false)]
    [Tooltip("0 = instantáneo (daño, curación: se aplica y listo). > 0 = dura esos segundos " +
             "(buffs, debuffs, cooldowns).")]
    [Label("Duración (s, 0 = instantáneo)")]
    public float Duration = 0f;

    // DURACIÓN INFINITA (18 de noviembre de 2026, de las "Mejoras del GAS"). Antes se fingía con un número grande
    // (9999 s), que se veía como un reloj que nunca bajaba. Un infinito no vence ni muestra tiempo; se va como
    // cualquiera: lo quitan (una habilidad, una limpieza, "Se va solo"), morir (si es buff o debuff) o cambiar de clase.
    [Tooltip("No vence nunca y no muestra tiempo: dura hasta que lo quiten (una habilidad, una limpieza, \"Se va solo\"), " +
             "hasta morir (si es buff o debuff) o hasta cambiar de clase. Para una pasiva, un estado del modo de juego, " +
             "lo que dura mientras estás en una zona...")]
    [Label("Dura para siempre (hasta que lo quiten)")]
    public bool Infinite = false;

    // DE AURA (18 de noviembre de 2026): un efecto que un aura o una zona re-aplica cada pocos décimos (dura ~1 s y
    // se refresca mientras estás dentro, y se cae solo al salir). La lógica no cambia; solo que la barra de buffs no
    // dibuja un reloj que se llena y se vacía sin parar, y la red no reenvía cada refresco (ver
    // NetworkAbilitySystemComponent.SyncActiveEffect).
    [ShowIf(nameof(Duration), ShowIfAttribute.Positive)]
    [Tooltip("Para lo que un aura o una zona renueva todo el tiempo (las auras del Paladín, la voz del Comandante, una " +
             "trampa que ralentiza mientras la pisas): sigue durando lo mismo y cayéndose solo al salir, pero en la barra " +
             "se ve sin reloj y los refrescos no se mandan por la red.")]
    [Label("Es de aura: no mostrar tiempo")]
    public bool ShowsNoTime = false;

    // Sin reloj en la barra: los infinitos y los de aura.
    public bool HidesTime => Infinite || ShowsNoTime;

    // ¿Es un efecto CON duración (finita o infinita)? Lo que antes preguntaba "Duration > 0" pregunta esto.
    public bool HasDuration => Infinite || Duration > 0f;

    // La duración a aplicar: la del asset, o infinita.
    public float EffectiveDuration => Infinite ? float.PositiveInfinity : Duration;

    // Para [ShowIf]: "tiene duración" = Duration > 0 O Infinite (la barra es "o", ver SectionedInspector).
    public const string HasDurationFields = nameof(Duration) + "|" + nameof(Infinite);

    // Si es mayor a 0, mientras el efecto está activo se re-ejecuta cada
    // tantos segundos (ej: veneno que daña cada 2s).
    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("Cada cuántos segundos se vuelve a aplicar mientras dura (un veneno que daña cada " +
             "2 s). 0 = no es periódico.")]
    [Label("Período (s)")]
    public float Period = 0f;

    [Tooltip("Qué pasa si se vuelve a aplicar mientras ya está activo.\n\n" +
             "· Refresh: reinicia su duración (uno solo).\n" +
             "· Stack: se acumula (dos venenos = el doble).\n" +
             "· Override: el nuevo reemplaza al viejo.")]
    [Label("Al reaplicarse")]
    public EStackingType StackingPolicy = EStackingType.Stack;

    [ShowIf(nameof(StackingPolicy), EStackingType.Stack)]
    [Tooltip("Máximo de acumulaciones. 0 = sin límite. Al llegar al tope, aplicarlo de nuevo " +
             "refresca la acumulación que esté por expirar en vez de agregar otra (salvo que uses " +
             "OnMaxStacksEffect).")]
    [Label("Acumulaciones máximas (0 = sin límite)")]
    public int MaxStacks = 0;

    [ShowIf(nameof(StackingPolicy), EStackingType.Stack)]
    [ShowIf(nameof(MaxStacks), ShowIfAttribute.Positive)]
    [Tooltip("Al llegar al tope de acumulaciones, en vez de refrescarlas se CONSUMEN todas y se " +
             "aplica este efecto al objetivo (la 'explosión' de las Heridas del Ilusionista). " +
             "None = el comportamiento normal de tope.")]
    [Label("Efecto al tope")]
    public GameplayEffect OnMaxStacksEffect;

    [Section(EffectSection.Group, startCollapsed: true)]
    [Tooltip("Efectos con el MISMO grupo (≠ None) se excluyen: solo vive el de mayor Priority a la vez. Ej: el buff normal del tótem y su versión potenciada comparten grupo, así no se acumulan.")]
    [Label("Grupo")]
    public EGameplayTag EffectGroup = EGameplayTag.None;

    [ShowIf(nameof(EffectGroup), EGameplayTag.None, true)]
    [Tooltip("Dentro de un EffectGroup, mayor Priority gana. Aplicar uno de Priority MENOR a uno ya activo del grupo no hace nada; uno de Priority MAYOR reemplaza a los inferiores.")]
    [Label("Prioridad (mayor gana)")]
    public int Priority = 0;

    [ShowIf(nameof(EffectGroup), EGameplayTag.None, true)]
    [Tooltip("Dentro de su EffectGroup, el ÚLTIMO en aplicarse reemplaza a los demás, sin " +
             "importar Priority. Es para estados excluyentes que se alternan, como las posturas " +
             "del Guerrero: entrar a la ofensiva saca la defensiva en el mismo instante, así " +
             "nunca se tienen las dos ventajas a la vez.")]
    [Label("Reemplaza al grupo")]
    public bool ReplacesGroup = false;

    [Section(EffectSection.General)]
    [Tooltip("Ícono en la barra de buffs/debuffs (no hace falta si es Hidden).")]
    [Label("Ícono")]
    public Sprite Icon;

    [Tooltip("Buff (verde) o Debuff (rojo) en la barra de efectos; Hidden no se muestra (un " +
             "cooldown, un daño instantáneo).")]
    [Label("Tipo")]
    public EEffectType EffectType;

    // Qué atributos cambia este efecto y cuánto (ver Modifier.cs).
    [Section(EffectSection.Modifiers)]
    [Tooltip("Qué atributos cambia y cuánto. Cada entrada: atributo · cómo (sumar, multiplicar, " +
             "reemplazar) · cuánto, y si escala con un stat del que lo aplica o con la vida del objetivo.")]
    [Label("Modificadores")]
    public List<Modifier> Modifiers = new List<Modifier>();

    [Section(EffectSection.Control)]
    [Tooltip("Marca este efecto como CONTROL: su duración se acorta (o se alarga) con la " +
             "Resistencia al control del objetivo — ver EAttributeType.CCResistance.\n\n" +
             "No hace falta marcarlo si el efecto otorga State_Stunned, State_Rooted o " +
             "State_Silenced: esos ya cuentan como control solos. Es para el CC que no pasa por " +
             "esos tags (cegueras, miedos, ralentizaciones fuertes) y que igual querés que la " +
             "resistencia recorte.")]
    [Label("Es control")]
    public bool IsCrowdControl = false;

    // ¿Sube la vida? Un modificador sumado sobre Health con algo positivo (fijo o escalado).
    // Lo usa la habilidad para sus VFX "al curar" (ver GameplayAbility.ApplyHitEffects).
    public bool HealsHealth
    {
        get
        {
            if (Modifiers == null) return false;
            foreach (Modifier m in Modifiers)
            {
                if (m.Attribute != EAttributeType.Health || m.Type != Modifier.EModificationType.Add) continue;
                if (m.Magnitude > 0f ||
                    (m.UseAttributeScaling && m.AttributeCoefficient > 0f) ||
                    (m.UseTargetHealthScaling && m.TargetHealthCoefficient > 0f))
                    return true;
            }
            return false;
        }
    }

    // Tags de control "duros" del core (Group_HardControl en GameplayTagTree: aturdido, enraizado, silencio): un efecto
    // que otorgue cualquiera de estos cuenta como CC aunque no tenga la casilla marcada. Existe para que los GEs que YA
    // estaban (GE_Stun y compañía) obedezcan la resistencia sin tener que editarlos uno por uno. Desde el 18 de noviembre
    // de 2026 es el grupo del árbol de tags (antes, una lista aparte con los mismos tres).

    // True si a este efecto le corresponde que la Resistencia al control le toque la
    // duración. Lo consulta AbilitySystemComponent.ApplyGameplayEffect.
    public bool CountsAsCrowdControl
    {
        get
        {
            if (IsCrowdControl) return true;
            if (GrantedTags == null) return false;

            // Un BUFF que da un tag de control se lo pone uno mismo (la Tumba helada del Mago:
            // quieto y a salvo): no es un control que le hacen, la resistencia no lo recorta.
            if (EffectType == EEffectType.Buff) return false;

            foreach (EGameplayTag granted in GrantedTags)
                if (GameplayTagTree.Matches(granted, EGameplayTag.Group_HardControl)) return true;

            return false;
        }
    }

    [Tooltip("Metros que mueve al objetivo en el instante en que recibe el efecto. 0 = no lo " +
             "mueve. Es CONTROL: no le hace nada a un Imparable (Status_Unstoppable) y la " +
             "Resistencia al control recorta la distancia. El escudo no lo frena (el escudo solo " +
             "mitiga daño). Solo pasa al APLICARSE, no en cada tick.")]
    [Label("Distancia (m)")]
    public float KnockbackDistance = 0f;

    [ShowIf(nameof(KnockbackDistance), ShowIfAttribute.Positive)]
    [Tooltip("Repeler (lejos de quien lo aplicó), atraer (hacia él) o hacia donde mira quien lo aplicó.")]
    [Label("Dirección")]
    public EKnockbackDirection KnockbackDirection = EKnockbackDirection.AwayFromSource;

    [ShowIf(nameof(KnockbackDistance), ShowIfAttribute.Positive)]
    [Tooltip("Segundos que tarda el desplazamiento. Arranca rápido y frena al final.")]
    [Label("Duración (s)")]
    public float KnockbackDuration = 0.3f;

    [ShowIf(nameof(KnockbackDistance), ShowIfAttribute.Positive)]
    [Tooltip("Velocidad hacia arriba al empujar (lo levanta un poco del piso). 0 = a ras del piso.")]
    [Label("Impulso hacia arriba (m/s)")]
    public float KnockbackUpVelocity = 0f;

    [ShowIf(nameof(KnockbackDistance), ShowIfAttribute.Positive)]
    [ShowIf(nameof(KnockbackDirection), EKnockbackDirection.TowardSource)]
    [Tooltip("Solo al ATRAER: a cuántos metros de quien lo aplicó se detiene, para que no lo atraviese.")]
    [Label("Se detiene a (m)")]
    public float PullStopDistance = 1.5f;

    [Tooltip("EJECUTAR: mientras tenga este efecto, si su vida queda en esta fracción de su vida " +
             "máxima o menos, muere (0.05 = al 5 %). Se mira al recibir el efecto y en cada daño " +
             "mientras dura. La baja es de quien le puso el efecto. No mata a un inmortal (Preservar " +
             "vida) ni a un inmune. 0 = no ejecuta (el Corte final del Samurái).")]
    [Label("Ejecutar con vida bajo (0 a 1)")]
    [Range(0f, 1f)] public float ExecuteBelowHealth = 0f;

    [Tooltip("El daño de este efecto sale CRÍTICO si el que lo recibe tiene este tag (la estocada " +
             "del Trampero: crítica contra un enemigo con heridas, Status_Wound). Es la misma capa que " +
             "el golpe por la espalda: no se suma a ella. Nada = como siempre.")]
    [Label("Crítico si el objetivo tiene")]
    public EGameplayTag CritIfTargetHas = EGameplayTag.None;

    [Section(EffectSection.Visuals)]
    [Tooltip("VFX que aparece sobre QUIEN RECIBE este efecto y vive lo que viva el efecto. " +
             "Pensado para que un debuff importante se vea encima del jugador afectado (las " +
             "flechas cayendo, un aura, unas cadenas), sin tener que instanciarlo a mano desde " +
             "la habilidad. Siempre queda ENGANCHADO al objetivo: lo sigue si se mueve, y se " +
             "destruye solo cuando el efecto expira o se lo quitan. Solo tiene sentido en " +
             "efectos CON duración: uno instantáneo se aplica y se va en el mismo frame. " +
             "Dejalo en None si el efecto no necesita nada visible.")]
    [Label("VFX en el objetivo")]
    public GameObject TargetVFX;

    [ShowIf(nameof(TargetVFX))]
    [Tooltip("Desplazamiento del VFX respecto al pivote del objetivo, en su espacio local. " +
             "Los pivotes están a los pies, así que casi siempre vas a querer subirlo en Y " +
             "(2-3 para algo que flote sobre la cabeza).")]
    [Label("VFX: posición (m)")]
    public Vector3 TargetVFXOffset;

    [ShowIf(nameof(TargetVFX))]
    [Tooltip("Rotación extra del VFX respecto al objetivo, en grados.")]
    [Label("VFX: rotación (°)")]
    public Vector3 TargetVFXRotation;

    [ShowIf(nameof(TargetVFX))]
    [Tooltip("Escala del VFX. En CERO usa la escala del prefab tal cual.")]
    [Label("VFX: escala (×)")]
    public Vector3 TargetVFXScale;

    [ShowIf(nameof(TargetVFX))]
    [Tooltip("Repetir las partículas del VFX mientras dure el efecto. Muchos VFX de los packs " +
             "son de un solo disparo (emiten un segundo y se apagan), así que sin esto el " +
             "objeto sigue vivo pero vacío hasta que el efecto termina. Al terminar, deja de " +
             "emitir y las partículas que quedan se desvanecen solas en vez de cortarse de golpe. " +
             "Apagalo si el VFX está pensado para verse UNA vez al aplicarse.")]
    [Label("Repetir partículas")]
    public bool TargetVFXLoop = true;

    [Tooltip("Mientras dura, cada SALTO de quien lo tiene (del piso o desde una pared) deja este " +
             "VFX en el lugar donde saltó: el humo del Manto de oscuridad del Shinobi. Se ve en " +
             "todas las pantallas, también en las de los enemigos. Vacío = nada.")]
    [Label("VFX al saltar")]
    public GameObject JumpVFX;

    [Section(EffectSection.Sound)]
    [Tooltip("Suena en el personaje cuando el efecto se le aplica (solo efectos CON duración: " +
             "quemadura, stun, escudo...). El daño instantáneo tiene su propio sonido de golpe " +
             "recibido en AudioLibrary. Vacío = silencio.")]
    [Label("Sonido al aplicarse")]
    public SfxCue TargetSound;

    // Tags que se le agregan al objetivo mientras el efecto está activo
    // (ej: Stunned) y se le quitan al terminar. El primer tag de esta
    // lista también sirve como "identidad" del efecto para cooldowns y
    // sincronización en red — ver GameplayAbility.CanActivate() y
    // NetworkAbilitySystemComponent.
    // =========================================================
    // QUITAR EFECTOS E INMUNIDAD (17 de noviembre de 2026, de las "Mejoras del GAS")
    // =========================================================
    //
    // Sin código por habilidad: LIMPIAR (quitarle los debuffs a un aliado), PURGAR (quitarle los buffs a un enemigo),
    // condiciones por tag ("no entra si el objetivo tiene…", "solo si tiene…") e INMUNIDADES ("mientras dura, los
    // efectos que dan estos tags no entran"). Lo resuelve AbilitySystemComponent.ApplyGameplayEffect, antes que nada.
    // Nunca se quitan: los cooldowns (efectos ocultos), las pasivas de la clase ni lo marcado "No se puede quitar".
    // Una inmunidad reconoce a un efecto por los TAGS que da: un veneno sin tag propio no se puede bloquear así.

    [Section(EffectSection.Cleanse, startCollapsed: true)]
    [Tooltip("Al aplicarse, le quita al objetivo sus debuffs (limpiar), sus buffs (purgar) o los dos. Sirve en efectos " +
             "instantáneos (una curación que limpia) y con duración.")]
    [Label("Al aplicarse, quita")]
    public ERemoveKind RemoveOnApply = ERemoveKind.Nothing;

    [Section(EffectSection.Cleanse)]
    [Tooltip("Al aplicarse, también le quita los efectos que dan alguno de estos tags (cualquier tipo, menos ocultos).")]
    [Label("Al aplicarse, quita los que dan estos tags")]
    public List<EGameplayTag> RemoveEffectsWithTags = new List<EGameplayTag>();

    [Section(EffectSection.Cleanse)]
    [Tooltip("Cuántos quita como mucho, empezando por los más nuevos. 0 = todos.")]
    [Label("Cuántos quita (0 = todos)")]
    public int MaxRemoved = 0;

    [Section(EffectSection.Cleanse)]
    [Tooltip("Si el objetivo tiene alguno de estos tags, este efecto no entra (ni quita nada).")]
    [Label("No se aplica si el objetivo tiene")]
    public List<EGameplayTag> BlockedIfTargetHas = new List<EGameplayTag>();

    [Section(EffectSection.Cleanse)]
    [Tooltip("Este efecto solo entra si el objetivo tiene TODOS estos tags.")]
    [Label("Solo se aplica si el objetivo tiene")]
    public List<EGameplayTag> RequiresTargetTags = new List<EGameplayTag>();

    [Section(EffectSection.Cleanse)]
    [Tooltip("Mientras este efecto está activo, el objetivo es INMUNE a los efectos que dan alguno de estos tags: no " +
             "entran. Al aplicarse, también le quita los que ya tenía. Solo en efectos con duración.")]
    [Label("Mientras dura, inmune a los que dan")]
    public List<EGameplayTag> GrantsImmunityTo = new List<EGameplayTag>();

    [Section(EffectSection.Cleanse)]
    [Tooltip("Ninguna limpieza, purga ni inmunidad lo quita (la bendición del jefe, un estado del modo de juego…).")]
    [Label("No se puede quitar")]
    public bool CannotBeRemoved = false;

    // "SE VA SOLO" (18 de noviembre de 2026): un sigilo que se rompe al atacar, un sueño que se rompe al recibir daño,
    // una postura que se pierde al moverse. Cuenta como "quitado antes" (dispara sus "Si se lo quitan antes"). Un evento
    // del MISMO frame en que se aplicó no cuenta (el golpe que duerme no lo despierta). Lo resuelve
    // AbilitySystemComponent.RemoveEffectsOnEvent; "No se puede quitar" no lo frena (es del propio efecto).
    [Section(EffectSection.Cleanse)]
    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("Se quita solo cuando a quien lo tiene le pasa algo de esto (se pueden marcar varios):\n" +
             "· Al recibir daño: de cualquiera menos de sí mismo, también de un veneno o una zona.\n" +
             "· Al hacer daño: cuando le baja vida o escudo a otro (también con sus venenos).\n" +
             "· Al moverse: si se aleja más de medio metro de donde lo recibió (caminar, saltar, que lo empujen).\n" +
             "· Al usar una habilidad: cualquiera que de verdad salga.\n" +
             "Cuenta como \"quitado antes\": dispara sus \"Si se lo quitan antes\".")]
    [Label("Se va solo")]
    public ERemoveOnEvent RemoveOn = ERemoveOnEvent.Nothing;

    // =========================================================
    // ENCADENADOS (18 de noviembre de 2026, de las "Mejoras del GAS")
    // =========================================================
    //
    // Lo que pasa cuando un efecto CON duración se va: "al vencer" (se le acabó el tiempo) o "si se lo quitan antes"
    // (lo limpiaron, lo purgaron, una inmunidad, o una habilidad lo quitó o lo consumió). Una marca que explota al
    // vencer, un buff que al terminar deja cansado, una marca que al consumirla detona. Se aplican sobre QUIEN LO TENÍA,
    // firmados por quien se lo puso (escalan con sus stats y la baja es suya) y con la habilidad de origen (el resumen
    // de muerte). NO encadenan: volver a aplicarlo (refrescar, pisar, el grupo, la explosión al tope), la limpieza de
    // morir, revivir o cambiar de clase, ni un muerto. Lo resuelve AbilitySystemComponent.RemoveActiveEffect.

    [Section(EffectSection.Chain, startCollapsed: true)]
    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("Cuando se le acaba el tiempo, se le aplican estos efectos a quien lo tenía (firmados por quien se lo " +
             "puso). Solo al vencer: si se lo quitan antes, no.")]
    [Label("Al vencer, aplica")]
    public List<GameplayEffect> OnExpireEffects = new List<GameplayEffect>();

    [Section(EffectSection.Chain)]
    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("Si se lo quitan ANTES de que venza (una limpieza, una purga, una inmunidad, o una habilidad que lo quita " +
             "o lo consume), se le aplican estos efectos a quien lo tenía (firmados por quien se lo puso). Volver a " +
             "aplicarlo, morir o cambiar de clase no cuentan.")]
    [Label("Si se lo quitan antes, aplica")]
    public List<GameplayEffect> OnRemovedEffects = new List<GameplayEffect>();

    // MARCAR Y DETONAR (19 de noviembre de 2026, de las "Mejoras del GAS"): la marca que, al gastarla (una habilidad con
    // "Gasta los puntos", una reacción "Y se gasta", una limpieza), explota en área alrededor de quien la tenía. Cada marca
    // acumulada es un efecto aparte: tres marcas gastadas juntas son tres explosiones.
    [Section(EffectSection.Chain)]
    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("Si se lo quitan antes (la detonación), un GA_InstantAoE tal cual —su radio, sus efectos y su VFX— estalla " +
             "donde está quien lo tenía, a nombre de quien se lo puso (escala con sus stats y la baja es suya; a quién le " +
             "hace qué lo dice la lista de esa habilidad). Sin su costo, su cooldown ni su animación. Vacío = nada.")]
    [Label("Si se lo quitan antes, explota (GA_InstantAoE)")]
    public GA_InstantAoE ExplosionOnRemoved;

    // BURLAR A LA MUERTE (20 de noviembre de 2026): mientras dura, el golpe que lo mataría lo deja con vida y se gasta (cuenta
    // como "quitado antes": sus "Si se lo quitan antes" son lo que viene después, una invulnerabilidad corta, un VFX). Lo
    // resuelve AbilitySystemComponent.SetCurrentAttributeValue antes de morir. La Ira del Inmortal es aparte (su habilidad).
    [Section(EffectSection.Chain)]
    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("El golpe que mataría a quien lo tiene lo deja con la vida de abajo, y este efecto se gasta (una vez). Lo que " +
             "venga después (unos segundos de invulnerabilidad, un VFX) va en \"Si se lo quitan antes, aplica\".")]
    [Label("Burla a la muerte (una vez)")]
    public bool PreventsDeath = false;

    [Section(EffectSection.Chain)]
    [ShowIf(nameof(PreventsDeath))]
    [Tooltip("Con cuánta vida queda al burlar a la muerte, como fracción de su vida máxima (al menos 1 de vida).")]
    [Label("Queda con (0 a 1 de la vida)")]
    [Range(0f, 1f)] public float PreventDeathHealth = 0.3f;

    [Section(EffectSection.Chain)]
    [ShowIf(nameof(ExplosionOnRemoved))]
    [Tooltip("La explosión sale también cuando se le acaba el tiempo (no solo al gastarla).")]
    [Label("También explota al vencer")]
    public bool ExplodesOnExpire = false;

    // REACCIONES (18 de noviembre de 2026, los eventos de juego): mientras este efecto está activo, cuando a quien lo
    // tiene le pasa algo (hizo daño, recibió un crítico, mató...), aplica efectos. Así una pasiva es un GE infinito con
    // reacciones, sin script. Ver GameplayEvents.cs y AbilitySystemComponent.Events.
    [Section(EffectSection.Reactions, startCollapsed: true)]
    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("Mientras dura: cuando a quien lo tiene le pasa algo (hacer daño, recibir un crítico, matar, curar...), " +
             "aplica efectos a él, al otro o a los dos. Lo que aplica una reacción no dispara otras reacciones.")]
    [Label("Reacciones")]
    public List<EffectReaction> Reactions = new List<EffectReaction>();

    // VÍNCULO ENTRE DOS (19 de noviembre de 2026, de las "Mejoras del GAS"): mientras dura, une a quien lo tiene con QUIEN
    // SE LO PUSO. Un protector que se pone esto sobre un aliado se lleva parte de sus golpes; un vínculo de vida le devuelve
    // parte de lo que lo curan. Lo resuelve AbilitySystemComponent (parte Damage: RedirectLinkedDamage, ShareLinkedHeal;
    // y el corte por distancia en ProcessActiveEffects). Lo que llega por el vínculo no vuelve a pasar por otro vínculo.
    [Section(EffectSection.Link, startCollapsed: true)]
    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("Qué parte del daño que RECIBE quien lo tiene (ya pasado por bloqueo y defensas, antes de su escudo) se la " +
             "lleva QUIEN SE LO PUSO en su lugar (contra su escudo y su vida; si muere por esto, la baja es del que pegó). " +
             "0.4 = el 40 %. Si el que lo puso está muerto o es inmune, no se pasa nada. 0 = nada.")]
    [Label("Pasa del daño al que lo puso (0 a 1)")]
    [Range(0f, 1f)] public float LinkDamageShare = 0f;

    [Section(EffectSection.Link)]
    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("Qué parte de la curación que RECIBE quien lo tiene le llega TAMBIÉN a quien se lo puso (una copia: al " +
             "curado no se le quita nada). 0 = nada.")]
    [Label("Comparte la curación con el que lo puso (0 a 1)")]
    [Range(0f, 1f)] public float LinkHealShare = 0f;

    [Section(EffectSection.Link)]
    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("Si se alejan más que esto (o el que lo puso muere o se va), el vínculo se corta: se quita el efecto (cuenta " +
             "como \"quitado antes\" para los encadenados).")]
    [Label("Se corta a (m; 0 = nunca)")]
    public float LinkMaxDistance = 0f;

    [Section(EffectSection.Link)]
    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("La cuerda que se ve entre los dos mientras dura, en todas las pantallas: un prefab con LineRenderer (y, si " +
             "se quiere, LinkTether para su altura y su comba). VFX_LinkTether es la de siempre. Vacío = no se ve.")]
    [Label("Cuerda (prefab)")]
    public GameObject LinkTetherPrefab;

    // ¿Es un vínculo? (algo de la sección de arriba puesto)
    public bool IsLink => LinkDamageShare > 0f || LinkHealShare > 0f || LinkMaxDistance > 0f || LinkTetherPrefab != null;

    [Section(EffectSection.Tags)]
    [Tooltip("Tags que tiene el objetivo mientras el efecto está activo (State_Stunned, " +
             "Status_Slow...). El PRIMERO es además la identidad del efecto: con él se bloquea un " +
             "cooldown y se reconoce el efecto en la red y en la UI.")]
    [Label("Tags que da")]
    public List<EGameplayTag> GrantedTags = new List<EGameplayTag>();

    [Tooltip("Solo si da Status_HealShield (el escudo que cura: Cubrir con escudo de la Conquista): qué " +
             "parte de lo que el escudo frena vuelve como vida. 1 = todo, 0.5 = la mitad.")]
    [Label("Escudo que cura: vida por lo frenado (0 a 1)")]
    [Range(0f, 1f)] public float HealShieldReturn = 1f;

    [ShowIf(HasDurationFields, ShowIfAttribute.Positive)]
    [Tooltip("Mientras dura, el EQUIPO de quien lo puso ve a quien lo tiene a través de las paredes (la Marca del cazador). " +
             "El equipo del marcado y los demás no. Se renueva en cada refresco.")]
    [Label("Revela al equipo del que lo puso")]
    public bool RevealsToSourceTeam = false;

    // =========================================================
    // COPIAS CONGELADAS (el proyectil devuelto por un parry)
    // =========================================================

    // Cuando este efecto es una COPIA hecha en juego (CreateFrozenCopy), apunta al asset
    // original. La red y la UI la tratan como el original: mismo índice en
    // GameplayEffectRegistry, mismo ícono y mismo VFX. NonSerialized: es de runtime.
    [System.NonSerialized] public GameplayEffect SourceTemplate;

    // El asset que este efecto "es": el original si es una copia, él mismo si no.
    public GameplayEffect Template => SourceTemplate != null ? SourceTemplate : this;

    // Una copia de este efecto con los stats de 'statsFrom' ya puestos como número fijo:
    // cada modificador que escala con un stat del autor deja de leerlo y se queda con lo
    // que valía AHORA. Todo lo demás queda igual (duración, ticks, tags, VFX), y también el
    // tipo de daño: un daño mágico sigue diciendo que escala del daño mágico, con
    // coeficiente 0, así que sigue ignorando la armadura.
    //
    // La usa el proyectil devuelto por un parry (GC_Projectile.Reflect): lo firma el que
    // lo devolvió (la baja es suya), pero hace el daño que traía, calculado con los stats
    // de quien lo tiró. Con el efecto original, el daño se recalcularía con los del
    // Guardián, y un rayo mágico devuelto (el Guardián no tiene daño mágico) no haría nada.
    public GameplayEffect CreateFrozenCopy(AbilitySystemComponent statsFrom)
    {
        // Instantiate copia la lista de modificadores entera (objetos nuevos): tocar los
        // de la copia no toca el asset.
        GameplayEffect copy = Instantiate(this);
        copy.name           = name + "(congelado)";
        copy.SourceTemplate = Template;
        if (statsFrom == null) return copy;

        foreach (Modifier m in copy.Modifiers)
        {
            if (!m.UseAttributeScaling) continue;

            float value = m.Magnitude + statsFrom.GetAttributeValue(m.SourceAttribute) * m.AttributeCoefficient;

            // Con el stat en 0 quedaría en 0, y el piso (MinMagnitude) ya no sabría si era daño
            // o curación (lo saca del coeficiente): se deja una pizca con el signo que tenía.
            if (m.MinMagnitude != 0f && Mathf.Abs(value) < 0.0001f)
                value = (m.Magnitude != 0f ? Mathf.Sign(m.Magnitude) : Mathf.Sign(m.AttributeCoefficient)) * 0.0001f;

            m.Magnitude            = value;
            m.AttributeCoefficient = 0f;
        }
        return copy;
    }

    // Con qué escala de daño se hizo esta copia (CreateDamageScaledCopy); 1 en los assets.
    [System.NonSerialized] public float CreatedWithDamageScale = 1f;

    // Una copia que hace 'scale' del DAÑO: los modificadores sumados que le BAJAN la vida (fijos,
    // escalados por stat o por la vida del objetivo) se multiplican; todo lo demás queda igual
    // (duración, ticks, tags, lentitud, VFX). Si no baja vida, devuelve el mismo efecto. La usan
    // las copias de los ecos (la Proyección arcana: la mitad del daño, también en la quemadura).
    public GameplayEffect CreateDamageScaledCopy(float scale)
    {
        if (Mathf.Approximately(scale, 1f) || !DamagesHealth) return this;

        GameplayEffect copy = Instantiate(this);
        copy.name                   = name + $"(×{scale:0.##})";
        copy.SourceTemplate         = Template;
        copy.CreatedWithDamageScale = scale;

        foreach (Modifier m in copy.Modifiers)
        {
            if (!IsDamage(m)) continue;
            m.Magnitude               *= scale;
            m.AttributeCoefficient    *= scale;
            m.TargetHealthCoefficient *= scale;
            m.MinMagnitude            *= scale;
        }
        return copy;
    }

    // Una copia cuyo DAÑO es mágico: los modificadores que bajan vida escalando del Ataque pasan a
    // escalar del Daño mágico (con el mismo coeficiente), así ignora la armadura y le rompe el doble
    // al escudo. Sin esos, devuelve el mismo efecto. La usa la Espada mágica del Filo danzante (el
    // "Daño mágico con el tag" de sus cortes).
    public GameplayEffect CreateMagicCopy()
    {
        if (!HasPhysicalScaledDamage) return this;

        GameplayEffect copy = Instantiate(this);
        copy.name                   = name + "(mágico)";
        copy.SourceTemplate         = Template;
        copy.CreatedWithDamageScale = CreatedWithDamageScale;

        foreach (Modifier m in copy.Modifiers)
            if (IsPhysicalScaledDamage(m)) m.SourceAttribute = EAttributeType.MagicDamage;
        return copy;
    }

    private bool HasPhysicalScaledDamage
    {
        get
        {
            if (Modifiers == null) return false;
            foreach (Modifier m in Modifiers)
                if (IsPhysicalScaledDamage(m)) return true;
            return false;
        }
    }

    private static bool IsPhysicalScaledDamage(Modifier m)
        => IsDamage(m) && m.UseAttributeScaling && m.SourceAttribute == EAttributeType.Attack;

    // ¿Le baja la vida a alguien? (un modificador sumado sobre Health con algo negativo)
    public bool DamagesHealth
    {
        get
        {
            if (Modifiers == null) return false;
            foreach (Modifier m in Modifiers)
                if (IsDamage(m)) return true;
            return false;
        }
    }

    private static bool IsDamage(Modifier m)
        => m.Attribute == EAttributeType.Health && m.Type == Modifier.EModificationType.Add &&
           (m.Magnitude < 0f ||
            (m.UseAttributeScaling && m.AttributeCoefficient < 0f) ||
            (m.UseTargetHealthScaling && m.TargetHealthCoefficient < 0f));

    // ¿Algún modificador depende de los stats del autor? Si no, congelarlo no cambia nada.
    public bool ScalesWithSource
    {
        get
        {
            foreach (Modifier m in Modifiers)
                if (m.UseAttributeScaling) return true;
            return false;
        }
    }
}
