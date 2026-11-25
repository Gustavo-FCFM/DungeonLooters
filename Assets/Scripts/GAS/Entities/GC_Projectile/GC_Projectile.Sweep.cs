using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;

// ============================================================
// GC_Projectile — EL CHOQUE POR BARRIDO (servidor) Y EL REBOTE
//
// Una parte de GC_Projectile (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/GC_Projectile.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class GC_Projectile
{

    // Desde el cuerpo del que lo tira (a la altura a la que sale) hasta donde aparece.
    private void StartSweep(AbilitySystemComponent shooter)
    {
        _shape = GetComponent<Collider>();
        // Las mismas capas con las que chocaba antes (la matriz de colisiones de Physics).
        _mask  = CollisionMask(gameObject.layer);
        Vector3 center = ShapeCenter();
        _lastCenter = shooter != null
            ? new Vector3(shooter.transform.position.x, center.y, shooter.transform.position.z)
            : center;
        _sweeping = true;
    }

    private Vector3 ShapeCenter()
    {
        if (_shape is BoxCollider box)       return transform.TransformPoint(box.center);
        if (_shape is SphereCollider sphere) return transform.TransformPoint(sphere.center);
        return transform.position;
    }

    private void Sweep()
    {
        if (!_sweeping || _gone || _shape == null) return;

        Vector3 from = _lastCenter;
        Vector3 to   = ShapeCenter();
        _lastCenter  = to;

        // Cruzó algo que lo potencia (el Muro de fuego de su mismo Mago): la Gran bola de fuego.
        if (!_boosted && !_reflected && sourceAbility is GA_ProjectileShoot boostable && boostable.CanBeBoosted &&
            Summon_ProjectileBooster.Crosses(from, to, sourceASC))
            Boost(boostable.BoostedScale);

        Vector3 delta = to - from;
        float   dist  = delta.magnitude;
        Vector3 dir   = dist > 0.0001f ? delta / dist : transform.forward;

        int mask = _mask;

        s_found.Clear();
        CollectShape(from, dir, dist, mask);
        if (CharacterHitRadius > 0f) CollectSphere(from, dir, dist, CharacterHitRadius, mask, charactersOnly: true);
        s_found.Sort((a, b) => a.Key.CompareTo(b.Key));

        int trajectory = _trajectory;
        foreach (KeyValuePair<float, Collider> found in s_found)
        {
            if (_gone || trajectory != _trajectory) break;   // chocó, o un parry lo dio vuelta
            if (found.Value == null) continue;
            if (found.Value == _bouncedOff && Time.time < _bouncedOffUntil) continue;   // la pared de la que acaba de rebotar
            if (!_touched.Add(found.Value)) continue;
            HandleHit(found.Value, from + dir * found.Key);
        }
    }

    // La forma del proyectil (su caja o su esfera), contra todo: paredes, escudos y gente.
    private void CollectShape(Vector3 from, Vector3 dir, float dist, int mask)
    {
        Vector3 scale = transform.lossyScale;
        if (_shape is BoxCollider box)
        {
            Vector3 half = new Vector3(Mathf.Abs(box.size.x * scale.x), Mathf.Abs(box.size.y * scale.y),
                                       Mathf.Abs(box.size.z * scale.z)) * 0.5f;
            int n = Physics.OverlapBoxNonAlloc(from, half, s_overlaps, transform.rotation, mask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++) AddFound(0f, s_overlaps[i], false);
            if (dist <= 0f) return;
            n = Physics.BoxCastNonAlloc(from, half, dir, s_hits, transform.rotation, dist, mask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++) AddFound(s_hits[i].distance, s_hits[i].collider, false);
            return;
        }

        float radius = _shape is SphereCollider sphere
            ? sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z))
            : Mathf.Min(_shape.bounds.extents.x, _shape.bounds.extents.y, _shape.bounds.extents.z);
        CollectSphere(from, dir, dist, radius, mask, charactersOnly: false);
    }

    private void CollectSphere(Vector3 from, Vector3 dir, float dist, float radius, int mask, bool charactersOnly)
    {
        QueryTriggerInteraction triggers = charactersOnly ? QueryTriggerInteraction.Ignore : QueryTriggerInteraction.Collide;
        int n = Physics.OverlapSphereNonAlloc(from, radius, s_overlaps, mask, triggers);
        for (int i = 0; i < n; i++) AddFound(0f, s_overlaps[i], charactersOnly);
        if (dist <= 0f) return;
        n = Physics.SphereCastNonAlloc(from, radius, dir, s_hits, dist, mask, triggers);
        for (int i = 0; i < n; i++) AddFound(s_hits[i].distance, s_hits[i].collider, charactersOnly);
    }

    // Las capas que chocan con 'layer' según la matriz de Project Settings ▸ Physics.
    private static int CollisionMask(int layer)
    {
        int mask = 0;
        for (int i = 0; i < 32; i++)
            if (!Physics.GetIgnoreLayerCollision(layer, i)) mask |= 1 << i;
        return mask;
    }

    private void AddFound(float distance, Collider col, bool charactersOnly)
    {
        if (col == null || col.transform.IsChildOf(transform)) return;   // el propio proyectil
        if (charactersOnly && col.GetComponentInParent<AbilitySystemComponent>() == null) return;
        s_found.Add(new KeyValuePair<float, Collider>(distance, col));
    }

    private GA_ProjectileShoot Shot => sourceAbility as GA_ProjectileShoot;
}
