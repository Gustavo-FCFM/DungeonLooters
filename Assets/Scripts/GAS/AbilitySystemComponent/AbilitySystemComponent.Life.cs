using UnityEngine;
using System.Collections.Generic;
using System;

// ============================================================
// AbilitySystemComponent — VIDA, MUERTE Y NIVEL
//
// Una parte de AbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/AbilitySystemComponent.cs; ahí está también la lista de las partes.
// ============================================================
public partial class AbilitySystemComponent
{
    // =========================================================
    // VIDA Y MUERTE
    // =========================================================

    // Marca al personaje como muerto (tag State_Dead) y notifica OnDeath.
    // La dispara SetCurrentAttributeValue cuando la Vida llega a 0.
    private void Die()
    {
        // Un muerto se lleva sus buffs y sus debuffs al morir, no al revivir: así el
        // cadáver no sigue quemándose ni brillando, y el que reaparece nace limpio.
        // Los cooldowns (Hidden) y las pasivas de la clase se quedan.
        RemoveAllBuffsAndDebuffs();

        AddTag(EGameplayTag.State_Dead);

        // El que lo mató se entera (OnKilledTarget). Solo en el servidor: es el único que
        // sabe quién dio el golpe final. Va antes de OnDeath, que puede reaparecerlo.
        AbilitySystemComponent killer = LastAttacker;
        NetworkAbilitySystemComponent netAsc = GetComponent<NetworkAbilitySystemComponent>();
        if (killer != null && !ReferenceEquals(killer, this) && (netAsc == null || netAsc.IsServerInitialized))
            killer.OnKilledTarget?.Invoke(this);

        OnDeath?.Invoke();
    }

    // Saca el tag de muerte y restaura la vida al máximo. La llama
    // NetworkGameManager al respawnear a un jugador.
    //
    // resetAbilities: devuelve las habilidades listas (cooldown a cero, cargas llenas,
    // menos la R). Es true también para la auto-revivida del Inmortal: revive para
    // seguir pegando, y con el kit en cooldown no tendría con qué.
    //
    // healthFraction: con cuánta vida vuelve, como fracción de la máxima. 1 = llena (el
    // respawn en la base); la Resurrección del Clérigo trae a un aliado con la mitad.
    public void Revive(bool resetAbilities = true, float healthFraction = 1f)
    {
        // Olvidar quién nos mató la vida anterior: si después caemos a una
        // DeathZone o morimos por otra vía, no queremos darle la baja a alguien
        // que nos pegó hace rato.
        LastAttacker = null;
        _lastHitBy.Clear();
        _lastHealedBy.Clear();

        // Por si algo se aplicó entre la muerte y el respawn (un veneno que llegó
        // tarde): la limpieza principal ya la hizo Die().
        RemoveAllBuffsAndDebuffs();

        // Las habilidades vuelven listas: cooldown a cero y cargas llenas — menos la
        // definitiva (R), que conserva lo suyo. La marca de cuál es la R la puso
        // PlayerController al equipar (GameplayAbility.IsUltimate).
        if (resetAbilities)
            foreach (GameplayAbility ability in GrantedAbilities)
                if (ability != null && !ability.IsUltimate) ability.ResetForRespawn();

        RemoveTag(EGameplayTag.State_Dead);
        if (Attributes.ContainsKey(EAttributeType.MaxHealth))
        {
            float max = Attributes[EAttributeType.MaxHealth].CurrentValue;
            SetCurrentAttributeValue(EAttributeType.Health, Mathf.Max(1f, max * Mathf.Clamp01(healthFraction)));
        }
        OnRevive?.Invoke();
    }

    // =========================================================
    // EXPERIENCIA Y NIVEL
    // =========================================================

    // Suma experiencia y sube de nivel las veces que corresponda (soporta
    // ganar de golpe experiencia suficiente para varios niveles). No hace
    // nada si ya está en el nivel máximo.
    public void GainExperience(float amount)
    {
        if (!Attributes.ContainsKey(EAttributeType.Exp)) return;
        if (GetAttributeValue(EAttributeType.Level) >= MaxLevel) return;

        float newExp = GetAttributeValue(EAttributeType.Exp) + amount;
        float maxExp = GetAttributeValue(EAttributeType.MaxExp);

        while (newExp >= maxExp)
        {
            newExp -= maxExp;
            HandleLevelUp();
            if (GetAttributeValue(EAttributeType.Level) >= MaxLevel) { newExp = 0; OnMaxLevelReached?.Invoke(); break; }
            maxExp = Mathf.Round(maxExp * 1.5f);
            SetCurrentAttributeValue(EAttributeType.MaxExp, maxExp);
        }
        SetCurrentAttributeValue(EAttributeType.Exp, newExp);
    }

    // Aplica el crecimiento de stats de la clase actual (StatGrowthPerLevel),
    // sube el nivel, rellena Vida/Maná al máximo, y dispara OnLevelUp
    // (y OnMaxLevelReached si corresponde).
    private void HandleLevelUp()
    {
        float newLevel = GetAttributeValue(EAttributeType.Level) + 1;
        SetCurrentAttributeValue(EAttributeType.Level, newLevel);

        if (CurrentClass != null)
        {
            foreach (var growth in CurrentClass.StatGrowthPerLevel)
                if (Attributes.ContainsKey(growth.Attribute))
                    Attributes[growth.Attribute].BaseValue += growth.AmountPerLevel;
            RecalculateAllAttributes();
            SetCurrentAttributeValue(EAttributeType.Health, GetAttributeValue(EAttributeType.MaxHealth));
            SetCurrentAttributeValue(EAttributeType.Mana,   GetAttributeValue(EAttributeType.MaxMana));
        }

        OnLevelUp?.Invoke();
        if (newLevel >= MaxLevel) OnMaxLevelReached?.Invoke();
    }

    // Lleva al personaje HASTA 'targetLevel' aplicando, nivel por nivel, el mismo
    // crecimiento de stats que la experiencia (HandleLevelUp). Nunca baja de nivel.
    //
    // Existe para la progresión COMPARTIDA del modo Mercenarios: ahí el nivel es una
    // propiedad del EQUIPO (sale de una bolsa de experiencia común), así que el
    // servidor tiene que poder decir "este jugador va en el nivel 2" sin fabricar
    // experiencia individual. También es lo que hace que cambiar de clase no cueste
    // progreso: equipar una clase nueva reinicia al personaje a nivel 1, y el modo de
    // juego le vuelve a poner el nivel del equipo en el tick siguiente.
    //
    // Es idempotente: llamarla con un nivel que ya se tiene no hace nada.
    public void SetLevelTo(int targetLevel)
    {
        if (!Attributes.ContainsKey(EAttributeType.Level)) return;

        targetLevel = Mathf.Min(targetLevel, MaxLevel);

        // Tope de seguridad: si algo dejara el nivel sin subir, un while sobre un
        // atributo que no avanza congelaría el juego entero.
        int guard = 0;
        while (GetAttributeValue(EAttributeType.Level) < targetLevel && guard++ < 100)
            HandleLevelUp();
    }

    // Dispara OnMaxLevelReached a mano (sin pasar por GainExperience). Lo usa la
    // capa de red para hacer aparecer la selección de subclase en el cliente DUEÑO:
    // el nivel sube en el SERVIDOR, así que el ASC local del dueño remoto nunca se
    // enteraría — ver NetworkAbilitySystemComponent.TargetShowSubclassSelection.
    public void TriggerMaxLevelReached() => OnMaxLevelReached?.Invoke();
}
