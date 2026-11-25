using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// BotController — EL INSTINTO (20 de noviembre de 2026, pedido de Gustavo: "más humanos y más inteligentes, que sepan
// escapar del daño, afrontar los problemas y rodear un escudo")
//
// Una parte de BotController (partial class): los campos del Inspector están en BotController.cs ("Instinto"). Lo de
// acá NO decide adónde va la pelea (eso es Think → DecideGoal): mira lo que pasa alrededor y CORRIGE como lo haría una
// persona:
//
//   · ESQUIVAR: ve venir los proyectiles enemigos (GC_Projectile.Flying) y da un paso al costado. No todos: lo nota
//     según su destreza, y no lo ve si ya lo tiene encima.
//   · SALIR DE LAS ZONAS: si está parado en algo que le hace daño (la explosión que se está cargando, la zona de fuego,
//     la trampa; AreaThreats), se sale por el lado más corto; si la carga está por caer y tiene un dash o un
//     teletransporte, lo usa. Y no elige pararse adentro de una (salvo el Bárbaro entero: él pelea donde haya que pelear).
//   · RODEAR EL ESCUDO: si entre él y su objetivo hay un escudo enemigo levantado (Entity_ShieldBarrier.RaisedNow), va
//     a un costado desde donde el escudo no lo tape. Uno que cubre todo (la cápsula) no se rodea: busca a otro.
//   · ELEGIR MEJOR A QUIÉN: se queda con el que venía peleando (no cambia por un metro), le responde al que le está
//     pegando, remata al que está por caer y se suma a lo que pelean sus compañeros bots.
//   · RETIRARSE BIEN: todos se retiran (el Bárbaro y el soporte más tarde), también si están superados en número y
//     heridos; van al botiquín más cercano si les queda más a mano que la base, y no se van si el que tienen al lado
//     está por caer.
//   · COMO UNA PERSONA: tarda en reaccionar a un enemigo nuevo, apunta con un error que depende de su destreza y se
//     adelanta a lo que corre (si tira proyectiles), más o menos según lo bueno que sea. Cada bot nace con su destreza.
// Corre en el servidor, como todo el bot. Lo llama Think (cada ThinkInterval) y lo usan Steer, Fight y Activate.
// ============================================================
public partial class BotController
{
    // ---- Estado ----
    private float _skill;                                   // la destreza de este bot (ver Skill)
    private AbilitySystemComponent _reactedTo;              // el objetivo al que ya reaccionó
    private float _reactUntil;                              // todavía no reacciona a su objetivo nuevo
    private Vector3 _aimOffset;
    private float _aimOffsetUntil;
    private AbilitySystemComponent _trackedTarget;          // a quién le mide la velocidad
    private Vector3 _trackedPos;
    private float _trackedAt;
    private Vector3 _targetVelocity;
    private Vector3 _dodgePoint;
    private float _dodgeUntil;
    private readonly HashSet<int> _judgedProjectiles = new HashSet<int>();
    private float _judgedResetAt;
    private Vector3 _escapePoint;
    private float _escapeUntil;
    private AbilitySystemComponent _lastAttacker;
    private float _lastHurtAt = -99f;
    private float _orbitScale = 1f;                         // el anillo de rodeo, un poco distinto en cada vuelta

    // Los bots vivos (para pelear en equipo: a quién le pegan los compañeros).
    private static readonly List<BotController> s_bots = new List<BotController>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetBots() => s_bots.Clear();

    // Cuánto dura "me acaba de pegar" para responderle (s).
    private const float RetaliateSeconds = 2.5f;

    // La llama ServerInitialize: su destreza y escuchar quién le pega.
    private void InitInstinct()
    {
        _skill = Skill > 0f ? Skill : Random.Range(0.45f, 0.85f);
        if (_asc != null) { _asc.OnTookDamage -= OnHurt; _asc.OnTookDamage += OnHurt; }   // una sola vez aunque lo inicien dos
        if (!s_bots.Contains(this)) s_bots.Add(this);
    }

    private void OnDestroy()
    {
        s_bots.Remove(this);
        if (_asc != null) _asc.OnTookDamage -= OnHurt;
    }

    private void OnHurt(AbilitySystemComponent attacker)
    {
        if (attacker == null || attacker == _asc) return;
        _lastAttacker = attacker;
        _lastHurtAt   = Time.time;
    }

    // =========================================================
    // EL CICLO: mirar, decidir (DecideGoal) y corregir
    // =========================================================

    private void Think()
    {
        SenseTarget();
        DecideGoal();
        ReactToTargetChange();

        if (_asc.HasTag(EGameplayTag.State_Dead)) return;

        _destination = SafeDestination(_destination);
        LeaveDangerZones();
        DodgeProjectiles();
    }

    // Adónde camina AHORA: el paso al costado de una esquiva, la salida de una zona, o lo que decidió.
    private Vector3 CurrentGoal()
    {
        if (Time.time < _dodgeUntil)  return _dodgePoint;
        if (Time.time < _escapeUntil) return _escapePoint;
        return _destination;
    }

    // =========================================================
    // COMO UNA PERSONA: reacción, puntería, adelantarse
    // =========================================================

    // La velocidad del objetivo, medida entre una decisión y la siguiente (para adelantarse a lo que corre).
    private void SenseTarget()
    {
        if (_target == null || _target != _trackedTarget)
        {
            _trackedTarget  = _target;
            _targetVelocity = Vector3.zero;
            if (_target != null) { _trackedPos = _target.transform.position; _trackedAt = Time.time; }
            return;
        }

        float dt = Time.time - _trackedAt;
        if (dt < 0.05f) return;
        Vector3 v = (_target.transform.position - _trackedPos) / dt;
        if (v.sqrMagnitude > 400f) v = Vector3.zero;   // un teletransporte no es correr a 20 m/s
        _targetVelocity = Vector3.Lerp(_targetVelocity, v, 0.6f);
        _trackedPos = _target.transform.position;
        _trackedAt  = Time.time;
    }

    // Un enemigo nuevo: tarda un poco en reaccionar (menos si es el que le está pegando: ese ya lo tenía en la cabeza).
    private void ReactToTargetChange()
    {
        if (_target == _reactedTo) return;
        _reactedTo = _target;
        if (_target == null) return;

        float delay = Mathf.Lerp(ReactionMax, ReactionMin, _skill) * Random.Range(0.8f, 1.2f);
        if (_target == _lastAttacker && Time.time - _lastHurtAt < RetaliateSeconds) delay *= 0.6f;
        _reactUntil = Time.time + delay;
    }

    // ¿Todavía no reaccionó a su objetivo? (Fight no ataca mientras tanto)
    private bool StillReacting => Time.time < _reactUntil;

    // El error de su puntería a esta distancia: cambia cada tanto (no es un temblor, es apuntar un poco mal).
    private Vector3 AimError(float dist)
    {
        if (Time.time >= _aimOffsetUntil)
        {
            float spread = AimErrorAt10m * Mathf.Clamp01(1.15f - _skill) * Mathf.Clamp(dist / 10f, 0.3f, 2.5f);
            Vector2 r = Random.insideUnitCircle * spread;
            _aimOffset = transform.right * r.x + Vector3.up * (r.y * 0.5f);
            _aimOffsetUntil = Time.time + Random.Range(0.25f, 0.6f);
        }
        return _aimOffset;
    }

    // Cuánto se adelanta a lo que corre con un proyectil de esta velocidad (el bueno, casi todo; el malo, la mitad).
    private Vector3 LeadFor(float projectileSpeed)
    {
        if (_target == null || projectileSpeed <= 1f) return Vector3.zero;
        float t = Vector3.Distance(transform.position, _target.transform.position) / projectileSpeed;
        Vector3 v = _targetVelocity;
        v.y = 0f;
        return v * (Mathf.Min(t, 1.5f) * Mathf.Lerp(0.5f, 1f, _skill));
    }

    // ¿Esta mira apunta a su objetivo? (para adelantarse solo con lo que va a él, no a un aliado o al piso)
    private bool AimsAtTarget(Vector3 aim)
        => _target != null && (aim - (_target.transform.position + Vector3.up * 1.2f)).sqrMagnitude < 16f;

    // =========================================================
    // ELEGIR A QUIÉN
    // =========================================================

    // Lo que suma o resta el instinto al puntaje de un enemigo (menos = mejor; ver FindBestTarget).
    private float InstinctScore(AbilitySystemComponent other)
    {
        float score = 0f;

        // El que venía peleando: no se cambia por uno que está un metro más cerca.
        if (other == _target) score -= 3f;

        // El que me está pegando: se le responde.
        if (other == _lastAttacker && Time.time - _lastHurtAt < RetaliateSeconds) score -= 6f;

        // El que está por caer: se lo remata.
        score -= (1f - HealthFraction(other)) * 8f;

        // Lo que pelean mis compañeros: juntos caen antes.
        int mates = 0;
        foreach (BotController b in s_bots)
            if (b != null && b != this && b._teamId == _teamId && b._target == other && ++mates >= 2) break;
        score -= mates * 2f;

        // Detrás de un escudo que lo cubre todo (la cápsula): cualquier otro antes.
        if (HasOmniShield(other)) score += 15f;

        // Metido en algo que me hace daño (su propio fuego): el que pega de cerca tendría que entrar a buscarlo, así que
        // otro antes que esperarlo en el borde (el Bárbaro entero entra igual, ver BraveInDanger).
        if (!FightsFromAfar(ResolveRole()) && !BraveInDanger() && AreaThreats.IsDangerous(other.transform.position, _asc))
            score += 6f;

        return score;
    }

    // =========================================================
    // ESQUIVAR LOS PROYECTILES
    // =========================================================

    // Un proyectil enemigo que le va a pegar en menos de un segundo: si lo nota (según su destreza), un paso al costado.
    private void DodgeProjectiles()
    {
        if (Time.time < _dodgeUntil || _flying || _dashing) return;
        if (_asc.HasTag(EGameplayTag.State_Stunned) || _asc.HasTag(EGameplayTag.State_Rooted)) return;
        if (Time.time >= _judgedResetAt) { _judgedProjectiles.Clear(); _judgedResetAt = Time.time + 5f; }

        Vector3 me = transform.position + Vector3.up * 1.1f;
        IReadOnlyList<GC_Projectile> flying = GC_Projectile.Flying;
        for (int i = 0; i < flying.Count; i++)
        {
            GC_Projectile p = flying[i];
            if (p == null) continue;
            AbilitySystemComponent shooter = p.Shooter;
            if (shooter == null || shooter == _asc || !shooter.IsEnemyOf(_asc)) continue;

            Vector3 v = p.Velocity;
            Vector3 flatV = new Vector3(v.x, 0f, v.z);
            float flat2 = flatV.sqrMagnitude;
            if (flat2 < 9f) continue;   // casi quieto, o cayendo derecho

            // Cuándo pasa más cerca (en el plano) y por dónde pasa en ese momento: con la caída, si cae en curva (una
            // flecha o un hacha apuntadas con la curva bajan justo sobre él; derecho, la cuenta las veía pasar arriba).
            Vector3 from = p.transform.position;
            Vector3 toMe = me - from;
            float t = (toMe.x * flatV.x + toMe.z * flatV.z) / flat2;
            if (t <= 0.12f || t > 1.2f) continue;   // ya lo tiene encima (no llega), o falta mucho
            Vector3 at = from + v * t;
            if (p.Falls) at += Physics.gravity * (0.5f * t * t);
            Vector3 miss = me - at;
            if (miss.magnitude > p.HitRadius + 0.6f) continue;

            // Cada proyectil se juzga UNA vez: o lo vio, o no.
            if (!_judgedProjectiles.Add(p.GetInstanceID())) continue;
            if (Random.value > Mathf.Lerp(0.25f, 0.8f, _skill)) continue;

            Vector3 side = Vector3.Cross(Vector3.up, flatV);
            side.y = 0f;
            if (side.sqrMagnitude < 0.0001f) continue;
            side.Normalize();
            float along = Vector3.Dot(miss, side);
            float sign = Mathf.Abs(along) < 0.15f ? _strafeSign : Mathf.Sign(along);

            // Para el lado que lo saca de la línea; si está tapado, para el otro.
            if (!TryStep(side * sign, DodgeStep, out Vector3 point) && !TryStep(-side * sign, DodgeStep, out point)) continue;

            _dodgePoint = point;
            _dodgeUntil = Time.time + Mathf.Clamp(t + 0.2f, 0.35f, 0.8f);
            return;
        }
    }

    // Un punto caminable a 'distance' hacia 'dir' (sin pared en medio y fuera de las zonas de daño).
    private bool TryStep(Vector3 dir, float distance, out Vector3 point)
    {
        point = transform.position;
        Vector3 want = transform.position + dir * distance;
        if (NavMesh.Raycast(transform.position, want, out NavMeshHit blocked, NavMesh.AllAreas))
        {
            if ((blocked.position - transform.position).sqrMagnitude < 1f) return false;   // pegado a la pared
            want = blocked.position;
        }
        if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 1.5f, NavMesh.AllAreas)) return false;
        if (AreaThreats.IsDangerous(hit.position, _asc, 0.2f)) return false;
        point = hit.position;
        return true;
    }

    // =========================================================
    // SALIR DE LAS ZONAS
    // =========================================================

    // Parado en algo que le hace daño: salir por el lado más corto (o el más cercano que esté libre). Si la salida es
    // larga, con su dash o su teletransporte.
    private void LeaveDangerZones()
    {
        if (_flying || _dashing) return;
        if (OnDeliveryPad()) return;   // con la carga sobre la entrega, se aguanta: irse la cancela
        if (BraveInDanger()) { _escapeUntil = 0f; return; }

        if (!AreaThreats.Find(transform.position, _asc, 0.3f, out Vector3 dir, out float exit))
        {
            _escapeUntil = 0f;
            return;
        }

        if (dir.sqrMagnitude < 0.0001f)
        {
            dir = _target != null ? transform.position - _target.transform.position : -transform.forward;
            dir.y = 0f;
            dir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward;
        }

        // El camino más corto, y si está tapado, girando de a poco hacia los costados.
        Vector3 escape = transform.position;
        bool found = false;
        foreach (float turn in EscapeTurns)
        {
            Vector3 d = Quaternion.Euler(0f, turn, 0f) * dir;
            if (!TryStep(d, exit + DangerMargin, out escape)) continue;
            found = true;
            break;
        }
        if (!found) return;

        _escapePoint = escape;
        _escapeUntil = Time.time + ThinkInterval * 1.6f;

        // Lejos de la salida y con algo para irse rápido: se usa (un dash, un teletransporte).
        if (exit > 2.5f && Time.time >= _nextAbilityAt && TryQuickEscape(escape))
            _nextAbilityAt = Time.time + AbilityInterval;
    }

    // Hacia dónde prueba salir de una zona: derecho afuera y, si está tapado, girando hacia los costados (°).
    private static readonly float[] EscapeTurns = { 0f, 40f, -40f, 80f, -80f, 120f, -120f };

    // Irse rápido a 'point' con la habilidad de movimiento, si es de las que llevan a un punto (un dash, un
    // teletransporte). Un salto o una embestida no: van hacia el enemigo.
    private bool TryQuickEscape(Vector3 point)
    {
        GameplayAbility ability = FindAbility(EAbilityInput.Movement);
        if (ability == null) return false;
        GameplayAbility moving = ability is GA_TagSwitch sw && sw.CurrentVariant != null ? sw.CurrentVariant : ability;
        if (!(moving is GA_Dash) && !(moving is GA_Teleport)) return false;
        return Activate(EAbilityInput.Movement, point + Vector3.up, DirectionTo(point));
    }

    // El Bárbaro entero, peleando, no le escapa a una zona: pelea donde haya que pelear. Vale para no elegir un lugar
    // adentro (SafeDestination) y para no salirse (LeaveDangerZones): con una sola de las dos, iba y venía en el borde.
    private bool BraveInDanger()
        => ResolveRole() == EBotRole.Bruiser && _target != null && HealthFraction(_asc) > 0.6f;

    // Un destino adentro de una zona de daño se corre afuera (salvo el valiente, ver BraveInDanger).
    private Vector3 SafeDestination(Vector3 destination)
    {
        if (BraveInDanger()) return destination;
        if (OnDeliveryPad()) return destination;
        if (!AreaThreats.Find(destination, _asc, 0.5f, out Vector3 dir, out float exit)) return destination;

        if (dir.sqrMagnitude < 0.0001f)
        {
            dir = transform.position - destination;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return destination;
            dir.Normalize();
        }
        Vector3 p = destination + dir * (exit + 0.5f);
        return NavMesh.SamplePosition(p, out NavMeshHit hit, 2.5f, NavMesh.AllAreas) ? hit.position : destination;
    }

    // ¿Lleva la carga y ya está sobre su entrega?
    private bool OnDeliveryPad()
    {
        if (!_delivering) return false;
        MercenariesGameMode gm = MercenariesGameMode.Instance;
        MercTeamBase home = gm != null ? gm.GetBase(_teamId) : null;
        return home != null && home.IsInDeliveryZone(transform.position);
    }

    // =========================================================
    // RODEAR EL ESCUDO
    // =========================================================

    // Un escudo ENEMIGO levantado que tapa la línea entre mí y mi objetivo (el suyo, o el de un compañero suyo que lo
    // cubre). null si no hay.
    private Entity_ShieldBarrier ShieldInTheWay(AbilitySystemComponent target, Vector3 from)
    {
        if (target == null) return null;
        Vector3 to = target.transform.position;
        IReadOnlyList<Entity_ShieldBarrier> raised = Entity_ShieldBarrier.RaisedNow;
        for (int i = 0; i < raised.Count; i++)
        {
            Entity_ShieldBarrier b = raised[i];
            if (b == null || b.Owner == null || !b.IsHostile(_asc)) continue;
            if ((b.transform.position - to).sqrMagnitude > 64f) continue;   // lejos de mi objetivo: no lo cubre
            if (b.BlocksShot(from, to)) return b;
        }
        return null;
    }

    // ¿Lo cubre un escudo que tapa todo (la cápsula)? Ese no se rodea.
    private bool HasOmniShield(AbilitySystemComponent target)
    {
        IReadOnlyList<Entity_ShieldBarrier> raised = Entity_ShieldBarrier.RaisedNow;
        for (int i = 0; i < raised.Count; i++)
        {
            Entity_ShieldBarrier b = raised[i];
            if (b != null && b.Omnidirectional && b.Owner == target && b.IsHostile(_asc)) return true;
        }
        return false;
    }

    // Si un escudo le tapa a su objetivo: un punto a un costado (girando alrededor del objetivo, para el lado al que ya
    // rodeaba) desde donde el escudo no lo tape. false = no hay escudo, o es una cápsula (no se rodea).
    private bool TryFlankShield(AbilitySystemComponent target, float keep, out Vector3 point)
    {
        point = default;
        Entity_ShieldBarrier shield = ShieldInTheWay(target, transform.position);
        if (shield == null || shield.Omnidirectional) return false;

        Vector3 center = target.transform.position;
        Vector3 away = transform.position - center;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = -target.transform.forward;
        away.Normalize();

        // De a 30°, primero para el lado al que rodeaba, hasta FlankMaxDegrees.
        for (float a = 30f; a <= FlankMaxDegrees + 0.1f; a += 30f)
        {
            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? _strafeSign : -_strafeSign;
                Vector3 candidate = center + Quaternion.Euler(0f, sign * a, 0f) * away * keep;
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas)) continue;
                if (shield.BlocksShot(hit.position, center)) continue;
                if (AreaThreats.IsDangerous(hit.position, _asc, 0.2f)) continue;
                _strafeSign = sign;
                point = hit.position;
                return true;
            }
        }
        return false;
    }

    // =========================================================
    // RETIRARSE
    // =========================================================

    // ¿Estoy superado en número acá? (dos JUGADORES enemigos más que los míos, contándome; los fantasmas no cuentan: de
    // tres NPC débiles no se huye)
    private bool Outnumbered()
        => EnemyPlayersNear(transform.position, 10f) >= AlliesNear(transform.position, 10f) + 3;

    // Cuántos jugadores enemigos (y bots) hay alrededor de un punto, vivos y a la vista.
    private int EnemyPlayersNear(Vector3 center, float radius)
    {
        int count = Physics.OverlapSphereNonAlloc(center, radius, _hits, CharacterLayer, QueryTriggerInteraction.Ignore);
        int enemies = 0;
        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i].GetComponentInParent<AbilitySystemComponent>();
            if (other == null || other == _asc || other.HasTag(EGameplayTag.State_Dead)) continue;
            if (_asc.IsAllyOf(other, includeSelf: false) || other.Strikeable != null) continue;
            if (other.GetComponent<PlayerController>() == null || !CanPerceive(other)) continue;
            enemies++;
        }
        return enemies;
    }

    // ¿El que tengo al lado está por caer? Entonces lo remato antes de irme.
    private bool AboutToFinish()
    {
        if (_target == null) return false;
        return HealthFraction(_target) < 0.15f &&
               Vector3.Distance(transform.position, _target.transform.position) <= RangedDistance;
    }

    // Adónde va a curarse: el botiquín listo más cercano si le queda más a mano que la base (y sin enemigos encima);
    // si no, su base.
    private Vector3 RetreatDestination(MercenariesGameMode gm)
    {
        Vector3 home = ResolveHealSpot(gm);
        if (FindHealthPack(HealthPackSeekRadius, out Vector3 pack) &&
            (pack - transform.position).sqrMagnitude < (home - transform.position).sqrMagnitude * 0.64f)
            return pack;
        return home;
    }

    // El botiquín listo más cercano a 'radius', sin un enemigo al lado. false = ninguno.
    private bool FindHealthPack(float radius, out Vector3 point)
    {
        point = default;
        float best = radius * radius;
        bool found = false;
        IReadOnlyList<HealthPack> packs = HealthPack.All;
        for (int i = 0; i < packs.Count; i++)
        {
            HealthPack h = packs[i];
            if (h == null || !h.IsReady) continue;
            Vector3 p = h.transform.position;
            float d = (p - transform.position).sqrMagnitude;
            if (d >= best) continue;
            if (FindEnemyNear(p, 6f) != null) continue;
            best  = d;
            point = p;
            found = true;
        }
        if (found && NavMesh.SamplePosition(point, out NavMeshHit hit, 2f, NavMesh.AllAreas)) point = hit.position;
        return found;
    }

    // Retirándose con alguien encima: se va con su dash o su teletransporte, lejos de él.
    private bool TryEscapeChaser()
    {
        AbilitySystemComponent chaser = FindEnemyNear(transform.position, MeleeRange * 3f);
        if (chaser == null) return false;
        Vector3 away = transform.position - chaser.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = -transform.forward;
        Vector3 toward = (_destination - transform.position);
        toward.y = 0f;
        // Hacia donde iba a curarse, si eso no es ir hacia el que lo persigue.
        Vector3 dir = toward.sqrMagnitude > 0.01f && Vector3.Dot(toward.normalized, away.normalized) > -0.2f
            ? toward.normalized : away.normalized;
        return TryStep(dir, 6f, out Vector3 point) && TryQuickEscape(point);
    }
}
