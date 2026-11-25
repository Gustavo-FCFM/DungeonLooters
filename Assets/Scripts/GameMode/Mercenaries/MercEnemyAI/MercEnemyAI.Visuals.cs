using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// MercEnemyAI — LA PRESENTACIÓN (todos los peers)
//
// Una parte de MercEnemyAI (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/MercEnemyAI.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class MercEnemyAI
{

    // =========================================================
    // PRESENTACIÓN (todos los peers)
    // =========================================================

    // La velocidad para el Animator se deduce de cuánto se movió el transform, no del
    // NavMeshAgent: en los clientes el agente está apagado, y así los dos lados usan
    // exactamente el mismo cálculo.
    private void UpdateVisuals()
    {
        if (_animator == null) return;

        float dt = Time.deltaTime;
        if (dt > 0f)
        {
            float instant = Vector3.Distance(transform.position, _lastVisualPos) / dt;
            _visualSpeed  = Mathf.Lerp(_visualSpeed, instant, dt * 8f);
            _lastVisualPos = transform.position;
        }

        if (!string.IsNullOrEmpty(SpeedParameter) && HasParameter(SpeedParameter))
            _animator.SetFloat(SpeedParameter, _visualSpeed);
    }

    [ObserversRpc(RunLocally = true)]
    private void ObserversPlayAttack()
    {
        if (_animator != null && HasParameter(AttackTrigger)) _animator.SetTrigger(AttackTrigger);
    }

    [ObserversRpc(RunLocally = true)]
    private void ObserversSpawnHitVfx(Vector3 position)
    {
        if (HitVFX == null) return;
        GameObject vfx = Instantiate(HitVFX, position, Quaternion.identity);
        Destroy(vfx, 1.5f);
    }

    // El Animator del fantasma puede no tener el parámetro (los prefabs de la jam
    // varían). Sin este chequeo, Unity llena la consola de warnings por cada golpe.
    private bool HasParameter(string parameterName)
    {
        if (_animator == null || string.IsNullOrEmpty(parameterName)) return false;
        foreach (var p in _animator.parameters)
            if (p.name == parameterName) return true;
        return false;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.5f, 0.2f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, DetectionRadius);
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.4f);
        Gizmos.DrawWireSphere(Application.isPlaying ? _homePosition : transform.position, LeashRadius);
    }
}
