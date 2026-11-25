using System.Collections;
using System.Collections.Generic;
using FishNet;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// ============================================================
// UI_LobbyPanel — PEDIDOS A LA SALA
//
// Una parte de UI_LobbyPanel (partial class). La clase, sus campos del Inspector y sus
// eventos están en UI/UI_LobbyPanel.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class UI_LobbyPanel
{

    // =========================================================
    // PEDIDOS A LA SALA
    // =========================================================

    // Todo cambio (nombre, equipo, clase) se manda con el estado COMPLETO: la sala no
    // tiene mensajes parciales, así que siempre se le cuenta todo lo que tenemos.
    private void Submit(int team, int classIndex, bool spectator)
    {
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby == null || !lobby.IsLobbyReady) return;

        lobby.ServerSubmit(CurrentName(), team, classIndex, spectator);
    }

    private void JoinTeam(int team)
    {
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby == null) return;

        // El servidor revalida el cupo igual; esto solo evita el viaje de ida y vuelta
        // cuando ya se ve lleno desde acá.
        if (team > 0 && lobby.IsTeamFull(team)) return;

        int classIndex = lobby.TryGetLocalEntry(out LobbyEntry me) ? me.ClassIndex : -1;
        Submit(team, classIndex, spectator: team <= 0);
    }

    // El host mete un bot en un lugar libre. Entra ya listo y con la primera clase de la
    // lista; cambiársela es tocarle el ícono.
    private void AddBot(int team)
    {
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby == null || !lobby.IsLobbyReady) return;
        if (team > 0 && lobby.IsTeamFull(team)) return;

        lobby.ServerAddBot(team, 0);
    }

    private void RemoveBot(int botId)
    {
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby == null || !lobby.IsLobbyReady || !LobbyManager.IsBot(botId)) return;

        lobby.ServerRemoveBot(botId);
    }

    private void ShowClassPickerFor(int entryId)
    {
        _pickerTarget = LobbyManager.IsBot(entryId) ? entryId : 0;
        ShowClassPicker(true);
    }

    private void ChooseClass(int classIndex)
    {
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby == null || !lobby.IsLobbyReady) return;

        // Elegirle la clase a un bot es otro pedido: el bot no tiene nombre ni equipo que
        // reenviar, solo cambia su clase.
        if (LobbyManager.IsBot(_pickerTarget))
        {
            lobby.ServerSetBotClass(_pickerTarget, classIndex);
            ShowClassPicker(false);
            return;
        }

        if (!lobby.TryGetLocalEntry(out LobbyEntry me)) return;

        Submit(me.Team, classIndex, me.Spectator);
        ShowClassPicker(false);
    }

    // Confirmar = alternar "listo". Solo tiene sentido en un equipo y con clase elegida.
    private void ToggleReady()
    {
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby == null || !lobby.IsLobbyReady) return;
        if (!lobby.TryGetLocalEntry(out LobbyEntry me)) return;
        if (me.Spectator || me.Team <= 0 || me.ClassIndex < 0) return;

        lobby.ServerSetReady(!me.Ready);
    }

    private void RequestStart()
    {
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby != null && lobby.IsLobbyReady) lobby.ServerRequestStart();
    }

    // Salir al menú principal. Siendo host cierra la sala para todos: pregunta antes (LeaveConfirm).
    private void Leave()
    {
        if (!_leaveConfirm.Press(LobbyManager.LocalIsHost, LeaveConfirmSeconds)) { Refresh(); return; }
        UI_MainMenu.ReturnToMenu();
    }

    private void OnNameChanged(string _)
    {
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby == null || !lobby.TryGetLocalEntry(out LobbyEntry me))
        {
            // Todavía no estás en la sala: anotarte como espectador es lo primero que
            // pasa al escribir tu nombre (ver el paso 1 de la cabecera).
            Submit(0, -1, spectator: true);
            return;
        }

        Submit(me.Team, me.ClassIndex, me.Spectator);
    }
}
