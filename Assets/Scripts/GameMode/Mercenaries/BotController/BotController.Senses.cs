using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// BotController — PERCEPCIÓN, MOVIMIENTO Y MIRADA
//
// Una parte de BotController (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/BotController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class BotController
{

    // =========================================================
    // PERCEPCIÓN
    // =========================================================

    // El enemigo más conveniente a la vista. "Conveniente" es el más cercano, salvo que
    // haya un jugador: un jugador vale más que un NPC porque da más experiencia y porque
    // es contra quien hay que pelear.
    private AbilitySystemComponent FindBestTarget()
    {
        // PROVOCADO (Status_Taunted): va por el que lo provocó mientras le dure, si es enemigo y está a la vista.
        AbilitySystemComponent forced = _asc.GetTaunter();
        if (forced != null && !_asc.IsAllyOf(forced, includeSelf: false) &&
            Vector3.Distance(transform.position, forced.transform.position) <= VisionRadius * 1.5f)
            return forced;

        int count = Physics.OverlapSphereNonAlloc(
            transform.position, VisionRadius, _hits, CharacterLayer, QueryTriggerInteraction.Ignore);

        AbilitySystemComponent best = null;
        float bestScore = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i].GetComponentInParent<AbilitySystemComponent>();
            if (other == null || other == _asc) continue;
            if (other.HasTag(EGameplayTag.State_Dead)) continue;
            if (_asc.IsAllyOf(other, includeSelf: false)) continue;
            if (other.Strikeable != null) continue;   // algo del mapa (la campana): no es a quién pelear
            if (!CanPerceive(other)) continue;   // invisible y sin revelar: no lo veo

            float dist = Vector3.Distance(transform.position, other.transform.position);

            // Un jugador cuenta como si estuviera 8 m más cerca de lo que está. No es
            // exacto a propósito: si el NPC te está pegando en la cara, seguís con el NPC.
            bool isPlayer = other.GetComponent<PlayerController>() != null;
            float score = isPlayer ? dist - 8f : dist;

            // Y SOBRE EL QUE APOSTÉ va primero: el Pirata le pega más fuerte (ver
            // GamblePassive), así que ignorarlo es tirar su pasiva a la basura. El
            // descuento es grande para que gane a cualquier otro a la vista.
            if (other.HasTag(EGameplayTag.Status_Gambled)) score -= 40f;

            // Un INMUNE (la Tumba helada, la Protección divina, el que está en su sala) no recibe nada:
            // cualquier otro a la vista va antes (23 de octubre de 2026).
            if (IsImmune(other)) score += ImmuneTargetPenalty;

            // El instinto: el que venía peleando, el que le pega, el que está por caer, el de los compañeros, el que
            // tiene un escudo que lo cubre todo (ver InstinctScore).
            score += InstinctScore(other);

            if (score >= bestScore) continue;
            bestScore = score;
            best      = other;
        }

        return best;
    }

    // El enemigo más cercano a UN PUNTO que no es el mío. Lo usa la escolta: el que hay
    // que sacar de encima es el que está cerca del portador, no el que me queda cómodo.
    private AbilitySystemComponent FindEnemyNear(Vector3 center, float radius)
    {
        int count = Physics.OverlapSphereNonAlloc(
            center, radius, _hits, CharacterLayer, QueryTriggerInteraction.Ignore);

        AbilitySystemComponent best = null;
        float bestDist = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i].GetComponentInParent<AbilitySystemComponent>();
            if (other == null || other == _asc) continue;
            if (other.HasTag(EGameplayTag.State_Dead)) continue;
            if (_asc.IsAllyOf(other, includeSelf: false)) continue;
            if (other.Strikeable != null) continue;   // algo del mapa (la campana): no es a quién pelear
            if (!CanPerceive(other)) continue;   // invisible y sin revelar: no lo veo

            float dist = Vector3.Distance(center, other.transform.position);
            if (IsImmune(other)) dist += ImmuneTargetPenalty;   // un inmune, solo si no hay otro
            if (dist >= bestDist) continue;
            bestDist = dist;
            best     = other;
        }

        return best;
    }

    private AbilitySystemComponent FindNearestAlly()
    {
        int count = Physics.OverlapSphereNonAlloc(
            transform.position, VisionRadius, _hits, CharacterLayer, QueryTriggerInteraction.Ignore);

        AbilitySystemComponent best = null;
        float bestDist = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i].GetComponentInParent<AbilitySystemComponent>();
            if (other == null || other == _asc) continue;
            if (other.HasTag(EGameplayTag.State_Dead)) continue;
            if (!_asc.IsAllyOf(other, includeSelf: false)) continue;

            // Solo compañeros JUGADORES: seguir a un NPC aliado no tiene sentido.
            if (other.GetComponent<PlayerController>() == null) continue;

            float dist = Vector3.Distance(transform.position, other.transform.position);
            if (dist >= bestDist) continue;
            bestDist = dist;
            best     = other;
        }

        return best;
    }

    private float HealthFraction(AbilitySystemComponent asc)
    {
        if (asc == null) return 1f;

        float max = asc.GetAttributeValue(EAttributeType.MaxHealth);
        if (max <= 0f) return 1f;
        return Mathf.Clamp01(asc.GetAttributeValue(EAttributeType.Health) / max);
    }

    // =========================================================
    // MOVIMIENTO Y MIRADA
    // =========================================================

    private void Steer()
    {
        if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh) return;

        // Adónde AHORA: el paso al costado de una esquiva, la salida de una zona, o lo que decidió (ver CurrentGoal).
        Vector3 goal = CurrentGoal();
        _agent.SetDestination(AvoidEnemySafeRooms(goal));

        // El NavMesh de la arena tiene cortes: escalones más altos de lo que el agente
        // puede subir, y bordes donde el horneado no llegó. Cuando el destino queda del
        // otro lado, el agente calcula un camino PARCIAL, llega hasta el borde y se
        // planta ahí para siempre — que es exactamente lo que se veía.
        //
        // Cuando eso pasa se apunta al punto ALCANZABLE más cercano al destino. No lo
        // resuelve del todo (si de verdad no hay paso, no hay paso), pero el bot se sigue
        // moviendo y rodea en vez de quedarse clavado contra un escalón.
        if (!_agent.pathPending && _agent.pathStatus != NavMeshPathStatus.PathComplete)
        {
            if (NavMesh.SamplePosition(goal, out NavMeshHit hit, 12f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }

        // Mirar al objetivo si lo hay; si no, hacia donde voy. Un bot que camina de
        // espaldas a su enemigo se ve roto, y encima las habilidades salen para atrás:
        // casi todas usan la rotación del cuerpo como dirección.
        Vector3 look = _target != null
            ? _target.transform.position - transform.position
            : _agent.velocity;

        look.y = 0f;
        if (look.sqrMagnitude < 0.01f) return;

        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, Quaternion.LookRotation(look.normalized), 720f * Time.deltaTime);
    }

    // El Animator del jugador lo maneja su dueño (PlayerController.UpdateAnimations, que
    // arranca con IsOwner). Un bot no tiene dueño, así que sus parámetros los escribe
    // acá el servidor — y de acá viajan a los clientes por BroadcastLocomotion, igual
    // que los de un jugador (ver "LOCOMOCIÓN EN RED" en PlayerController).
    private void DriveAnimator(Vector3 velocity, bool airborne = false)
    {
        if (_anim == null) return;

        Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
        float speed = flat.magnitude;
        _anim.SetFloat("Speed", speed, 0.1f, Time.deltaTime);

        float max = Mathf.Max(0.1f, ResolveMoveSpeed());
        Vector3 local = transform.InverseTransformDirection(flat) / max;
        _anim.SetFloat("MoveX", local.x, 0.1f, Time.deltaTime);
        _anim.SetFloat("MoveY", local.z, 0.1f, Time.deltaTime);

        // Caminando por NavMesh nunca salta ni cae. Volando (el Mago) va en la pose del aire, con
        // su velocidad vertical, igual que un jugador que vuela.
        float vertical = airborne ? velocity.y : 0f;
        _anim.SetBool("IsJumping", airborne);
        if (_pc != null && !string.IsNullOrEmpty(_pc.VerticalSpeedParam)) _anim.SetFloat(_pc.VerticalSpeedParam, vertical);
        if (_pc != null) _pc.BroadcastLocomotion(speed, local.x, local.z, airborne, vertical);
    }
}
