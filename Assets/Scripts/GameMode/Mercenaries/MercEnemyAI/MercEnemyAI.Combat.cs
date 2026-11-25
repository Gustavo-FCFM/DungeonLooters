using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// MercEnemyAI — EL COMBATE (el bloqueo después de atacar y la rotación de habilidades)
//
// Una parte de MercEnemyAI (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/MercEnemyAI.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class MercEnemyAI
{

    private void HandleTookDamage(AbilitySystemComponent attacker)
    {
        if (attacker == null || attacker == _asc) return;
        _lastHitter = attacker;
        _lastHitAt  = Time.time;
    }

    // Se cubre hacia quien le pegó hace poco (si no, hacia su objetivo) y se queda así.
    [Server]
    private void ServerStartBlock()
    {
        if (BlockAfterAttack == null || _asc == null) return;

        AbilitySystemComponent from = _lastHitter != null && Time.time - _lastHitAt <= 3f &&
                                      !_lastHitter.HasTag(EGameplayTag.State_Dead) ? _lastHitter : _target;
        Vector3 dir = from != null ? from.transform.position - transform.position : transform.forward;
        dir.y = 0f;
        _blockDirection = dir.sqrMagnitude > 0.001f ? dir.normalized : transform.forward;
        _blockUntil = Time.time + Mathf.Max(0.1f, BlockSeconds);
        _asc.ApplyGameplayEffect(BlockAfterAttack, _asc, BlockSeconds);
    }

    // =========================================================
    // ROTACIÓN DE HABILIDADES
    // =========================================================

    [Server]
    private void GrantExtraAbilities()
    {
        if (ExtraAbilities == null || ExtraAbilities.Count == 0) return;

        _extraRuntime = new GameplayAbility[ExtraAbilities.Count];
        _extraNextUse = new float[ExtraAbilities.Count];

        for (int i = 0; i < ExtraAbilities.Count; i++)
        {
            ExtraAbility extra = ExtraAbilities[i];
            if (extra == null || extra.Ability == null) continue;

            _extraRuntime[i] = _asc.GrantAbility(extra.Ability);
            // El retardo inicial evita que el jefe salude con su golpe más fuerte en el
            // mismo segundo en que aparece, cuando nadie lo vio venir.
            _extraNextUse[i] = Time.time + Mathf.Max(0f, extra.InitialDelay);
        }
    }

    // Busca la primera habilidad de la rotación que esté lista y en rango. Se recorren en
    // ORDEN a propósito: es la forma de decir "esta es la prioritaria" sin inventar un
    // sistema de pesos que nadie va a querer configurar.
    [Server]
    private bool TryUseExtraAbility(float distanceToTarget)
    {
        if (_extraRuntime == null) return false;

        float healthPercent = HealthPercent();

        for (int i = 0; i < _extraRuntime.Length; i++)
        {
            GameplayAbility ability = _extraRuntime[i];
            if (ability == null) continue;

            ExtraAbility extra = ExtraAbilities[i];
            if (Time.time < _extraNextUse[i]) continue;
            if (healthPercent > extra.UseBelowHealthPercent) continue;

            float range = extra.Range > 0f ? extra.Range : AttackRange;
            if (distanceToTarget > range) continue;

            if (!ability.CanActivate()) continue;

            ability.Activate();
            _extraNextUse[i] = Time.time + Mathf.Max(0.5f, extra.Cooldown);
            return true;
        }

        return false;
    }

    // Fracción de vida que le queda (1 = intacto). La usa la "segunda fase" de la rotación.
    private float HealthPercent()
    {
        if (_asc == null) return 1f;

        float max = _asc.GetAttributeValue(EAttributeType.MaxHealth);
        if (max <= 0f) return 1f;

        return Mathf.Clamp01(_asc.GetAttributeValue(EAttributeType.Health) / max);
    }

    // Golpe a melee: si no le pusiste un GameplayEffect, se arma uno en código con el
    // daño plano. Es Hidden e instantáneo, así que no necesita estar registrado.
    private GameplayEffect ResolveDamageEffect()
    {
        if (DamageEffect != null) return DamageEffect;
        if (_runtimeDamage != null) return _runtimeDamage;

        _runtimeDamage = ScriptableObject.CreateInstance<GameplayEffect>();
        _runtimeDamage.name       = "GE_GolpeNPC(runtime)";
        _runtimeDamage.Duration   = 0f;
        _runtimeDamage.EffectType = GameplayEffect.EEffectType.Hidden;
        _runtimeDamage.Modifiers  = new System.Collections.Generic.List<Modifier>
        {
            new Modifier
            {
                Attribute = EAttributeType.Health,
                Type      = Modifier.EModificationType.Add,
                Magnitude = -Mathf.Abs(FallbackDamage),
            }
        };
        return _runtimeDamage;
    }

    [Server]
    private void FaceTarget(Vector3 targetPos)
    {
        Vector3 dir = targetPos - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.001f) return;

        transform.rotation = Quaternion.Slerp(transform.rotation,
            Quaternion.LookRotation(dir), Time.deltaTime * TurnSpeed);
    }
}
