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

        if (s_applyingReactions || ActiveEffects.Count == 0 || HasTag(EGameplayTag.State_Dead)) return;

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

        if (s_holdDepth == 0) FlushReactions();
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
    // TODOS salen al terminar. Va en pares (BeginHold / EndHold en un finally).
    private void BeginHold()
    {
        _chainHold++;
        s_holdDepth++;
    }

    private void EndHold()
    {
        s_holdDepth--;
        EndChainHold();
        if (s_holdDepth == 0) FlushReactions();
    }
}
