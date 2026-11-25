using UnityEngine;
using System.Collections.Generic;

// ============================================================
// Entity_ShieldBarrier — EL BLOQUEO (IIncomingDamageModifier) Y DEVOLVER (Guardián)
//
// Una parte de Entity_ShieldBarrier (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/Entity_ShieldBarrier.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class Entity_ShieldBarrier
{

    // =========================================================
    // BLOQUEO (IIncomingDamageModifier)
    // =========================================================

    // Corre en el ASC del que RECIBE el golpe, antes de sus defensas. Ver
    // AbilitySystemComponent.ResolveIncomingDamage.
    public void ModifyIncomingDamage(ref IncomingDamageContext ctx)
    {
        if (!_raised || _ownerASC == null || _box == null) return;

        // Sin atacante no hay trayectoria que cortar (caídas, zonas de muerte).
        if (ctx.Source == null || ctx.Target == null) return;

        // El fuego amigo no se bloquea (y un escudo no te protege de vos mismo).
        if (ReferenceEquals(ctx.Source, _ownerASC) || _ownerASC.IsAllyOf(ctx.Source)) return;

        float incoming = -ctx.Magnitude;
        if (incoming <= 0f) return;

        // ¿La barrera se interpone de verdad entre el atacante y la víctima? La cápsula no
        // pregunta: cubre al dueño (y solo a él) desde cualquier lado.
        Vector3 hitPoint;
        if (Omnidirectional)
        {
            if (!ReferenceEquals(ctx.Target, _ownerASC)) return;
            hitPoint = OmniHitPoint(ctx.Source.transform.position);
        }
        else if (!Blocks(ctx.Source.transform.position, ctx.Target.transform.position, out hitPoint)) return;

        // PARRY: en la ventana de recién levantado, el golpe se frena ENTERO y no gasta
        // energía. Si el atacante está cerca (cuerpo a cuerpo), además queda aturdido; a
        // uno de lejos solo se le frena el golpe.
        if (IsParrying)
        {
            ctx.Magnitude  = 0f;
            ctx.WasBlocked = true;

            if (ParryStunEffect != null &&
                Vector3.Distance(ctx.Source.transform.position, _ownerASC.transform.position) <= ParryMeleeRange)
                ctx.Source.ApplyGameplayEffect(ParryStunEffect, _ownerASC);

            OnDamageBlocked?.Invoke(incoming, hitPoint);
            BroadcastFlash(hitPoint);
            if (!ctx.IsPeriodicTick)
            {
                ChargeUltimateForBlock(ctx.Source);
                ReflectToAttacker(ctx.Source, incoming);
                PunishAttacker(ctx.Source);
            }
            return;
        }

        float energy = _ownerASC.GetAttributeValue(EAttributeType.Energy);
        // Sin energía no frena nada (la habilidad ya está bajando el escudo). Con "Energía por punto"
        // en 0 el escudo es gratis y no mira la energía: el del esqueleto (los NPCs no tienen).
        if (EnergyPerDamageBlocked > 0f && energy <= 0f) return;

        float blocked = incoming * DamageReduction;

        // Si no alcanza la energía para frenar todo, se frena solo lo que se pueda
        // pagar y el resto pasa. Así el escudo se "rompe" en el golpe grande que lo
        // agota, en vez de aguantarlo entero gratis.
        float cost = blocked * EnergyPerDamageBlocked;
        if (EnergyPerDamageBlocked > 0f && cost > energy)
        {
            blocked = energy / EnergyPerDamageBlocked;
            cost    = energy;
        }

        if (cost > 0f)
            _ownerASC.SetCurrentAttributeValue(EAttributeType.Energy, energy - cost);

        ctx.Magnitude  = -(incoming - blocked);
        ctx.WasBlocked = true;

        OnDamageBlocked?.Invoke(blocked, hitPoint);
        BroadcastFlash(hitPoint);
        if (!ctx.IsPeriodicTick)
        {
            ChargeUltimateForBlock(ctx.Source);
            ReflectToAttacker(ctx.Source, blocked);
            PunishAttacker(ctx.Source);
        }
    }

    // Le aplica OnBlockAttackerEffect al atacante de un golpe frenado (las heridas del
    // Samurái). Corre en el servidor, donde se resuelven los bloqueos.
    private void PunishAttacker(AbilitySystemComponent attacker)
    {
        if (OnBlockAttackerEffect == null || attacker == null || _ownerASC == null) return;
        if (attacker.HasTag(EGameplayTag.State_Dead) || !_ownerASC.IsEnemyOf(attacker)) return;
        if (ReferenceEquals(attacker, _lastPunishedAttacker) && Time.frameCount == _lastPunishedFrame) return;

        _lastPunishedAttacker = attacker;
        _lastPunishedFrame    = Time.frameCount;
        attacker.ApplyGameplayEffect(OnBlockAttackerEffect, _ownerASC);
    }

    // Le adelanta la definitiva al dueño por un golpe frenado (ver UltimateSecondsPerBlock).
    // Corre en el servidor, donde se resuelven los bloqueos.
    private void ChargeUltimateForBlock(AbilitySystemComponent attacker)
    {
        if (UltimateSecondsPerBlock <= 0f || _ownerASC == null) return;
        if (ReferenceEquals(attacker, _lastBlockedAttacker) && Time.frameCount == _lastBlockedFrame) return;

        _lastBlockedAttacker = attacker;
        _lastBlockedFrame    = Time.frameCount;
        _ownerASC.ReduceCooldownByTag(EGameplayTag.Ability_Cooldown_Ultimate, UltimateSecondsPerBlock);
    }

    // =========================================================
    // DEVOLVER (Guardián)
    // =========================================================

    // Le devuelve al atacante cuerpo a cuerpo lo frenado (por ReflectMeleeFraction), como
    // un golpe físico normal del dueño de la barrera: pasa por la armadura del atacante y
    // su propio escudo lo puede frenar, pero no devolver (ver s_reflecting).
    private void ReflectToAttacker(AbilitySystemComponent attacker, float blocked)
    {
        if (ReflectMeleeFraction <= 0f || s_reflecting || attacker == null || blocked <= 0f) return;
        if (attacker.HasTag(EGameplayTag.State_Dead)) return;
        if (Vector3.Distance(attacker.transform.position, _ownerASC.transform.position) > ParryMeleeRange) return;

        GameplayEffect damage = ReflectEffect();
        damage.Modifiers[0].Magnitude = -blocked * ReflectMeleeFraction;

        s_reflecting = true;
        try     { attacker.ApplyGameplayEffect(damage, _ownerASC); }
        finally { s_reflecting = false; }
    }

    // Un efecto de daño plano, creado una vez y reusado (la magnitud se pone en cada golpe).
    private GameplayEffect ReflectEffect()
    {
        if (_reflectEffect != null) return _reflectEffect;

        _reflectEffect = ScriptableObject.CreateInstance<GameplayEffect>();
        _reflectEffect.name       = "GE_ReflectedDamage(runtime)";
        _reflectEffect.Duration   = 0f;
        _reflectEffect.EffectType = GameplayEffect.EEffectType.Hidden;
        _reflectEffect.Modifiers  = new List<Modifier>
        {
            new Modifier { Attribute = EAttributeType.Health, Type = Modifier.EModificationType.Add },
        };
        return _reflectEffect;
    }

    // Hacia dónde apunta el dueño: el giro del cuerpo y la inclinación de la mira
    // (UpperBodyAim, que llega a todas las copias). El parry manda ahí los proyectiles.
    public Vector3 AimDirection
    {
        get
        {
            if (_ownerASC == null) return transform.forward;
            float pitch = _aim != null ? _aim.CurrentPitch : 0f;
            return Quaternion.Euler(pitch, _ownerASC.transform.eulerAngles.y, 0f) * Vector3.forward;
        }
    }

    public AbilitySystemComponent Owner => _ownerASC;

    // True si un proyectil que toca la barrera AHORA se devuelve en vez de pararse.
    public bool ReflectsProjectilesNow => ReflectProjectilesOnParry && IsParrying;
}
