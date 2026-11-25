using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// Entity_Companion — LO PROPIO DE CADA BESTIA (el búho, la presencia y el aullido)
//
// Una parte de Entity_Companion (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/Entity_Companion.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class Entity_Companion
{

    // =========================================================
    // EL BÚHO: flota junto al dueño y aparece junto al objetivo
    // =========================================================

    private void TickBlinker(bool stopped)
    {
        if (_blinkReturnAt > 0f && Time.time < _blinkReturnAt) return;   // está picoteando
        _blinkReturnAt = 0f;

        // Flota sobre el hombro del dueño (suave: en la pantalla de todos se ve volar).
        Vector3 hover = OwnerPoint(FollowOffset);
        transform.position = Vector3.Lerp(transform.position, hover, 1f - Mathf.Exp(-8f * Time.deltaTime));
        Face(transform.position + OwnerASC.transform.forward);

        if (stopped || _target == null || Time.time < _attackReadyAt) return;

        // Aparece un poco delante y arriba del objetivo, del lado del dueño.
        Vector3 targetPos = _target.transform.position;
        Vector3 back = OwnerASC.transform.position - targetPos;
        back.y = 0f;
        back = back.sqrMagnitude > 0.01f ? back.normalized : Vector3.forward;
        transform.position = targetPos + back * 1.2f + Vector3.up * 1.6f;
        Face(targetPos);

        Attack();
        _blinkReturnAt = Time.time + 0.45f;
    }

    // =========================================================
    // PRESENCIA BESTIAL Y AULLIDO
    // =========================================================

    // Cada medio segundo: los aliados cerca del compañero o del dueño reciben la presencia.
    private void TickPresence()
    {
        if (PresenceEffect == null) return;
        _presenceTimer -= Time.deltaTime;
        if (_presenceTimer > 0f) return;
        _presenceTimer = 0.5f;

        _seen.Clear();
        GivePresenceAround(transform.position);
        GivePresenceAround(OwnerASC.transform.position);
        // El compañero también la lleva (el Búho no tiene collider: no sale en la búsqueda).
        if (_seen.Add(_asc) && !_asc.HasTag(EGameplayTag.State_Dead)) GivePresence(_asc);
    }

    // La presencia, y con la Ira primal también la extra (vale doble).
    private void GivePresence(AbilitySystemComponent ally)
    {
        ally.ApplyGameplayEffect(PresenceEffect, OwnerASC);
        if (FuryPresenceEffect != null && OwnerInFury) ally.ApplyGameplayEffect(FuryPresenceEffect, OwnerASC);
    }

    private bool OwnerInFury => OwnerASC != null && OwnerASC.HasTag(EGameplayTag.Status_PrimalFury);

    private void GivePresenceAround(Vector3 center)
    {
        int count = Physics.OverlapSphereNonAlloc(center, PresenceRadius, _hits, CharacterLayer, QueryTriggerInteraction.Collide);
        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i] != null ? _hits[i].GetComponentInParent<AbilitySystemComponent>() : null;
            if (other == null || !_seen.Add(other)) continue;
            if (!OwnerASC.IsAllyOf(other, includeSelf: true) || other.HasTag(EGameplayTag.State_Dead)) continue;
            GivePresence(other);
        }
    }
    private void TickHowl()
    {
        if (_bitten.Count == 0) return;
        _expired.Clear();
        bool howl = false;
        foreach (var pair in _bitten)
        {
            if (pair.Key == null || Time.time - pair.Value > HowlMemory) { _expired.Add(pair.Key); continue; }
            if (pair.Key.HasTag(EGameplayTag.State_Dead)) { howl = true; _expired.Add(pair.Key); }
        }
        foreach (var gone in _expired) _bitten.Remove(gone);

        if (!howl || HowlEffect == null) return;

        int count = Physics.OverlapSphereNonAlloc(transform.position, HowlRadius, _hits, CharacterLayer, QueryTriggerInteraction.Collide);
        _seen.Clear();
        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i] != null ? _hits[i].GetComponentInParent<AbilitySystemComponent>() : null;
            if (other == null || !_seen.Add(other)) continue;
            if (!OwnerASC.IsAllyOf(other, includeSelf: true) || other.HasTag(EGameplayTag.State_Dead)) continue;
            other.ApplyGameplayEffect(HowlEffect, OwnerASC);
        }
        ObserversHowl();
    }
}
