using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Connection;

// ============================================================
// NetworkAbilitySystemComponent — LO QUE SE VE DEL COMBATE (destellos, sensación, números, barra de canalizar)
//
// Una parte de NetworkAbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en Network/NetworkAbilitySystemComponent.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class NetworkAbilitySystemComponent
{

    // =========================================================
    // RESPUESTA VISUAL DEL COMBATE
    //
    // Un golpe le tiene que decir algo a los DOS, y cosas distintas:
    //
    //   · al que pegó   → la X en su retícula, más opaca cuanto menos vida le quedó al
    //                     otro, y la calavera si lo mató
    //   · al que recibió→ el arco rojo del lado por donde vino el golpe
    //
    // Va por TargetRpc a cada dueño y no por ObserversRpc: esto es información PRIVADA
    // de cada jugador. Mandarla a todos gastaría red y le contaría a los demás que
    // alguien está peleando.
    //
    // Lo llama el ASC desde el final de su pipeline de daño, que es el único punto donde
    // se sabe todo junto (ver AbilitySystemComponent.ReportCombatFeedback).
    // =========================================================

    // ESTE componente es el del que RECIBIÓ el golpe.
    [Server]
    public void ServerReportDamage(AbilitySystemComponent attacker, bool critical = false)
    {
        if (_asc == null || attacker == null) return;

        // --- lo que ve el que PEGÓ ---
        NetworkAbilitySystemComponent attackerNet = attacker.GetComponent<NetworkAbilitySystemComponent>();
        if (attackerNet != null && attackerNet.HasOwnerConnection())
        {
            float max    = _asc.GetAttributeValue(EAttributeType.MaxHealth);
            float health = _asc.GetAttributeValue(EAttributeType.Health);
            float fraction = max > 0f ? Mathf.Clamp01(health / max) : 0f;

            // Una baja solo cuenta si cayó un PERSONAJE (jugador o bot). Los monstruos
            // del mapa se matan de a montones: si cada uno tirara calavera, dejaría de
            // significar algo.
            bool killedCharacter = _asc.HasTag(EGameplayTag.State_Dead)
                                && GetComponent<PlayerController>() != null;

            attackerNet.TargetDamageDealt(attackerNet.Owner, fraction, killedCharacter, critical);
        }

        // --- lo que ve el que RECIBIÓ ---
        if (HasOwnerConnection())
            TargetDamageTaken(Owner, attacker.transform.position);
    }

    [TargetRpc]
    private void TargetDamageDealt(NetworkConnection conn, float victimHealthFraction, bool killedCharacter, bool critical)
    {
        UI_CombatFeedback feedback = UI_CombatFeedback.Get();
        if (feedback == null) return;

        feedback.ShowHit(victimHealthFraction, critical);
        if (killedCharacter) feedback.ShowKill();
    }

    // =========================================================
    // SENSACIÓN DEL GOLPE (ver CombatFeel)
    //
    // Dos avisos que no llevan lógica: cada pantalla decide si lo muestra. El parón lo ven todos,
    // porque el que se congela es el personaje. La definitiva solo le interesa a su dueño.
    // =========================================================

    // ESTE componente es el del que PEGÓ.
    [Server]
    public void ServerImpactStop()
    {
        if (_asc == null || !CombatFeel.Config.HitStop) return;

        ObserversImpactStop(CombatFeel.Config.HitStopSecondsFor(_asc.GetAttributeValue(EAttributeType.AtkSpeed)));
    }

    [ObserversRpc]
    private void ObserversImpactStop(float seconds,
                                     FishNet.Transporting.Channel channel = FishNet.Transporting.Channel.Unreliable)
        => CombatFeel.RaiseImpactStop(this, seconds);

    [Server]
    public void ServerUltimateCast()
    {
        if (HasOwnerConnection()) TargetUltimateCast(Owner);
    }

    [TargetRpc]
    private void TargetUltimateCast(NetworkConnection conn) => CombatFeel.RaiseUltimateCast();

    [Server]
    public void ServerShowCombatNumber(float amount, ECombatNumber kind, bool shielded = false)
    {
        if (amount <= 0f) return;

        if (kind == ECombatNumber.Heal)
        {
            if (_pendingHeal <= 0f) _pendingHealFlushAt = Time.time + HealNumberBatchSeconds;
            _pendingHeal += amount;
            return;
        }

        SendCombatNumber(amount, kind, shielded);
    }

    private void FlushPendingHealNumber()
    {
        if (_pendingHeal <= 0f || Time.time < _pendingHealFlushAt) return;

        SendCombatNumber(_pendingHeal, ECombatNumber.Heal);
        _pendingHeal = 0f;
    }

    private void SendCombatNumber(float amount, ECombatNumber kind, bool shielded = false)
    {
        // Menos de medio punto no se muestra: un "0" o un "1" que no fue nada confunde.
        int rounded = Mathf.RoundToInt(amount);
        if (rounded <= 0) return;

        ObserversCombatNumber((ushort)Mathf.Min(rounded, ushort.MaxValue), (byte)kind, shielded);
    }

    [ObserversRpc]
    private void ObserversCombatNumber(ushort amount, byte kind, bool shielded,
                                       FishNet.Transporting.Channel channel = FishNet.Transporting.Channel.Unreliable)
    {
        if (_asc == null) return;

        // Un enemigo invisible no puede delatarse por los números que le salen encima
        // (salvo que esté revelado, ver IsHiddenFromEnemies).
        if (_asc.IsHiddenFromEnemies)
        {
            PlayerController local = PlayerController.LocalPlayer;
            AbilitySystemComponent localAsc = local != null ? local.GetComponent<AbilitySystemComponent>() : null;
            if (localAsc == null || localAsc.IsEnemyOf(_asc)) return;
        }

        UI_CombatFlash.Get()?.Show(this, (ECombatNumber)kind, shielded);

        if (IsOwner && NetMaxHealth > 0f)
            CombatFeel.RaiseLocalCombatNumber((ECombatNumber)kind, shielded, amount / NetMaxHealth * 100f);

        // Dónde salen: entre los pies y la barra de vida (ver UI_DamageNumbers.HeightFraction),
        // así caen a la altura del pecho —donde uno apunta— en un personaje de cualquier
        // tamaño: el jefe es más alto, y su barra también.
        UI_WorldHealthbar bar = GetComponent<UI_WorldHealthbar>();
        Vector3 feet = transform.position;
        Vector3 top  = bar != null && bar.BarRoot != null
            ? bar.BarRoot.position
            : feet + Vector3.up * 2.2f;

        UI_DamageNumbers.Get()?.Spawn(feet, top, amount, (ECombatNumber)kind);
    }

    [TargetRpc]
    private void TargetDamageTaken(NetworkConnection conn, Vector3 attackerPosition)
    {
        UI_CombatFeedback feedback = UI_CombatFeedback.Get();
        if (feedback != null) feedback.ShowDamageFrom(attackerPosition, transform);
    }

    [Server]
    public void ServerStartLeap(GA_LeapAttack ability, float upVelocity, float forwardForce)
    {
        _pendingLeapImpact = ability;

        // Sin dueño (un BOT) el salto lo simula el servidor sobre el propio bot, y al
        // aterrizar resuelve el impacto por el mismo camino. Antes se descartaba: los
        // bárbaros nunca saltaban.
        if (!HasOwnerConnection())
        {
            BotController brain = GetComponent<BotController>();
            if (brain != null) brain.ServerLeap(transform.forward * forwardForce, upVelocity, ServerApplyLeapImpact);
            else               _pendingLeapImpact = null;
            return;
        }

        TargetExecuteLeap(Owner, upVelocity, forwardForce);
    }

    // Solo corre en el cliente dueño: calcula la dirección del salto con
    // SU cámara y le pide a PlayerController que aplique el impulso.
    [TargetRpc]
    private void TargetExecuteLeap(NetworkConnection conn, float upVelocity, float forwardForce)
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc == null) { Debug.LogWarning("[NetASC] No hay PlayerController — no se puede ejecutar el salto."); return; }
        if (Camera.main == null) { Debug.LogWarning("[NetASC] Camera.main es null — no se puede calcular la dirección del salto."); return; }

        Vector3 camFwd = Camera.main.transform.forward;
        camFwd.y = 0;
        camFwd.Normalize();

        if (pc.ApplyAbilityVelocity(camFwd * forwardForce, upVelocity))
            StartCoroutine(WaitForLandingThenResolve(pc));
    }

    // Espera a que el dueño despegue del piso y después a que vuelva a
    // aterrizar, y recién ahí le pide al servidor que resuelva el impacto.
    private IEnumerator WaitForLandingThenResolve(PlayerController pc)
    {
        // BUG QUE ARREGLAMOS: acá había un solo "frame de gracia" antes de
        // chequear IsGrounded una vez. El orden entre el Update() de
        // PlayerController (que recién ahí aplica el impulso hacia arriba
        // vía Move()) y la reanudación de esta corutina no está garantizado
        // — si esa primera lectura llegaba a pasar ANTES de que el
        // CharacterController realmente despegara, el while de abajo nunca
        // entraba y resolvíamos el impacto instantáneamente en el punto de
        // DESPEGUE en vez del de ATERRIZAJE, así que casi nunca había un
        // enemigo cerca.
        //
        // Solución: esperar explícitamente la transición "despegó" antes de
        // esperar la transición "aterrizó" — así no importa el orden de
        // ejecución entre scripts, solo importa el estado real de
        // IsGrounded. El timeout es un salvavidas por si algo (ej. un
        // obstáculo encima) impide despegar del todo.
        float elapsed = 0f;
        const float liftoffTimeout = 1f;
        while (pc.IsGrounded && elapsed < liftoffTimeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (elapsed >= liftoffTimeout)
            Debug.LogWarning("[NetASC] Salto: nunca detecté el despegue (timeout) — probablemente algo bloqueó el impulso hacia arriba.");

        // Aterrizar, o engancharse a una pared a mitad del salto (el Explorador y el Pícaro):
        // las dos cosas terminan el salto.
        while (!pc.IsGrounded && !pc.IsOnWall)
            yield return null;

        pc.ClearAbilityVelocity();
        pc.FinishAttack();
        ServerResolveLeapImpact();
    }

    // Resuelve el daño en área del aterrizaje, con la instancia de
    // GA_LeapAttack guardada en ServerStartLeap.
    [ServerRpc]
    private void ServerResolveLeapImpact() => ServerApplyLeapImpact();

    // El impacto en sí, sin pasar por el ServerRpc de arriba.
    //
    // Un ServerRpc lo tiene que invocar el DUEÑO, y un BOT no tiene: llamarlo sobre su
    // objeto fallaría. Esta versión la usa el salto de un bot, que ya corre en el servidor.
    [Server]
    private void ServerApplyLeapImpact()
    {
        _pendingLeapImpact?.ExecuteImpactCheck();
        _pendingLeapImpact = null;
    }

    // =========================================================
    // BARRA DE CANALIZAR (UI_CastBar) — solo la ve el dueño. La piden las habilidades en
    // el servidor con GameplayAbility.ShowCastBar / HideCastBar. Un bot no tiene pantalla.
    // =========================================================

    // Viaja el ÍNDICE de la habilidad, no su nombre: el dueño lo escribe en SU idioma.
    [Server]
    public void ServerShowCastBar(GameplayAbility ability, float duration, bool channel)
    {
        if (!HasOwnerConnection()) return;
        int index = GameplayAbilityRegistry.Instance != null && ability != null
            ? GameplayAbilityRegistry.Instance.GetIndex(ability) : -1;
        TargetShowCastBar(Owner, index, duration, channel);
    }

    [TargetRpc]
    private void TargetShowCastBar(NetworkConnection conn, int abilityIndex, float duration, bool channel)
    {
        GameplayAbility ability = abilityIndex >= 0
            ? GameplayAbilityRegistry.Instance?.GetAbility(abilityIndex) : null;
        UI_CastBar.Show(ability != null ? ability.DisplayName : "", duration, channel);
    }

    [Server]
    public void ServerHideCastBar(bool interrupted)
    {
        if (!HasOwnerConnection()) return;
        TargetHideCastBar(Owner, interrupted);
    }

    [TargetRpc]
    private void TargetHideCastBar(NetworkConnection conn, bool interrupted)
    {
        UI_CastBar.Hide(interrupted);
    }

    // Un NPC: el servidor es su autoridad. Con NavMeshAgent se mueve con agent.Move, que
    // no lo saca del NavMesh (no lo mete en una pared ni lo tira del mapa); sin agente, el
    // transform directo. Arranca rápido y frena al final, igual que en un jugador.
    private IEnumerator ServerPushRoutine(Vector3 displacement, float duration)
    {
        UnityEngine.AI.NavMeshAgent agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
        float elapsed = 0f;

        while (elapsed < duration)
        {
            float before = PlayerController.KnockbackEase(elapsed / duration);
            elapsed += Time.deltaTime;
            float after  = PlayerController.KnockbackEase(Mathf.Min(1f, elapsed / duration));

            Vector3 step = displacement * (after - before);
            if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Move(step);
            else                                                      transform.position += step;

            yield return null;
        }
    }
}
