using System.Collections.Generic;
using UnityEngine;

// ============================================================
// EVENTOS DE JUEGO (18 de noviembre de 2026, de las "Mejoras del GAS")
//
// Lo que le PASA a un personaje en combate, con sus datos: hizo daño, recibió daño, un crítico, un golpe por la espalda,
// bloqueó, mató, curó, lo curaron, usó una habilidad. Lo manda el ASC (AbilitySystemComponent.SendGameplayEvent) desde
// el camino del daño, la muerte, el bloqueo y CommitAbility; siempre desde los ojos de a quién le pasó: "hizo daño" le
// llega al que pegó (Other = el golpeado), "recibió daño" al golpeado (Other = el que pegó).
//
// Dos formas de escucharlo:
//   · SIN CÓDIGO: las "Reacciones" de un GE (EffectReaction): mientras el efecto está activo, cuando pasa tal evento (con
//     sus condiciones) aplica tales efectos a quien lo tiene, al otro o a los dos. Una pasiva es un GE infinito con
//     reacciones (la marca del Punto débil: "al hacer daño, al otro: GE_WeakPoint").
//   · Con código: AbilitySystemComponent.OnGameplayEvent (un solo evento C# con todos los datos).
//
// EGameplayEvent se guarda por NÚMERO en los assets: se amplía SOLO al final.
// ============================================================
public enum EGameplayEvent
{
    [InspectorName("(ninguno)")]                     None          = 0,
    [InspectorName("Al hacer daño")]                 DamageDealt   = 1,
    [InspectorName("Al recibir daño")]               DamageTaken   = 2,
    [InspectorName("Al hacer un crítico")]           CriticalDealt = 3,
    [InspectorName("Al recibir un crítico")]         CriticalTaken = 4,
    [InspectorName("Al golpear por la espalda")]     BackstabDealt = 5,
    [InspectorName("Al bloquear un golpe")]          Blocked       = 6,
    [InspectorName("Al matar")]                      Kill          = 7,
    [InspectorName("Al curar a un aliado")]          HealDealt     = 8,
    [InspectorName("Al recibir curación")]           HealReceived  = 9,
    [InspectorName("Al usar una habilidad")]         AbilityUsed   = 10,
}

// Los datos de un evento, desde los ojos de 'Self'.
public struct GameplayEventData
{
    public EGameplayEvent Event;
    public AbilitySystemComponent Self;    // a quién le pasó (quien recibe el aviso)
    public AbilitySystemComponent Other;   // el otro: el golpeado / el que pegó / el muerto / el curado. Puede ser null (una zona sin autor, usar una habilidad)
    public float Magnitude;                // cuánto: el daño que entró (escudo incluido), lo curado, lo bloqueado. 0 si no aplica
    public float ToHealth;                 // del daño, lo que llegó a la VIDA (sin lo que se comió un escudo). En los demás, = Magnitude
    public GameplayAbility Ability;        // con qué habilidad, si se sabe (AbilitySystemComponent.DamageContext)
    public bool IsPeriodic;                // vino de un tick (un veneno, una zona)
}

// A quién le aplica sus efectos una reacción.
public enum EReactionTarget
{
    [InspectorName("A quien tiene el efecto")]   Self  = 0,
    [InspectorName("Al otro")]                   Other = 1,
    [InspectorName("A los dos")]                 Both  = 2,
}

// UNA reacción de un GE (la lista "Reacciones (mientras dura)"). Lo que aplica va firmado por quien tiene el efecto.
[System.Serializable]
public class EffectReaction
{
    [Tooltip("El evento que la dispara (siempre desde quien tiene el efecto: \"al hacer daño\" es cuando ÉL pega).")]
    [Label("Cuando")]
    public EGameplayEvent When = EGameplayEvent.DamageDealt;

    [Tooltip("A quien tiene el efecto, al otro (el golpeado, el que pegó, el curado...) o a los dos. \"Al usar una " +
             "habilidad\" no tiene otro.")]
    [Label("A quién")]
    public EReactionTarget ApplyTo = EReactionTarget.Other;

    [Tooltip("Los efectos que aplica, firmados por quien tiene este efecto.")]
    [Label("Aplica")]
    public List<GameplayEffect> Effects = new List<GameplayEffect>();

    [Tooltip("Solo si quien tiene el efecto tiene este tag (la versión potenciada de una pasiva). None = siempre.")]
    [Label("Solo si quien lo tiene tiene")]
    public EGameplayTag OnlyIfOwnerHas = EGameplayTag.None;

    [Tooltip("Solo si el otro tiene este tag (pegarle a uno marcado). None = siempre.")]
    [Label("Solo si el otro tiene")]
    public EGameplayTag OnlyIfOtherHas = EGameplayTag.None;

    [Tooltip("NO sale si quien tiene el efecto tiene este tag: la versión normal de una pasiva que tiene otra potenciada " +
             "(la normal con \"NO tiene Romper límites\", la potenciada con \"tiene Romper límites\"). None = siempre.")]
    [Label("Solo si quien lo tiene NO tiene")]
    public EGameplayTag OnlyIfOwnerLacks = EGameplayTag.None;

    [Tooltip("Con el daño: solo si algo llegó a la VIDA (un golpe que se comió entero un escudo no cuenta).")]
    [Label("Solo si llegó a la vida")]
    public bool RequiresHealthDamage = false;

    [Tooltip("Solo si el daño, la curación o lo bloqueado llega a esto. 0 = cualquiera.")]
    [Label("Mínimo (vida; 0 = cualquiera)")]
    public float MinMagnitude = 0f;

    [Tooltip("Si también cuentan los ticks (un veneno, una zona). Apagado: solo los golpes directos.")]
    [Label("También con ticks")]
    public bool IncludePeriodic = false;

    [Tooltip("Probabilidad de que salga cada vez (1 = siempre).")]
    [Label("Probabilidad (0 a 1)")]
    [Range(0f, 1f)] public float Chance = 1f;

    [Tooltip("Después de salir, cuánto tarda en poder salir otra vez. 0 = cada vez.")]
    [Label("Espera entre una y otra (s)")]
    public float Cooldown = 0f;

    [Tooltip("Al salir, este efecto se va (\"el próximo golpe hace X\"). Cuenta como quitado antes: dispara sus encadenados.")]
    [Label("Y se gasta")]
    public bool ConsumesEffect = false;
}
