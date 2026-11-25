using System;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

// ============================================================
// MercenariesGameMode — LAS FASES (preparación, partida, fin y vuelta a la sala)
//
// Una parte de MercenariesGameMode (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/MercenariesGameMode.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class MercenariesGameMode
{

    // =========================================================
    // FASES
    // =========================================================

    [Server]
    private void ServerBeginWarmup()
    {
        _netState.Value = (int)EMatchState.Warmup;
        _phaseTimer     = Mathf.Max(1f, WarmupSeconds);
        PublishPhaseTime();

        _objectivePending      = false;
        _netObjectiveEta.Value = 0f;

        Debug.Log($"[Mercenarios] Preparación: {WarmupSeconds:F0}s.");
    }

    [Server]
    private void ServerStartMatch()
    {
        _netState.Value = (int)EMatchState.Playing;
        _phaseTimer     = Mathf.Max(30f, MatchDurationSeconds);
        PublishPhaseTime();

        ScheduleObjective(FirstObjectiveDelaySeconds);
        Announce(EMatchAnnouncement.MatchStarted, 0, 0);

        Debug.Log("[Mercenarios] ¡Partida iniciada!");
    }

    [Server]
    private void ServerEndMatch(int winnerTeam)
    {
        _netState.Value  = (int)EMatchState.Ended;
        _netWinner.Value = winnerTeam;
        _phaseTimer      = Mathf.Max(3f, RestartDelay);
        PublishPhaseTime();

        DespawnObjective();
        _objectivePending      = false;
        _netObjectiveEta.Value = 0f;

        Announce(EMatchAnnouncement.MatchEnded, winnerTeam, 0);
        Debug.Log($"[Mercenarios] Fin de partida. Ganador: {(winnerTeam > 0 ? TeamName(winnerTeam) : "empate")}.");
    }

    // Deja todo como al principio: puntajes, bolsas de experiencia y niveles. Los
    // jugadores conservan su clase, pero se los reequipa para que vuelvan a nivel 1
    // (el nivel no "baja" solo: reequipar es lo único que recalcula los stats base).
    [Server]
    // Terminada la partida, TODOS VUELVEN A LA SALA.
    //
    // Antes esto reiniciaba en el LUGAR: se ponían los puntajes en cero, se revivía a
    // cada uno y se lo teletransportaba a su base, reusando los mismos personajes. El
    // problema es que un personaje arrastra mucho más que vida y posición —efectos
    // activos, cooldowns, cargas, tags, el Objetivo encima, la cámara del espectador—
    // y cada cosa que ese reinicio no limpiara reaparecía en la partida siguiente como
    // un error sin causa visible.
    //
    // Volviendo a la sala se despawnea todo y la próxima partida nace de cero, por el
    // mismo camino que la primera. Es menos código y, sobre todo, un solo camino que
    // mantener en vez de dos.
    public void ServerRestartMatch()
    {
        for (int i = 0; i < TeamCount; i++)
        {
            _teamScore[i] = 0;
            _teamXp[i]    = 0f;
            _teamLevel[i] = 1;
            _teamWiped[i] = false;
            _netScores[i] = 0;
            _netLevels[i] = 1;
            _netXpNorm[i] = 0f;
        }

        // Saca a todos del mapa y baja MatchStarted, con lo que el panel de la sala
        // vuelve solo a la pantalla de cada uno.
        if (LobbyManager.Instance != null) LobbyManager.Instance.ServerReturnToLobby();

        _players.Clear();

        // La preparación queda armada y CONGELADA: el gate del tick la frena hasta que
        // el host apriete Start de nuevo (ver el chequeo de MatchStarted más arriba).
        ServerBeginWarmup();
    }

    // Quién va ganando (0 si hay empate). Se usa si se acaba el tiempo.
    private int ResolveLeader() => LeaderOf(_teamScore);
}
