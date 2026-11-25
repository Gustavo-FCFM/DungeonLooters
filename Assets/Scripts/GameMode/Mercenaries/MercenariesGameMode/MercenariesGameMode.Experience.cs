using System;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

// ============================================================
// MercenariesGameMode — LA EXPERIENCIA COMPARTIDA (la bolsa del equipo)
//
// Una parte de MercenariesGameMode (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/MercenariesGameMode.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class MercenariesGameMode
{

    // =========================================================
    // EXPERIENCIA COMPARTIDA
    //
    // Cada equipo tiene UNA bolsa. Todo lo que gana cualquiera de sus jugadores cae
    // ahí, y el nivel de los TRES sale de esa bolsa. Como el nivel es del equipo y no
    // de la persona, cambiar de clase en la base no cuesta progreso: el servidor le
    // vuelve a subir el nivel al que acaba de cambiar (ver ServerSyncPlayerLevels).
    // =========================================================

    // Experiencia total para llegar al nivel máximo (suma de todos los saltos).
    public float TotalXpForMaxLevel => XpToMaxLevel(XpPerLevel, MaxTeamLevel);

    // Experiencia pasiva por segundo. Por defecto sale sola de
    // MinutesToGuaranteedMaxLevel: es el piso que garantiza que NADIE se quede en
    // nivel 1 aunque no pelee.
    public float PassiveXpPerSecond
        => PassiveXpRate(XpPerLevel, MaxTeamLevel, MinutesToGuaranteedMaxLevel, PassiveXpPerSecondOverride);

    [Server]
    private void ServerTickSharedExperience()
    {
        // Este tick corre 2 veces por segundo (ver Update).
        float amount = PassiveXpPerSecond * 0.5f;
        for (int team = 1; team <= TeamCount; team++) ServerAddTeamXp(team, amount);
    }

    // Suma experiencia a la bolsa de un equipo y sube su nivel si corresponde.
    [Server]
    public void ServerAddTeamXp(int team, float amount, bool announce = true)
    {
        if (!IsValidTeam(team) || amount <= 0f) return;
        if (State != EMatchState.Playing) return;

        int i = team - 1;
        if (_teamLevel[i] >= MaxTeamLevel) return;

        _teamXp[i] += amount;

        int newLevel = LevelFromXp(_teamXp[i]);
        if (newLevel > _teamLevel[i])
        {
            _teamLevel[i] = newLevel;
            _netLevels[i] = newLevel;
            ServerApplyTeamLevel(team);
            if (announce) Announce(EMatchAnnouncement.TeamLevelUp, team, newLevel);
        }

        _netXpNorm[i] = XpProgressWithinLevel(_teamXp[i], _teamLevel[i]);
    }

    // Nivel que corresponde a una cantidad de experiencia acumulada.
    private int LevelFromXp(float xp) => LevelFromXp(xp, XpPerLevel, MaxTeamLevel);

    // Progreso 0..1 DENTRO del nivel actual (para la barrita del HUD).
    private float XpProgressWithinLevel(float xp, int level) => XpProgressWithinLevel(xp, level, XpPerLevel, MaxTeamLevel);

    // Una baja: el equipo del que remató se lleva la experiencia. La llama
    // NetworkAbilitySystemComponent.AwardKillExperience, que es el único punto del
    // core que sabe quién dio el golpe final (LastAttacker).
    [Server]
    public void ServerAwardKill(AbilitySystemComponent killer, AbilitySystemComponent victim)
    {
        if (killer == null || victim == null) return;

        bool victimIsPlayer = victim.GetComponent<PlayerController>() != null;

        // Un NPC puede valer más que el resto (un jefe no da lo mismo que un fantasma):
        // si trae su propia recompensa, esa manda sobre el valor general del modo.
        MercEnemyAI enemy = victimIsPlayer ? null : victim.GetComponent<MercEnemyAI>();
        float amount = KillXp(victimIsPlayer, enemy != null ? enemy.ExperienceReward : 0f, XpPerPlayerTakedown, XpPerNpcKill);

        ServerAddTeamXp(killer.TeamID, amount);

        OnServerKill?.Invoke(killer, victim);
    }

    // Le pone a cada jugador del equipo el nivel de la bolsa. Es idempotente: si ya
    // lo tiene no hace nada, así que se puede llamar todo lo seguido que haga falta.
    [Server]
    private void ServerApplyTeamLevel(int team)
    {
        int level = _teamLevel[Mathf.Clamp(team - 1, 0, TeamCount - 1)];

        foreach (PlayerController pc in _players)
        {
            if (pc == null) continue;
            AbilitySystemComponent asc = pc.GetComponent<AbilitySystemComponent>();
            if (asc == null || asc.TeamID != team) continue;
            asc.SetLevelTo(level);
        }
    }

    // Repasa a TODOS los jugadores y les deja el nivel de su equipo. Corre 2 veces por
    // segundo, y es lo que hace que cambiar de clase (que reinicia el personaje a nivel
    // 1) no cueste nada: al instante siguiente vuelve al nivel del equipo.
    //
    // De paso publica la bolsa del equipo en los atributos Exp/MaxExp del jugador, así
    // la barra de experiencia del HUD personal muestra el progreso COMPARTIDO.
    [Server]
    private void ServerSyncPlayerLevels()
    {
        foreach (PlayerController pc in _players)
        {
            if (pc == null) continue;
            AbilitySystemComponent asc = pc.GetComponent<AbilitySystemComponent>();
            if (asc == null || !IsValidTeam(asc.TeamID)) continue;

            int i = asc.TeamID - 1;
            asc.SetLevelTo(_teamLevel[i]);

            float need  = XpNeededForLevel(_teamLevel[i], XpPerLevel);
            float shown = _netXpNorm[i] * need;

            if (!Mathf.Approximately(asc.GetAttributeValue(EAttributeType.MaxExp), need))
                asc.SetCurrentAttributeValue(EAttributeType.MaxExp, need);
            if (Mathf.Abs(asc.GetAttributeValue(EAttributeType.Exp) - shown) > 0.5f)
                asc.SetCurrentAttributeValue(EAttributeType.Exp, shown);
        }
    }
}
