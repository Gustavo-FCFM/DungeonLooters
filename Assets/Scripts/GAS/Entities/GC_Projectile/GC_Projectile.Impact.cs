using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;

// ============================================================
// GC_Projectile — EL IMPACTO (servidor)
//
// Una parte de GC_Projectile (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/GC_Projectile.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class GC_Projectile
{

    // =========================================================
    // IMPACTO — autoridad de servidor
    // =========================================================

    // Lo que pasa al encontrar algo en el barrido ('point': dónde estaba el proyectil al
    // tocarlo): el VFX de impacto en todos los peers y, si es un personaje enemigo, sus
    // efectos (un aliado se atraviesa); si es un obstáculo sólido, se despawnea.
    private void HandleHit(Collider other, Vector3 point)
    {
        // Se ignora TODO lo que cuelgue del lanzador, no solo el GameObject exacto de su
        // ASC: el arma que lleva en la mano tiene colliders propios y cuelga del hueso
        // del rig, así que un proyectil que sale cerca del cuerpo chocaba contra su
        // propia arma y se despawneaba al instante, sin llegar a volar.
        if (sourceASC != null && other.transform.root == sourceASC.transform.root) return;

        // Y lo que cuelgue de su objeto de red, por si su raíz no es la de siempre (un personaje
        // emparentado a otra cosa).
        if (_shooterNob.Value != null && other.GetComponentInParent<NetworkObject>() == _shooterNob.Value) return;

        // BARRERA (escudo del Paladín y compañía). Va ANTES del descarte de triggers
        // de abajo a propósito: la barrera ES un trigger (para no empujar a nadie ni
        // pelearse con el CharacterController de su dueño), así que si la dejáramos
        // caer en ese return el proyectil la atravesaría como si no existiera.
        //
        // Solo para una barrera LEVANTADA y ENEMIGA: los proyectiles de tu propio
        // equipo la cruzan sin frenarse, como en cualquier hero shooter.
        Entity_ShieldBarrier barrier = other.GetComponent<Entity_ShieldBarrier>();
        if (barrier != null)
        {
            if (!barrier.IsRaised || !barrier.IsHostile(sourceASC)) return;

            // El dueño ESQUIVA proyectiles (el bloqueo del Shinobi): su barrera no los para,
            // lo atraviesan a él también (ver abajo) y siguen de largo.
            if (barrier.Owner != null && barrier.Owner.HasTag(EGameplayTag.Status_DodgeProjectiles)) return;

            // PARRY QUE DEVUELVE (Guardián): el proyectil no se para, cambia de dueño y sale
            // hacia donde apunta el dueño de la barrera. Una sola vez por proyectil.
            if (barrier.ReflectsProjectilesNow && !_reflected)
            {
                barrier.NotifyProjectileBlocked(PrimaryDamage, sourceASC, point);
                // Sale desde donde tocó el escudo: el barrido lo encuentra un paso tarde, y
                // para entonces el proyectil ya podía estar del otro lado.
                transform.position += point - ShapeCenter();
                Reflect(barrier.Owner, barrier.AimDirection);
                return;
            }

            // Le pasamos el efecto de daño (no un número) para que la barrera pueda
            // reportar cuánto daño evitó de verdad — ver NotifyProjectileBlocked.
            barrier.NotifyProjectileBlocked(PrimaryDamage, sourceASC, point);
            PlayImpactVFXEverywhere(point);
            Impact(point);
            DespawnSelf();
            return;
        }

        if (other.isTrigger) return;

        AbilitySystemComponent targetASC = other.GetComponentInParent<AbilitySystemComponent>();

        // ESQUIVA (Status_DodgeProjectiles, el bloqueo del Shinobi): un enemigo que esquiva no
        // recibe nada; el proyectil lo atraviesa como si no estuviera, sin impacto ni VFX.
        if (targetASC != null && sourceASC != null && targetASC.HasTag(EGameplayTag.Status_DodgeProjectiles) &&
            sourceASC.IsEnemyOf(targetASC))
            return;

        // Algo del mapa que se golpea (la campana, StrikeTarget): se le avisa y el proyectil se deshace
        // contra él como contra una pared.
        if (targetASC != null && targetASC.Strikeable != null)
        {
            if (sourceASC != null) targetASC.Strikeable.Strike(sourceASC, sourceAbility);
            PlayImpactVFXEverywhere(point);
            Impact(point);
            DespawnSelf();
            return;
        }

        // 1. VFX de impacto, sea lo que sea lo que golpeamos (a un personaje, una vez aunque
        // tenga varios colliders).
        if (targetASC == null || _impactShown.Add(targetASC)) PlayImpactVFXEverywhere(point);

        // 2. ¿Es un personaje?
        if (targetASC != null && sourceAbility != null && sourceASC != null)
        {
            // SISTEMA DE EQUIPOS (AFILIACIÓN LÓGICA): neutral (equipo 0) cuenta como enemigo.
            bool isEnemy = sourceASC.IsEnemyOf(targetASC);

            // A) Es enemigo y no lo hemos golpeado antes
            if (isEnemy)
            {
                if (enemiesHit.Add(targetASC))
                {
                    // La lista de efectos de la habilidad, en su orden. El PRIMER enemigo de
                    // este proyectil recibe además los de "al primer golpe" (el aturdido del
                    // Clérigo del Orden). Devuelto por un parry: solo lo que va a los enemigos,
                    // con el daño que traía (sus copias congeladas, ver Reflect).
                    bool first = enemiesHit.Count == 1 && !_reflected;
                    sourceAbility.ApplyHitEffects(targetASC, first, sourceASC, _reflected ? _frozen : null,
                                                  onlyHostile: _reflected);

                    if (ultChargeAmount > 0)
                        sourceASC.ReduceCooldownByTag(EGameplayTag.Ability_Cooldown_Ultimate, ultChargeAmount);

                    // Los VFX "al golpear" sobre él (el sonido ya va con el impacto de arriba).
                    if (!_reflected) sourceAbility.BroadcastHitVFX(targetASC, withSound: false);

                    // "Salta entre enemigos": sale hacia el próximo (ver REBOTE Y VUELTA).
                    if (TryJumpToNextEnemy(targetASC, point)) return;
                }

                // "Se deshace en el primer enemigo" (GA_ProjectileShoot.StopAtFirstEnemy: los
                // Misiles arcanos del Mago): le pegó a uno y se va. Sin la casilla, lo atraviesa
                // y sigue (cada enemigo, una vez), como siempre.
                // A la vuelta de un bumerán, no: atraviesa a todos hasta llegarle al dueño.
                if (sourceAbility is GA_ProjectileShoot shot && shot.StopAtFirstEnemy && !_returning)
                {
                    // "Vuelve al chocar": da la vuelta en vez de deshacerse.
                    if (CanReturn && shot.ReturnsOnHit) { MoveShapeTo(point); StartReturn(); return; }
                    Impact(point);
                    DespawnSelf();
                    return;
                }
            }
            // B) Es aliado
            else
            {
                // Nunca le hacemos daño, pero SÍ le aplicamos lo que la lista trae para
                // los aliados (Castigo divino del Paladín: la estela daña enemigos y cura
                // aliados a su paso). Sin nada para ellos, esto no hace nada, que es el
                // comportamiento de siempre. Devuelto por un parry, tampoco: curaría a los
                // del Guardián con la magia del Paladín.
                //
                // El proyectil lo atraviesa en los dos casos: no lo destruimos acá.
                if (!_reflected && sourceAbility.HasAllyHitEffects && alliesHit.Add(targetASC))
                {
                    sourceAbility.ApplyHitEffects(targetASC, false, sourceASC);
                    sourceAbility.BroadcastHitVFX(targetASC, withSound: false);
                }
            }
        }
        else
        {
            // 3. ¿Es una pared / entorno sólido?
            // Si llegamos aquí, no tiene ASC y no es trigger.
            // A la vuelta de un bumerán, las paredes se atraviesan (no se pierde detrás de una).
            if (_returning) return;
            // Rebota (le quedan rebotes) o da la vuelta ("Vuelve al chocar"), en vez de deshacerse.
            if (TryBounceOffWall(other, point)) return;
            if (CanReturn && sourceAbility is GA_ProjectileShoot back && back.ReturnsOnHit)
            {
                MoveShapeTo(point);
                StartReturn();
                return;
            }
            WarnIfNotScenery(other, targetASC);
            Impact(point);
            DespawnSelf();
        }
    }

    // DIAGNÓSTICO (29 de octubre de 2026). En la prueba por internet los proyectiles "se deshacían
    // contra uno mismo" (al alcanzarlos corriendo) y el arco de luz del Clérigo "se frenaba en el
    // primero", y en el código no aparece por qué: los dos atraviesan personajes. Si un proyectil se
    // deshace como contra una pared en algo que NO es el mapa (un personaje, o algo que se mueve: con
    // Rigidbody o colgado de un objeto de red), queda escrito en la consola qué era.
    private void WarnIfNotScenery(Collider other, AbilitySystemComponent targetASC)
    {
        bool moves = other.attachedRigidbody != null || other.GetComponentInParent<NetworkObject>() != null;
        if (targetASC == null && !moves) return;

        string path = other.name;
        for (Transform t = other.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;

        Debug.LogWarning($"[Proyectil] {(sourceAbility != null ? sourceAbility.name : "(sin habilidad)")} de " +
                         $"{(sourceASC != null ? sourceASC.name : "(sin dueño)")} se deshizo contra '{path}' " +
                         $"(capa {LayerMask.LayerToName(other.gameObject.layer)}, " +
                         $"{(targetASC != null ? "ES un personaje: " + targetASC.name : "sin ASC")}). Si no es una pared, es el choque raro.");
    }
    private void Impact(Vector3 point)
    {
        if (_impacted || _gone || _reflected) return;
        _impacted = true;
        if (sourceAbility is GA_ProjectileShoot shot) shot.OnProjectileImpact(point, _boosted);
    }
}
