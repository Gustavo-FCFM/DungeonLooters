using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — MUERTE, RESPAWN Y AVISOS DE UNA MUERTE
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    // =========================================================
    // MUERTE Y RESPAWN
    // =========================================================

    // Reacciona a la muerte del personaje: si tiene la resurrección de
    // Inmortal disponible la usa, si no pide el respawn normal.
    private void HandlePlayerDeath()
    {
        // ASC.OnDeath se suscribe en OnStartClient() para TODOS los peers
        // (dueño, servidor y observadores remotos), porque cada uno tiene
        // su propia copia local del ASC que dispara Die() cuando su salud
        // llega a 0 (el servidor lo dispara directo al aplicar el daño; el
        // dueño remoto lo vuelve a disparar un instante después, al recibir
        // el SyncVar de vida en 0). Sin este guard, ServerRequestRespawn()
        // se llama dos veces por muerte: una directa en el servidor y otra
        // vía RPC desde el cliente dueño, duplicando el respawn.
        if (!IsServerInitialized) return;

        // Una baja de personaje carga la definitiva de quien la hizo, si su rol es el
        // de Daño. LastAttacker todavía está: se borra recién al revivir.
        PlayerController killer = ASC.LastAttacker != null ? ASC.LastAttacker.GetComponent<PlayerController>() : null;
        if (killer != null && killer != this) killer.NotifyKilledCharacter();
        ServerCountKda(killer);

        bool revivesNow = AbilityR is GA_ImmortalWrath && AbilityR.CanActivate();
        ServerAnnounceDeath(showDeathScreen: !revivesNow);

        if (revivesNow)
        {
            // Activación DIRECTA server-side: HandlePlayerDeath ya corre en el
            // servidor. NO usamos RequestAbility porque ese va por el [ServerRpc]
            // de dueño, que el servidor no puede invocar sobre un cliente remoto
            // → el Inmortal remoto no revivía ni respawneaba. El aimPoint no
            // importa para un auto-revive.
            if (NetASC != null) NetASC.ServerActivateAbility(EAbilityInput.Action3, transform.position);
            else                ActivateAbilityBySlot(EAbilityInput.Action3); // fallback singleplayer
            return;
        }
        ServerRequestRespawn();
    }

    // El KDA de esta muerte. Solo cuenta entre personajes (jugadores y bots) y con la partida
    // en juego: morir por un NPC suma la muerte y a nadie la baja. La asistencia es de los
    // compañeros del que hizo la baja: los que le pegaron a la víctima hace poco y los que
    // curaron al que la hizo. En un 3c3c3 el de otro equipo que también le pegaba no asiste.
    [Server]
    private void ServerCountKda(PlayerController killer)
    {
        MercenariesGameMode gm = MercenariesGameMode.Instance;
        if (gm != null && gm.State != EMatchState.Playing) return;

        _netDeaths.Value++;
        if (killer == null || killer == this) return;

        killer._netKills.Value++;

        var helpers = new HashSet<AbilitySystemComponent>();
        ASC.CollectRecentHitters(AssistWindowSeconds, helpers);
        killer.ASC.CollectRecentHealers(AssistWindowSeconds, helpers);

        foreach (AbilitySystemComponent helper in helpers)
        {
            if (ReferenceEquals(helper, killer.ASC) || ReferenceEquals(helper, ASC)) continue;
            if (helper.IsEnemyOf(killer.ASC)) continue;

            PlayerController ally = helper.GetComponent<PlayerController>();
            if (ally != null) ally._netAssists.Value++;
        }
    }

    // --- Daño y curación hechos (la tabla) ---

    private void ServerCountDamage(AbilitySystemComponent victim, float amount)
    {
        if (!CountsStats(amount) || ReferenceEquals(victim, ASC)) return;
        _damageDoneExact += amount;
        int whole = Mathf.FloorToInt(_damageDoneExact);
        if (_netDamageDone.Value != whole) _netDamageDone.Value = whole;
    }

    private void ServerCountHealingAlly(AbilitySystemComponent ally, float healed) => ServerCountHealing(healed);

    private void ServerCountHealing(float healed)
    {
        if (!CountsStats(healed)) return;
        _healingDoneExact += healed;
        int whole = Mathf.FloorToInt(_healingDoneExact);
        if (_netHealingDone.Value != whole) _netHealingDone.Value = whole;
    }

    private bool CountsStats(float amount)
    {
        if (amount <= 0f || !IsServerInitialized) return false;
        MercenariesGameMode gm = MercenariesGameMode.Instance;
        return gm == null || gm.State == EMatchState.Playing;
    }

    [Server]
    private void ServerAnnounceDeath(bool showDeathScreen)
    {
        AbilitySystemComponent killerAsc = ASC.LastAttacker;
        if (ReferenceEquals(killerAsc, ASC)) killerAsc = null;   // matarse solo no tiene autor

        string killerName = DescribeCharacter(killerAsc, out int killerNpc, out int killerClass,
                                              out int killerTeam, out int killerId);
        string victimName = DescribeCharacter(ASC, out int victimNpc, out int victimClass,
                                              out int victimTeam, out int victimId);

        ObserversKillFeed(killerName, killerNpc, killerClass, killerTeam, killerId,
                          victimName, victimNpc, victimClass, victimTeam, victimId);

        // El resumen de muerte: lo de los últimos segundos, y se borra (la próxima vida empieza limpia).
        BuildDeathRecap(out string[] recapNames, out int[] recapNpcs, out int[] recapTeams,
                        out int[] recapAbilities, out int[] recapFlags, out int[] recapAmounts);
        ASC.ClearDamageTaken();

        // Un bot no tiene pantalla; a una persona se le avisa por su conexión.
        if (!showDeathScreen || IsBot || Owner == null || !Owner.IsValid) return;

        float respawn = MercenariesGameMode.Instance != null ? MercenariesGameMode.Instance.RespawnSeconds : 3f;
        TargetShowDeath(Owner, killerName, killerNpc, killerClass, killerTeam, respawn,
                        recapNames, recapNpcs, recapTeams, recapAbilities, recapFlags, recapAmounts);
    }

    private void BuildDeathRecap(out string[] names, out int[] npcs, out int[] teams,
                                 out int[] abilities, out int[] flags, out int[] amounts)
    {
        var records = new List<AbilitySystemComponent.DamageTakenRecord>();
        ASC.CollectRecentDamage(DeathRecapSeconds, records);

        var sums = new Dictionary<(AbilitySystemComponent, GameplayAbility, bool), float>();
        foreach (AbilitySystemComponent.DamageTakenRecord r in records)
        {
            var key = (r.Attacker, r.Ability, r.OverTime);
            sums[key] = (sums.TryGetValue(key, out float s) ? s : 0f) + r.Amount;
        }
        var lines = new List<KeyValuePair<(AbilitySystemComponent, GameplayAbility, bool), float>>(sums);
        lines.Sort((a, b) => b.Value.CompareTo(a.Value));
        int count = Mathf.Min(lines.Count, Mathf.Max(0, DeathRecapLines));

        names = new string[count]; npcs = new int[count]; teams = new int[count];
        abilities = new int[count]; flags = new int[count]; amounts = new int[count];
        GameplayAbilityRegistry registry = GameplayAbilityRegistry.Instance;
        for (int i = 0; i < count; i++)
        {
            var ((attacker, ability, overTime), amount) = (lines[i].Key, lines[i].Value);
            names[i]     = DescribeCharacter(attacker, out npcs[i], out _, out teams[i], out _);
            abilities[i] = registry != null ? registry.GetIndex(ability) : -1;
            flags[i]     = (overTime ? RecapOverTime : 0) | (ReferenceEquals(attacker, ASC) ? RecapSelf : 0);
            amounts[i]   = Mathf.Max(1, Mathf.RoundToInt(amount));
        }
    }

    // Nombre, clase y equipo de quien aparece en la baja. Un jugador o bot da su nombre y
    // el índice de su clase; un NPC da qué es (npc); sin nadie, nombre vacío. objectId
    // sirve para que cada pantalla sepa si la baja la involucra (-1 si no es un personaje).
    private static string DescribeCharacter(AbilitySystemComponent asc, out int npc, out int classIndex,
                                            out int team, out int objectId)
    {
        npc        = (int)ENpcLabel.None;
        classIndex = -1;
        team       = 0;
        objectId   = -1;
        if (asc == null) return "";

        team = asc.TeamID;

        PlayerController pc = asc.GetComponent<PlayerController>();
        if (pc != null)
        {
            objectId   = pc.ObjectId;
            classIndex = pc.CurrentClassDef != null ? pc.GetClassIndex(pc.CurrentClassDef) : -1;

            NetworkAbilitySystemComponent net = asc.GetComponent<NetworkAbilitySystemComponent>();
            string name = net != null ? net.PlayerName : "";
            return string.IsNullOrEmpty(name) ? pc.gameObject.name : name;
        }

        // NPCs: no guardan su tipo, pero sus prefabs se llaman por lo que son (los más
        // específicos primero: "Net_IceMage" también contiene "Mage").
        string go = asc.gameObject.name;
        npc = (int)(go.Contains("Necromancer")    ? ENpcLabel.Necromancer
                  : go.Contains("Skeleton")       ? ENpcLabel.ShieldSkeleton
                  : go.Contains("Zombie")         ? ENpcLabel.BloatedZombie
                  : go.Contains("SilenceBanshee") ? ENpcLabel.SilenceBanshee
                  : go.Contains("DamageBanshee")  ? ENpcLabel.DamageBanshee
                  : go.Contains("Boss")           ? ENpcLabel.Boss
                  : go.Contains("IceMage")        ? ENpcLabel.IceMage
                  : go.Contains("RockMage")       ? ENpcLabel.RockMage
                  : go.Contains("Mage")           ? ENpcLabel.Mage
                  : go.Contains("Totem")          ? ENpcLabel.Totem
                                                  : ENpcLabel.Ghost);
        return "";
    }

    // Del lado de cada pantalla: el nombre a mostrar (el del jugador, o el NPC en su idioma).
    private static string ResolveCharacterName(string name, int npc)
    {
        switch ((ENpcLabel)npc)
        {
            case ENpcLabel.Boss:  return Loc.T("npc.boss");
            case ENpcLabel.Mage:  return Loc.T("npc.mage");
            case ENpcLabel.Totem: return Loc.T("npc.totem");
            case ENpcLabel.Ghost: return Loc.T("npc.ghost");
            case ENpcLabel.IceMage:        return Loc.T("npc.ice_mage");
            case ENpcLabel.RockMage:       return Loc.T("npc.rock_mage");
            case ENpcLabel.DamageBanshee:  return Loc.T("npc.damage_banshee");
            case ENpcLabel.SilenceBanshee: return Loc.T("npc.silence_banshee");
            case ENpcLabel.BloatedZombie:  return Loc.T("npc.bloated_zombie");
            case ENpcLabel.ShieldSkeleton: return Loc.T("npc.shield_skeleton");
            case ENpcLabel.Necromancer:    return Loc.T("npc.necromancer");
            default:              return name;
        }
    }

    // El nombre de la clase a partir de su índice (AllClasses es igual en todos los peers).
    private string ResolveClassName(int classIndex)
    {
        CharacterClassDefinition def = GetClassByIndex(classIndex);
        return def != null ? def.DisplayName : "";
    }

    [ObserversRpc]
    private void ObserversKillFeed(string killerName, int killerNpc, int killerClass, int killerTeam, int killerId,
                                   string victimName, int victimNpc, int victimClass, int victimTeam, int victimId)
    {
        int localId = LocalPlayer != null ? LocalPlayer.ObjectId : -2;
        bool involvesLocal = localId == killerId || localId == victimId;

        UI_KillFeed.Get()?.AddKill(ResolveCharacterName(killerName, killerNpc), ResolveClassName(killerClass), killerTeam,
                                  ResolveCharacterName(victimName, victimNpc), ResolveClassName(victimClass), victimTeam,
                                  involvesLocal);

        if (killerId >= 0 && killerId != victimId) CombatFeel.RaiseCharacterKilled(killerId);
    }

    [TargetRpc]
    private void TargetShowDeath(FishNet.Connection.NetworkConnection conn, string killerName, int killerNpc,
                                 int killerClass, int killerTeam, float respawnSeconds,
                                 string[] recapNames, int[] recapNpcs, int[] recapTeams,
                                 int[] recapAbilities, int[] recapFlags, int[] recapAmounts)
    {
        UI_ScreenFeedback screen = UI_ScreenFeedback.Get();
        if (screen == null) return;
        screen.ShowDeath(ResolveCharacterName(killerName, killerNpc), ResolveClassName(killerClass),
                         killerTeam, respawnSeconds);

        // Cada línea del resumen, ya escrita en el idioma de esta pantalla.
        var lines = new List<UI_ScreenFeedback.RecapLine>();
        GameplayAbilityRegistry registry = GameplayAbilityRegistry.Instance;
        int count = recapAmounts != null ? recapAmounts.Length : 0;
        for (int i = 0; i < count; i++)
        {
            bool self     = (recapFlags[i] & RecapSelf) != 0;
            bool overTime = (recapFlags[i] & RecapOverTime) != 0;
            GameplayAbility ability = registry != null ? registry.GetAbility(recapAbilities[i]) : null;
            string who = self ? Loc.T("death.recap.self") : ResolveCharacterName(recapNames[i], recapNpcs[i]);
            if (string.IsNullOrEmpty(who)) who = Loc.T("death.recap.environment");
            lines.Add(new UI_ScreenFeedback.RecapLine
            {
                Attacker = who,
                Team     = recapTeams[i],
                Ability  = ability != null ? ability.DisplayName : Loc.T(overTime ? "death.recap.dot" : "death.recap.hit"),
                Icon     = ability != null ? ability.AbilityIcon : null,
                Amount   = recapAmounts[i],
            });
        }
        screen.ShowRecap(lines);
    }

    // Pide el respawn al NetworkGameManager de la escena (o hace un
    // respawn simple si no hay uno). Es [Server], NO [ServerRpc]: lo llama
    // HandlePlayerDeath, que ya corre solo en el servidor. Con [ServerRpc]
    // fallaba para los jugadores NO-host: un ServerRpc requiere que lo invoque
    // el DUEÑO, y el servidor no es dueño de un cliente remoto → FishNet lo
    // bloqueaba y ese jugador nunca respawneaba (parecía "no poder morir").
    [Server]
    private void ServerRequestRespawn()
    {
        NetworkGameManager gm = FindFirstObjectByType<NetworkGameManager>();
        if (gm == null)
        {
            StartCoroutine(SimpleServerRespawn(3f));
            return;
        }

        // Los bots no tienen conexión: el respawn normal está indexado por dueño y
        // para ellos no encontraría nada — se quedaban muertos en el piso para siempre.
        if (IsBot) gm.RespawnBot(this, 3f);
        else       gm.RespawnPlayer(Owner, 3f);
    }

    // Respawn de emergencia (sin NetworkGameManager en la escena): espera,
    // reposiciona en el punto de spawn original, y revive.
    private System.Collections.IEnumerator SimpleServerRespawn(float delay)
    {
        yield return new WaitForSeconds(delay);

        // Igual que en NetworkGameManager: el transform es client-authoritative, así
        // que el teletransporte lo ejecuta el DUEÑO (si lo escribiéramos acá, en el
        // servidor, el próximo paquete del dueño lo pisaría).
        Vector3 respawnPos = new Vector3(spawnPosition.x, 3f, spawnPosition.z);
        if (NetASC != null) NetASC.ServerTeleportOwnerTo(respawnPos, transform.forward);
        else                TeleportTo(respawnPos, transform.forward); // sin red

        ASC.Revive();
    }

    // Teletransporta al personaje de vuelta a su punto de spawn (ej: al
    // caer a un DeathZone), sin pasar por el flujo de muerte/revivir.
    public void TeleportToSpawn()
    {
        characterController.enabled = false;
        if (_wallActive) StopWall();
        transform.position = new Vector3(spawnPosition.x, 3f, spawnPosition.z);
        verticalVelocity   = 0f;
        _inertiaVelocity   = Vector3.zero;
        characterController.enabled = true;
    }
}
