using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// MercEnemyAI — LA MUERTE
//
// Una parte de MercEnemyAI (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/MercEnemyAI.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class MercEnemyAI
{

    // =========================================================
    // MUERTE
    // =========================================================

    // La experiencia NO se reparte acá: NetworkAbilitySystemComponent.HandleDeath ya
    // corre en el servidor con LastAttacker cargado y la manda a la bolsa del equipo
    // que remató (ver MercenariesGameMode.ServerAwardKill).
    [Server]
    private void HandleDeath()
    {
        if (_dead) return;
        _dead = true;

        if (_agent != null && _agent.enabled) _agent.isStopped = true;
        if (Spawner != null) Spawner.ServerNotifyEnemyDied(this);

        // El estallido y lo que deja, un instante después: la muerte llega en medio del daño, y
        // aplicar efectos ahí adentro es meterse en ese camino.
        if (!_dismissed && (DeathBurstRadius > 0f || DeathSummon != null))
        {
            _deathPoint = transform.position;
            Invoke(nameof(ServerOnDeathBurst), 0.05f);
        }

        Invoke(nameof(ServerDespawnSelf), Mathf.Max(0.1f, DespawnDelay, 0.15f));
    }

    [Server]
    private void ServerOnDeathBurst()
    {
        if (DeathBurstRadius > 0f && DeathBurstEffect != null && _asc != null)
        {
            var seen = new HashSet<AbilitySystemComponent>();
            int count = Physics.OverlapSphereNonAlloc(_deathPoint, DeathBurstRadius, s_burstHits, CharacterLayer,
                                                      QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; i++)
            {
                AbilitySystemComponent target = s_burstHits[i] != null ? s_burstHits[i].GetComponentInParent<AbilitySystemComponent>() : null;
                if (target == null || target == _asc || !seen.Add(target)) continue;
                if (target.HasTag(EGameplayTag.State_Dead) || !_asc.IsEnemyOf(target)) continue;
                target.ApplyGameplayEffect(DeathBurstEffect, _asc);
            }
        }

        if (DeathBurstVFX != null) ObserversDeathBurstVfx(_deathPoint);
        if (DeathSummon != null)
            Entity_Summon.Spawn(DeathSummon, _asc, _deathPoint, Quaternion.identity, 0, false);
    }

    [ObserversRpc(RunLocally = true)]
    private void ObserversDeathBurstVfx(Vector3 position)
    {
        if (DeathBurstVFX == null) return;
        GameObject vfx = Instantiate(DeathBurstVFX, position + Vector3.up * 0.5f, Quaternion.identity);
        Destroy(vfx, 3f);
    }

    [Server]
    private void ServerDespawnSelf()
    {
        if (IsSpawned) ServerManager.Despawn(gameObject);
    }

    // Lo saca de juego SIN darle la baja a nadie: muere como siempre (se ve caer y desaparece),
    // pero sin quién lo mató, así que no reparte experiencia ni suma para nada. Lo usan los
    // santuarios para llevarse a sus enemigos extra cuando se apagan (MercEnemySpawner).
    [Server]
    public void ServerDismiss()
    {
        if (_dead || _asc == null) return;
        _dismissed = true;   // retirado: no revienta ni deja nada
        _asc.LastAttacker = null;
        _asc.SetCurrentAttributeValue(EAttributeType.Health, 0f);
    }
}
