using UnityEngine;
using System.Collections.Generic;

// ============================================================
// Entity_ShieldBarrier — LEVANTAR, BAJAR Y A QUIÉN CUBRE
//
// Una parte de Entity_ShieldBarrier (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/Entity_ShieldBarrier.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class Entity_ShieldBarrier
{

    // =========================================================
    // LEVANTAR / BAJAR
    // =========================================================

    private void SetRaised(bool raised)
    {
        if (_raised == raised) return;
        ApplyRaisedState(raised);
    }

    // Aplica el estado SIN el guard de "ya estaba así". Separado de SetRaised para que
    // Awake pueda forzar el estado apagado de arranque (ver el comentario allá).
    private void ApplyRaisedState(bool raised)
    {
        _raised = raised;

        if (_box != null) _box.enabled = raised;

        if (_visuals != null)
            foreach (var v in _visuals)
                if (v != null) v.gameObject.SetActive(raised);

        if (raised) { if (!s_raised.Contains(this)) s_raised.Add(this); }   // la lista de los bots (ver RaisedNow)
        else        s_raised.Remove(this);

        if (raised)
        {
            _raisedAt = Time.time; // arranca la ventana del parry
            Register(_ownerASC);   // el dueño siempre queda cubierto
            _scanTimer = 0f;       // que el primer barrido de aliados sea inmediato
        }
        else
        {
            UnregisterAll();
        }
    }

    // =========================================================
    // A QUIÉN CUBRE
    // =========================================================

    // Registra el modificador en los aliados cercanos y lo saca de los que ya no
    // están. Estar registrado NO significa estar protegido: solo significa "a este
    // se le va a consultar la barrera cuando reciba un golpe". Si el aliado no está
    // realmente detrás, el raycast de Blocks() da false y el golpe pasa entero.
    private void RefreshCoveredAllies()
    {
        _scanBuffer.Clear();
        _scanBuffer.Add(_ownerASC);

        using var cols = PhysicsQuery.OverlapSphere(_ownerASC.transform.position, AllyScanRange, CharacterLayer);
        foreach (var c in cols)
        {
            AbilitySystemComponent asc = c.GetComponentInParent<AbilitySystemComponent>();
            if (asc == null || _scanBuffer.Contains(asc)) continue;
            if (!_ownerASC.IsAllyOf(asc, includeSelf: true)) continue;
            _scanBuffer.Add(asc);
        }

        // Bajas: los que estaban registrados y ya no aparecen.
        for (int i = _registeredOn.Count - 1; i >= 0; i--)
        {
            AbilitySystemComponent asc = _registeredOn[i];
            if (asc != null && _scanBuffer.Contains(asc)) continue;

            asc?.UnregisterIncomingDamageModifier(this);
            _registeredOn.RemoveAt(i);
        }

        // Altas: los nuevos.
        foreach (var asc in _scanBuffer) Register(asc);
    }

    private void Register(AbilitySystemComponent asc)
    {
        if (asc == null || _registeredOn.Contains(asc)) return;
        asc.RegisterIncomingDamageModifier(this);
        _registeredOn.Add(asc);
    }

    private void UnregisterAll()
    {
        foreach (var asc in _registeredOn) asc?.UnregisterIncomingDamageModifier(this);
        _registeredOn.Clear();
    }
}
