using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — DESATASCARSE
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    // Solo servidor: dónde "se quedó" el personaje y desde cuándo.
    private void TrackStillness()
    {
        Vector3 p = transform.position;
        if ((p - _stillAnchor).sqrMagnitude > UnstuckStillRadius * UnstuckStillRadius)
        {
            _stillAnchor = p;
            _stillSince  = Time.time;
        }
    }

    public void RequestUnstuck()
    {
        // Lo local, que es barato y no le da ventaja a nadie: el modo de input según los
        // menús que de verdad estén abiertos (Apply limpia los ya destruidos) y el
        // estado de combate local.
        // La foto ANTES de tocar nada: después de Apply y FinishAttack ya no se vería qué estaba
        // trabado.
        string clientReport = DescribeLocalState();

        UICursor.Apply();
        FinishAttack();

        Debug.Log($"[Unstuck] Pedido local. {clientReport}");

        // Y la decisión, que es del servidor (con la foto, para que el host la guarde).
        if (IsSpawned) ServerUnstuck(clientReport);
    }

    // Lo que ve el DUEÑO de su personaje: lo que el servidor no puede saber (el input, los
    // menús, el cursor, el movimiento, que son de este lado).
    private string DescribeLocalState()
    {
        var parts = new List<string>
        {
            $"cursor/input: {UICursor.DescribeHolders()}",
            $"input listo: {(_input != null && _input.IsReady)}",
            $"bloqueado por menú: {_inputLocked}",
            $"atacando: {isAttacking}" + (isAttacking ? $" ({Time.time - _attackStartTime:0.0} s, ranura {_runningSlot})" : ""),
        };
        if (_holdAbility != null)          parts.Add($"mantenido: {_holdAbility.AbilityName}");
        if (isRadialMenuOpen)              parts.Add($"rueda abierta: {(currentRadialAbility != null ? currentRadialAbility.AbilityName : "?")}");
        if (_groundTargetAbility != null)  parts.Add($"apuntando zona: {_groundTargetAbility.AbilityName}");

        var moving = new List<string>();
        if (_knockbackActive)        moving.Add("empujado");
        if (_rushActive)             moving.Add("embestida");
        if (_dashActive)             moving.Add("dash");
        if (_abilityVelocityActive)  moving.Add("impulso de habilidad");
        if (_leapTakeoffPending)     moving.Add("despegue de salto pendiente");
        if (_wallActive)             moving.Add("en una pared");
        if (IsFlying || _flightFalling) moving.Add("vuelo");
        if (moving.Count > 0) parts.Add($"movimiento especial: {string.Join(", ", moving)}");

        if (characterController != null)
            parts.Add($"CharacterController: {(characterController.enabled ? "prendido" : "APAGADO")}, " +
                      $"en el piso: {characterController.isGrounded}");
        if (_ragdoll != null && _ragdoll.IsActive) parts.Add("ragdoll ACTIVO");
        parts.Add($"posición: {transform.position:F1}");
        parts.Add($"clase: {(CurrentClassDef != null ? CurrentClassDef.ClassName : "ninguna")}");
        if (ASC != null) parts.Add($"tags (vistos acá): {DescribeTags()}");
        parts.Add($"cursor: {Cursor.lockState}, timeScale: {Time.timeScale}");
        return string.Join(" · ", parts);
    }

    private string DescribeTags()
    {
        var tags = new List<string>();
        foreach (EGameplayTag tag in ASC.ActiveTags) tags.Add(tag.ToString());
        return tags.Count > 0 ? string.Join(", ", tags) : "ninguno";
    }

    private static void WriteUnstuckLog(string entry)
    {
        try
        {
            string folder = Application.isEditor
                ? System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "Logs"))
                : Application.persistentDataPath;
            System.IO.Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, "Unstuck.log");
            System.IO.File.AppendAllText(path, $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}] {entry}\n\n");

            if (!_unstuckPathAnnounced)
            {
                _unstuckPathAnnounced = true;
                Debug.Log($"[Unstuck] Guardado en {path}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Unstuck] No se pudo guardar el registro: {e.Message}");
        }
    }

    [ServerRpc]
    private void ServerUnstuck(string clientReport)
    {
        string who = Owner != null && Owner.IsValid ? $"'{name}' (cliente {Owner.ClientId})" : $"'{name}'";

        string rejection = UnstuckRejection(out int seconds);
        if (rejection != null)
        {
            WriteUnstuckLog($"{who} pidió desatascarse — RECHAZADO ({rejection}).\n" +
                            $"  Su lado: {clientReport}\n" +
                            $"  Servidor: {DescribeServerState()}");
            TargetUnstuckRejected(Owner, rejection, seconds, Mathf.RoundToInt(UnstuckStillSeconds));
            return;
        }

        // --- 1) Diagnóstico: qué estaba trabado (sin tocarlo: el reemplazo lo arregla) ---
        List<string> found = new List<string>();
        if (ASC != null)
        {
            foreach (GameplayAbility granted in ASC.GrantedAbilities)
                if (granted is IHoldAbility hold && hold.IsHolding)
                    found.Add($"mantenido abierto ({granted.AbilityName})");

            foreach (EGameplayTag tag in ControlTags)
                if (ASC.HasTag(tag) && !HasEffectGranting(tag))
                    found.Add($"{tag} pegado sin efecto");
        }
        if (isAttacking) found.Add("isAttacking trabado en el servidor");

        string foundText = found.Count > 0 ? string.Join(" · ", found)
                                           : "nada trabado del lado del servidor (mirar su lado)";
        Debug.LogWarning($"[Unstuck] {who} pidió desatascarse. Encontrado: {foundText}");
        WriteUnstuckLog($"{who} se desatascó. Encontrado: {foundText}\n" +
                        $"  Su lado: {clientReport}\n" +
                        $"  Servidor: {DescribeServerState()}");

        // --- 2) Personaje nuevo, misma clase ---
        int   classIndex = VisualClassIndex;
        float ultCharge  = ASC != null && AbilityR != null ? ASC.GetUltimateCharge() : -1f;

        _unstuckReadyAt[Owner.ClientId] = Time.time + UnstuckCooldownSeconds;

        NetworkGameManager gm = FindFirstObjectByType<NetworkGameManager>();
        PlayerController fresh = gm != null ? gm.ServerRespawnFresh(this) : null;
        if (fresh == null)
        {
            Debug.LogWarning("[Unstuck] No se pudo crear el personaje nuevo (¿sin NetworkGameManager?).");
            return;
        }

        // La definitiva se conserva entera: desatascarse no es cambiar de clase.
        if (ultCharge >= 0f) fresh._carriedUltimateCharge = ultCharge;
        if (classIndex >= 0) fresh._netRespawnClassIndex.Value = classIndex;
    }

    // Motivo para NO aceptar el pedido, o null si se acepta. Es la CLAVE del texto en Loc
    // (lo escribe la pantalla del jugador, en su idioma); 'seconds' es el número que va
    // adentro cuando hace falta (cuánto falta).
    [Server]
    private string UnstuckRejection(out int seconds)
    {
        seconds = 0;
        if (ASC == null || Owner == null || !Owner.IsValid) return "unstuck.not_now";
        if (ASC.HasTag(EGameplayTag.State_Dead)) return "unstuck.dead";

        if (_unstuckReadyAt.TryGetValue(Owner.ClientId, out float readyAt) && Time.time < readyAt)
        {
            seconds = Mathf.CeilToInt(readyAt - Time.time);
            return "unstuck.cooldown";
        }

        if (Time.time - _lastDamagedAt < UnstuckCombatLockSeconds)
            return "unstuck.combat";

        foreach (EGameplayTag tag in ControlTags)
            if (ASC.HasTag(tag) && HasEffectGranting(tag))
                return "unstuck.wait_effect";

        float still = Time.time - _stillSince;
        if (still < UnstuckStillSeconds)
        {
            seconds = Mathf.CeilToInt(UnstuckStillSeconds - still);
            return "unstuck.stand_still";
        }

        return null;
    }

    // Lo que ve el SERVIDOR del mismo personaje: lo de juego (tags, efectos, si está atacando).
    [Server]
    private string DescribeServerState()
    {
        var parts = new List<string>
        {
            $"quieto hace {Time.time - _stillSince:0.0} s",
            $"último golpe recibido hace {Time.time - _lastDamagedAt:0.0} s",
            $"atacando: {isAttacking}",
            $"posición: {transform.position:F1}",
        };
        if (ASC != null)
        {
            parts.Add($"tags: {DescribeTags()}");
            var effects = new List<string>();
            foreach (ActiveGameplayEffect e in ASC.GetActiveEffects())
                if (e != null && e.Definition != null && e.Definition.EffectType != GameplayEffect.EEffectType.Hidden)
                    effects.Add($"{e.Definition.name} ({e.DurationRemaining:0.0} s)");
            parts.Add($"efectos: {(effects.Count > 0 ? string.Join(", ", effects) : "ninguno")}");
        }
        return string.Join(" · ", parts);
    }

    // ¿Hay un efecto ACTIVO que otorgue este tag? Si sí, el control es legítimo; si el
    // tag está y ningún efecto lo da, quedó pegado (el bug).
    private bool HasEffectGranting(EGameplayTag tag)
    {
        foreach (ActiveGameplayEffect e in ASC.GetActiveEffects())
            if (e != null && e.Definition != null && e.Definition.GrantedTags != null &&
                e.Definition.GrantedTags.Contains(tag))
                return true;
        return false;
    }

    [TargetRpc]
    private void TargetUnstuckRejected(FishNet.Connection.NetworkConnection conn, string reasonKey,
                                       int seconds, int stillSeconds)
    {
        string reason = Loc.T(reasonKey, ("seconds", seconds), ("total", stillSeconds));
        UI_ScreenFeedback.Get()?.ShowToast(reason, new Color(1f, 0.75f, 0.3f, 1f));
        Debug.Log($"[Unstuck] Rechazado: {reasonKey}");
    }

    private void OnNetRespawnClassIndexChanged(int prev, int next, bool asServer)
    {
        if (!asServer) TryEquipRespawnClass();
    }

    private void TryEquipRespawnClass()
    {
        if (!IsOwner || _respawnClassEquipped) return;

        int idx = _netRespawnClassIndex.Value;
        if (idx < 0) return;

        CharacterClassDefinition def = GetClassByIndex(idx);
        if (def == null) return;

        _respawnClassEquipped = true;
        EquipCharacterClass(def);
    }
}
