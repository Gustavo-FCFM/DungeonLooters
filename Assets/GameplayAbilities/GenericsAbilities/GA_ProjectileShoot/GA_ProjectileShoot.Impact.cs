using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet;
using FishNet.Object;

// ============================================================
// GA_ProjectileShoot — EL IMPACTO (explosión, zona y efectos al primer golpe)
//
// Una parte de GA_ProjectileShoot (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameplayAbilities/GenericsAbilities/GA_ProjectileShoot.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class GA_ProjectileShoot
{

    // =========================================================
    // IMPACTO (lo llama el proyectil al chocar)
    // =========================================================

    // Lo llama el proyectil (GC_Projectile, en el servidor) donde choca y se deshace. 'boosted': si
    // en el camino cruzó algo que potencia proyectiles (sale la explosión potenciada).
    public void OnProjectileImpact(Vector3 point, bool boosted = false)
    {
        if (!IsServer || OwnerASC == null) return;

        // La copia de un eco pasa lo suyo (la mitad del daño, sin cargar la definitiva).
        if (boosted && BoostedExplosion != null)
        {
            _boostedExplosion = GA_InstantAoE.Deployable(BoostedExplosion, OwnerASC, _boostedExplosion);
            _boostedExplosion.DamageScale = DamageScale;
            _boostedExplosion.IsEcho      = IsEcho;
            _boostedExplosion.DetonateAt(point);
        }
        else if (ImpactExplosion != null)
        {
            _impactExplosion = GA_InstantAoE.Deployable(ImpactExplosion, OwnerASC, _impactExplosion);
            _impactExplosion.DamageScale = DamageScale;
            _impactExplosion.IsEcho      = IsEcho;
            _impactExplosion.DetonateAt(point);
        }

        // La zona, en el piso de abajo del choque (contra una pared, cae al pie de la pared).
        if (ImpactZone != null && GA_ContinuousAoE.TryGroundPoint(point, point.y, out Vector3 ground))
        {
            _impactZone = GA_ContinuousAoE.Deployable(ImpactZone, OwnerASC, _impactZone);
            _impactZone.DamageScale = DamageScale;
            _impactZone.IsEcho      = IsEcho;
            _impactZone.DeployZoneAt(ground);
        }
    }
}
