using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — CLASE Y EQUIPAMIENTO
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    // Equipa una clase en un bot, DESDE EL SERVIDOR.
    //
    // EquipCharacterClass sincroniza el índice de clase dentro de un `if (IsOwner)`,
    // porque en un jugador normal es el dueño quien elige. Un bot no tiene dueño, así
    // que ese bloque nunca corre y los demás peers no verían ni su arma ni su animator.
    // Acá se hace las dos cosas: equipar server-side y escribir el SyncVar.
    [Server]
    public void ServerEquipClassAsBot(CharacterClassDefinition newClass)
    {
        if (newClass == null) return;

        EquipCharacterClass(newClass);

        int idx = GetClassIndex(newClass);
        if (idx >= 0) _netClassIndex.Value = idx;
        else Debug.LogWarning($"[Bot] '{newClass.ClassName}' no está en MainBaseClasses ni en " +
                              "sus subclases: los demás no van a ver su arma ni su animación.");
    }

    // Teletransporta un bot, DESDE EL SERVIDOR.
    //
    // El respawn normal va por ServerTeleportOwnerTo (un TargetRpc al dueño, porque el
    // transform es client-authoritative). Sin dueño ese RPC no llega a ningún lado, así
    // que el bot se quedaba muerto en el piso. Acá el servidor ES la autoridad del
    // objeto, así que puede escribir el transform directo.
    [Server]
    public void ServerTeleportBot(Vector3 position, Vector3 faceDir)
    {
        TeleportTo(position, faceDir);

        // El agente de navegación tiene su propia idea de dónde está: sin avisarle,
        // vuelve caminando desde el punto de muerte.
        BotController bot = GetComponent<BotController>();
        if (bot != null) bot.OnServerTeleported();
    }
}
