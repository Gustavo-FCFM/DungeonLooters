using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// BotController — LOS MOVIMIENTOS SIN DUEÑO (dash, salto y vuelo)
//
// Una parte de BotController (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/BotController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class BotController
{

    // =========================================================
    // DASH
    // =========================================================

    // La llama NetworkAbilitySystemComponent.ServerStartDash cuando el personaje NO tiene
    // dueño, o sea cuando es un bot.
    //
    // En un jugador el dash lo ejecuta su CharacterController, que choca con las paredes.
    // Un bot no lo usa (su Update es de dueño), así que acá se avanza a mano pero PEGADO
    // AL NAVMESH: el mismo mapa que dice por dónde puede caminar dice hasta dónde puede
    // deslizarse, y así el dash no lo mete dentro de un muro ni lo tira fuera del mapa.
    public void ServerDash(Vector3 velocity, float duration, bool faceVelocity)
    {
        if (!_ready || duration <= 0f) return;

        CancelMovementRoutines();
        _dashRoutine = StartCoroutine(DashRoutine(velocity, duration, faceVelocity));
    }

    // Corta el dash en curso (la embestida que frenó en un enemigo: NetworkASC.ServerStopRush).
    public void ServerStopDash()
    {
        if (_dashRoutine == null) return;

        StopCoroutine(_dashRoutine);
        _dashRoutine = null;
        _dashing     = false;
        RestoreAgent();
        WarpToNavMesh();
    }

    private IEnumerator DashRoutine(Vector3 velocity, float duration, bool faceVelocity)
    {
        _dashing = true;

        if (_agent != null && _agent.enabled && _agent.isOnNavMesh) _agent.isStopped = true;

        // El dash del pícaro puede ser DIRECCIONAL (esquiva hacia el costado sin girar el
        // cuerpo): faceVelocity en false respeta eso, igual que en un jugador.
        if (faceVelocity)
        {
            Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
            if (flat.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(flat.normalized);
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            // Igual que el salto: por el CharacterController, que es lo que hace que el
            // impulso se frene contra una pared en vez de atravesarla.
            MoveWithCollision(velocity * Time.deltaTime);

            elapsed += Time.deltaTime;
            yield return null;
        }

        _dashing     = false;
        _dashRoutine = null;
        RestoreAgent();
        WarpToNavMesh();
    }

    // =========================================================
    // SALTO
    // =========================================================

    // La llama NetworkAbilitySystemComponent.ServerStartLeap cuando no hay dueño.
    //
    // En un jugador el salto lo aplica su CharacterController y una corutina espera a que
    // vuelva a tocar el piso para resolver el impacto. Un bot no usa el CharacterController,
    // así que acá se simula el arco a mano: sube, cae, y al aterrizar avisa para que el
    // servidor resuelva el golpe en el mismo lugar donde cayó.
    // Calcula con qué velocidad horizontal saltar para CAER DONDE QUIERE.
    //
    // Un jugador apunta el salto y cae ahí. Un bot que solo se impulsa hacia adelante con
    // toda la fuerza de la habilidad recorre, con los números del Bárbaro (subida 15,
    // empuje 15), unos 46 metros — más que el radio de la arena, que es 42. O sea que un
    // salto la cruzaba entera y aterrizaba afuera.
    //
    // Acá se resuelve el tiro: se mide el tiempo de vuelo, se apunta al objetivo, y se
    // recorta a lo que la habilidad permite Y a un punto donde de verdad se pueda estar
    // parado. Si no hay ninguno, se salta en el lugar.
    private Vector3 AimLeapVelocity(Vector3 abilityVelocity, float upVelocity)
    {
        float gravity  = Mathf.Abs(Physics.gravity.y);
        float flight   = 2f * upVelocity / gravity;
        float maxSpeed = abilityVelocity.magnitude;

        if (flight < 0.05f || maxSpeed < 0.01f) return abilityVelocity;

        Vector3 landing = _target != null ? _target.transform.position : _destination;

        Vector3 to = landing - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.01f) return Vector3.zero;

        // Ni más lejos que el objetivo, ni más de lo que la habilidad da.
        float distance = Mathf.Min(to.magnitude, maxSpeed * flight);
        Vector3 wanted = transform.position + to.normalized * distance;

        // Y que el punto exista: si ahí no hay piso navegable, no se salta hacia allá.
        if (!NavMesh.SamplePosition(wanted, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            return Vector3.zero;

        Vector3 corrected = hit.position - transform.position;
        corrected.y = 0f;
        return corrected / flight;
    }

    public void ServerLeap(Vector3 horizontalVelocity, float upVelocity, System.Action onLanded)
    {
        if (!_ready) { onLanded?.Invoke(); return; }

        CancelMovementRoutines();
        _leapRoutine = StartCoroutine(LeapRoutine(AimLeapVelocity(horizontalVelocity, upVelocity),
                                                  upVelocity, onLanded));
    }

    private IEnumerator LeapRoutine(Vector3 horizontalVelocity, float upVelocity, System.Action onLanded)
    {
        _dashing = true;

        // El agente se APAGA, no se pausa: un NavMeshAgent encendido pega el transform al
        // suelo en cada frame, y el salto no tendría altura.
        // El agente se APAGA, no se pausa: encendido pega el transform al suelo en cada
        // frame y el salto no tendría altura. Se vuelve a encender SIEMPRE al terminar —
        // no "si estaba encendido": si un segundo salto interrumpía al primero, la rutina
        // nueva veía el agente ya apagado, concluía que no había que encenderlo, y el bot
        // se quedaba parado para siempre.
        if (_agent != null) _agent.enabled = false;

        float gravity  = Mathf.Abs(Physics.gravity.y);
        float vy       = upVelocity;
        float elapsed  = 0f;
        float startY   = transform.position.y;

        // Dos topes de seguridad, y el de tiempo se calcula del arco REAL.
        //
        // Estaba fijo en 2.5 s, pero el salto del Bárbaro dura 3.06 (dos veces la velocidad
        // de subida sobre la gravedad): se cortaba SIEMPRE a mitad de la bajada, con el bot
        // todavía a varios metros del piso. Ahí corría la red de contención, no encontraba
        // NavMesh cerca —porque estaba en el aire— y lo mandaba a la base. Los bárbaros
        // desaparecían en pleno salto sin que nadie los tocara.
        float maxFlight = 2f * upVelocity / gravity * 1.6f + 0.5f;
        const float maxFall = 12f;

        while (elapsed < maxFlight && transform.position.y > startY - maxFall)
        {
            vy -= gravity * Time.deltaTime;

            MoveWithCollision((horizontalVelocity + Vector3.up * vy) * Time.deltaTime);

            // Aterrizó. NO se pregunta por el CharacterController: su estado lo tocan
            // otras partes del juego y no se le puede creer (ver MoveWithCollision). Un
            // rayo corto hacia abajo es la respuesta directa.
            if (vy < 0f && Physics.Raycast(transform.position + Vector3.up * 0.4f, Vector3.down,
                                           0.7f, CollisionMask, QueryTriggerInteraction.Ignore))
                break;

            elapsed += Time.deltaTime;
            yield return null;
        }

        RestoreAgent();

        // Al aterrizar solo se le avisa al agente dónde quedó. NADA de mandarlo a la base:
        // un salto que salió bien termina donde el bot quiso, y devolverlo era peor que el
        // problema que intentaba resolver.
        WarpToNavMesh();

        _dashing     = false;
        _leapRoutine = null;

        onLanded?.Invoke();
    }

    // =========================================================
    // VUELO (el Mago, desde el 29 de octubre de 2026)
    //
    // Un jugador vuela con su CharacterController (PlayerController → VUELO LIBRE): WASD hacia la
    // cámara, Espacio sube, Ctrl baja. Un bot no tiene cámara ni dueño, así que lo mueve esta
    // rutina, con los MISMOS números del GA_Flight: va hacia donde decidió Think (la pelea, la
    // base, el Objetivo) a FlightHeight metros sobre ese punto. Mientras, Fight sigue tirando
    // hechizos desde el aire. El vuelo dura lo que Status_Flying (recibir daño lo corta, salvo la
    // Sobrecarga); al terminar cae lento, igual que un jugador, y vuelve a caminar.
    //
    // Cuándo despega lo decide ShouldUse (GA_Flight): para escapar, para pelear desde arriba y
    // para cruzar el mapa (TryFlyToDestination).
    // =========================================================

    // La llama GA_Flight.Activate cuando el que vuela es un bot.
    public void ServerStartFlight(float launchSpeed, float speedMultiplier, float forwardBoost, float verticalSpeed,
                                  float idleSink, bool towardAim, float minUpAngle)
    {
        if (!_ready) return;

        CancelMovementRoutines();
        _flightRoutine = StartCoroutine(FlightRoutine(launchSpeed, speedMultiplier, forwardBoost, verticalSpeed,
                                                      idleSink, towardAim, minUpAngle));
    }

    // El Terremoto lanzado volando: el servidor ya cortó el vuelo; cae en picada hasta el piso
    // (GA_GroundFissure espera a verlo ahí para largar la grieta).
    public void ServerDive(float speed)
    {
        if (!_flying) return;
        _diveRequested = true;
        _diveSpeed     = Mathf.Max(1f, Mathf.Abs(speed));
    }

    private IEnumerator FlightRoutine(float launchSpeed, float speedMultiplier, float forwardBoost, float verticalSpeed,
                                      float idleSink, bool towardAim, float minUpAngle)
    {
        _dashing       = true;   // el agente no manda y la red de contención no corre (está en el aire a propósito)
        _flying        = true;
        _diveRequested = false;

        // Apagado, no pausado: encendido pega el transform al piso (ver LeapRoutine).
        if (_agent != null) _agent.enabled = false;

        // El despegue: hacia arriba, o hacia donde va con un mínimo hacia arriba (como un jugador
        // que despega mirando al frente desde el piso).
        Vector3 launchDir = Vector3.up;
        if (towardAim)
        {
            Vector3 flat = _destination - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f) flat = transform.forward;
            float up = Mathf.Clamp(minUpAngle, 0f, 89f) * Mathf.Deg2Rad;
            launchDir = flat.normalized * Mathf.Cos(up) + Vector3.up * Mathf.Sin(up);
        }
        Vector3 launch = launchDir.normalized * Mathf.Max(0f, launchSpeed);

        float sink = 0f;    // lo que cae por quedarse quieto
        float vy   = 0f;    // la caída, terminado el vuelo (o aturdido)
        float giveUpAt = Time.time + 90f;   // tope de seguridad: ningún vuelo dura tanto

        while (Time.time < giveUpAt && !_asc.HasTag(EGameplayTag.State_Dead))
        {
            float dt = Time.deltaTime;
            bool flying = _asc.HasTag(EGameplayTag.Status_Flying) && !_diveRequested;
            Vector3 velocity;

            if (flying && !_asc.HasTag(EGameplayTag.State_Stunned))
            {
                launch *= Mathf.Exp(-3f * dt);
                if (launch.sqrMagnitude < 0.0025f) launch = Vector3.zero;

                Vector3 to   = FlightGoal(out bool landing) - transform.position;
                Vector3 flat = new Vector3(to.x, 0f, to.z);
                float   far  = flat.magnitude;

                // Llegó a donde iba sin nadie a quien pegarle y ya está en el piso: aterriza (se
                // termina el vuelo) y sigue caminando. Un jugador puede seguir volando al ras;
                // un bot que se queda flotando ahí solo pierde el tiempo.
                if (landing && far < 1.5f && Physics.Raycast(transform.position + Vector3.up * 0.4f, Vector3.down, 0.9f,
                                                              CollisionMask, QueryTriggerInteraction.Ignore))
                {
                    _asc.RemoveEffectsWithTag(EGameplayTag.Status_Flying);
                    break;
                }

                // Como un jugador: más rápido si solo va derecho hacia adelante (lejos del punto).
                float speed = ResolveMoveSpeed() * Mathf.Max(0.1f, speedMultiplier);
                if (far > 5f) speed *= Mathf.Max(1f, forwardBoost);

                Vector3 horizontal = far > 0.5f ? flat / far * Mathf.Min(speed, far * 2f) : Vector3.zero;
                float   climb      = Mathf.Clamp(to.y * 1.5f, -verticalSpeed, verticalSpeed);

                // Quieto en el aire va cayendo despacio, igual que un jugador (ver GA_Flight).
                bool idle = horizontal.sqrMagnitude < 0.25f && Mathf.Abs(climb) < 0.3f;
                sink = idle ? Mathf.MoveTowards(sink, idleSink, idleSink * dt) : 0f;

                velocity = horizontal + launch + Vector3.up * (climb - sink);
                vy = 0f;
            }
            else
            {
                // Terminó el vuelo (o lo aturdieron volando): cae lento, como un jugador; en picada
                // si fue el Terremoto. Al tocar el piso vuelve a caminar.
                launch = Vector3.zero;
                if (_diveRequested) vy = -_diveSpeed;
                else
                {
                    vy += _pc.gravity * _pc.featherFallGravityScale * dt;
                    if (_pc.featherFallMaxSpeed > 0f && vy < -_pc.featherFallMaxSpeed) vy = -_pc.featherFallMaxSpeed;
                }
                velocity = Vector3.up * vy;

                if (vy < 0f && Physics.Raycast(transform.position + Vector3.up * 0.4f, Vector3.down, 0.7f,
                                               CollisionMask, QueryTriggerInteraction.Ignore))
                    break;
            }

            MoveWithCollision(velocity * dt);
            _airVelocity = velocity;
            FaceWhileFlying(velocity);
            yield return null;
        }

        _flying        = false;
        _airVelocity   = Vector3.zero;
        _diveRequested = false;
        _dashing       = false;
        _flightRoutine = null;

        RestoreAgent();
        WarpToNavMesh();
    }

    // A dónde vuela: arriba del punto que decidió Think, a FlightHeight. Sin nadie a quien pegarle
    // y ya cerca (el Objetivo, la entrega), baja a ese punto y aterriza. Si el punto está bajo
    // techo (su sala segura, adonde vuelve a curarse), aterriza AFUERA, a unos metros, y el resto
    // lo camina: volando se quedaba pegado arriba del techo. Nunca sube más que el techo invisible
    // de la arena.
    private Vector3 FlightGoal(out bool landing)
    {
        Vector3 goal = _destination;

        Vector3 flat = goal - transform.position;
        flat.y = 0f;
        landing = false;

        if (_target == null)
        {
            bool roofed = Physics.Raycast(_destination + Vector3.up * 0.5f, Vector3.up, FlightHeight + 2f,
                                          CollisionMask, QueryTriggerInteraction.Ignore);
            if (roofed && flat.magnitude < RoofedLandingDistance + 6f)
            {
                Vector3 away = -flat;
                if (away.sqrMagnitude < 0.01f) away = -transform.forward;
                Vector3 outside = _destination + away.normalized * RoofedLandingDistance;
                if (NavMesh.SamplePosition(outside, out NavMeshHit hit, 4f, NavMesh.AllAreas)) outside = hit.position;
                landing = true;
                return outside;
            }
            if (!roofed && flat.magnitude < 6f) { landing = true; return goal; }
        }

        goal.y = _destination.y + FlightHeight;

        MercArenaBounds bounds = ResolveBounds();
        if (bounds != null) goal.y = Mathf.Min(goal.y, bounds.CeilingHeight - 1.5f);
        return goal;
    }

    // Volando mira al que pelea (los hechizos salen hacia donde mira el cuerpo); si no hay nadie,
    // hacia donde va.
    private void FaceWhileFlying(Vector3 velocity)
    {
        Vector3 look = _target != null ? _target.transform.position - transform.position : velocity;
        look.y = 0f;
        if (look.sqrMagnitude < 0.01f) return;

        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, Quaternion.LookRotation(look.normalized), 720f * Time.deltaTime);
    }

    // ¿Hay lugar arriba para despegar? Bajo un techo (su base, un panteón) el vuelo lo dejaría
    // pegado contra él.
    private bool HasHeadroom(float height)
        => !Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.up, height,
                            CollisionMask, QueryTriggerInteraction.Ignore);

    // Sin nadie a quien pegarle (yendo a la pelea, al Objetivo, o volviendo a curarse): si lo que
    // tiene en el Shift es un vuelo y el lugar queda lejos, vuela hasta allá.
    private bool TryFlyToDestination()
    {
        if (_flying || !(FindAbility(EAbilityInput.Movement) is GA_Flight)) return false;

        Vector3 flat = _destination - transform.position;
        flat.y = 0f;
        if (flat.magnitude <= FlyTravelDistance || !HasHeadroom(FlightHeight)) return false;

        return Activate(EAbilityInput.Movement, _destination + Vector3.up, DirectionTo(_destination));
    }
}
