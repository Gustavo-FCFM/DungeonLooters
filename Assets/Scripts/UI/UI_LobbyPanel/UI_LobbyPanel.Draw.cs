using System.Collections;
using System.Collections.Generic;
using FishNet;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// ============================================================
// UI_LobbyPanel — DIBUJO
//
// Una parte de UI_LobbyPanel (partial class). La clase, sus campos del Inspector y sus
// eventos están en UI/UI_LobbyPanel.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class UI_LobbyPanel
{

    // =========================================================
    // DIBUJO
    // =========================================================

    private void Refresh()
    {
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby == null || !_built) return;

        lobby.TryGetLocalEntry(out LobbyEntry me);
        int myId = me.ClientId;

        // --- IP ---
        if (IpText != null)
        {
            // El host ve SU IP en la red (la que les da a los demás); un cliente, a dónde se conectó.
            string ip = LobbyManager.LocalIsHost ? NetAddress.LanAddress()
                      : string.IsNullOrEmpty(NetAddress.Joined) ? "—" : NetAddress.Joined;
            IpText.text = Loc.T("lobby.host_ip", ("ip", ip));
        }

        // --- equipos ---
        for (int t = 0; t < LobbyManager.TeamCount; t++)
        {
            int team = t + 1;
            if (TeamHeaders != null && t < TeamHeaders.Length && TeamHeaders[t] != null)
                TeamHeaders[t].text = Loc.T("lobby.team", ("team", team));
            List<LobbyEntry> members = MembersOf(lobby, team);

            for (int s = 0; s < _teamSlots[t].Count; s++)
            {
                bool occupied = s < members.Count;
                DrawSlot(_teamSlots[t][s], occupied ? members[s] : default, occupied, myId, team);
            }
        }

        // --- espectadores ---
        // Una fila LIBRE de más al final: ese es el botón "Espectador", el camino de
        // vuelta para salirse de un equipo sin desconectarse. Las que sobran se apagan
        // para no dejar una escalera de botones repetidos.
        List<LobbyEntry> specs = MembersOf(lobby, 0);
        EnsureSpectatorRows(specs.Count + 1);

        for (int s = 0; s < _spectatorSlots.Count; s++)
        {
            UI_LobbySlot slot = _spectatorSlots[s];
            bool used = s <= specs.Count;
            if (slot.gameObject.activeSelf != used) slot.gameObject.SetActive(used);
            if (!used) continue;

            bool occupied = s < specs.Count;
            DrawSlot(slot, occupied ? specs[s] : default, occupied, myId, 0);
        }

        // --- botones ---
        bool inTeam   = !me.Spectator && me.Team > 0;
        bool canReady = inTeam && me.ClassIndex >= 0;
        bool allReady = lobby.AllReady;

        if (ConfirmButton != null)
        {
            ConfirmButton.interactable = canReady;
            Image bg = ConfirmButton.GetComponent<Image>();
            ButtonTint.Paint(bg, me.Ready ? ReadyColor : IdleColor);
            if (ConfirmLabel != null)
                ConfirmLabel.text = Loc.T(me.Ready ? "lobby.ready" : "lobby.confirm");
        }

        // Salir: siendo host, eso cierra la sala para todos; el botón lo dice y pregunta antes.
        if (LeaveLabel != null)
            LeaveLabel.text = Loc.T(!LobbyManager.LocalIsHost ? "lobby.leave"
                                  : _leaveConfirm.Asking ? "lobby.leave_confirm" : "lobby.leave_host");

        if (StartButton != null)
        {
            // El Start solo lo ve quien puede apretarlo.
            bool isHost = LobbyManager.LocalIsHost;
            if (StartButton.gameObject.activeSelf != isHost)
                StartButton.gameObject.SetActive(isHost);

            Image bg = StartButton.GetComponent<Image>();
            ButtonTint.Paint(bg, allReady ? ReadyColor : IdleColor);
            if (StartLabel != null) StartLabel.color = allReady ? Color.black : Color.white;
        }

        // --- linea de estado ---
        // El host puede arrancar cuando se le cante, asi que el verde del Start no
        // alcanza: hay que DECIRLE cuantos faltan. Y al que todavia no confirmo, por que
        // no puede.
        if (StatusText != null)
        {
            int players = 0, ready = 0;
            foreach (LobbyEntry e in lobby.Entries)
            {
                if (e.Spectator || e.Team <= 0) continue;
                players++;
                if (e.Ready) ready++;
            }

            string text;
            Color   color;

            if (players == 0)
            {
                text  = Loc.T("lobby.status.nobody");
                color = PendingColor;
            }
            else if (ready >= players)
            {
                text  = LobbyManager.LocalIsHost
                      ? Loc.T("lobby.status.all_ready_host", ("players", players))
                      : Loc.T("lobby.status.all_ready_wait", ("players", players));
                color = ReadyColor;
            }
            else
            {
                int missing = players - ready;
                text  = missing == 1 ? Loc.T("lobby.status.one_missing", ("players", players))
                                     : Loc.T("lobby.status.missing", ("missing", missing), ("players", players));
                color = PendingColor;
            }

            // Lo que te toca hacer A VOS gana sobre el recuento: es la unica linea de
            // texto del panel, y sirve mas como instruccion que como marcador.
            if (inTeam && !canReady)
            {
                // Con control no se hace clic: se aprieta A (o X en PlayStation) sobre tu lugar.
                text  = MenuInput.UsingPad
                      ? Loc.T("lobby.status.choose_class_pad", ("button", InputGlyphs.ShowKeyboardHints ? "A" : InputGlyphs.Confirm))
                      : Loc.T("lobby.status.choose_class");
                color = PendingColor;
            }
            else if (me.Spectator || me.Team <= 0)
            {
                text += "   " + Loc.T("lobby.status.spectating");
            }

            StatusText.text  = text;
            StatusText.color = color;
        }

        FollowStep();
    }

    // Pinta un lugar. Ocupado: ícono de clase + nombre. Libre: "Unirte" en un equipo,
    // "Espectador" en la franja de espectadores (que SÍ es un destino: es como te salís
    // de un equipo sin desconectarte).
    private void DrawSlot(UI_LobbySlot slot, LobbyEntry entry, bool occupied, int myId, int team)
    {
        bool isMine  = occupied && entry.ClientId == myId;
        bool isHost  = LobbyManager.LocalIsHost;
        bool isBot   = occupied && LobbyManager.IsBot(entry.ClientId);
        bool inTeam  = team > 0;

        slot.EntryId = occupied ? entry.ClientId : 0;

        // El recuadro solo se pinta cuando hay alguien: en un lugar libre, el fondo ya lo
        // pone el botón de Unirte, y dos recuadros encimados se ven sucios.
        if (slot.Background != null)
            slot.Background.color = !occupied ? Color.clear
                                  : isMine    ? OwnRowColor
                                              : RowColor;

        slot.Icon.gameObject.SetActive(occupied);
        slot.Label.gameObject.SetActive(occupied);
        slot.JoinButton.gameObject.SetActive(!occupied);

        // El "+" solo en un lugar LIBRE de equipo, y solo para el host. El "x" solo sobre
        // un bot, también solo para el host: a un jugador no se lo echa desde acá.
        slot.AddBotButton.gameObject.SetActive(!occupied && inTeam && isHost);
        slot.RemoveButton.gameObject.SetActive(isBot && isHost);

        if (occupied)
        {
            CharacterClassDefinition cls = ClassAt(entry.ClassIndex);

            // El ícono SIEMPRE se dibuja, tenga clase o no. Sin clase queda un recuadro
            // vacío pero visible: es el botón que hay que tocar para elegirla, y si se
            // apagaba no había nada que clickear — no se podía elegir clase, y sin clase
            // el Confirmar quedaba muerto.
            slot.Icon.enabled = true;
            slot.Icon.sprite  = cls != null ? cls.ClassIcon : null;
            ButtonTint.Paint(slot.Icon, cls != null && cls.ClassIcon != null ? Color.white : SlotColor);

            // Tu ícono lo tocás vos; el de un bot, el host. Nadie toca el de otro jugador.
            slot.IconButton.interactable = inTeam && (isMine || (isBot && isHost));

            bool needsClass = entry.ClassIndex < 0 && inTeam;
            slot.Label.text = (isMine || (isBot && isHost)) && needsClass
                            ? Loc.T("lobby.choose_class", ("name", entry.PlayerName))
                            : entry.PlayerName;

            // Los bots van en otro color aunque estén listos: en una captura conviene ver
            // de un vistazo quién es persona y quién no.
            slot.Label.color = isBot   ? BotColor
                             : entry.Ready ? ReadyColor
                                           : PendingColor;
            return;
        }

        slot.JoinButton.interactable = true;

        if (slot.JoinLabel != null) slot.JoinLabel.text = Loc.T(inTeam ? "lobby.join" : "lobby.spectate");
    }

    private List<LobbyEntry> MembersOf(LobbyManager lobby, int team)
    {
        var list = new List<LobbyEntry>();
        foreach (LobbyEntry e in lobby.Entries)
        {
            bool isSpectator = e.Spectator || e.Team <= 0;
            if (team == 0 ? isSpectator : (!isSpectator && e.Team == team)) list.Add(e);
        }
        return list;
    }

    // Pública: la sala la consulta al arrancar para saber con qué clase spawnear cada bot.
    public CharacterClassDefinition ClassAt(int index)
        => SelectableClasses != null && index >= 0 && index < SelectableClasses.Length
           ? SelectableClasses[index] : null;

    private string CurrentName()
    {
        string n = NameInput != null ? NameInput.text : null;
        return string.IsNullOrWhiteSpace(n) ? Loc.T("lobby.default_name") : n.Trim();
    }
}
