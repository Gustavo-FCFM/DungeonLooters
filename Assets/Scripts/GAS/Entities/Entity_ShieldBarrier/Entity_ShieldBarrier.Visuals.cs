using UnityEngine;
using System.Collections.Generic;

// ============================================================
// Entity_ShieldBarrier — EL DESTELLO DEL BLOQUEO
//
// Una parte de Entity_ShieldBarrier (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/Entity_ShieldBarrier.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class Entity_ShieldBarrier
{

    // =========================================================
    // FEEDBACK VISUAL DEL BLOQUEO
    // =========================================================

    // Dispara el destello acá y en todos los demás peers. Se llama desde el SERVIDOR
    // (los dos caminos de bloqueo se resuelven ahí), así que sin la RPC el efecto solo
    // lo vería el host — y en un hero shooter el que dispara TIENE que ver que su tiro
    // pegó en un escudo.
    private void BroadcastFlash(Vector3 hitPoint)
    {
        PlayFlash(hitPoint);   // el que resuelve (host) lo ve ya mismo

        if (_ownerASC == null) return;
        NetworkAbilitySystemComponent netAsc = _ownerASC.GetComponent<NetworkAbilitySystemComponent>();
        if (netAsc != null) netAsc.ServerBroadcastShieldBlockFlash(hitPoint);
    }

    // Reproduce el destello en ESTE peer. Pública porque la llama la RPC de
    // NetworkAbilitySystemComponent al llegar a cada cliente.
    public void PlayFlash(Vector3 hitPoint)
    {
        if (!_raised) return;
        OnFlash?.Invoke(hitPoint);
    }

    // Cápsula: el destello sale del lado del atacante, a OmniFlashRadius del pecho.
    private Vector3 OmniHitPoint(Vector3 attackerPos)
    {
        Vector3 chest = _ownerASC.transform.position + Vector3.up * AimHeight;
        Vector3 dir   = attackerPos + Vector3.up * AimHeight - chest;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) dir = _ownerASC.transform.forward;
        return chest + dir.normalized * OmniFlashRadius;
    }

    // True si el segmento atacante → víctima atraviesa el volumen de la barrera.
    //
    // Se mide a la altura del pecho (AimHeight): a ras del piso el segmento pasaría
    // por debajo del escudo y no bloquearía nunca.
    //
    // Collider.Raycast devuelve false si el rayo ARRANCA dentro del collider, y eso
    // es justo lo que queremos: un enemigo que se te mete encima del escudo te pega
    // igual, como en cualquier hero shooter.
    // 'hitPoint' devuelve dónde entró el golpe en la barrera, para el destello.
    private bool Blocks(Vector3 attackerPos, Vector3 victimPos, out Vector3 hitPoint)
    {
        hitPoint = Vector3.zero;

        Vector3 from = attackerPos + Vector3.up * AimHeight;
        Vector3 to   = victimPos   + Vector3.up * AimHeight;

        // Si el segmento queda ENTERO por encima o por debajo de la caja, el escudo
        // no bloquearía nunca por un simple desajuste de alturas (ej: la barrera
        // montada a la altura de los pies y AimHeight a la del pecho). En ese caso
        // bajamos/subimos el segmento entero hasta el centro de la caja: el chequeo
        // sigue siendo direccional, que es lo que importa, y conserva el desnivel
        // relativo entre atacante y víctima.
        Bounds box = _box.bounds;
        float lowest  = Mathf.Min(from.y, to.y);
        float highest = Mathf.Max(from.y, to.y);

        if (lowest > box.max.y || highest < box.min.y)
        {
            float shift = box.center.y - (lowest > box.max.y ? lowest : highest);
            from.y += shift;
            to.y   += shift;
        }

        Vector3 delta = to - from;
        float   dist  = delta.magnitude;
        if (dist < 0.001f) return false;

        if (!_box.Raycast(new Ray(from, delta / dist), out RaycastHit hit, dist)) return false;

        hitPoint = hit.point;
        return true;
    }

    // La barrera de MANTENER de un personaje (la que levanta GA_ShieldBlock): la primera que
    // no sea cápsula. El Monje tiene dos —el bloqueo y la de Defensa paciente— y el escudo
    // se tiene que enganchar a la suya.
    public static Entity_ShieldBarrier FindHeld(Component owner)
    {
        if (owner == null) return null;
        Entity_ShieldBarrier first = null;
        foreach (Entity_ShieldBarrier b in owner.GetComponentsInChildren<Entity_ShieldBarrier>(true))
        {
            if (!b.Omnidirectional) return b;
            if (first == null) first = b;
        }
        return first;
    }

    // True si esta barrera está levantada. Lo consulta GC_Projectile al chocar.
    public bool IsRaised => _raised;
    public static IReadOnlyList<Entity_ShieldBarrier> RaisedNow => s_raised;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetRaised() => s_raised.Clear();

    // ¿Frenaría un golpe de 'attackerPos' a 'victimPos'? (la misma cuenta que el bloqueo de verdad)
    public bool BlocksShot(Vector3 attackerPos, Vector3 victimPos)
        => _raised && _box != null && Blocks(attackerPos, victimPos, out _);

    // Le avisa a la barrera que paró un PROYECTIL físico (que se resuelve por
    // colisión con el collider, no por el pipeline de daño). Cobra
    // EnergyPerProjectileBlocked y dispara el mismo feedback que un bloqueo normal.
    //
    // Se le pasa el EFECTO de daño que traía el proyectil, no un número: el evento
    // OnDamageBlocked reporta DAÑO EVITADO, y quien lo escucha escala con eso (la
    // curación del aura del Paladín). Antes acá se mandaba el costo en energía, que
    // es otra unidad — un proyectil de 5 y uno de 80 curaban exactamente lo mismo.
    // El daño real se estima contra el dueño de la barrera, que es quien lo habría
    // recibido (ver AbilitySystemComponent.EstimateInstantDamage).
    public float NotifyProjectileBlocked(GameplayEffect damageEffect, AbilitySystemComponent shooter, Vector3 hitPoint)
    {
        if (!_raised || _ownerASC == null) return 0f;

        // Un proyectil parado en la ventana del parry no gasta energía (es de lejos: no
        // aturde a nadie, solo se frena entero, que un proyectil ya hace siempre).
        if (EnergyPerProjectileBlocked > 0f && !IsParrying)
        {
            float energy = _ownerASC.GetAttributeValue(EAttributeType.Energy);
            _ownerASC.SetCurrentAttributeValue(EAttributeType.Energy,
                                               Mathf.Max(0f, energy - EnergyPerProjectileBlocked));
        }

        float blocked = _ownerASC.EstimateInstantDamage(damageEffect, shooter);
        if (blocked > 0f) OnDamageBlocked?.Invoke(blocked, hitPoint);

        BroadcastFlash(hitPoint);
        ChargeUltimateForBlock(shooter);
        PunishAttacker(shooter);
        return blocked;
    }

    // True si este golpe viene de un enemigo del dueño de la barrera. Lo usa
    // GC_Projectile para dejar pasar los proyectiles aliados.
    public bool IsHostile(AbilitySystemComponent shooter)
        => shooter != null && _ownerASC != null && _ownerASC.IsEnemyOf(shooter);

    // Vista previa del volumen real de bloqueo en el Editor, con los mismos
    // valores del collider que usa Blocks().
    private void OnDrawGizmosSelected()
    {
        Collider col = _box != null ? _box : GetComponent<Collider>();
        if (col is CapsuleCollider capsule)
        {
            // La cápsula: las dos esferas de las puntas (en Y) y las líneas que las unen.
            Matrix4x4 old = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color  = new Color(0.3f, 0.7f, 1f, 1f);
            float half = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
            Vector3 top = capsule.center + Vector3.up * half;
            Vector3 bottom = capsule.center - Vector3.up * half;
            Gizmos.DrawWireSphere(top, capsule.radius);
            Gizmos.DrawWireSphere(bottom, capsule.radius);
            foreach (Vector3 side in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                Gizmos.DrawLine(top + side * capsule.radius, bottom + side * capsule.radius);
            Gizmos.matrix = old;
            return;
        }

        BoxCollider box = col as BoxCollider;
        if (box == null) return;

        Matrix4x4 prev = Gizmos.matrix;
        Gizmos.matrix  = transform.localToWorldMatrix;

        Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.25f);
        Gizmos.DrawCube(box.center, box.size);
        Gizmos.color = new Color(0.3f, 0.7f, 1f, 1f);
        Gizmos.DrawWireCube(box.center, box.size);

        Gizmos.matrix = prev;
    }
}
