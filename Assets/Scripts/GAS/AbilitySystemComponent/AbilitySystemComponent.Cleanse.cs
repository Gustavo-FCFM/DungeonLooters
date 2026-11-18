using UnityEngine;
using System.Collections.Generic;
using System;

// ============================================================
// AbilitySystemComponent — QUITAR EFECTOS E INMUNIDAD
//
// Una parte de AbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/AbilitySystemComponent.cs; ahí está también la lista de las partes.
//
// Limpiar y purgar desde el GE, las inmunidades y condiciones por tag, y las formas de quitar
// efectos desde el código. Nunca se quitan los cooldowns, las pasivas de la clase ni lo
// marcado "No se puede quitar".
// ============================================================
public partial class AbilitySystemComponent
{
    // =========================================================
    // QUITAR EFECTOS E INMUNIDAD (17 de noviembre de 2026)
    // =========================================================

    // Tags a los que este personaje es inmune ahora, con cuántos efectos activos lo hacen inmune (dos fuentes = 2).
    private readonly Dictionary<EGameplayTag, int> _immunities = new Dictionary<EGameplayTag, int>();
    public IReadOnlyDictionary<EGameplayTag, int> DebugImmunities => _immunities;

    private bool PassesApplicationTags(GameplayEffect effect)
    {
        if (effect.BlockedIfTargetHas != null)
            foreach (EGameplayTag tag in effect.BlockedIfTargetHas)
                if (tag != EGameplayTag.None && HasTag(tag)) return false;
        if (effect.RequiresTargetTags != null)
            foreach (EGameplayTag tag in effect.RequiresTargetTags)
                if (tag != EGameplayTag.None && !HasTag(tag)) return false;
        return true;
    }

    // ¿Da este efecto algún tag al que el personaje es inmune? (Lo que no se puede quitar, tampoco se bloquea.)
    private bool IsImmuneTo(GameplayEffect effect)
    {
        if (_immunities.Count == 0 || effect.CannotBeRemoved || effect.GrantedTags == null) return false;
        foreach (EGameplayTag tag in effect.GrantedTags)
            if (_immunities.TryGetValue(tag, out int n) && n > 0) return true;
        return false;
    }

    private void CountImmunities(GameplayEffect effect, int delta)
    {
        if (effect.GrantsImmunityTo == null) return;
        foreach (EGameplayTag tag in effect.GrantsImmunityTo)
        {
            if (tag == EGameplayTag.None) continue;
            int n = (_immunities.TryGetValue(tag, out int c) ? c : 0) + delta;
            if (n > 0) _immunities[tag] = n; else _immunities.Remove(tag);
        }
    }

    // Lo que un efecto le quita al personaje al aplicarse: por tipo (limpiar/purgar), por tags, y lo que ya tenía de
    // aquello a lo que ahora será inmune. Los más nuevos primero, hasta "Cuántos quita".
    private void RemoveOnApply(GameplayEffect effect, float duration)
    {
        bool byKind = effect.RemoveOnApply != GameplayEffect.ERemoveKind.Nothing;
        bool byTags = effect.RemoveEffectsWithTags != null && effect.RemoveEffectsWithTags.Count > 0;
        bool byImmunity = duration > 0f && effect.GrantsImmunityTo != null && effect.GrantsImmunityTo.Count > 0;
        if (!byKind && !byTags && !byImmunity) return;

        int removed = 0;
        for (int i = ActiveEffects.Count - 1; i >= 0; i--)
        {
            if (i >= ActiveEffects.Count) continue;
            ActiveGameplayEffect active = ActiveEffects[i];
            GameplayEffect other = active.Definition;
            if (other == null || other == effect || !CanBeRemoved(other)) continue;

            bool kindMatch = (effect.RemoveOnApply == GameplayEffect.ERemoveKind.Debuffs && other.EffectType == GameplayEffect.EEffectType.Debuff) ||
                             (effect.RemoveOnApply == GameplayEffect.ERemoveKind.Buffs   && other.EffectType == GameplayEffect.EEffectType.Buff) ||
                             (effect.RemoveOnApply == GameplayEffect.ERemoveKind.Both    && other.EffectType != GameplayEffect.EEffectType.Hidden);
            bool tagMatch = byTags && GrantsAny(other, effect.RemoveEffectsWithTags);
            bool immuneMatch = byImmunity && GrantsAny(other, effect.GrantsImmunityTo);
            if (!kindMatch && !tagMatch && !immuneMatch) continue;

            // Lo de la inmunidad se quita siempre; el tope es para limpiar/purgar/quitar por tag.
            if (!immuneMatch && effect.MaxRemoved > 0 && removed >= effect.MaxRemoved) continue;
            RemoveActiveEffect(active);
            if (!immuneMatch) removed++;
        }
    }

    private bool CanBeRemoved(GameplayEffect other) =>
        other.EffectType != GameplayEffect.EEffectType.Hidden && !other.CannotBeRemoved && !_classPassives.Contains(other);

    private static bool GrantsAny(GameplayEffect effect, List<EGameplayTag> tags)
    {
        if (effect.GrantedTags == null) return false;
        foreach (EGameplayTag tag in tags)
            if (tag != EGameplayTag.None && effect.GrantedTags.Contains(tag)) return true;
        return false;
    }

    // =========================================================
    // QUITAR EFECTOS DESDE EL CÓDIGO (por definición, por tag, de a uno, los debuffs, todos)
    // =========================================================

    // Quita todas las instancias activas de un efecto concreto, revirtiendo sus
    // modificadores, tags y escudo. Sirve para cancelar un buff antes de tiempo
    // (ej: el escudo de carga de Golpe Final si lo interrumpen).
    public void RemoveEffectsByDefinition(GameplayEffect definition) => RemoveEffectsByDefinition(definition, EEffectEnd.Removed);

    // Las que recorren ActiveEffects dejan los encadenados para el final de la vuelta (ver ENCADENADOS, en Effects).
    private void RemoveEffectsByDefinition(GameplayEffect definition, EEffectEnd why)
    {
        if (definition == null) return;

        _chainHold++;
        try
        {
            for (int i = ActiveEffects.Count - 1; i >= 0; i--)
                if (ActiveEffects[i].Definition == definition)
                    RemoveActiveEffect(ActiveEffects[i], why);
        }
        finally { EndChainHold(); }
    }

    // Quita todos los efectos activos que otorguen un tag dado (revirtiendo sus
    // modificadores/tags/escudo). Genérico: lo usa BreakInvisibility, y sirve para
    // cualquier "romper el estado X" (ej: una purga que quita todos los debuffs).
    public void RemoveEffectsWithTag(EGameplayTag tag)
    {
        _chainHold++;
        try
        {
            for (int i = ActiveEffects.Count - 1; i >= 0; i--)
                if (ActiveEffects[i].Definition.GrantedTags.Contains(tag))
                    RemoveActiveEffect(ActiveEffects[i]);
        }
        finally { EndChainHold(); }
    }

    // Quita UNA SOLA acumulación de las que otorgan un tag: la que está por vencer.
    // Devuelve true si encontró alguna.
    //
    // Es la versión "de a una" de RemoveEffectsWithTag, para los estados que se
    // CONSUMEN por uso y pueden acumularse (ej. las cargas del Castigo divino del
    // Paladín). Quitando una sola, el CONTEO del tag baja de 2 a 1 y el personaje
    // sigue teniendo el tag — o sea, sigue cargado para el próximo golpe. Con la
    // versión que las borra todas, un solo ataque se llevaba puestas las dos cargas.
    //
    // Se elige la más vieja (menor DurationRemaining) porque es la que se perdería
    // sola por tiempo: gastar esa primero es lo que más aprovecha las acumulaciones.
    public bool RemoveOneEffectWithTag(EGameplayTag tag)
    {
        ActiveGameplayEffect oldest = null;

        foreach (var active in ActiveEffects)
        {
            if (!active.Definition.GrantedTags.Contains(tag)) continue;
            if (oldest == null || active.DurationRemaining < oldest.DurationRemaining)
                oldest = active;
        }

        if (oldest == null) return false;

        RemoveActiveEffect(oldest);
        return true;
    }

    // Quita todos los efectos activos marcados como Debuff (EffectType, el mismo campo
    // que usa la UI para pintarlos de rojo). Lo usa el buff Imparable del Pirata para
    // limpiar CC/debuffs al activarse. Solo recorre ActiveEffects (efectos con
    // duración), así que el daño instantáneo no cuenta.
    public void RemoveAllDebuffs()
    {
        _chainHold++;
        try
        {
            for (int i = ActiveEffects.Count - 1; i >= 0; i--)
                if (ActiveEffects[i].Definition.EffectType == GameplayEffect.EEffectType.Debuff)
                    RemoveActiveEffect(ActiveEffects[i]);
        }
        finally { EndChainHold(); }
    }

    // Quita buffs Y debuffs, y deja lo que no es "estado de combate": los Hidden (los
    // cooldowns de las habilidades) y las pasivas de la clase. Es lo que se limpia al
    // morir: un muerto no conserva ni el veneno ni el Frenesí.
    public void RemoveAllBuffsAndDebuffs()
    {
        for (int i = ActiveEffects.Count - 1; i >= 0; i--)
        {
            ActiveGameplayEffect e = ActiveEffects[i];
            if (e.Definition.EffectType == GameplayEffect.EEffectType.Hidden) continue;
            if (_classPassives.Contains(e.Definition)) continue;
            RemoveActiveEffect(e, EEffectEnd.Cleared);   // morir / revivir: no encadena
        }
    }

    // =========================================================
    // "SE VA SOLO" (GameplayEffect.RemoveOn, 18 de noviembre de 2026)
    // =========================================================

    // Cuánto se puede alejar de donde recibió el efecto antes de que cuente como "se movió" (m). Con algo de margen: el
    // transform de un jugador llega por red y tiembla un poco aunque esté quieto.
    private const float MoveTolerance = 0.5f;

    // Le pasó 'what' a este personaje: se van los efectos que lo piden. Cuenta como "quitado antes" (encadena). Lo llaman
    // el camino del daño (recibir / hacer daño), CommitAbility (usar una habilidad) y el paso de cada frame (moverse).
    public void RemoveEffectsOnEvent(GameplayEffect.ERemoveOnEvent what)
    {
        if (ActiveEffects.Count == 0) return;
        _chainHold++;
        try
        {
            for (int i = ActiveEffects.Count - 1; i >= 0; i--)
            {
                if (i >= ActiveEffects.Count) continue;
                ActiveGameplayEffect active = ActiveEffects[i];
                if ((active.Definition.RemoveOn & what) == 0) continue;
                if (active.AppliedFrame == Time.frameCount) continue;   // lo que lo puso en este mismo frame no lo saca
                if (what == GameplayEffect.ERemoveOnEvent.Move &&
                    (transform.position - active.AppliedPosition).sqrMagnitude <= MoveTolerance * MoveTolerance) continue;
                RemoveActiveEffect(active);
            }
        }
        finally { EndChainHold(); }
    }

    // Quita TODOS los efectos activos de una (ej: al cambiar de clase). No encadena.
    public void RemoveAllActiveEffects()
    {
        for (int i = ActiveEffects.Count - 1; i >= 0; i--)
            RemoveActiveEffect(ActiveEffects[i], EEffectEnd.Cleared);
        _classPassives.Clear();
    }
}
