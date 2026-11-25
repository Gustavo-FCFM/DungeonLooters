using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Connection;

// ============================================================
// NetworkAbilitySystemComponent — MUERTE, REVIVIR Y EXPERIENCIA
//
// Una parte de NetworkAbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en Network/NetworkAbilitySystemComponent.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class NetworkAbilitySystemComponent
{

    // =========================================================
    // MUERTE Y REVIVIR — Notificar a todos los clientes
    // =========================================================

    private void HandleDeath()
    {
        if (!IsServerInitialized) return;
        AwardKillExperience();

        // De dónde vino el golpe que mató: del atacante hacia acá. Es lo que el ragdoll
        // usa para salir despedido. Sin atacante conocido, cae hacia atrás.
        Vector3 hitDir = _asc.LastAttacker != null
            ? (transform.position - _asc.LastAttacker.transform.position)
            : -transform.forward;
        hitDir.y = 0f;
        hitDir = hitDir.sqrMagnitude > 0.001f ? hitDir.normalized : -transform.forward;

        ObserversHandleDeath(hitDir);
    }

    // Le da EXP al jugador que consiguió esta baja. El ASC dejó anotado quién pegó
    // el daño en LastAttacker (poblado solo en la copia del servidor, que es donde
    // corre HandleDeath). El _netExp del matador sincroniza el cambio a su cliente,
    // igual que hace el cheat de nivel — no hace falta nada extra acá.
    [Server]
    private void AwardKillExperience()
    {
        if (_asc == null) return;

        AbilitySystemComponent killer = _asc.LastAttacker;
        if (killer == null || ReferenceEquals(killer, _asc)) return;

        // En el modo Mercenarios la experiencia NO es individual: va a la bolsa
        // compartida del equipo del que remató, y de ahí sale el nivel de sus tres
        // jugadores. Este es el único punto del core que sabe quién dio el golpe final,
        // así que la baja se rutea acá y el modo decide cuánto vale (NPC o jugador).
        if (MercenariesGameMode.Instance != null)
        {
            MercenariesGameMode.Instance.ServerAwardKill(killer, _asc);
            return;
        }

        // CAMINO VIEJO (sin modo de juego, ej. la escena de pruebas): experiencia
        // INDIVIDUAL para el que remató, con el valor de este componente.
        //
        // El chequeo de ExperiencePerKill va acá abajo y no arriba de todo a propósito:
        // arriba cortaba el método entero, así que poner este campo en 0 —cosa razonable
        // cuando uno cree que no se usa— dejaba también al modo Mercenarios sin repartir
        // NADA de experiencia por bajas, sin ningún aviso.
        if (ExperiencePerKill <= 0f) return;

        NetworkAbilitySystemComponent killerNet = killer.GetComponent<NetworkAbilitySystemComponent>();
        if (killerNet != null) killerNet.ServerGainExperience(ExperiencePerKill);
    }

    // El ASC llegó a nivel máximo (por experiencia de bajas). OnMaxLevelReached se
    // disparó en la copia del SERVIDOR, pero la UI de selección de subclase escucha
    // ese evento en el ASC LOCAL del dueño — así que a un dueño remoto hay que
    // avisarle por su conexión. En el host el dueño ES este proceso y su UI ya
    // escuchó el evento local, por eso solo reenviamos cuando !IsOwner.
    private void HandleMaxLevelReached()
    {
        if (!IsServerInitialized) return;

        // El menú de subclases es para una PERSONA. Un bot elige sola (ver
        // BotController.TryChooseSubclass), y mandarle el aviso a una conexión que no
        // existe era el grupo de avisos que salía justo cuando los equipos llegaban a
        // nivel 3.
        if (!IsOwner && HasOwnerConnection()) TargetShowSubclassSelection(Owner);
    }

    [TargetRpc]
    private void TargetShowSubclassSelection(NetworkConnection conn)
    {
        // En el host el evento ya se disparó localmente; evitamos re-disparar.
        if (IsServerInitialized) return;
        if (_asc != null) _asc.TriggerMaxLevelReached();
    }

    private void HandleRevive()
    {
        if (!IsServerInitialized) return;
        ObserversHandleRevive();
    }

    [ObserversRpc]
    private void ObserversHandleDeath(Vector3 hitDirection)
    {
        // Jugadores y bots: ragdoll o animación de muerte (ver PlayerController.PlayDeath).
        // El resto (NPCs) sigue con el trigger opcional de siempre.
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.PlayDeath(hitDirection);
        else            SafeSetTrigger(GetComponentInChildren<Animator>(), "Death");

        UI_CombatFlash.Get()?.ShowKill(this);
    }

    [ObserversRpc]
    private void ObserversHandleRevive()
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.PlayRevive();
        else            SafeSetTrigger(GetComponentInChildren<Animator>(), "Revive");
    }

    // Dispara un trigger del Animator solo si ese parámetro existe. Muerte y
    // revivir son animaciones OPCIONALES: si el AnimatorOverrideController de una
    // clase no define el parámetro, Unity llenaba la consola con "Parameter
    // 'Revive' does not exist". Si querés la animación, agregá el trigger al
    // Animator Controller; si no, esto lo ignora en silencio.
    private static void SafeSetTrigger(Animator anim, string trigger)
    {
        if (anim == null || string.IsNullOrEmpty(trigger)) return;

        foreach (var p in anim.parameters)
        {
            if (p.name == trigger)
            {
                anim.SetTrigger(trigger);
                return;
            }
        }
    }

    // Revive al personaje (lo llama NetworkGameManager al respawnearlo).
    [Server]
    public void Revive()
    {
        if (_asc != null) _asc.Revive();
    }

    // Revivir DONDE ESTÁ, traído por otro (la Resurrección del Clérigo). A diferencia
    // del respawn: no se mueve, vuelve con parte de la vida y conserva sus cooldowns
    // (lo traen de vuelta en medio de la pelea, no reaparece descansado). Cancela la
    // reaparición en la base que ya estaba programada: si no, a los segundos lo
    // mandaría a la base igual.
    [Server]
    public void ServerResurrect(float healthFraction)
    {
        if (_asc == null || !_asc.HasTag(EGameplayTag.State_Dead)) return;

        NetworkGameManager gm = FindFirstObjectByType<NetworkGameManager>();
        if (gm != null) gm.CancelRespawn(this);

        _asc.Revive(resetAbilities: false, healthFraction: healthFraction);
    }

    // =========================================================
    // EXPERIENCIA (el servidor la otorga, ej. al matar enemigo)
    // =========================================================

    [Server]
    public void ServerGainExperience(float amount)
    {
        if (_asc != null) _asc.GainExperience(amount);
    }
}
