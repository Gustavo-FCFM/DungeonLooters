using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — DATOS VIEJOS → FORMATO NUEVO
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // DATOS VIEJOS → FORMATO NUEVO
    //
    // Cada GA tenía sus propios campos sueltos para los efectos y los VFX. Esos campos
    // siguen existiendo (escondidos, con su nombre de siempre) SOLO para poder leer lo que
    // ya estaba cargado en los assets: al cargarse, cada habilidad los pasa a las listas
    // nuevas y los vacía. No hay que volver a cargar nada a mano.
    //
    // Pasa en memoria cada vez que se carga el asset (en el editor y en la build), así que
    // funciona aunque el asset nunca se vuelva a guardar. 'Mercenarios ▸ Guardar las
    // habilidades en el formato nuevo' los guarda en disco de una vez.
    // =========================================================

    // True si al cargarse pasó algo del formato viejo al nuevo y todavía no se guardó.
    [System.NonSerialized] public bool UpgradedOnLoad;

    protected virtual void OnEnable()
    {
        if (UpgradeLegacyData()) UpgradedOnLoad = true;
    }

    // Pasa los campos viejos a las listas. true = cambió algo.
    public bool UpgradeLegacyData()
    {
        if (Effects == null) Effects = new List<AbilityEffect>();
        if (Visuals == null) Visuals = new List<AbilityVisual>();

        bool changed = false;
        OnUpgradeLegacyData(ref changed);
        return changed;
    }

    // Cada GA con campos viejos los pasa acá (llamando primero a la base).
    protected virtual void OnUpgradeLegacyData(ref bool changed) { }

    // Ayudas para OnUpgradeLegacyData: agregan y vacían el campo viejo.
    protected void UpgradeEffect(ref GameplayEffect legacy, EEffectWhen when, EEffectTarget applyTo, ref bool changed)
    {
        if (legacy == null) return;
        Effects.Add(new AbilityEffect(legacy, when, applyTo));
        legacy = null;
        changed = true;
    }

    protected void UpgradeEffects(List<GameplayEffect> legacy, EEffectWhen when, EEffectTarget applyTo, ref bool changed)
    {
        if (legacy == null || legacy.Count == 0) return;
        foreach (GameplayEffect e in legacy)
            if (e != null) Effects.Add(new AbilityEffect(e, when, applyTo));
        legacy.Clear();
        changed = true;
    }

    protected void UpgradeVisual(ref GameObject legacy, AbilityVisual visual, ref bool changed)
    {
        if (legacy == null) return;
        visual.VFXPrefab = legacy;
        Visuals.Add(visual);
        legacy = null;
        changed = true;
    }
}
