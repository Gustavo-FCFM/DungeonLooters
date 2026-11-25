using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Connection;

// ============================================================
// NetworkAbilitySystemComponent — MOVER DESDE EL SERVIDOR (teletransporte, empuje, vuelo, embestida, dash, salto)
//
// Una parte de NetworkAbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en Network/NetworkAbilitySystemComponent.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class NetworkAbilitySystemComponent
{

    // =========================================================
    // SALTO (GA_LeapAttack) — TODA la orquestación de esta habilidad vive
    // acá, no en PlayerController. Ese script solo expone primitivas
    // genéricas de movimiento (ApplyAbilityVelocity/ClearAbilityVelocity/
    // IsGrounded) — no sabe qué es "GA_LeapAttack" ni cuándo termina.
    //
    // PlayerController tampoco puede saberlo: su Update() está bloqueado
    // con IsOwner, así que la física del salto (y por lo tanto detectar
    // cuándo aterriza) solo puede correr en el proceso DUEÑO. Y
    // GA_LeapAttack.Activate() tampoco puede correr ahí — como toda
    // GameplayAbility en este proyecto, Activate() es server-only. Este
    // componente es el único lugar que existe en ambos lados (llega el
    // TargetRpc del servidor Y corre en el proceso dueño), así que acá es
    // donde tiene que vivir el puente entre las dos partes.
    //
    // Flujo: GA_LeapAttack.Activate() (servidor) -> ServerStartLeap() ->
    // TargetExecuteLeap() (solo a la conexión dueña, calcula la dirección
    // con SU cámara) -> WaitForLandingThenResolve() (corutina local del
    // dueño, espera a que IsGrounded vuelva a true) -> ServerResolveLeapImpact()
    // -> el servidor resuelve el daño con la MISMA instancia de
    // GA_LeapAttack que se guardó en ServerStartLeap().
    // =========================================================

    // Guarda la habilidad pendiente y manda el TargetRpc a la conexión dueña.
    // ¿Hay una conexión dueña a la que mandarle un TargetRpc?
    //
    // Los BOTS son objetos del servidor: no tienen dueño. Mandarles un TargetRpc hace que
    // FishNet avise "Target is not an observer for object Player(Clone)" en cada intento
    // —y no mueve a nadie—. Las habilidades que dependen de eso ya se filtran antes (ver
    // GameplayAbility.MovesThroughOwner); esto es la red de seguridad para las que solo
    // lo usan de adorno, como el retroceso del disparo.
    private bool HasOwnerConnection() => Owner != null && Owner.IsValid;

    // =========================================================
    // TELETRANSPORTE — mover al jugador desde el servidor.
    //
    // IMPORTANTE: el transform del jugador es CLIENT-AUTHORITATIVE (su
    // NetworkTransform lo manda el dueño). Si el servidor le escribe la posición
    // directo, el próximo paquete del dueño —que sigue donde estaba— la PISA: se
    // ve como que el personaje aparece en el destino y "vuelve" al instante.
    // Por eso cualquier reposicionamiento tiene que ejecutarlo el proceso DUEÑO,
    // y el servidor solo le manda a dónde ir por TargetRpc.
    //
    // Lo usan el blink del Pícaro (GA_Blink) y el respawn (NetworkGameManager /
    // PlayerController). DeathZone hace lo mismo por su cuenta, filtrando por
    // IsOwner.
    // =========================================================

    // El servidor le pide al dueño que se teletransporte a 'position' mirando
    // hacia 'faceDir'. turnCamera: girar también la cámara del dueño (ver
    // PlayerController.TeleportTo) — el Blink lo usa para quedar de cara al enemigo.
    [Server]
    public void ServerTeleportOwnerTo(Vector3 position, Vector3 faceDir, bool turnCamera = false)
    {
        // Un BOT no tiene dueño a quien mandarle el TargetRpc, pero el servidor SÍ es su
        // autoridad sobre el transform: lo mueve directo. Sin esto el Parpadeo del pícaro
        // se activaba, pagaba el cooldown y no lo movía un centímetro.
        if (!HasOwnerConnection())
        {
            PlayerController bot = GetComponent<PlayerController>();
            if (bot != null && bot.IsBot) bot.ServerTeleportBot(position, faceDir);
            else if (bot == null) ServerTeleportNpc(position, faceDir);
            return;
        }

        TargetExecuteTeleport(Owner, position, faceDir, turnCamera);
    }

    // Un NPC (sin PlayerController): el servidor es su autoridad y su NetworkTransform lo lleva a todos. Con
    // NavMeshAgent, por Warp al punto del NavMesh más cercano (si no, el agente lo devuelve caminando). Desde el 19 de
    // noviembre de 2026 (intercambiar posición, GA_SwapPosition); antes un NPC no se teletransportaba.
    [Server]
    private void ServerTeleportNpc(Vector3 position, Vector3 faceDir)
    {
        UnityEngine.AI.NavMeshAgent agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (agent != null && agent.enabled &&
            UnityEngine.AI.NavMesh.SamplePosition(position, out UnityEngine.AI.NavMeshHit hit, 2f, UnityEngine.AI.NavMesh.AllAreas))
            agent.Warp(hit.position);
        else
            transform.position = position;

        faceDir.y = 0f;
        if (faceDir.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(faceDir.normalized);
    }

    [TargetRpc]
    private void TargetExecuteTeleport(NetworkConnection conn, Vector3 position, Vector3 faceDir, bool turnCamera)
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.TeleportTo(position, faceDir, turnCamera);
    }

    // =========================================================
    // REPELER / ATRAER — lo pide AbilitySystemComponent.TryApplyKnockback al recibir un
    // efecto con KnockbackDistance. displacement es el recorrido horizontal entero.
    //
    // Mismo reparto que el dash: a un jugador lo mueve SU cliente (TargetRpc, el transform
    // es suyo); a un bot, su cerebro sobre el NavMesh; a un NPC, el servidor acá mismo.
    // =========================================================

    [Server]
    public void ServerApplyKnockback(Vector3 displacement, float duration, float upVelocity)
    {
        if (HasOwnerConnection())
        {
            TargetExecuteKnockback(Owner, displacement, duration, upVelocity);
            return;
        }

        BotController brain = GetComponent<BotController>();
        if (brain != null) brain.ServerDash(displacement / duration, duration, false);
        else               StartCoroutine(ServerPushRoutine(displacement, duration));

        // El impulso hacia arriba (el Levantamiento del Mago): hasta el 21 de octubre solo lo
        // sentían los jugadores; a un bot o a un NPC se lo empujaba de lado y nada más.
        if (upVelocity > 0f) ServerLaunch(upVelocity);
    }

    [Server]
    private void ServerLaunch(float upVelocity)
    {
        UnityEngine.AI.NavMeshAgent agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (_launch != null) StopCoroutine(_launch);
        else if (agent != null) _launchRestOffset = agent.baseOffset;
        _launch = StartCoroutine(ServerLaunchRoutine(agent, upVelocity));
    }

    private IEnumerator ServerLaunchRoutine(UnityEngine.AI.NavMeshAgent agent, float upVelocity)
    {
        float start = _launchHeight;
        float t     = 0f;

        while (true)
        {
            t += Time.deltaTime;
            float y = start + upVelocity * t - 0.5f * LaunchGravity * t * t;
            if (y <= 0f) break;
            SetLaunchHeight(agent, y);
            yield return null;
        }

        SetLaunchHeight(agent, 0f);
        _launch = null;
    }

    // Con agente, siempre por el baseOffset (aunque lo apaguen en el aire: al terminar vuelve al
    // del piso y no queda flotando al reaparecer).
    private void SetLaunchHeight(UnityEngine.AI.NavMeshAgent agent, float y)
    {
        if (agent != null) agent.baseOffset = _launchRestOffset + y;
        else               transform.position += Vector3.up * (y - _launchHeight);
        _launchHeight = y;
    }

    [TargetRpc]
    private void TargetExecuteKnockback(NetworkConnection conn, Vector3 displacement, float duration, float upVelocity)
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.ApplyKnockback(displacement, duration, upVelocity);
    }

    // =========================================================
    // VUELO LIBRE (GA_Flight) — el vuelo dura lo que Status_Flying; esto solo le manda al
    // DUEÑO los números del vuelo y el impulso de despegue (el transform es suyo). Un bot
    // vuela con su cerebro (BotController.ServerStartFlight, desde el 29 de octubre de 2026).
    // =========================================================

    // ATERRIZAR (29 de octubre de 2026, pedido de Gustavo): el dueño corta su vuelo y cae lento.
    // Shift volando corta SOLO el vuelo del Shift (lo que su GA_Flight le dio; el cooldown sigue).
    // Pedir cambiar de clase o subclase en el aire corta TODO vuelo, también la levitación de la
    // Sobrecarga: el menú pide estar en el piso (UI_ClassMenu lo abre al aterrizar).
    [ServerRpc]
    public void ServerRequestLand(bool anyFlight)
    {
        if (_asc == null || !_asc.HasTag(EGameplayTag.Status_Flying)) return;

        if (anyFlight)
        {
            _asc.RemoveEffectsWithTag(EGameplayTag.Status_Flying);
            return;
        }

        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null && pc.MovementAbility is GA_Flight flight) flight.EndFlight();
    }

    // PICADA (el Terremoto del Mago lanzado volando): el servidor ya cortó el vuelo; el DUEÑO cae
    // rápido hasta el piso (el transform es suyo). El servidor espera a verlo en el piso. Un bot
    // la cae con su cerebro, como el dash.
    [Server]
    public void ServerStartDive(float speed)
    {
        if (!HasOwnerConnection())
        {
            BotController brain = GetComponent<BotController>();
            if (brain != null) brain.ServerDive(speed);
            return;
        }
        TargetStartDive(Owner, speed);
    }

    [TargetRpc]
    private void TargetStartDive(NetworkConnection conn, float speed)
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.StartDive(speed);
    }

    // towardAim: el despegue sale hacia la mira del dueño (la resuelve él, que tiene la cámara).
    [Server]
    public void ServerStartFlight(float launchSpeed, float speedMultiplier, float forwardBoost, float verticalSpeed,
                                  float idleSink, bool towardAim = false, float minUpAngle = 0f)
    {
        if (!HasOwnerConnection()) return;
        TargetStartFlight(Owner, launchSpeed, speedMultiplier, forwardBoost, verticalSpeed, idleSink, towardAim, minUpAngle);
    }

    [TargetRpc]
    private void TargetStartFlight(NetworkConnection conn, float launchSpeed, float speedMultiplier,
                                   float forwardBoost, float verticalSpeed, float idleSink,
                                   bool towardAim, float minUpAngle)
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.StartFlight(launchSpeed, speedMultiplier, forwardBoost, verticalSpeed, idleSink,
                                       towardAim, minUpAngle);
    }

    [Server]
    public void ServerStartRush(Vector3 velocity, float duration, float strafeSpeed, int excludeMask, bool cancelable)
    {
        _rushStopRequested = false;

        // Sin dueño (un bot): la corre su cerebro como un dash, derecho (sin corrimiento de costado ni
        // corte por botón: no tiene). Así los bots usan la Patada voladora y el Corte final (23 de
        // octubre de 2026).
        if (!HasOwnerConnection())
        {
            BotController brain = GetComponent<BotController>();
            if (brain != null) brain.ServerDash(velocity, duration, true);
            return;
        }

        TargetStartRush(Owner, velocity, duration, strafeSpeed, excludeMask, cancelable);
    }

    [TargetRpc]
    private void TargetStartRush(NetworkConnection conn, Vector3 velocity, float duration, float strafeSpeed,
                                 int excludeMask, bool cancelable)
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.StartRush(velocity, duration, strafeSpeed, excludeMask, cancelable);
    }

    [Server]
    public void ServerStopRush()
    {
        if (!HasOwnerConnection())
        {
            BotController brain = GetComponent<BotController>();
            if (brain != null) brain.ServerStopDash();
            return;
        }
        TargetStopRush(Owner);
    }

    [TargetRpc]
    private void TargetStopRush(NetworkConnection conn)
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.StopRush(false);
    }

    // El dueño la cortó (volvió a apretar) o chocó con una pared.
    [ServerRpc]
    public void ServerRequestStopRush()
    {
        _rushStopRequested = true;
    }

    // Lo pregunta la embestida cada frame en el servidor.
    public bool ConsumeRushStopRequest()
    {
        bool requested = _rushStopRequested;
        _rushStopRequested = false;
        return requested;
    }

    // =========================================================
    // DASH (GA_Dash / Dash siniestro) — impulso rápido con inercia.
    //
    // El daño a lo largo del trayecto lo resuelve el servidor al activar (ver
    // GA_Dash, que hace un barrido geométrico del recorrido). Acá solo movemos
    // al dueño: le aplicamos el impulso (que se atenúa solo en PlayerController,
    // dando el "frenado con inercia") y excluimos la capa de jugadores de su
    // colisión durante el dash para atravesarlos, restaurándola al terminar.
    // La colisión con paredes se mantiene (no se excluye esa capa).
    //
    // El LayerMask viaja como int: FishNet serializa int de forma nativa, no así
    // el struct LayerMask.
    // =========================================================

    [Server]
    public void ServerStartDash(Vector3 velocity, float duration, int excludeMask, bool faceVelocity = true,
                               float exitSpeedPercent = 0f, float exitDamping = 3f)
    {
        // Mismo caso que el teletransporte: sin dueño, el dash lo corre el cerebro del bot
        // sobre su NavMeshAgent (ver BotController.ServerDash).
        if (!HasOwnerConnection())
        {
            BotController brain = GetComponent<BotController>();
            if (brain != null) brain.ServerDash(velocity, duration, faceVelocity);
            return;
        }

        TargetExecuteDash(Owner, velocity, duration, excludeMask, faceVelocity, exitSpeedPercent, exitDamping);
    }

    [TargetRpc]
    private void TargetExecuteDash(NetworkConnection conn, Vector3 velocity, float duration, int excludeMask,
                                  bool faceVelocity, float exitSpeedPercent, float exitDamping)
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc == null) return;

        // Dash 3D: se puede en el aire y en cualquier dirección (incluye vertical).
        // faceVelocity=false en la esquiva direccional del pícaro: el cuerpo NO gira
        // hacia el dash, mantiene la orientación a la mira.
        pc.ApplyDashVelocity(velocity, faceVelocity);
        StartCoroutine(DashRoutine(pc, duration, excludeMask, exitSpeedPercent, exitDamping));
    }

    // Corre en el proceso dueño: mantiene la exclusión de colisión durante el
    // dash y, al terminar, la restaura, devuelve el control del movimiento y
    // libera el estado "atacando".
    private IEnumerator DashRoutine(PlayerController pc, float duration, int excludeMask,
                                   float exitSpeedPercent, float exitDamping)
    {
        pc.SetCollisionExclusion(excludeMask, true);
        yield return new WaitForSeconds(duration);
        pc.SetCollisionExclusion(excludeMask, false);
        pc.ClearDashVelocity(exitSpeedPercent, exitDamping);

        // Con el escudo arriba (la Carga defensiva del Guerrero, UsableWhileHolding) el
        // dash termina pero el escudo NO: un FinishAttack() "de todo" acá le borraba el
        // mantenido al dueño mientras el servidor lo seguía teniendo, y al soltar el botón
        // nadie avisaba — el escudo eterno. Se baja soltando el botón, como siempre.
        if (!pc.IsHoldingAbility) pc.FinishAttack();
    }
}
