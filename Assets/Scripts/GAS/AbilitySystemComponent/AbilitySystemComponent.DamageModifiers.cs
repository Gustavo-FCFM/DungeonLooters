using UnityEngine;
using System.Collections.Generic;
using System;

// ============================================================
// AbilitySystemComponent — LO QUE CAMBIA EL NÚMERO
//
// Una parte de AbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/AbilitySystemComponent.cs; ahí está también la lista de las partes.
//
// Las defensas del que recibe (vulnerabilidad, resistencia, armadura), los modificadores de
// daño y de curación SALIENTES (pasivas, críticos), los ENTRANTES (bloqueos) y la espalda.
// ============================================================
public partial class AbilitySystemComponent
{
    // =========================================================
    // DEFENSAS (del que RECIBE el golpe)
    // =========================================================

    // Aplica las tres defensas al daño que está por entrar, en este orden:
    //
    //   1) VULNERABILIDAD (%): sube el daño. 0.1 = recibe 10% más.
    //   2) RESISTENCIA (%): lo baja. 0.2 = evita el 20%.
    //   3) Redondeo hacia ABAJO.
    //   4) DEFENSA (fija): resta un valor plano.
    //
    // Ejemplo del diseño: 10 de daño, +10% vulnerabilidad → 11; −20% resistencia →
    // 8.8; redondeo → 8; defensa 1 → 7. Recién después entran escudo y vida.
    //
    // Las dos porcentuales se combinan en UN multiplicador y se aplican por igual al
    // daño físico y al mágico (son "cuánto duele todo lo que entra"). La DEFENSA en
    // cambio solo recorta el FÍSICO: el daño mágico la ignora, igual que ya penetra
    // el escudo — es lo que hace que subir defensa no vuelva a nadie inmune a magia.
    //
    // LA ARMADURA ES UN PORCENTAJE, no una resta: mitigación = Def / (Def + ArmorConstant).
    // Antes era una resta fija por golpe, y con los golpes chicos de este juego eso no
    // convive: Def 5 le comía el 83 % a la daga de 6 del pícaro y el 10 % al golpe de
    // 50 del jefe. El porcentaje trata igual a la daga y al jefe, nunca llega a la
    // inmunidad, y cada punto vale menos que el anterior (seguro de apilar con buffs).
    //
    // Def viene de la clase de armadura de D&D menos 10: pícaro 3 (23 %), bárbaro 5
    // (33 %), paladín 8 (44 %). ArmorConstant es "cuánta armadura hace falta para
    // reducir el daño a la mitad": bajarla hace que la armadura valga más.
    private const float ArmorConstant = 10f;

    // Daño mínimo que deja pasar la armadura: un golpe físico que conecta nunca hace
    // menos de 1.
    private const float MinDamageAfterDefense = 1f;

    private void ApplyDefenses(ref float physicalDamage, ref float magicDamage)
    {
        float vulnerability = GetAttributeValue(EAttributeType.Vulnerability);
        float resistance    = GetAttributeValue(EAttributeType.Resistance);

        // Tope de resistencia: al 100% el daño sería 0 (inmunidad total) y por encima
        // el golpe pasaría a CURAR. Se corta en 90% por más que se apilen buffs.
        resistance = Mathf.Min(resistance, 0.9f);

        float multiplier = (1f + vulnerability) * (1f - resistance);
        if (multiplier < 0f) multiplier = 0f; // una vulnerabilidad negativa enorme tampoco cura

        physicalDamage *= multiplier;
        magicDamage    *= multiplier;

        // Redondeo hacia abajo (8.8 → 8): a favor de quien recibe, y deja números
        // enteros en los indicadores de daño.
        physicalDamage = Mathf.Floor(physicalDamage);
        magicDamage    = Mathf.Floor(magicDamage);

        // Armadura: porcentaje sobre el físico (ver ArmorConstant). Se redondea hacia
        // abajo otra vez para que el indicador siga mostrando enteros, con piso de 1: un
        // golpe que conecta nunca hace NADA. El piso solo aplica a golpes que traían daño
        // físico: si el ataque era puro mágico (physicalDamage 0), no se inventa daño.
        float defense = GetAttributeValue(EAttributeType.Def);
        if (defense > 0f && physicalDamage > 0f)
        {
            float mitigation = defense / (defense + ArmorConstant);
            physicalDamage = Mathf.Max(MinDamageAfterDefense, Mathf.Floor(physicalDamage * (1f - mitigation)));
        }
    }

    // =========================================================
    // DAÑO SALIENTE (modificadores + críticos)
    // =========================================================

    // Modificadores de daño saliente que las pasivas de la clase registran acá
    // (ver IDamageModifier). Solo existen mientras la clase que los trae está
    // equipada: se registran/desregistran desde los PassiveBehaviorsPrefab. El core
    // no sabe qué es un backstab; solo recorre la lista y resuelve las banderas.
    private readonly List<IDamageModifier> _damageModifiers = new List<IDamageModifier>();

    public void RegisterDamageModifier(IDamageModifier modifier)
    {
        if (modifier != null && !_damageModifiers.Contains(modifier))
            _damageModifiers.Add(modifier);
    }

    public void UnregisterDamageModifier(IDamageModifier modifier)
    {
        _damageModifiers.Remove(modifier);
    }

    // Lo mismo para las curaciones que reparte este personaje (ver IHealModifier).
    private readonly List<IHealModifier> _healModifiers = new List<IHealModifier>();

    public void RegisterHealModifier(IHealModifier modifier)
    {
        if (modifier != null && !_healModifiers.Contains(modifier))
            _healModifiers.Add(modifier);
    }

    public void UnregisterHealModifier(IHealModifier modifier)
    {
        _healModifiers.Remove(modifier);
    }

    // Corre los modificadores de curación de ESTE personaje (el que cura) sobre una
    // curación a 'target'. La llama ExecuteInstantEffect sobre el ASC del autor.
    public float ResolveOutgoingHeal(AbilitySystemComponent target, float magnitude, bool isPeriodicTick)
    {
        if (_healModifiers.Count == 0) return magnitude;

        HealContext ctx = new HealContext
        {
            Source         = this,
            Target         = target,
            IsPeriodicTick = isPeriodicTick,
            Magnitude      = magnitude,
        };

        for (int i = 0; i < _healModifiers.Count; i++)
            _healModifiers[i]?.ModifyOutgoingHeal(ref ctx);

        return Mathf.Max(0f, ctx.Magnitude);
    }

    // Corre el pipeline de daño SALIENTE de ESTE personaje (el atacante) contra
    // 'target' y devuelve la magnitud final. Recorre los modificadores registrados
    // (que pueden mutar la magnitud y marcar críticos) y después resuelve las
    // banderas de crítico. La llama ExecuteInstantEffect sobre el ASC del atacante:
    // sourceASC.ResolveOutgoingDamage(this, magnitud, isPeriodicTick).
    //
    // Sobre las capas de crítico (con CritDamage = 2 y un golpe base de 6):
    //   1) "Es crítico" (x2 → 12): el backstab (por la espalda) y el crítico
    //      asegurado son dos formas de lo MISMO. NO se apilan entre sí.
    //   2) "Crítico mejorado" (otro x2): capa aparte que SÍ se acumula con la
    //      anterior (espalda + mejorado = x4 → 24).
    public float ResolveOutgoingDamage(AbilitySystemComponent target, float magnitude, bool isPeriodicTick)
        => ResolveOutgoingDamage(target, magnitude, isPeriodicTick, out _);

    // Igual, pero además dice si el golpe salió CRÍTICO (cualquiera de las dos capas):
    // lo usan el número amarillo y la X de crítico.
    // forceCrit: el efecto que pega pide crítico ("Crítico si el objetivo tiene"); los ticks no critean.
    public float ResolveOutgoingDamage(AbilitySystemComponent target, float magnitude, bool isPeriodicTick,
                                       out bool critical, bool forceCrit = false)
    {
        DamageContext ctx = new DamageContext
        {
            Source         = this,
            Target         = target,
            IsPeriodicTick = isPeriodicTick,
            Magnitude      = magnitude,
        };

        for (int i = 0; i < _damageModifiers.Count; i++)
            _damageModifiers[i]?.ModifyOutgoingDamage(ref ctx);

        magnitude = ctx.Magnitude;

        // Crítico asegurado: mecánica GENERAL (cualquier habilidad puede otorgar el
        // tag, ej. Emboscada sombría), no de una clase puntual, así que se resuelve
        // acá y no en un modificador. Es otra forma de "es crítico" —no se apila con
        // el backstab— y se consume al usarlo, haya criteado o no. Los ticks de DoT
        // no lo consumen (no critean).
        bool isCrit = ctx.IsCrit || (forceCrit && !isPeriodicTick);
        if (!isPeriodicTick && HasTag(EGameplayTag.Status_GuaranteedCrit))
        {
            isCrit = true;
            RemoveTag(EGameplayTag.Status_GuaranteedCrit);
        }

        // Resolución del crítico (regla core): cada capa marcada multiplica por
        // CritDamage. Por defecto x2 si la clase no configuró el atributo.
        if (isCrit || ctx.IsImprovedCrit)
        {
            float critDamage = GetAttributeValue(EAttributeType.CritDamage);
            if (critDamage < 1f) critDamage = 2f;
            if (isCrit)             magnitude *= critDamage;
            if (ctx.IsImprovedCrit) magnitude *= critDamage;
        }

        critical = isCrit || ctx.IsImprovedCrit;
        return magnitude;
    }

    // =========================================================
    // DAÑO ENTRANTE (bloqueo direccional)
    // =========================================================

    // Modificadores de daño ENTRANTE que registran las mecánicas de bloqueo de
    // ESTE personaje (ver IIncomingDamageModifier). Gemelo de _damageModifiers,
    // pero del lado del que recibe: existen mientras la barrera/postura esté
    // levantada, y se dan de baja al bajarla.
    private readonly List<IIncomingDamageModifier> _incomingDamageModifiers = new List<IIncomingDamageModifier>();

    public void RegisterIncomingDamageModifier(IIncomingDamageModifier modifier)
    {
        if (modifier != null && !_incomingDamageModifiers.Contains(modifier))
            _incomingDamageModifiers.Add(modifier);
    }

    public void UnregisterIncomingDamageModifier(IIncomingDamageModifier modifier)
    {
        _incomingDamageModifiers.Remove(modifier);
    }

    // Corre el pipeline de daño ENTRANTE sobre un golpe que va a recibir este
    // personaje, ANTES de las defensas (ver ExecuteInstantEffect).
    //
    // Recibe el daño ya separado en físico y mágico porque las etapas siguientes
    // los tratan distinto (la Defensa solo recorta el físico; el escudo cuesta el
    // doble contra el mágico). Pero un bloqueo frena "el golpe", no una de sus
    // componentes — así que el pipeline trabaja sobre el TOTAL y el resultado se
    // reparte de vuelta en la MISMA proporción, sin inventar ni perder la mezcla.
    private void ResolveIncomingDamage(AbilitySystemComponent sourceASC, ref float physicalDamage,
                                       ref float magicDamage, bool isPeriodicTick)
    {
        if (_incomingDamageModifiers.Count == 0) return;

        float total = physicalDamage + magicDamage;
        if (total <= 0f) return;

        IncomingDamageContext ctx = new IncomingDamageContext
        {
            Source         = sourceASC,
            Target         = this,
            IsPeriodicTick = isPeriodicTick,
            Magnitude      = -total,   // negativa = daño, igual que en el pipeline saliente
        };

        for (int i = 0; i < _incomingDamageModifiers.Count; i++)
            _incomingDamageModifiers[i]?.ModifyIncomingDamage(ref ctx);

        float remaining = Mathf.Max(0f, -ctx.Magnitude);
        if (Mathf.Approximately(remaining, total)) return;

        // Evento de juego: lo frenó un bloqueo de ESTE personaje.
        SendGameplayEvent(EGameplayEvent.Blocked, sourceASC, total - remaining, isPeriodicTick);

        float ratio = remaining / total;
        physicalDamage *= ratio;
        magicDamage    *= ratio;
    }

    // Readout del Crítico mejorado para el HUD/nameplate. El estado y la lógica
    // viven en el modificador FirstStrikeCritModifier (en el prefab del Asesino);
    // acá solo lo buscamos y delegamos, para que el core no dependa de la clase más
    // que por este acceso opcional. Se cachea y se re-busca si el modificador
    // desaparece (cambio de clase → GetComponentInChildren vuelve a dar null).
    private FirstStrikeCritModifier _firstStrikeCache;
    private FirstStrikeCritModifier FirstStrike
    {
        get
        {
            if (_firstStrikeCache == null) _firstStrikeCache = GetComponentInChildren<FirstStrikeCritModifier>();
            return _firstStrikeCache;
        }
    }

    // ¿El Crítico mejorado está disponible (pasó su reutilización global)? En clases
    // sin la pasiva no hay modificador, así que es false.
    public bool IsFirstStrikeReady => FirstStrike != null && FirstStrike.IsReady;

    // ¿Le puedo clavar un Crítico mejorado a ESTE enemigo ahora? (reutilización
    // global + "frescura" del objetivo). Ver la nota de red en FirstStrikeCritModifier.
    public bool IsFirstStrikeReadyAgainst(AbilitySystemComponent target)
        => FirstStrike != null && FirstStrike.IsReadyAgainst(target);

    // =========================================================
    // BACKSTAB (helper posicional, lo usa BackstabDamageModifier)
    // =========================================================

    // Ángulo trasero a partir del cual un atacante cuenta como "por la espalda".
    // Dot(forward, dirHaciaAtacante) < este umbral ⇒ el atacante está detrás.
    // -0.25 ≈ arco trasero de ~150° (más permisivo que exactamente 180°).
    private const float BackstabDotThreshold = -0.25f;

    // True si 'attacker' está por detrás de ESTE personaje (usando hacia dónde
    // mira este objetivo, no el atacante). Lo usa BackstabDamageModifier (el Ataque
    // Furtivo del Pícaro) para decidir el crítico por la espalda.
    public bool IsBackstab(AbilitySystemComponent attacker)
    {
        if (attacker == null) return false;
        Vector3 toAttacker = attacker.transform.position - transform.position;
        toAttacker.y = 0;
        if (toAttacker.sqrMagnitude < 0.0001f) return false;
        return Vector3.Dot(transform.forward, toAttacker.normalized) < BackstabDotThreshold;
    }
}
