using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — ACUMULACIONES
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
//
// Las acumulaciones (sección Acumulaciones), "al terminar de golpear" y el daño mágico con un tag.
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // ACUMULACIONES (sección Acumulaciones)
    // =========================================================

    // Al activarse (CommitAbility): cuántas acumulaciones hay, y si se gastan ya o al
    // terminar. Sin StacksTag no lee nada y conserva lo que le haya pasado un combo.
    private void ReadStacks()
    {
        _consumeStacksOnEnd = false;
        if (StacksTag == EGameplayTag.None || OwnerASC == null) return;

        StackSnapshot = OwnerASC.GetTagCount(StacksTag);
        if (StackSnapshot <= 0) return;

        if (ConsumeStacks == EStackConsume.OnActivate) OwnerASC.RemoveEffectsWithTag(StacksTag);
        else if (ConsumeStacks == EStackConsume.OnEnd) _consumeStacksOnEnd = true;
    }

    // Cuántas veces se aplica una entrada según las acumulaciones leídas, y con qué
    // duración (-1 = la del GE). La regla: lo que escala, sin acumulaciones no se aplica.
    private int StackApplications(AbilityEffect e, out float duration)
    {
        duration = -1f;
        int stacks = Mathf.Max(0, StackSnapshot);

        switch (e.StackScaling)
        {
            case EStackScaling.OnlyWithStacks:   return stacks > 0 ? 1 : 0;
            case EStackScaling.OncePerStack:     return stacks;
            case EStackScaling.DurationPerStack:
                if (stacks <= 0 || e.PerStack <= 0f) return 0;
                duration = e.PerStack * stacks;
                return 1;
            default:                             return 1;
        }
    }

    // Aplica una entrada a 'to' tantas veces como diga su escalado.
    private static void ApplyScaled(AbilitySystemComponent to, GameplayEffect effect, object source,
                                    int times, float duration)
    {
        for (int i = 0; i < times; i++) to.ApplyGameplayEffect(effect, source, duration);
    }

    // ¿Se cumple la condición de la entrada? 'subject' es el objetivo alcanzado (o el
    // lanzador, en los efectos al activarse).
    // Escala una entrada con los puntos del objetivo ('points'): cuánto dura o cuántas veces va, con
    // su mínimo (que la aplica aunque no tenga puntos) y su máximo. false = no se aplica.
    private static bool ScaleByTargetPoints(AbilityEffect e, int points, ref int times, ref float duration)
    {
        switch (e.TargetScaling)
        {
            case ETargetStackScaling.DurationPerStack:
            {
                float d = Mathf.Max(0f, e.TargetPerStack) * points;
                if (e.TargetMinimum > 0f) d = Mathf.Max(d, e.TargetMinimum);
                if (e.TargetMaximum > 0f) d = Mathf.Min(d, e.TargetMaximum);
                if (d <= 0f) return false;
                duration = d;
                return true;
            }
            case ETargetStackScaling.ExtraDurationPerStack:
            {
                float baseDuration = duration >= 0f ? duration : e.Effect.Duration;
                float d = baseDuration + Mathf.Max(0f, e.TargetPerStack) * points;
                if (e.TargetMaximum > 0f) d = Mathf.Min(d, e.TargetMaximum);
                duration = d;
                return true;
            }
            case ETargetStackScaling.TimesPerStack:
            {
                float n = Mathf.Max(0f, e.TargetPerStack) * points;
                if (e.TargetMinimum > 0f) n = Mathf.Max(n, e.TargetMinimum);
                if (e.TargetMaximum > 0f) n = Mathf.Min(n, e.TargetMaximum);
                int k = Mathf.RoundToInt(n);
                if (k <= 0) return false;
                times *= k;
                return true;
            }
            default:
                return true;
        }
    }

    // =========================================================
    // "AL TERMINAR DE GOLPEAR" (el beneficio propio del corte avanzando del Filo danzante, 23 de
    // octubre de 2026)
    // =========================================================

    // Los enemigos golpeados en esta activación y la suma de sus puntos, por tag (servidor).
    [System.NonSerialized] private int _afterHitsEnemies;
    [System.NonSerialized] private Dictionary<EGameplayTag, int> _afterHitsPoints;
    private static readonly List<EGameplayTag> s_tallyTags = new List<EGameplayTag>();

    // ¿Esta habilidad aplica las entradas "al terminar de golpear"? Las que lo hacen llaman a
    // ApplyAfterHitsEffects cuando terminaron (el dash, la explosión).
    protected virtual bool SupportsAfterHits => false;

    private void ResetAfterHitsTally()
    {
        _afterHitsEnemies = 0;
        _afterHitsPoints?.Clear();
    }

    private void TallyForAfterHits(AbilitySystemComponent target)
    {
        if (Effects == null || target == null) return;

        s_tallyTags.Clear();
        bool any = false;
        foreach (AbilityEffect e in Effects)
        {
            if (e.When != EEffectWhen.AfterHits) continue;
            any = true;
            if (e.TargetStacksTag != EGameplayTag.None && !s_tallyTags.Contains(e.TargetStacksTag))
                s_tallyTags.Add(e.TargetStacksTag);
        }
        if (!any) return;

        _afterHitsEnemies++;
        _afterHitsPoints ??= new Dictionary<EGameplayTag, int>();
        foreach (EGameplayTag tag in s_tallyTags)
        {
            _afterHitsPoints.TryGetValue(tag, out int sum);
            _afterHitsPoints[tag] = sum + target.GetTagCount(tag);
        }
    }

    // Las entradas "al terminar de golpear": UNA vez, al lanzador, si golpeó a algún enemigo, con los
    // puntos de TODOS los golpeados sumados. La llama cada habilidad que lo soporta cuando terminó
    // de golpear (servidor). Deja la cuenta en cero.
    protected void ApplyAfterHitsEffects()
    {
        int enemies = _afterHitsEnemies;
        _afterHitsEnemies = 0;

        if (enemies > 0 && OwnerASC != null && Effects != null)
        {
            foreach (AbilityEffect e in Effects)
            {
                if (e.Effect == null || e.When != EEffectWhen.AfterHits) continue;
                if (!PassesCondition(e, OwnerASC)) continue;

                int times = StackApplications(e, out float duration);
                if (times <= 0) continue;

                if (e.TargetScaling != ETargetStackScaling.None && e.TargetStacksTag != EGameplayTag.None)
                {
                    int sum = 0;
                    if (_afterHitsPoints != null) _afterHitsPoints.TryGetValue(e.TargetStacksTag, out sum);
                    if (!ScaleByTargetPoints(e, sum, ref times, ref duration)) continue;
                }

                ApplyScaled(OwnerASC, e.Effect, OwnerASC, times, duration);
            }
        }

        _afterHitsPoints?.Clear();
    }

    // =========================================================
    // DAÑO MÁGICO CON UN TAG (la Espada mágica del Filo danzante, 23 de octubre de 2026)
    // =========================================================

    // Con este tag en el lanzador, el daño de la habilidad es MÁGICO (escala del daño mágico en vez
    // del ataque). None = como está. Lo declaran como campo las que lo usan (el corte, el dash), para
    // no ponerlo en el Inspector de todas.
    protected virtual EGameplayTag MagicDamageTag => EGameplayTag.None;

    [System.NonSerialized] private Dictionary<GameplayEffect, GameplayEffect> _magicEffects;

    private GameplayEffect AsMagicWhileTagged(GameplayEffect effect)
    {
        EGameplayTag tag = MagicDamageTag;
        if (effect == null || tag == EGameplayTag.None || OwnerASC == null || !OwnerASC.HasTag(tag)) return effect;

        _magicEffects ??= new Dictionary<GameplayEffect, GameplayEffect>();
        if (!_magicEffects.TryGetValue(effect, out GameplayEffect magic) || magic == null)
        {
            magic = effect.CreateMagicCopy();
            _magicEffects[effect] = magic;
        }
        return magic;
    }
}
