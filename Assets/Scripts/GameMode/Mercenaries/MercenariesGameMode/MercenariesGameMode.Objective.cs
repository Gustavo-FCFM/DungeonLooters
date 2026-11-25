using System;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

// ============================================================
// MercenariesGameMode — EL OBJETIVO (aparecer, levantar, entregar)
//
// Una parte de MercenariesGameMode (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/MercenariesGameMode.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class MercenariesGameMode
{

    // =========================================================
    // OBJETIVO
    // =========================================================

    [Server]
    private void ScheduleObjective(float delay)
    {
        _objectivePending      = true;
        _objectiveTimer        = Mathf.Max(0f, delay);
        _netObjectiveEta.Value = _objectiveTimer;

        if (delay > 1f) Announce(EMatchAnnouncement.ObjectiveReturning, 0, Mathf.RoundToInt(delay));
    }

    [Server]
    private void TickObjective(float dt, bool slowTick)
    {
        if (!_objectivePending) return;

        _objectiveTimer -= dt;
        if (slowTick) _netObjectiveEta.Value = Mathf.Max(0f, _objectiveTimer);

        if (_objectiveTimer > 0f) return;

        _objectivePending      = false;
        _netObjectiveEta.Value = 0f;
        SpawnObjective();
    }

    [Server]
    private void SpawnObjective()
    {
        if (ObjectivePrefab == null)
        {
            Debug.LogError("[Mercenarios] Sin ObjectivePrefab asignado — el Objetivo nunca va a aparecer.");
            return;
        }

        DespawnObjective();

        Vector3 pos = ObjectiveSpawnPoint != null ? ObjectiveSpawnPoint.position : transform.position;
        GameObject go = Instantiate(ObjectivePrefab, pos, Quaternion.identity);
        ServerManager.Spawn(go);

        _activeObjective = go.GetComponent<MercObjective>();
        if (_activeObjective != null) _activeObjective.ServerInitialize(this, pos);
        else Debug.LogError("[Mercenarios] El prefab del Objetivo no tiene el componente MercObjective — " +
                            "se creó una caja que no se puede levantar ni entregar.");

        Announce(EMatchAnnouncement.ObjectiveSpawned, 0, 0);
        Debug.Log($"[Mercenarios] Objetivo en juego, en {pos}.");
    }

    // Atajo para probar sin esperar el reloj: con el juego corriendo, clic derecho en
    // el componente → esta opción. Solo tiene efecto en el servidor (o en el host).
    [ContextMenu("DEBUG · Hacer aparecer el Objetivo ya")]
    private void DebugSpawnObjectiveNow()
    {
        if (!Application.isPlaying || !IsServerInitialized)
        {
            Debug.LogWarning("[Mercenarios] Esto solo funciona con la partida corriendo y siendo servidor/host.");
            return;
        }

        // Si todavía estamos en preparación, arrancamos la partida: un Objetivo con la
        // partida sin empezar no lo podría puntuar nadie.
        if (State != EMatchState.Playing) ServerStartMatch();

        // Y se reprograma para "ya". Va por el camino normal (ScheduleObjective →
        // TickObjective → SpawnObjective) a propósito: si lo spawneáramos a mano acá,
        // quedaría además el que ya estaba agendado y se pisarían entre ellos.
        ScheduleObjective(0.1f);
    }

    [Server]
    private void DespawnObjective()
    {
        if (_activeObjective == null) return;

        _activeObjective.ServerReleaseCarrier();
        if (_activeObjective.IsSpawned) ServerManager.Despawn(_activeObjective.gameObject);
        _activeObjective = null;
    }

    // La llama MercObjective cuando alguien lo levanta.
    [Server]
    public void ServerNotifyObjectiveTaken(int team)
        => Announce(EMatchAnnouncement.ObjectiveTaken, team, 0);

    // La llama MercObjective cuando se cae al piso (soltado o por muerte del portador).
    [Server]
    public void ServerNotifyObjectiveDropped(int team)
        => Announce(EMatchAnnouncement.ObjectiveDropped, team, 0);

    // La llama MercObjective al entregarlo en la base de 'team'.
    [Server]
    public void ServerScoreObjective(int team)
    {
        if (!IsValidTeam(team) || State != EMatchState.Playing) return;

        int i = team - 1;
        _teamScore[i]++;
        _netScores[i] = _teamScore[i];

        Announce(EMatchAnnouncement.ObjectiveDelivered, team, _teamScore[i]);

        DespawnObjective();

        if (HasWon(_teamScore[i], PointsToWin)) { ServerEndMatch(team); return; }

        ScheduleObjective(ObjectiveRespawnDelay);
    }
}
