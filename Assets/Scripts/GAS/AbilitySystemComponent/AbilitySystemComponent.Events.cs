using UnityEngine;
using System.Collections.Generic;
using System;

// ============================================================
// AbilitySystemComponent — LOS EVENTOS DE JUEGO
//
// Una parte de AbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/AbilitySystemComponent.cs; ahí está también la lista de las partes.
//
// SendGameplayEvent avisa lo que le pasó a ESTE personaje (ver GameplayEvents.cs) y corre las "Reacciones" de sus
// efectos activos. Lo que aplican las reacciones va en cola y sale cuando ningún personaje está aplicando ni recorriendo
// efectos (el mismo cuidado que los encadenados), y NO dispara otras reacciones: "al hacer daño, pega 5" no se vuelve a
// disparar con sus propios 5.
// ============================================================
public partial class AbilitySystemComponent
{
    // Todos los eventos de juego de este personaje, con sus datos (para código; sin código, las Reacciones del GE).
    public event Action<GameplayEventData> OnGameplayEvent;

    private struct PendingReaction
    {
        public AbilitySystemComponent Target;
        public GameplayEffect Effect;
        public AbilitySystemComponent Source;
        public GameplayAbility Ability;
    }

    private static readonly List<PendingReaction> s_pendingReactions = new List<PendingReaction>();
    private static int s_holdDepth;            // ApplyGameplayEffect / el paso de cada frame / quitar efectos, en CUALQUIER personaje
    private static bool s_applyingReactions;   // mientras se aplican reacciones, los eventos no disparan otras

    private readonly List<ActiveGameplayEffect> _consumedByReaction = new List<ActiveGameplayEffect>();

    // Le pasó 'what' a este personaje. 'other': el otro de la historia (puede ser null). Solo tiene efecto en el
    // servidor, que es donde hay efectos activos.
    public void SendGameplayEvent(EGameplayEvent what, AbilitySystemComponent other, float magnitude = 0f,
                                  bool periodic = false, GameplayAbility ability = null, float toHealth = -1f)
    {
        var data = new GameplayEventData
        {
            Event = what, Self = this, Other = other, Magnitude = magnitude, ToHealth = toHealth >= 0f ? toHealth : magnitude,
            Ability = ability != null ? ability : DamageContext, IsPeriodic = periodic,
        };
        OnGameplayEvent?.Invoke(data);
        if (HasTag(EGameplayTag.State_Dead)) return;

        if (!s_applyingReactions && ActiveEffects.Count > 0) RunReactions(data);

        // Las habilidades que se activan solas: no con lo que hizo una reacción ni otra habilidad activada así (sin
        // ping-pong: un contraataque no dispara otro contraataque).
        if (!s_applyingReactions && !s_activatingTriggered) QueueTriggeredAbilities(data);

        if (s_holdDepth == 0) { FlushReactions(); FlushTriggered(); }
    }

    // Las "Reacciones" de los efectos activos de ESTE personaje.
    private void RunReactions(GameplayEventData data)
    {
        EGameplayEvent what = data.Event; AbilitySystemComponent other = data.Other;
        float magnitude = data.Magnitude; bool periodic = data.IsPeriodic;

        for (int i = 0; i < ActiveEffects.Count; i++)
        {
            ActiveGameplayEffect active = ActiveEffects[i];
            List<EffectReaction> reactions = active.Definition.Reactions;
            if (reactions == null || reactions.Count == 0) continue;

            for (int r = 0; r < reactions.Count; r++)
            {
                EffectReaction x = reactions[r];
                if (x == null || x.When != what) continue;
                if (periodic && !x.IncludePeriodic) continue;
                if (x.MinMagnitude > 0f && magnitude < x.MinMagnitude) continue;
                if (x.OnlyIfOwnerHas != EGameplayTag.None && !HasTag(x.OnlyIfOwnerHas)) continue;
                if (x.OnlyIfOtherHas != EGameplayTag.None && (other == null || !other.HasTag(x.OnlyIfOtherHas))) continue;
                if (x.OnlyIfOwnerLacks != EGameplayTag.None && HasTag(x.OnlyIfOwnerLacks)) continue;
                if (x.RequiresHealthDamage && data.ToHealth <= 0f) continue;
                if (active.ReactionReadyAt != null && r < active.ReactionReadyAt.Length && Time.time < active.ReactionReadyAt[r]) continue;
                if (x.OncePerFrame && active.ReactionFrame != null && r < active.ReactionFrame.Length &&
                    active.ReactionFrame[r] == Time.frameCount) continue;
                if (x.Chance < 1f && UnityEngine.Random.value >= x.Chance) continue;

                if (x.OncePerFrame)
                {
                    if (active.ReactionFrame == null || active.ReactionFrame.Length < reactions.Count)
                    {
                        int old = active.ReactionFrame?.Length ?? 0;
                        Array.Resize(ref active.ReactionFrame, reactions.Count);
                        for (int k = old; k < active.ReactionFrame.Length; k++) active.ReactionFrame[k] = -1;
                    }
                    active.ReactionFrame[r] = Time.frameCount;
                }

                if (x.Cooldown > 0f)
                {
                    if (active.ReactionReadyAt == null || active.ReactionReadyAt.Length < reactions.Count)
                        Array.Resize(ref active.ReactionReadyAt, reactions.Count);
                    active.ReactionReadyAt[r] = Time.time + x.Cooldown;
                }

                foreach (GameplayEffect effect in x.Effects)
                {
                    if (effect == null) continue;
                    if (x.ApplyTo != EReactionTarget.Other) QueueReaction(this, effect, data.Ability);
                    if (x.ApplyTo != EReactionTarget.Self && other != null) QueueReaction(other, effect, data.Ability);
                }
                if (x.ConsumesEffect && !_consumedByReaction.Contains(active)) _consumedByReaction.Add(active);
            }
        }

        // Lo que se gasta al reaccionar se va DESPUÉS de mirar todas las reacciones (cuenta como quitado antes).
        if (_consumedByReaction.Count > 0)
        {
            BeginHold();
            try
            {
                foreach (ActiveGameplayEffect gone in _consumedByReaction)
                    if (ActiveEffects.Contains(gone)) RemoveActiveEffect(gone);
            }
            finally { _consumedByReaction.Clear(); EndHold(); }
        }
    }

    // =========================================================
    // HABILIDADES QUE SE ACTIVAN SOLAS (GameplayAbility "Se activa sola", 18 de noviembre de 2026)
    // =========================================================

    private struct PendingTrigger { public AbilitySystemComponent Owner; public GameplayAbility Ability; public GameplayEventData Data; }

    private static readonly List<PendingTrigger> s_pendingTriggered = new List<PendingTrigger>();
    private static bool s_activatingTriggered;   // mientras corre una activación sola, los eventos no activan otras

    private void QueueTriggeredAbilities(GameplayEventData data)
    {
        bool checkedServer = false;
        foreach (GameplayAbility ga in GrantedAbilities)
        {
            if (ga == null || ga.TriggerEvent == EGameplayEvent.None || ga.TriggerEvent != data.Event) continue;
            if (!checkedServer)
            {
                // Solo el servidor activa (en un cliente no llegan estos eventos, pero por las dudas).
                NetworkAbilitySystemComponent net = GetComponent<NetworkAbilitySystemComponent>();
                if (net != null && !net.IsServerInitialized) return;
                checkedServer = true;
            }
            if (data.IsPeriodic && !ga.TriggerIncludePeriodic) continue;
            if (ga.TriggerMinMagnitude > 0f && data.Magnitude < ga.TriggerMinMagnitude) continue;
            if (ga.TriggerOnlyIfOwnerHas != EGameplayTag.None && !HasTag(ga.TriggerOnlyIfOwnerHas)) continue;
            if (ga.TriggerChance < 1f && UnityEngine.Random.value >= ga.TriggerChance) continue;
            s_pendingTriggered.Add(new PendingTrigger { Owner = this, Ability = ga, Data = data });
        }
    }

    private static void FlushTriggered()
    {
        if (s_pendingTriggered.Count == 0 || s_holdDepth > 0 || s_activatingTriggered || s_applyingReactions) return;

        var batch = new List<PendingTrigger>(s_pendingTriggered);
        s_pendingTriggered.Clear();
        s_activatingTriggered = true;
        try
        {
            foreach (PendingTrigger p in batch)
                if (p.Owner != null && !p.Owner.HasTag(EGameplayTag.State_Dead) && p.Ability != null)
                    p.Owner.ActivateTriggered(p.Ability, p.Data);
        }
        finally { s_activatingTriggered = false; }
    }

    // Con red, por el NetworkASC (la mira hacia el otro, la animación a todos, incluido el dueño). Sin red (un NPC sin
    // NetworkASC, las pruebas), directo.
    private void ActivateTriggered(GameplayAbility ability, GameplayEventData data)
    {
        // WasTriggered queda prendido hasta la próxima activación con botón (ServerActivateAbility lo apaga): así su
        // EndAbility, aunque llegue más tarde, sabe que no tiene que destrabar el ataque del dueño.
        ability.WasTriggered = true;
        ability.TriggeredBy  = data;
        NetworkAbilitySystemComponent net = GetComponent<NetworkAbilitySystemComponent>();
        if (net != null) { net.ServerActivateTriggered(ability, data); return; }
        if (!ability.CanActivate()) return;
        if (ability.UsesCast) ability.BeginCast();
        else ability.Activate();
    }

    private void QueueReaction(AbilitySystemComponent target, GameplayEffect effect, GameplayAbility ability)
    {
        s_pendingReactions.Add(new PendingReaction { Target = target, Effect = effect, Source = this, Ability = ability });
    }

    private static void FlushReactions()
    {
        if (s_pendingReactions.Count == 0 || s_holdDepth > 0 || s_applyingReactions) return;

        var batch = new List<PendingReaction>(s_pendingReactions);
        s_pendingReactions.Clear();
        s_applyingReactions = true;
        try
        {
            foreach (PendingReaction p in batch)
            {
                if (p.Target == null || p.Target.HasTag(EGameplayTag.State_Dead)) continue;
                GameplayAbility previousContext = DamageContext;
                DamageContext = p.Ability;
                try { p.Target.ApplyGameplayEffect(p.Effect, p.Source != null ? p.Source : null); }
                finally { DamageContext = previousContext; }
            }
        }
        finally { s_applyingReactions = false; }
    }

    // La "espera" de todo lo que aplica, quita o recorre efectos: los encadenados de ESTE personaje y las reacciones de
    // TODOS (y las habilidades que se activan solas) salen al terminar. Va en pares (BeginHold / EndHold en un finally).
    private void BeginHold()
    {
        _chainHold++;
        s_holdDepth++;
    }

    private void EndHold()
    {
        s_holdDepth--;
        EndChainHold();
        if (s_holdDepth == 0) { FlushReactions(); FlushTriggered(); }
    }
}
