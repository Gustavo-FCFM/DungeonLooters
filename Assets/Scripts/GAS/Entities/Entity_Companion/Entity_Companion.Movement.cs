using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// Entity_Companion — MOVERSE Y SALTAR
//
// Una parte de Entity_Companion (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/Entity_Companion.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class Entity_Companion
{

    // =========================================================
    // MOVERSE (por el piso)
    // =========================================================

    private void MoveTo(Vector3 point, float speed)
    {
        if (_agent != null && _agent.enabled)
        {
            if (!_agent.isOnNavMesh) { WarpNear(OwnerASC.transform.position); return; }

            // Un objetivo en una pared o en el aire: el punto del piso más cercano debajo.
            if (NavMesh.SamplePosition(point, out NavMeshHit hit, 4f, NavMesh.AllAreas)) point = hit.position;
            else if (Physics.Raycast(point + Vector3.up * 0.5f, Vector3.down, out RaycastHit ground, 30f, ~(1 << 7 | 1 << 2), QueryTriggerInteraction.Ignore) &&
                     NavMesh.SamplePosition(ground.point, out hit, 4f, NavMesh.AllAreas))
                point = hit.position;

            _agent.speed     = speed;
            _agent.isStopped = false;
            _agent.SetDestination(point);

            // Cada tanto: ¿el camino da mucha vuelta o no llega? Entonces salta derecho.
            if (Time.time >= _nextPathCheck && !_agent.pathPending)
            {
                _nextPathCheck = Time.time + 0.4f;
                if (ShouldLeap(point)) StartLeap(point);
            }
            return;
        }

        // Sin NavMesh: derecho hacia el punto, apoyado en el piso.
        Vector3 next = Vector3.MoveTowards(transform.position, new Vector3(point.x, transform.position.y, point.z), speed * Time.deltaTime);
        if (Physics.Raycast(next + Vector3.up * 2f, Vector3.down, out RaycastHit floor, 6f, ~(1 << 7 | 1 << 2), QueryTriggerInteraction.Ignore))
            next.y = floor.point.y;
        Face(point);
        transform.position = next;
    }

    private void Halt()
    {
        if (_agent != null && _agent.enabled && _agent.isOnNavMesh) _agent.isStopped = true;
    }

    private void Face(Vector3 point)
    {
        Vector3 dir = point - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.001f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), Time.deltaTime * 12f);
    }

    // Lejos del dueño, o trabado sin avanzar: reaparece a su lado.
    private void TeleportIfLost()
    {
        Vector3 ownerPos = OwnerASC.transform.position;
        float fromOwner = FlatDistance(transform.position, ownerPos);

        // Persiguiendo a alguien puede alejarse (hasta el alcance de las órdenes): reaparecer junto
        // al dueño en medio de la persecución lo dejaba en un bucle sin pegar nunca.
        if (_target == null && fromOwner > TeleportDistance) { WarpNear(ownerPos); return; }
        if (_target != null && fromOwner > CommandRange + 10f)
        {
            _target = null; _commanded = false;
            WarpNear(ownerPos);
            return;
        }

        bool tryingToMove = _agent != null && _agent.enabled && _agent.isOnNavMesh && !_agent.isStopped &&
                            _agent.remainingDistance > 1.5f;
        float moved = (transform.position - _lastPosition).magnitude;
        _lastPosition = transform.position;

        _stuckTimer = tryingToMove && moved < 0.02f ? _stuckTimer + Time.deltaTime : 0f;
        if (_stuckTimer > 2f)
        {
            _stuckTimer = 0f;
            if (_target != null) { _target = null; _commanded = false; }
            WarpNear(ownerPos);
        }
    }

    // =========================================================
    // SALTAR (a otro piso, a una plataforma)
    //
    // El NavMesh no une los pisos de la arena: el camino daba toda la vuelta o no llegaba. Si
    // el camino es mucho más largo que la línea recta (o no llega), y el punto está a tiro y
    // con el aire libre en el medio, salta derecho: un arco sin agente (el NetworkTransform lo
    // lleva a todos) y cae sobre el NavMesh del otro lado.
    // =========================================================

    private bool ShouldLeap(Vector3 destination)
    {
        if (Time.time < _nextLeapAt) return false;

        Vector3 from = transform.position;
        float flat = FlatDistance(from, destination);
        float dy   = Mathf.Abs(destination.y - from.y);
        if (flat > JumpRange || dy > JumpHeight || (flat < 1.5f && dy < 1f)) return false;

        bool unreachable = _agent.pathStatus != NavMeshPathStatus.PathComplete;
        float straight   = Vector3.Distance(from, destination);
        bool detour      = PathLength(_agent.path) > straight * JumpDetourFactor + 4f;
        if (!unreachable && !detour) return false;

        return ArcIsClear(from, destination, LeapHeightFor(from, destination));
    }

    private static float PathLength(NavMeshPath path)
    {
        if (path == null || path.corners.Length < 2) return 0f;
        float length = 0f;
        for (int i = 1; i < path.corners.Length; i++) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        return length;
    }

    // Cuánto sube el arco por encima de la línea entre los dos puntos.
    private static float LeapHeightFor(Vector3 from, Vector3 to) => 1.2f + Mathf.Abs(to.y - from.y) * 0.5f;

    // Que no atraviese un techo ni una pared: dos tramos, hasta la cima del arco y de ahí abajo.
    private static bool ArcIsClear(Vector3 from, Vector3 to, float height)
    {
        int mask = ~((1 << 7) | (1 << 2));
        Vector3 a    = from + Vector3.up * 0.6f;
        Vector3 b    = to + Vector3.up * 0.6f;
        Vector3 apex = (a + b) * 0.5f + Vector3.up * height;
        return !Physics.Linecast(a, apex, mask, QueryTriggerInteraction.Ignore) &&
               !Physics.Linecast(apex, b, mask, QueryTriggerInteraction.Ignore);
    }

    private void StartLeap(Vector3 destination)
    {
        Vector3 landing = destination;
        if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas)) landing = hit.position;

        _leapFrom     = transform.position;
        _leapTo       = landing;
        _leapStart    = Time.time;
        _leapDuration = Mathf.Clamp(FlatDistance(_leapFrom, _leapTo) / 14f, 0.35f, 0.8f);
        _leapHeight   = LeapHeightFor(_leapFrom, _leapTo);
        _leaping      = true;
        _stuckTimer   = 0f;

        if (_agent != null) _agent.enabled = false;   // en el aire no hay NavMesh
    }

    private void TickLeap()
    {
        float t = Mathf.Clamp01((Time.time - _leapStart) / _leapDuration);
        Vector3 p = Vector3.Lerp(_leapFrom, _leapTo, t) + Vector3.up * (4f * _leapHeight * t * (1f - t));
        Face(_leapTo);
        transform.position = p;
        if (t < 1f) return;

        _leaping      = false;
        _nextLeapAt   = Time.time + JumpCooldown;
        _lastPosition = _leapTo;
        if (_agent != null)
        {
            _agent.enabled = true;
            _agent.Warp(_leapTo);
        }
    }

    // Detrás del dueño, a su derecha, SIN cruzar paredes (29 de octubre de 2026, Gustavo: "los
    // animales terrestres pueden atravesar paredes"). Antes buscaba el NavMesh más cercano a
    // ese punto, que con el dueño pegado a una pared podía quedar del otro lado; y sin NavMesh
    // cerca lo ponía igual a su lado, fuera del NavMesh, y lo seguía así a través de todo. Ahora
    // sale del piso del dueño y avanza por el NavMesh hasta ese punto (o hasta la pared); sin
    // piso horneado cerca del dueño (está en el aire), espera donde está.
    private void WarpNear(Vector3 ownerPos)
    {
        Vector3 offset = Quaternion.Euler(0f, OwnerASC.transform.eulerAngles.y, 0f) * new Vector3(1.5f, 0f, -1.5f);
        if (_agent == null || !_agent.enabled)
        {
            transform.position = ownerPos + offset;
            return;
        }

        if (!NavMesh.SamplePosition(ownerPos, out NavMeshHit ownerFloor, 5f, NavMesh.AllAreas)) return;

        Vector3 spot = ownerFloor.position + offset;
        Vector3 to;
        if (NavMesh.Raycast(ownerFloor.position, spot, out NavMeshHit wall, NavMesh.AllAreas)) to = wall.position;
        else to = NavMesh.SamplePosition(spot, out NavMeshHit snapped, 1.5f, NavMesh.AllAreas) ? snapped.position : ownerFloor.position;

        if (Mathf.Abs(to.y - ownerFloor.position.y) > 2f) to = ownerFloor.position;
        _agent.Warp(to);
    }
}
