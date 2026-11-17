using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — LOS ECOS
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // ECOS (la Proyección arcana de la Abjuración — 22 de octubre de 2026; ver AbilityEcho)
    // =========================================================

    // Parte del daño que hacen sus golpes: 1 lo normal, 0.5 las copias de la Proyección.
    // Se lee en ApplyHitEffects (los efectos que bajan vida salen escalados).
    [System.NonSerialized] public float DamageScale = 1f;

    // Esta instancia es la copia de un eco: no carga la definitiva ni hace ecos propios.
    [System.NonSerialized] public bool IsEcho;

    [System.NonSerialized] private GameplayAbility _echoInstance;
    [System.NonSerialized] private Dictionary<GameplayEffect, GameplayEffect> _scaledEffects;
    private static readonly List<AbilityEcho> s_echoBuffer = new List<AbilityEcho>();

    // Lo llama cada habilidad en el momento en que DISPARA (servidor): pregunta al dueño desde
    // quiénes sale además (IAbilityEchoSource) y llama a 'fire' con la copia y el personaje desde
    // el que sale. La copia es una sola instancia por habilidad, reusada (sus proyectiles la
    // guardan para aplicar los efectos al chocar).
    protected void FireEchoes<T>(System.Action<T, AbilitySystemComponent> fire) where T : GameplayAbility
    {
        if (IsEcho || !IsServer || OwnerASC == null || fire == null) return;

        s_echoBuffer.Clear();
        OwnerASC.CollectEchoes(this, s_echoBuffer);
        if (s_echoBuffer.Count == 0) return;

        // Copia de la lista: disparar puede volver a preguntar (otra habilidad, otro eco).
        AbilityEcho[] echoes = s_echoBuffer.ToArray();
        foreach (AbilityEcho echo in echoes)
        {
            if (echo.From == null || echo.From.HasTag(EGameplayTag.State_Dead)) continue;
            if (EchoInstance(echo.DamageScale) is T copy) fire(copy, echo.From);
        }
    }

    private GameplayAbility EchoInstance(float damageScale)
    {
        if (_echoInstance == null)
        {
            _echoInstance = Instantiate(this);
            _echoInstance.Initialize(OwnerASC);
            _echoInstance.SourceTemplate  = SourceTemplate != null ? SourceTemplate : this;
            _echoInstance.ReportEndAs     = ReportEndAs != null ? ReportEndAs : this;
            _echoInstance.IsEcho          = true;
            _echoInstance.CooldownEffect  = null;
            _echoInstance.CostEffect      = null;
            _echoInstance.DisableCharges();
            _echoInstance.StackSnapshot     = StackSnapshot;
            _echoInstance.EffectCooldownKey = CooldownKey;
        }
        _echoInstance.DamageScale = DamageScale * damageScale;
        return _echoInstance;
    }

    // El efecto con el daño escalado (DamageScale), cacheado. Los que no bajan vida van tal cual.
    private GameplayEffect ScaledForDamage(GameplayEffect effect)
    {
        if (effect == null || Mathf.Approximately(DamageScale, 1f)) return effect;

        _scaledEffects ??= new Dictionary<GameplayEffect, GameplayEffect>();
        // (uno que no baja vida se guarda tal cual: vale para cualquier escala)
        if (_scaledEffects.TryGetValue(effect, out GameplayEffect scaled) && scaled != null &&
            (scaled == effect || Mathf.Approximately(scaled.CreatedWithDamageScale, DamageScale)))
            return scaled;

        scaled = effect.CreateDamageScaledCopy(DamageScale);
        _scaledEffects[effect] = scaled;
        return scaled;
    }
}
