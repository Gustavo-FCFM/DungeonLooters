using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;

// ============================================================
// GC_Projectile — POTENCIADO Y DEVUELTO POR UN PARRY
//
// Una parte de GC_Projectile (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/GC_Projectile.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class GC_Projectile
{

    [Server]
    private void Boost(float scale)
    {
        _boosted          = true;
        _boostScale.Value = Mathf.Max(0.1f, scale);
        ApplyBoostScale(_boostScale.Value);
    }

    // Crece desde el tamaño con que salió (en el servidor al potenciarse; en los demás, al llegar el SyncVar).
    private void ApplyBoostScale(float scale)
    {
        if (!_baseScaleKnown) { _baseScale = transform.localScale; _baseScaleKnown = true; }
        transform.localScale = _baseScale * scale;
    }

    private void OnBoostScaleChanged(float previous, float next, bool asServer)
    {
        if (!asServer) ApplyBoostScale(next);
    }

    // =========================================================
    // DEVUELTO POR UN PARRY (escudo del Guardián)
    // =========================================================

    // Las copias INSTANTÁNEAS se usan solo al chocar: se van con el proyectil. Las que
    // duran (la quemadura del Clérigo de la Luz) no: un efecto activo en alguien todavía
    // puede apuntar a ellas. Son chicas y se liberan al cambiar de escena.
    private void OnDestroy()
    {
        NotifyThrownGone();   // por si se fue sin DespawnSelf (fin de partida, cambio de escena)
        s_flying.Remove(this);
        foreach (GameplayEffect copy in _frozen.Values)
            if (copy != null && !copy.HasDuration) Destroy(copy);
    }

    private GameplayEffect Frozen(GameplayEffect effect)
        => effect != null && _frozen.TryGetValue(effect, out GameplayEffect copy) ? copy : effect;

    // Congela lo que el proyectil devuelto puede aplicar: lo de "al golpear" que va a los
    // enemigos (o a todos). Con los stats de 'shooter', el que lo tiró.
    private void FreezeEffects(AbilitySystemComponent shooter)
    {
        if (sourceAbility == null || shooter == null) return;

        List<GameplayEffect> hostile = new List<GameplayEffect>();
        sourceAbility.GetEffects(EEffectWhen.OnHit, EEffectTarget.Enemies, hostile);
        sourceAbility.GetEffects(EEffectWhen.OnHit, EEffectTarget.Everyone, hostile);

        foreach (GameplayEffect effect in hostile)
            if (effect.ScalesWithSource && !_frozen.ContainsKey(effect))
                _frozen[effect] = effect.CreateFrozenCopy(shooter);
    }

    // Cambia de dueño y de dirección, en el servidor (el vuelo lo simula él y el
    // NetworkTransform lo lleva a los demás). Conserva la velocidad y sus efectos: ahora
    // dañan al equipo del lanzador original, con el daño que traían (ver _frozen). Lo que
    // cura a aliados (la estela del Castigo divino) se descarta: curaría a los del
    // Guardián con la magia del Paladín.
    public void Reflect(AbilitySystemComponent newSource, Vector3 direction)
    {
        if (!IsServerInitialized || newSource == null) return;

        FreezeEffects(sourceASC);   // ANTES de cambiar de dueño: con los stats del que lo tiró

        _reflected      = true;
        StopBounceAndReturn();   // devuelto ya no rebota ni vuelve: sigue derecho
        sourceASC       = newSource;
        ultChargeAmount = 0f;
        enemiesHit.Clear();
        alliesHit.Clear();

        Vector3 dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : -transform.forward;

        Rigidbody rb = GetComponent<Rigidbody>();
        float speed = rb != null ? Mathf.Max(rb.linearVelocity.magnitude, 10f) : 10f;

        // Un poco hacia afuera, para que no siga adentro de la barrera que lo devolvió.
        transform.SetPositionAndRotation(transform.position + dir * 0.5f, Quaternion.LookRotation(dir));
        if (rb != null) rb.linearVelocity = dir * speed;

        // El barrido arranca de nuevo desde acá, sin recordar lo del tramo de ida: a la
        // vuelta puede pegarle al que lo tiró (o a uno de los suyos que ya atravesó).
        _trajectory++;
        _touched.Clear();
        _impactShown.Clear();
        _lastCenter = ShapeCenter();
    }

    // Reproduce el VFX de impacto en TODOS los peers. Un Instantiate() local acá
    // solo existiría en el proceso servidor (que es donde corre el choque), así
    // que reusamos el mismo mecanismo que las demás habilidades: el servidor lo
    // reproduce y le avisa a cada cliente que haga lo mismo con SU PROPIA copia de
    // esta habilidad, sin sincronizar el GameObject del VFX en sí.
    private void PlayImpactVFXEverywhere(Vector3 position)
    {
        if (sourceAbility == null || sourceASC == null || !sourceAbility.HasImpactFeedback) return;

        NetworkAbilitySystemComponent shooterNetAsc = sourceASC.GetComponent<NetworkAbilitySystemComponent>();
        if (shooterNetAsc != null)
            shooterNetAsc.ServerPlayAbilityVFX(sourceAbility, position);
        else
            sourceAbility.PlayImpactVFX(position); // fallback sin red
    }

    // Despawnea el proyectil en red (avisa a todos los clientes que
    // borren su copia) — un Destroy() normal solo lo sacaría del servidor.
    private void DespawnSelf()
    {
        s_flying.Remove(this);
        NotifyThrownGone();
        _gone = true;
        if (IsServerInitialized && IsSpawned)
            ServerManager.Despawn(gameObject);
    }
}
