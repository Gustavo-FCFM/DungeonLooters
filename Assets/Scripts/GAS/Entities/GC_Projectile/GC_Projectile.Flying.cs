using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;

// ============================================================
// GC_Projectile — LOS QUE ESTÁN VOLANDO (y el lanzamiento)
//
// Una parte de GC_Projectile (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/Entities/GC_Projectile.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class GC_Projectile
{
    public static IReadOnlyList<GC_Projectile> Flying => s_flying;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetFlying() => s_flying.Clear();

    // Quién lo tiró (o lo devolvió con un parry), su velocidad y con qué grosor pega a un personaje.
    public AbilitySystemComponent Shooter => sourceASC;
    public Vector3 Velocity => _body != null ? _body.linearVelocity : Vector3.zero;
    public bool Falls => _body != null && _body.useGravity;   // cae en curva (para calcular por dónde pasa)
    public float HitRadius => Mathf.Max(CharacterHitRadius, _shape != null ? _shape.bounds.extents.x : 0.3f);
    private void NotifyThrownGone()
    {
        if (_thrownBy == null) return;
        GA_ProjectileShoot by = _thrownBy;
        _thrownBy = null;
        by.OnThrownProjectileGone();
    }

    // Puede dar la vuelta: la habilidad lo pide, no la está dando ya y no lo devolvió un parry.
    private bool CanReturn => !_returning && !_reflected && !_gone && Shot != null && Shot.Returns && sourceASC != null;

    private void SetupBounceAndReturn()
    {
        _launchOrigin = transform.position;
        GA_ProjectileShoot shot = Shot;
        if (shot == null) return;
        _thrownBy = shot;
        _wallBouncesLeft  = Mathf.Max(0, shot.WallBounces);
        _enemyBouncesLeft = Mathf.Max(0, shot.EnemyBounces);
        _speed            = Mathf.Max(1f, shot.LaunchForce);
    }

    private void StopBounceAndReturn()
    {
        _wallBouncesLeft  = 0;
        _enemyBouncesLeft = 0;
        _returning        = false;
        _chase            = null;
    }

    // Cada paso de física, después del barrido: la persecución de un salto y la vuelta.
    private void UpdateBounceAndReturn()
    {
        if (_body == null) _body = GetComponent<Rigidbody>();
        if (_body == null || _body.isKinematic) return;

        if (_returning)
        {
            if (sourceASC == null || sourceASC.HasTag(EGameplayTag.State_Dead) ||
                Time.time - _returnStartedAt > MaxReturnSeconds) { DespawnSelf(); return; }

            Vector3 toOwner = sourceASC.transform.position + Vector3.up - transform.position;
            if (toOwner.magnitude <= ArriveDistance) { DespawnSelf(); return; }
            _body.linearVelocity = toOwner.normalized * _speed;
            return;
        }

        if (_chase != null)
        {
            if (enemiesHit.Contains(_chase) || _chase.HasTag(EGameplayTag.State_Dead) ||
                Time.time - _chaseStartedAt > MaxChaseSeconds) _chase = null;
            else
            {
                Vector3 toTarget = _chase.transform.position + Vector3.up - transform.position;
                if (toTarget.sqrMagnitude > 0.01f) _body.linearVelocity = toTarget.normalized * _speed;
            }
        }

        if (CanReturn && Vector3.Distance(transform.position, _launchOrigin) >= Shot.ReturnDistance)
            StartReturn();
    }

    // Da la vuelta hacia el dueño. "Pega otra vez a la vuelta": se olvida de a quién golpeó (menos de
    // lo que está tocando AHORA, que es donde dio la vuelta: no le pega dos veces seguidas).
    private void StartReturn()
    {
        GA_ProjectileShoot shot = Shot;
        _returning        = true;
        _returnStartedAt  = Time.time;
        _wallBouncesLeft  = 0;
        _enemyBouncesLeft = 0;
        _chase            = null;
        if (_body == null) _body = GetComponent<Rigidbody>();
        if (_body != null && !_body.isKinematic)
        {
            _body.useGravity = false;
            if (sourceASC != null)
                _body.linearVelocity = (sourceASC.transform.position + Vector3.up - transform.position).normalized * _speed;
        }

        _trajectory++;   // lo que quedaba del barrido de este paso ya no cuenta
        if (shot != null && shot.HitsAgainOnReturn)
        {
            enemiesHit.Clear();
            alliesHit.Clear();
            _impactShown.Clear();
            _touched.Clear();
            float radius = Mathf.Max(CharacterHitRadius, _shape != null ? _shape.bounds.extents.magnitude : 0.5f);
            int n = Physics.OverlapSphereNonAlloc(ShapeCenter(), radius, s_near, _mask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++) _touched.Add(s_near[i]);
        }
        _lastCenter = ShapeCenter();
    }

    // Pone el proyectil donde estaba al tocar algo (el barrido lo encuentra un paso tarde).
    private void MoveShapeTo(Vector3 point) => transform.position += point - ShapeCenter();

    // Rebota contra 'wall' si le quedan rebotes. false = no rebota (se deshace o da la vuelta).
    private bool TryBounceOffWall(Collider wall, Vector3 point)
    {
        if (_wallBouncesLeft <= 0 || _reflected) return false;
        if (_body == null) _body = GetComponent<Rigidbody>();
        if (_body == null || _body.isKinematic) return false;

        Vector3 velocity = BounceVelocity(wall, point, _body.linearVelocity);
        if (velocity.sqrMagnitude < 0.01f) return false;

        _wallBouncesLeft--;
        MoveShapeTo(point);
        transform.position += velocity.normalized * 0.05f;
        transform.rotation  = Quaternion.LookRotation(velocity);
        _body.linearVelocity = velocity;

        // La pared sale de lo ya tocado (puede volver a rebotar en ella más tarde) pero se ignora
        // un instante (no la vuelve a encontrar al salir); lo demás del tramo viejo ya no cuenta.
        _touched.Remove(wall);
        _bouncedOff      = wall;
        _bouncedOffUntil = Time.time + BounceIgnoreSeconds;
        _trajectory++;
        _lastCenter = ShapeCenter();
        return true;
    }

    // Salta hacia el próximo enemigo después de pegarle a 'hit'. false = no salta (no le quedan
    // saltos o no hay a quién).
    private bool TryJumpToNextEnemy(AbilitySystemComponent hit, Vector3 point)
    {
        if (_enemyBouncesLeft <= 0 || _reflected || _returning || Shot == null) return false;
        if (_body == null) _body = GetComponent<Rigidbody>();
        if (_body == null || _body.isKinematic) return false;

        AbilitySystemComponent next = FindNextEnemy(sourceASC, hit, Shot.EnemyBounceRange, enemiesHit,
                                                    _mask & ~(1 << CharacterLayer));
        if (next == null) return false;

        _enemyBouncesLeft--;
        MoveShapeTo(point);
        Vector3 dir = (next.transform.position + Vector3.up - transform.position).normalized;
        _body.useGravity     = false;   // va derecho al próximo (con gravedad caería antes)
        _body.linearVelocity = dir * _speed;
        transform.rotation   = Quaternion.LookRotation(dir);
        _chase          = next;
        _chaseStartedAt = Time.time;
        _trajectory++;   // lo que seguía en la dirección vieja ya no cuenta (lo tocado, sí: no le pega dos veces)
        _lastCenter = ShapeCenter();
        return true;
    }

    // La velocidad después de rebotar contra 'wall' (tocada con el centro del proyectil en 'point'):
    // reflejada contra la cara que tocó, con la misma rapidez. Público y estático para las pruebas.
    public static Vector3 BounceVelocity(Collider wall, Vector3 point, Vector3 velocity)
    {
        float speed = velocity.magnitude;
        if (wall == null || speed < 0.01f) return Vector3.zero;
        Vector3 dir = velocity / speed;

        // La cara: un rayo hacia donde iba, desde un poco antes. Si no la toca (un roce de costado),
        // el punto más cercano de la pared; si tampoco (una malla cóncava), vuelve por donde vino.
        Vector3 normal = -dir;
        if (wall.Raycast(new Ray(point - dir * 2f, dir), out RaycastHit hit, 4f)) normal = hit.normal;
        else
        {
            Vector3 away = point - wall.ClosestPoint(point);
            if (away.sqrMagnitude > 0.0001f) normal = away.normalized;
        }

        Vector3 bounced = Vector3.Reflect(velocity, normal);
        // Un roce que ya se alejaba de la cara no se "refleja" hacia adentro.
        if (Vector3.Dot(bounced, normal) < 0f) bounced = Vector3.ProjectOnPlane(velocity, normal) + normal * speed * 0.1f;
        return bounced.normalized * speed;
    }

    // El enemigo de 'source' más cercano a 'hit', dentro de 'range', que no esté en 'exclude', a la
    // vista (sin una pared de 'wallMask' en el medio), vivo y visible. null = ninguno. Público y
    // estático para las pruebas.
    public static AbilitySystemComponent FindNextEnemy(AbilitySystemComponent source, AbilitySystemComponent hit,
                                                       float range, ICollection<AbilitySystemComponent> exclude,
                                                       int wallMask)
    {
        if (source == null || hit == null || range <= 0f) return null;
        Vector3 from = hit.transform.position + Vector3.up;

        AbilitySystemComponent best = null;
        float bestDist = float.MaxValue;
        int n = Physics.OverlapSphereNonAlloc(from, range, s_near, 1 << CharacterLayer, QueryTriggerInteraction.Collide);
        for (int i = 0; i < n; i++)
        {
            AbilitySystemComponent asc = s_near[i].GetComponentInParent<AbilitySystemComponent>();
            if (asc == null || asc == hit || asc == source || (exclude != null && exclude.Contains(asc))) continue;
            if (!source.IsEnemyOf(asc) || asc.Strikeable != null) continue;
            if (asc.HasTag(EGameplayTag.State_Dead) || asc.IsHiddenFromEnemies) continue;

            Vector3 to = asc.transform.position + Vector3.up;
            float d = Vector3.Distance(from, to);
            if (d >= bestDist) continue;
            if (Physics.Linecast(from, to, wallMask, QueryTriggerInteraction.Ignore)) continue;
            best = asc;
            bestDist = d;
        }
        return best;
    }
}
