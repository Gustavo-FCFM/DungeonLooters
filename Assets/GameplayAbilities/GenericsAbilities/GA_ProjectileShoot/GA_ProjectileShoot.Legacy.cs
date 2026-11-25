using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet;
using FishNet.Object;

// ============================================================
// GA_ProjectileShoot — DATOS VIEJOS → FORMATO NUEVO
//
// Una parte de GA_ProjectileShoot (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameplayAbilities/GenericsAbilities/GA_ProjectileShoot.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class GA_ProjectileShoot
{

    protected override void OnUpgradeLegacyData(ref bool changed)
    {
        base.OnUpgradeLegacyData(ref changed);

        // En el orden en que se aplicaban: el aturdido de primer golpe iba ANTES del daño.
        UpgradeEffects(FirstHitEffects, EEffectWhen.OnFirstHit, EEffectTarget.Enemies, ref changed);
        UpgradeEffect(ref InstantDamageEffect, EEffectWhen.OnHit, EEffectTarget.Enemies, ref changed);
        UpgradeEffect(ref DurationEffect, EEffectWhen.OnHit, EEffectTarget.Enemies, ref changed);
        UpgradeEffects(AdditionalEffects, EEffectWhen.OnHit, EEffectTarget.Enemies, ref changed);
        UpgradeEffects(TargetEffects, EEffectWhen.OnHit, EEffectTarget.Allies, ref changed);
        UpgradeVisual(ref ImpactVFX, new AbilityVisual { When = EVisualWhen.OnImpact, DestroyTime = 1f }, ref changed);
    }
}
