using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// MercEnemyAI — LA CABEZA (servidor): a quién perseguir, volver al puesto
//
// Una parte de MercEnemyAI (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/MercEnemyAI.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class MercEnemyAI
{

    // =========================================================
    // SERVIDOR
    // =========================================================

    [Server]
    private void ServerTick()
    {
        if (_asc == null || _agent == null || !_agent.isOnNavMesh) return;

        // Cubriéndose: quieto, mirando hacia donde levantó el escudo, sin atacar.
        if (Time.time < _blockUntil)
        {
            _agent.isStopped = true;
            if (_blockDirection.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(_blockDirection),
                                                      Time.deltaTime * TurnSpeed);
            return;
        }

        // Aturdido/enraizado: se queda quieto (mismo criterio que el EnemyAI de la jam).
        if (_asc.HasTag(EGameplayTag.State_Stunned) || _asc.HasTag(EGameplayTag.State_Rooted))
        {
            _agent.isStopped = true;
            return;
        }

        _agent.speed = Mathf.Max(0.5f, _asc.GetAttributeValue(EAttributeType.MovSpeed));

        _retargetTimer -= Time.deltaTime;
        if (_retargetTimer <= 0f)
        {
            _retargetTimer = RetargetInterval;
            _target = FindTarget();
        }

        // Sin objetivo: volver al puesto.
        if (_target == null)
        {
            float toHome = Vector3.Distance(transform.position, _homePosition);
            if (toHome > 1.5f) { _agent.isStopped = false; _agent.SetDestination(_homePosition); }
            else _agent.isStopped = true;
            return;
        }

        Vector3 targetPos = _target.transform.position;
        float distance    = Vector3.Distance(transform.position, targetPos);

        // Se frena un poco ANTES del alcance máximo: parado justo en el límite, el más
        // mínimo movimiento del jugador lo deja fuera de rango y el enemigo se pasa la
        // pelea entrando y saliendo sin llegar a atacar nunca.
        float stopDistance = AttackRange * 0.9f;

        if (distance > stopDistance)
        {
            // Lejos: perseguir.
            _agent.isStopped = false;
            _agent.SetDestination(targetPos);
        }
        else if (KeepDistance > 0f && distance < KeepDistance)
        {
            // Demasiado cerca y es de los que pelean a distancia: retroceder sin dejar de
            // encararlo. Es lo que hace que un mago se sienta un mago y no un fantasma
            // que dispara.
            Vector3 away = transform.position + (transform.position - targetPos).normalized * 4f;
            _agent.isStopped = false;
            _agent.SetDestination(away);
        }
        else
        {
            _agent.isStopped = true;
        }

        // Encara SIEMPRE que lo tenga a tiro, aunque se esté moviendo: el proyectil sale
        // hacia adelante y con retardo, así que girar es parte de apuntar.
        FaceTarget(targetPos);

        if (Time.time < _lastAttackTime + AttackCooldown) return;
        if (distance > AttackRange)                       return;
        if (!IsAimingAt(targetPos))                       return;
        if (!HasLineOfSight(_target))                     return;

        Attack(distance);
    }

    // ¿Está lo bastante encarado como para que el tiro salga hacia el objetivo?
    private bool IsAimingAt(Vector3 targetPos)
    {
        Vector3 toTarget = targetPos - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.01f) return true;

        return Vector3.Angle(transform.forward, toTarget.normalized) <= AimTolerance;
    }

    // Línea de tiro despejada. Se traza de ojos a pecho (no de pies a pies): con los
    // pivotes en el piso, una línea a ras del suelo choca contra el primer escalón y el
    // enemigo no dispararía nunca.
    private bool HasLineOfSight(AbilitySystemComponent target)
    {
        if (!RequireLineOfSight || target == null) return true;

        Vector3 from = transform.position + Vector3.up * EyeHeight;
        Vector3 to   = target.transform.position + Vector3.up * 1.2f;

        return !Physics.Linecast(from, to, SightBlockers, QueryTriggerInteraction.Ignore);
    }

    // El jugador (o compañero) vivo más cercano dentro del radio de detección, siempre que
    // perseguirlo no lo saque de su zona.
    [Server]
    private AbilitySystemComponent FindTarget()
    {
        // PROVOCADO (Status_Taunted): va por el que lo provocó, aunque esté fuera de su radio de detección; la correa sí
        // vale (no sale de su zona). Invisible no importa: provocar es "te vi".
        AbilitySystemComponent forced = _asc.GetTaunter();
        if (forced != null && !forced.HasTag(EGameplayTag.Status_SafeZone) &&
            Vector3.Distance(_homePosition, forced.transform.position) <= LeashRadius)
            return forced;

        AbilitySystemComponent best = null;
        float bestDistance = float.MaxValue;
        AbilitySystemComponent taunter = null;   // el Oso del Maestro de bestias (Status_Taunting)
        bool otherThanTaunterOwner = false;   // hay algún jugador cerca que no es el dueño del Oso
        var playersNear = new System.Collections.Generic.List<AbilitySystemComponent>();

        using var hits = PhysicsQuery.OverlapSphere(transform.position, DetectionRadius,
                                                CharacterLayer, QueryTriggerInteraction.Collide);
        foreach (Collider col in hits)
        {
            if (col == null) continue;

            AbilitySystemComponent asc = col.GetComponentInParent<AbilitySystemComponent>();
            if (asc == null || asc == _asc) continue;

            // Un compañero que provoca (el Oso): se anota aparte, ver abajo.
            if (asc.HasTag(EGameplayTag.Status_Taunting) && !asc.HasTag(EGameplayTag.State_Dead) &&
                Vector3.Distance(_homePosition, asc.transform.position) <= LeashRadius)
            {
                if (taunter == null) taunter = asc;
                continue;
            }

            // Jugadores y los compañeros del Maestro de bestias (el Lobo; el Búho no se puede
            // pegar). Pedido de Gustavo, 14 de octubre de 2026: van por el compañero si está en
            // rango, como por cualquier jugador (el más cercano).
            bool isCompanion = asc.GetComponent<Entity_Companion>() != null;
            if (asc.GetComponent<PlayerController>() == null && !isCompanion) continue;
            if (isCompanion && asc.HasTag(EGameplayTag.Status_Immunity)) continue;
            if (asc.HasTag(EGameplayTag.State_Dead)) continue;
            if (asc.HasTag(EGameplayTag.Status_SafeZone)) continue;       // en su base no se los toca
            if (asc.IsHiddenFromEnemies) continue;   // invisible y sin revelar

            // Correa: si para alcanzarlo tendría que irse lejos de su puesto, lo ignora.
            if (Vector3.Distance(_homePosition, asc.transform.position) > LeashRadius) continue;
            if (!isCompanion && !playersNear.Contains(asc)) playersNear.Add(asc);

            float d = Vector3.Distance(transform.position, asc.transform.position);
            if (d < bestDistance) { bestDistance = d; best = asc; }
        }

        // El Oso provoca: si cerca no hay otro jugador que SU dueño (o ninguno), los fantasmas van
        // por él (pedido de Gustavo, 14 de octubre de 2026).
        if (taunter != null)
        {
            Entity_Companion bear = taunter.GetComponent<Entity_Companion>();
            foreach (AbilitySystemComponent p in playersNear)
                if (bear == null || p != bear.OwnerASC) otherThanTaunterOwner = true;
            if (!otherThanTaunterOwner) return taunter;
        }

        return best;
    }

    [Server]
    private void Attack(float distanceToTarget)
    {
        _lastAttackTime = Time.time;

        // Primero la rotación: si hay una habilidad especial lista, este turno se la
        // lleva ella y el ataque básico se saltea. Así un jefe alterna en vez de repetir.
        if (TryUseExtraAbility(distanceToTarget)) return;

        if (!string.IsNullOrEmpty(AttackTrigger)) ObserversPlayAttack();

        // Enemigo a distancia: deja que su habilidad haga todo (proyectil, VFX, etc.).
        if (_runtimeAbility != null)
        {
            if (_runtimeAbility.CanActivate()) _runtimeAbility.Activate();
            ServerStartBlock();
            return;
        }

        if (_target == null) return;

        _target.ApplyGameplayEffect(ResolveDamageEffect(), _asc);

        if (HitVFX != null) ObserversSpawnHitVfx(_target.transform.position + Vector3.up);
        ServerStartBlock();
    }
}
