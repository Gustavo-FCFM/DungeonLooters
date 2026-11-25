using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// Entity_Companion — LAS ÓRDENES, LA CABEZA (servidor) Y LA MUERTE
//
// Una parte de Entity_Companion (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/Entity_Companion.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class Entity_Companion
{

    // =========================================================
    // ÓRDENES (las da el CompanionManager)
    // =========================================================

    // El Explorador le pegó a 'victim': va a pegarle ya, corriendo.
    [Server]
    public void Command(AbilitySystemComponent victim)
    {
        if (!IsValidEnemy(victim)) return;
        // Muy lejos del dueño no llegaría nunca (y antes se quedaba en un bucle de correr y
        // reaparecer junto al dueño): esa orden se ignora.
        if (FlatDistance(OwnerASC.transform.position, victim.transform.position) > CommandRange) return;
        _target        = victim;
        _commanded     = true;
        _attackReadyAt = Time.time;   // se olvida del cooldown
    }

    // Le pegaron al Explorador: el que le pegó pasa a ser su objetivo.
    [Server]
    public void Defend(AbilitySystemComponent attacker)
    {
        if (!IsValidEnemy(attacker)) return;
        _defendFrom  = attacker;
        _defendUntil = Time.time + DefendMemory;
        if (_target == null || !_commanded)
        {
            _target    = attacker;
            _commanded = true;
        }
    }

    // =========================================================
    // SERVIDOR
    // =========================================================

    protected override void Update()
    {
        base.Update();
        TickLunge();
        if (!IsLiveOnServer || _dead || OwnerASC == null) return;
        if (OwnerASC.HasTag(EGameplayTag.State_Dead)) return;   // el manager lo saca

        TickPresence();
        TickHowl();

        ValidateTarget();
        if (_target == null) AcquireTarget();

        bool stopped = _asc.HasTag(EGameplayTag.State_Stunned) || _asc.HasTag(EGameplayTag.State_Rooted);

        if (BlinkAttack) { TickBlinker(stopped); return; }

        if (_leaping) { TickLeap(); return; }

        if (stopped) { Halt(); return; }
        if (_target != null) ChaseAndAttack();
        else                 FollowOwner();
        TeleportIfLost();
    }

    private bool IsValidEnemy(AbilitySystemComponent other)
    {
        if (other == null || OwnerASC == null || other == _asc || other == OwnerASC) return false;
        if (!IsEnemy(other)) return false;   // la regla de la base (la del ASC)
        if (other.HasTag(EGameplayTag.State_Dead) || other.HasTag(EGameplayTag.Status_SafeZone)) return false;
        if (other.HasTag(EGameplayTag.Status_Immunity) && other.GetComponent<Entity_Companion>() != null) return false;
        if (!HuntsInvisible && other.IsHiddenFromEnemies) return false;
        return true;
    }

    private void ValidateTarget()
    {
        if (_target == null) return;

        bool drop = !IsValidEnemy(_target);
        float fromOwner = FlatDistance(OwnerASC.transform.position, _target.transform.position);

        // Sin orden, se queda con él solo mientras siga en su rango; con orden, hasta un tope.
        if (!_commanded && fromOwner > PassiveRange * 1.15f) drop = true;
        if (_commanded && fromOwner > CommandRange) drop = true;

        if (drop) { _target = null; _commanded = false; }
    }

    private void AcquireTarget()
    {
        // Defender va antes que todo.
        if (_defendFrom != null && Time.time < _defendUntil && IsValidEnemy(_defendFrom))
        {
            _target    = _defendFrom;
            _commanded = true;
            return;
        }

        // El ataque pasivo espera su cooldown.
        if (Time.time < _attackReadyAt) return;
        _target    = FindPassiveTarget();
        _commanded = false;
    }

    // El mejor enemigo en el rango pasivo: primero los invisibles (si los caza), después
    // los jugadores, y entre iguales el más cercano al DUEÑO.
    private AbilitySystemComponent FindPassiveTarget()
    {
        Vector3 center = OwnerASC.transform.position;
        int count = Physics.OverlapSphereNonAlloc(center, PassiveRange, _hits, CharacterLayer, QueryTriggerInteraction.Collide);

        AbilitySystemComponent best = null;
        float bestScore = float.MaxValue;
        _seen.Clear();
        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i] != null ? _hits[i].GetComponentInParent<AbilitySystemComponent>() : null;
            if (other == null || !_seen.Add(other) || !IsValidEnemy(other)) continue;

            float score = FlatDistance(center, other.transform.position);
            if (other.GetComponent<PlayerController>() != null) score -= 1000f;
            if (HuntsInvisible && other.HasTag(EGameplayTag.Status_Invisible)) score -= 2000f;

            if (score < bestScore) { bestScore = score; best = other; }
        }
        return best;
    }

    private void ChaseAndAttack()
    {
        Vector3 targetPos = _target.transform.position;
        float flat = FlatDistance(transform.position, targetPos);
        float dy   = targetPos.y - transform.position.y;
        bool inReach = flat <= AttackReach && Mathf.Abs(dy) <= ReachHeight;

        if (inReach) { Halt(); Face(targetPos); }
        else         MoveTo(targetPos, MoveSpeed * (_commanded ? ChaseMultiplier : 1f));

        if (inReach && Time.time >= _attackReadyAt) Attack();
    }

    private void Attack()
    {
        _attackReadyAt = Time.time + AttackInterval * (OwnerInFury ? FuryAttackIntervalMultiplier : 1f);

        bool outOfRange = FlatDistance(OwnerASC.transform.position, _target.transform.position) > PassiveRange;
        AbilitySystemComponent victim = _target;

        ApplyAttack(victim);
        ObserversAttack(victim.transform.position + Vector3.up);

        // Tras una orden fuera de su rango, vuelve a merodear; dentro, se queda con él.
        if (_commanded && outOfRange) _target = null;
        _commanded = false;
    }

    private void ApplyAttack(AbilitySystemComponent victim)
    {
        if (_manager != null) _manager.BeginPetHit();
        try
        {
            foreach (AbilityEffect e in AttackEffects)
            {
                if (e.Effect == null || e.When != EEffectWhen.OnHit) continue;
                if (e.OnlyIfTargetHas != EGameplayTag.None && !victim.HasTag(e.OnlyIfTargetHas)) continue;
                if (e.PerTargetCooldown > 0f && !victim.TryStartEffectCooldown(this, e.Effect, OwnerASC, e.PerTargetCooldown)) continue;

                if (e.ApplyTo == EEffectTarget.Self) OwnerASC.ApplyGameplayEffect(e.Effect, OwnerASC);
                else                                 victim.ApplyGameplayEffect(e.Effect, OwnerASC);
            }
        }
        finally
        {
            if (_manager != null) _manager.EndPetHit();
        }

        if (RevealSeconds > 0f)
        {
            NetworkAbilitySystemComponent victimNet = victim.GetComponent<NetworkAbilitySystemComponent>();
            if (victimNet != null && victimNet.IsServerInitialized) victimNet.ServerRevealToTeam(OwnerASC.TeamID, RevealSeconds);
        }

        _bitten[victim] = Time.time;
    }

    // La vida que le queda, de 0 a 1 (la guarda el manager al cambiarlo por otro).
    public float HealthFraction01
    {
        get
        {
            float max = _asc.GetAttributeValue(EAttributeType.MaxHealth);
            return max > 0f ? Mathf.Clamp01(_asc.GetAttributeValue(EAttributeType.Health) / max) : 1f;
        }
    }

    // Sin objetivo: junto al dueño (el Oso, delante; el Lobo merodea; los demás, a un costado y atrás).
    private void FollowOwner()
    {
        if (Roam && !GuardOwner) { RoamAroundOwner(); return; }

        Transform owner = OwnerASC.transform;
        Vector3 goal = GuardOwner ? GuardPoint() : OwnerPoint(FollowOffset);

        float toOwner = FlatDistance(transform.position, owner.position);
        float speed   = MoveSpeed * (toOwner > 8f ? 1.5f : 1f);

        if (FlatDistance(transform.position, goal) > 1.2f) MoveTo(goal, speed);
        else { Halt(); Face(transform.position + owner.forward); }
    }

    // Da vueltas por la zona del dueño: elige un punto a su alrededor, camina hasta ahí, se
    // queda un rato y elige otro. Si el dueño se aleja, corre a alcanzarlo.
    private void RoamAroundOwner()
    {
        Vector3 ownerPos = OwnerASC.transform.position;
        float toOwner = FlatDistance(transform.position, ownerPos);

        // Quedó lejos: lo alcanza corriendo y elige un punto nuevo al llegar.
        if (toOwner > RoamRadius + 4f)
        {
            _hasRoamPoint = false;
            MoveTo(ownerPos, MoveSpeed * 1.5f);
            return;
        }

        // El punto quedó lejos del dueño (se movió): otro.
        if (_hasRoamPoint && FlatDistance(_roamPoint, ownerPos) > RoamRadius * 1.3f) _hasRoamPoint = false;

        if (!_hasRoamPoint)
        {
            if (Time.time < _roamWaitUntil) { Halt(); return; }
            _hasRoamPoint = TryPickRoamPoint(ownerPos, out _roamPoint);
            if (!_hasRoamPoint) { Halt(); return; }
        }

        if (FlatDistance(transform.position, _roamPoint) > 0.8f)
        {
            MoveTo(_roamPoint, MoveSpeed * RoamSpeedFactor);
            return;
        }

        // Llegó: una pausa mirando hacia afuera, y después otro punto.
        Halt();
        _hasRoamPoint  = false;
        _roamWaitUntil = Time.time + Random.Range(RoamPauseMin, Mathf.Max(RoamPauseMin, RoamPauseMax));
    }

    private bool TryPickRoamPoint(Vector3 ownerPos, out Vector3 point)
    {
        for (int i = 0; i < 6; i++)
        {
            Vector2 dir = Random.insideUnitCircle.normalized;
            float r = Random.Range(RoamMinRadius, Mathf.Max(RoamMinRadius, RoamRadius));
            Vector3 p = ownerPos + new Vector3(dir.x, 0f, dir.y) * r;
            if (_agent != null && _agent.enabled)
            {
                if (NavMesh.SamplePosition(p, out NavMeshHit hit, 2f, NavMesh.AllAreas) &&
                    Mathf.Abs(hit.position.y - ownerPos.y) < 2.5f)
                { point = hit.position; return true; }
            }
            else { point = p; return true; }
        }
        point = ownerPos;
        return false;
    }

    // Delante del dueño, hacia el enemigo más cercano (o hacia donde mira), corrido a un costado
    // para no taparle la vista.
    private Vector3 GuardPoint()
    {
        Transform owner = OwnerASC.transform;
        Vector3 dir = owner.forward;

        AbilitySystemComponent threat = NearestEnemyPlayer(owner.position, 20f);
        if (threat != null) dir = threat.transform.position - owner.position;
        dir.y = 0f;
        dir = dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.forward;

        Vector3 side = Vector3.Cross(Vector3.up, dir);
        return owner.position + dir * 3f + side * 1.2f;
    }

    private AbilitySystemComponent NearestEnemyPlayer(Vector3 center, float radius)
    {
        int count = Physics.OverlapSphereNonAlloc(center, radius, _hits, CharacterLayer, QueryTriggerInteraction.Collide);
        AbilitySystemComponent best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i] != null ? _hits[i].GetComponentInParent<AbilitySystemComponent>() : null;
            if (other == null || other.GetComponent<PlayerController>() == null || !IsValidEnemy(other)) continue;
            float d = FlatDistance(center, other.transform.position);
            if (d < bestDist) { bestDist = d; best = other; }
        }
        return best;
    }

    // Un punto en el espacio del dueño, girado solo por su yaw.
    private Vector3 OwnerPoint(Vector3 offset)
    {
        Transform owner = OwnerASC.transform;
        Quaternion yaw = Quaternion.Euler(0f, owner.eulerAngles.y, 0f);
        return owner.position + yaw * offset;
    }

    // =========================================================
    // MUERTE
    // =========================================================

    // Su vida llegó a 0 (la base lo avisa en Update, fuera del cálculo del daño): el manager le
    // empieza la espera para volver y el cuerpo se va un poco después.
    protected override void OnOwnDeath()
    {
        if (_dead) return;
        _dead = true;
        Halt();
        if (_manager != null) _manager.OnCompanionDied(this);
        Invoke(nameof(ServerDespawn), 1.2f);
    }
}
