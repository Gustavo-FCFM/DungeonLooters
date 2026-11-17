using UnityEngine;
using System.Collections.Generic;
using System;

// ============================================================
// AbilitySystemComponent — HABILIDADES, COOLDOWNS Y ECOS
//
// Una parte de AbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/AbilitySystemComponent.cs; ahí está también la lista de las partes.
// ============================================================
public partial class AbilitySystemComponent
{
    // =========================================================
    // HABILIDADES
    // =========================================================

    // Crea una instancia propia (clon) de una GameplayAbility y la agrega
    // a GrantedAbilities. Cada personaje necesita su propia instancia
    // porque una habilidad guarda estado (OwnerASC, cooldowns, etc.).
    public GameplayAbility GrantAbility(GameplayAbility template)
    {
        if (template == null) return null;
        GameplayAbility instance = Instantiate(template);
        instance.Initialize(this);
        instance.SourceTemplate = template; // para resolver su índice en GameplayAbilityRegistry
        GrantedAbilities.Add(instance);
        return instance;
    }

    // Vacía la lista de habilidades otorgadas (ej: antes de equipar una
    // clase nueva, para no arrastrar habilidades de la anterior).
    public void ClearGrantedAbilities() => GrantedAbilities.Clear();

    // Contador de cancelación del ataque básico (ver GameplayAbility.IsInterruptible).
    // Cada incremento corta el timing de golpe de las habilidades interrumpibles que
    // estén corriendo; las que arrancan después capturan el valor nuevo y no se enteran.
    public int CancelSerial { get; private set; }
    public void CancelInterruptibleAbilities() => CancelSerial++;

    // Punto único para que una GameplayAbility (ScriptableObject, sin
    // MonoBehaviour propio) arranque una corutina usando este componente
    // como dueño.
    public void StartAbilityCoroutine(System.Collections.IEnumerator routine)
        => StartCoroutine(routine);

    // =========================================================
    // COOLDOWNS
    // =========================================================

    // Busca si el CooldownEffect de una habilidad está actualmente activo
    // y, si es así, cuánto falta y cuánto duraba en total.
    public bool GetCooldownStatus(GameplayAbility ability, out float timeRemaining, out float totalDuration)
    {
        timeRemaining = 0f; totalDuration = 0f;
        GameplayEffect cooldown = ability?.CooldownEffectForDisplay;
        if (cooldown == null) return false;
        foreach (var e in ActiveEffects)
        {
            if (e.Definition == cooldown)
            {
                timeRemaining = e.DurationRemaining;
                totalDuration = e.TotalDuration;
                return true;
            }
        }
        return false;
    }

    // Adelanta (reduce) el cooldown de cualquier efecto activo que tenga
    // este tag — lo usa la carga de ultimate al golpear con otras
    // habilidades (ver GameplayAbility.ChargeUltimate).
    public void ReduceCooldownByTag(EGameplayTag tag, float amount)
    {
        foreach (var e in ActiveEffects)
            if (e.Definition.GrantedTags.Contains(tag) && !e.IsExpired)
                e.DurationRemaining = Mathf.Max(0, e.DurationRemaining - amount);
    }

    // Chequea si el personaje tiene suficiente de cada atributo que un
    // CostEffect vaya a restarle (ej: maná suficiente). No descuenta nada,
    // solo valida — el descuento real lo hace ApplyGameplayEffect.
    public bool CanAffordGameplayEffect(GameplayEffect costEffect)
    {
        if (costEffect == null) return true;
        foreach (var mod in costEffect.Modifiers)
        {
            if (mod.Type != Modifier.EModificationType.Add) continue;

            // Un costo que escala (el 50 % del maná MÁXIMO del corte avanzando del Filo danzante) se
            // cobra con los stats de uno mismo: se mide igual.
            float amount = mod.UseAttributeScaling || mod.UseTargetHealthScaling
                ? CalculateBaseMagnitude(mod, this) : mod.Magnitude;
            if (amount < 0 && Attributes.ContainsKey(mod.Attribute) &&
                Attributes[mod.Attribute].CurrentValue < Mathf.Abs(amount))
                return false;
        }
        return true;
    }

    // =========================================================
    // ECOS (la Proyección arcana de la Abjuración): desde quiénes salen además las habilidades
    // =========================================================

    // Los que hacen que las habilidades de este personaje salgan ADEMÁS desde otro (los ecos: la
    // Proyección arcana de la Abjuración). Ver IAbilityEchoSource y GameplayAbility.FireEchoes.
    private readonly List<IAbilityEchoSource> _echoSources = new List<IAbilityEchoSource>();

    public void RegisterEchoSource(IAbilityEchoSource source)
    {
        if (source != null && !_echoSources.Contains(source))
            _echoSources.Add(source);
    }

    public void UnregisterEchoSource(IAbilityEchoSource source)
    {
        _echoSources.Remove(source);
    }

    // Desde quiénes sale además 'ability', que acaba de disparar. Servidor.
    public void CollectEchoes(GameplayAbility ability, List<AbilityEcho> echoes)
    {
        for (int i = 0; i < _echoSources.Count; i++)
            _echoSources[i]?.CollectEchoes(ability, echoes);
    }
}
