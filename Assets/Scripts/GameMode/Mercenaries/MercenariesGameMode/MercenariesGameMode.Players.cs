using System;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

// ============================================================
// MercenariesGameMode — LOS JUGADORES Y LOS EQUIPOS ELIMINADOS
//
// Una parte de MercenariesGameMode (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/MercenariesGameMode.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class MercenariesGameMode
{

    // =========================================================
    // JUGADORES Y WIPES
    // =========================================================

    // Copia la lista de jugadores prendidos (PlayerController.All, que se mantiene sola al
    // prenderse y apagarse cada uno). Se copia porque lo de abajo teletransporta y cambia
    // niveles mientras la recorre.
    [Server]
    private void RefreshPlayers()
    {
        _players.Clear();
        _players.AddRange(PlayerController.All);
    }

    [Server]
    private void ServerKeepPlayersInBase()
    {
        foreach (PlayerController pc in _players)
        {
            if (pc == null) continue;

            AbilitySystemComponent asc = pc.GetComponent<AbilitySystemComponent>();
            if (asc == null || asc.HasTag(EGameplayTag.State_Dead)) continue;

            MercTeamBase home = GetBase(asc.TeamID);
            if (home == null || home.IsInsideSafeRoomFootprint(pc.transform.position, WarmupEscapeMargin))
                continue;

            Transform spawn = home.GetSpawnPoint();
            if (spawn == null) continue;

            // El transform es del dueño: a una persona se le pide por TargetRpc; a un bot,
            // que no tiene dueño, lo mueve el servidor.
            if (pc.IsBot)
            {
                pc.ServerTeleportBot(spawn.position, spawn.forward);
            }
            else
            {
                NetworkAbilitySystemComponent net = pc.GetComponent<NetworkAbilitySystemComponent>();
                if (net != null) net.ServerTeleportOwnerTo(spawn.position, spawn.forward);
            }

            Debug.Log($"[Mercenarios] '{pc.name}' salió de su base durante la preparación: de vuelta adentro.");
        }
    }

    // Cuántos jugadores de un equipo están vivos ahora mismo.
    public int CountAlive(int team)
    {
        int alive = 0;
        foreach (PlayerController pc in _players)
        {
            if (pc == null) continue;
            AbilitySystemComponent asc = pc.GetComponent<AbilitySystemComponent>();
            if (asc == null || asc.TeamID != team) continue;
            if (!asc.HasTag(EGameplayTag.State_Dead)) alive++;
        }
        return alive;
    }

    // Cuántos jugadores tiene un equipo (vivos o no).
    public int CountMembers(int team)
    {
        int n = 0;
        foreach (PlayerController pc in _players)
        {
            if (pc == null) continue;
            AbilitySystemComponent asc = pc.GetComponent<AbilitySystemComponent>();
            if (asc != null && asc.TeamID == team) n++;
        }
        return n;
    }

    // Avisa una sola vez por wipe: se rearma cuando alguien de ese equipo revive.
    [Server]
    private void ServerCheckWipes()
    {
        for (int team = 1; team <= TeamCount; team++)
        {
            int i = team - 1;
            if (WipeStep(CountMembers(team), CountAlive(team), ref _teamWiped[i]))
                Announce(EMatchAnnouncement.TeamWiped, team, 0);
        }
    }
}
