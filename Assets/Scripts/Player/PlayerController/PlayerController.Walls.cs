using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — PAREDES Y VUELO LIBRE
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    // =========================================================
    // MOVIMIENTO EN PAREDES (WallMovementProfile de la clase)
    //
    // En el aire, yendo con el WASD contra una pared, se engancha:
    //  · Pegarse (Pícaro): se frena y queda quieto ClingTime; después resbala SlideTime y
    //    se suelta solo.
    //  · Correr (Monje): WASD lo lleva a lo largo de la pared (solo de costado), bajando de
    //    a poco hasta tocar el piso. Si la pared se acaba, cae con el impulso que traía.
    // En los dos, Espacio salta hacia AFUERA de la pared y un poco hacia arriba; empujar
    // hacia afuera lo suelta. Los saltos se encadenan sin límite, pero a la pared de la que
    // se acaba de soltar no se vuelve a pegar hasta tocar el piso.
    //
    // Pegado se pueden usar las habilidades (Update sigue leyéndolas); las de movimiento
    // lo despegan al arrancar (ApplyDashVelocity, StartRush, TeleportTo...). Aturdido,
    // enraizado, repelido, volando o muerto, se suelta.
    //
    // Todo en el DUEÑO, como el resto del movimiento: los demás ven la posición por el
    // NetworkTransform, y la pose (correr en vez de caer, la inclinación del modelo) por la
    // locomoción en red.
    // =========================================================

    // El CharacterController avisa acá cada cosa con la que choca durante Move(). Solo se
    // guardan los roces de COSTADO: son los candidatos a pared.
    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (Mathf.Abs(hit.normal.y) > 0.7f) return;   // piso o techo
        _sideHitFrame    = Time.frameCount;
        _sideHitNormal   = hit.normal;
        _sideHitPoint    = hit.point;
        _sideHitCollider = hit.collider;
    }

    // ¿Se puede estar en una pared ahora? Lo que suelta a cualquiera (aturdido, enraizado,
    // volando, un impulso en curso) más los tags que bloquean en el perfil.
    private bool CanUseWalls(WallMovementProfile p)
    {
        if (ASC == null || p == null) return false;
        if (ASC.HasTag(EGameplayTag.State_Dead) || ASC.HasTag(EGameplayTag.State_Stunned) ||
            ASC.HasTag(EGameplayTag.State_Rooted) || IsFlying) return false;
        // El salto de una habilidad (la Retirada del Explorador) SÍ deja engancharse: el
        // impulso termina ahí (ver StartWall). El dash, la embestida y el empujón no.
        if (_dashActive || _rushActive || _knockbackActive) return false;

        if (p.BlockedByTags != null)
            foreach (EGameplayTag tag in p.BlockedByTags)
                if (tag != EGameplayTag.None && ASC.HasTag(tag)) return false;

        return true;
    }

    // ¿Esto cuenta como pared? Las invisibles (límites de la arena) no; tampoco nada que
    // tenga ASC (jugadores, NPCs, tótems), ni lo que tiene Rigidbody, ni los triggers.
    private static bool IsWallCollider(Collider col, WallMovementProfile p)
    {
        if (col == null || col.isTrigger) return false;

        int bit = 1 << col.gameObject.layer;
        if ((bit & p.WallLayers.value) == 0 || (bit & InvisibleWallsMask) != 0) return false;
        if (col.attachedRigidbody != null) return false;

        return col.GetComponentInParent<AbilitySystemComponent>() == null;
    }

    // ¿Es la misma pared de la que se acaba de soltar? Mismo collider, misma cara (menos
    // de ~30° de diferencia) y el mismo plano. Un mapa armado con UN solo collider tiene
    // muchas paredes: por eso no alcanza con comparar el collider.
    private bool IsLastWall(Collider col, Vector3 normal, Vector3 point)
    {
        if (!_hasLastWall || col != _lastWallCollider) return false;
        if (Vector3.Dot(normal, _lastWallNormal) < 0.85f) return false;
        return Mathf.Abs(Vector3.Dot(point - _lastWallPoint, _lastWallNormal)) < 0.5f;
    }

    // ¿Hay piso a menos de 'distance' debajo de los pies?
    private bool GroundWithin(float distance)
    {
        if (distance <= 0f) return false;

        Vector3 feet = transform.TransformPoint(characterController.center)
                     - Vector3.up * (characterController.height * 0.5f);
        int mask = ~((1 << 7) | InvisibleWallsMask);   // ni personajes ni paredes invisibles
        return Physics.Raycast(feet + Vector3.up * 0.05f, Vector3.down, distance + 0.05f, mask,
                               QueryTriggerInteraction.Ignore);
    }

    // Después de moverse en el aire: si chocó de costado con una pared válida yendo contra
    // ella, se engancha.
    private void TryStartWall(Vector3 inputVec)
    {
        WallMovementProfile p = WallProfile;
        if (p == null || !CanUseWalls(p)) return;

        // Recién teletransportado frente a una pared: se engancha solo, sin empujar.
        if (Time.time < _wallGraceUntil && TryGraceAttach(p)) return;

        if (_sideHitFrame != Time.frameCount) return;
        if (Mathf.Abs(_sideHitNormal.y) > p.MaxNormalY) return;

        Vector3 n = new Vector3(_sideHitNormal.x, 0f, _sideHitNormal.z);
        if (n.sqrMagnitude < 0.0001f) return;
        n.Normalize();

        // Tiene que ir CONTRA la pared con el WASD, no solo rozarla.
        if (inputVec.sqrMagnitude < 0.01f || Vector3.Dot(inputVec, -n) < p.MinInputIntoWall) return;

        if (!IsWallCollider(_sideHitCollider, p)) return;
        if (IsLastWall(_sideHitCollider, n, _sideHitPoint)) return;
        if (GroundWithin(p.MinHeightAboveGround)) return;

        StartWall(p, n, _sideHitCollider, _sideHitPoint);
    }

    // Engancharse a la pared que tiene enfrente justo después de un teletransporte (la
    // dirección es hacia donde llegó mirando). true = se enganchó.
    private bool TryGraceAttach(WallMovementProfile p)
    {
        if (_wallGraceDir.sqrMagnitude < 0.0001f) return false;

        Vector3 center = transform.TransformPoint(characterController.center);
        float   r      = characterController.radius * 0.5f;
        int     mask   = p.WallLayers.value & ~InvisibleWallsMask;
        if (!Physics.SphereCast(center, r, _wallGraceDir, out RaycastHit hit, r + 0.6f, mask,
                                QueryTriggerInteraction.Ignore))
            return false;
        if (Mathf.Abs(hit.normal.y) > p.MaxNormalY || !IsWallCollider(hit.collider, p)) return false;

        Vector3 n = new Vector3(hit.normal.x, 0f, hit.normal.z);
        if (n.sqrMagnitude < 0.0001f || GroundWithin(p.MinHeightAboveGround)) return false;

        _wallGraceUntil = 0f;
        StartWall(p, n.normalized, hit.collider, hit.point);
        return true;
    }

    private void StartWall(WallMovementProfile p, Vector3 n, Collider wall, Vector3 point)
    {
        // Enganchado a mitad de un salto de habilidad: el salto termina acá (el que lo lanzó
        // lo nota con IsOnWall y da el salto por terminado).
        if (_abilityVelocityActive) ClearAbilityVelocity();

        _wallActive        = true;
        _wallNormal        = n;
        _wallCollider      = wall;
        _wallPoint         = point;
        _wallStartTime     = Time.time;
        _wallAlongVelocity = Vector3.zero;
        _inertiaVelocity   = Vector3.zero;

        // Pegarse frena en seco; correr conserva parte de la subida (si llegó saltando sigue
        // subiendo un poco antes de empezar a bajar).
        _wallVertical = p.Mode == WallMovementProfile.EWallMode.Cling
            ? 0f
            : Mathf.Max(0f, verticalVelocity) * p.KeepUpwardSpeed;
        verticalVelocity = 0f;
    }

    // Avanza un frame enganchado. false = no está en ninguna pared (o se acaba de soltar
    // y el movimiento normal tiene que correr en este mismo frame).
    private bool TickWall()
    {
        if (!_wallActive) return false;

        WallMovementProfile p = WallProfile;
        if (p == null || characterController == null || !characterController.enabled || !CanUseWalls(p))
        {
            StopWall();
            return false;
        }

        // Espacio: salta hacia afuera de la pared.
        if (_input.Jump.WasPressedThisFrame())
        {
            WallJump(p);
            return false;
        }

        Vector2 move     = _input.MoveValue;
        Vector3 inputVec = GetWASDInputVector(move.x, move.y);

        // Empujar hacia AFUERA de la pared la suelta (cae).
        if (Vector3.Dot(inputVec, _wallNormal) > 0.6f)
        {
            StopWall();
            return false;
        }

        if (!isAttacking || IsHoldingAbility) FaceCameraForward();

        float elapsed = Time.time - _wallStartTime;
        Vector3 along = Vector3.zero;

        if (p.Mode == WallMovementProfile.EWallMode.Cling)
        {
            if (elapsed >= p.ClingTime + p.SlideTime)
            {
                StopWall();
                return false;
            }
            _wallVertical = elapsed < p.ClingTime ? 0f : -p.SlideSpeed;
        }
        else
        {
            float speed = ASC.GetAttributeValue(EAttributeType.MovSpeed);
            if (speed <= 0f) speed = 5f;

            Vector3 tangent = Vector3.Cross(Vector3.up, _wallNormal);   // a lo largo de la pared
            along = tangent * (Vector3.Dot(inputVec, tangent) * speed * p.RunSpeedMultiplier);
            _wallVertical = Mathf.Max(_wallVertical - p.SlideGravity * Time.deltaTime, -p.MaxSlideSpeed);
        }
        _wallAlongVelocity = along;

        // Un poco contra la pared, para seguir tocándola.
        Vector3 stick = -_wallNormal;
        CollisionFlags flags = characterController.Move((along + stick + Vector3.up * _wallVertical) * Time.deltaTime);

        // Llegó al piso: se terminó, y puede volver a pegarse a cualquier pared.
        if ((flags & CollisionFlags.Below) != 0 || characterController.isGrounded)
        {
            StopWall();
            _hasLastWall = false;
            return true;
        }

        // Un techo: deja de subir.
        if ((flags & CollisionFlags.Above) != 0 && _wallVertical > 0f) _wallVertical = 0f;

        // ¿Sigue la pared ahí? Si se acabó (o dio la vuelta a una esquina), se suelta.
        if (!ProbeWall(p)) StopWall();

        return true;
    }

    // Busca la pared justo detrás de la cápsula y actualiza su normal (sigue paredes
    // curvas). false = ya no hay pared a StickDistance.
    private bool ProbeWall(WallMovementProfile p)
    {
        Vector3 center = transform.TransformPoint(characterController.center);
        float   r      = characterController.radius * 0.5f;
        float   reach  = r + p.StickDistance + characterController.skinWidth;
        int     mask   = p.WallLayers.value & ~InvisibleWallsMask;

        if (!Physics.SphereCast(center, r, -_wallNormal, out RaycastHit hit, reach, mask, QueryTriggerInteraction.Ignore))
            return false;
        if (Mathf.Abs(hit.normal.y) > p.MaxNormalY) return false;
        if (hit.collider != _wallCollider && !IsWallCollider(hit.collider, p)) return false;

        Vector3 n = new Vector3(hit.normal.x, 0f, hit.normal.z);
        if (n.sqrMagnitude < 0.0001f) return false;
        n.Normalize();
        if (Vector3.Dot(n, _wallNormal) < 0.5f) return false;   // esquina de más de 60°

        _wallNormal   = n;
        _wallCollider = hit.collider;
        _wallPoint    = hit.point;
        return true;
    }

    // Salto desde la pared: hacia afuera y hacia arriba. "Afuera" es la normal de la pared
    // girada hacia donde mira la cámara, hasta JumpAimMaxAngle a cada lado (8 de octubre,
    // pedido de Gustavo): se puede elegir para qué lado sale, pero nunca derecho a donde
    // mira. El impulso horizontal va por la inercia —se disipa sola y se suma al WASD—, y un
    // rato el WASD no puede empujar de vuelta contra la pared.
    private void WallJump(WallMovementProfile p)
    {
        Vector3 away = _wallNormal;
        Camera cam = MainCamera;
        if (cam != null && p.JumpAimMaxAngle > 0f)
        {
            Vector3 look = cam.transform.forward;
            look.y = 0f;
            if (look.sqrMagnitude > 0.0001f)
            {
                float angle = Mathf.Clamp(Vector3.SignedAngle(_wallNormal, look.normalized, Vector3.up),
                                          -p.JumpAimMaxAngle, p.JumpAimMaxAngle);
                away = Quaternion.AngleAxis(angle, Vector3.up) * _wallNormal;
            }
        }
        Vector3 wallNormal = _wallNormal;
        StopWall();
        // Saltando (no soltándose solo) puede volver a esta misma pared: el bloqueo de abajo
        // ya le impide pegarse de rebote al apretar contra ella.
        if (p.SameWallAfterJump) _hasLastWall = false;

        verticalVelocity    = p.JumpOffUp;
        _inertiaVelocity    = away * p.JumpOffSpeed;
        _inertiaDamping     = Mathf.Max(0.01f, p.JumpOffDamping);
        _inertiaFromWall    = true;         // al tocar el piso se corta
        _wallJumpAwayNormal = wallNormal;   // el WASD no empuja de vuelta contra ESTA pared
        _wallJumpLockUntil  = Time.time + p.JumpOffLockTime;

        if (AudioLibrary.Instance != null) AudioManager.Play(AudioLibrary.Instance.Jump, transform.position);
        OnOwnerJumped();
    }

    // Suelta la pared: cae con lo que traía (y el Monje conserva lo que corría a lo largo).
    // Queda anotada como "la última": a esa no se vuelve a pegar hasta tocar el piso.
    private void StopWall()
    {
        if (!_wallActive) return;
        _wallActive = false;

        _hasLastWall      = true;
        _lastWallCollider = _wallCollider;
        _lastWallNormal   = _wallNormal;
        _lastWallPoint    = _wallPoint;

        verticalVelocity = _wallVertical;
        _inertiaVelocity = _wallAlongVelocity;
        _inertiaDamping  = 3f;
        _inertiaFromWall = true;
        _leanTargetLocal = Vector3.up;   // aturdido o empujado no pasa por UpdateAnimations
    }

    // Hacia dónde apunta el "arriba" del modelo, en el espacio de la raíz: inclinado hacia
    // afuera de la pared mientras está enganchado, derecho si no.
    private Vector3 WallLeanUpLocal()
    {
        WallMovementProfile p = WallProfile;
        if (!_wallActive || p == null || p.LeanAngle <= 0f) return Vector3.up;

        Vector3 up = (Vector3.up + _wallNormal * Mathf.Tan(p.LeanAngle * Mathf.Deg2Rad)).normalized;
        return transform.InverseTransformDirection(up);
    }

    // Lleva la inclinación del modelo hacia la pedida, suave. Corre en TODAS las copias
    // (desde LateUpdate): el dueño la calcula y los demás la reciben con la locomoción.
    private void TickModelLean()
    {
        _leanUpLocal = Vector3.Slerp(_leanUpLocal, _leanTargetLocal, 1f - Mathf.Exp(-12f * Time.deltaTime));
        if ((_leanUpLocal - _leanTargetLocal).sqrMagnitude < 0.00001f) _leanUpLocal = _leanTargetLocal;
    }

    // =========================================================
    // VUELO LIBRE
    // =========================================================

    // Lo llama NetworkAbilitySystemComponent.TargetStartFlight en el DUEÑO al activar un
    // GA_Flight: los números de ese vuelo y el impulso de despegue. El vuelo en sí dura lo
    // que dure Status_Flying.
    //
    // towardAim: el impulso sale hacia donde mira la cámara (solo el dueño la tiene, por eso se
    // resuelve acá). Desde el piso, por lo menos minUpAngle grados hacia arriba, así despega en
    // vez de chocar contra el suelo; en el aire, exacto a la mira (mirando abajo, en picada).
    public void StartFlight(float launchSpeed, float speedMultiplier, float forwardBoost, float verticalSpeed,
                            float idleSink, bool towardAim = false, float minUpAngle = 0f)
    {
        Vector3 launchDir = Vector3.up;
        Camera cam = MainCamera;
        if (towardAim && cam != null)
        {
            launchDir = cam.transform.forward;
            float minUp = Mathf.Clamp(minUpAngle, 0f, 89f);
            if (characterController.isGrounded && Mathf.Asin(Mathf.Clamp(launchDir.y, -1f, 1f)) * Mathf.Rad2Deg < minUp)
            {
                Vector3 flat = Vector3.ProjectOnPlane(launchDir, Vector3.up);
                if (flat.sqrMagnitude < 0.0001f) flat = transform.forward;
                flat.Normalize();
                launchDir = flat * Mathf.Cos(minUp * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(minUp * Mathf.Deg2Rad);
            }
        }

        _flightLaunch          = launchDir.normalized * Mathf.Max(0f, launchSpeed);
        _flightSpeedMultiplier = Mathf.Max(0.1f, speedMultiplier);
        _flightForwardBoost    = Mathf.Max(1f, forwardBoost);
        _flightVerticalSpeed   = Mathf.Max(0f, verticalSpeed);
        _flightIdleSink        = Mathf.Max(0f, idleSink);
        _flightSinkNow         = 0f;
        _flightFalling         = false;
    }

    // Detecta el fin del vuelo: si se acaba en el aire, cae lento hasta tocar el piso.
    private void UpdateFlightState()
    {
        bool flying = IsFlying;
        if (_wasFlying && !flying)
        {
            // En picada no cae lento: la picada sigue (el aviso de la picada y el fin del vuelo
            // pueden llegar en cualquier orden).
            _flightFalling = !_diving;
            _flightLaunch  = Vector3.zero;
            verticalVelocity = _diving ? -_diveSpeed : 0f;
        }
        _wasFlying = flying;

        if (_flightFalling && !flying && characterController.isGrounded) _flightFalling = false;
        if (_diving && !flying && characterController.isGrounded) _diving = false;
    }
    public void StartDive(float speed)
    {
        _diving          = true;
        _diveSpeed       = Mathf.Max(1f, Mathf.Abs(speed));
        _flightFalling   = false;
        _flightLaunch    = Vector3.zero;
        verticalVelocity = -_diveSpeed;
    }

    // Aturdido en pleno vuelo (o cayendo después de volar): no se queda colgado en el
    // aire, cae lento. El vuelo sigue cuando se le pasa el aturdido.
    private void SlowFall()
    {
        if (characterController == null || !characterController.enabled) return;

        if (characterController.isGrounded && verticalVelocity < 0) verticalVelocity = -2f;
        verticalVelocity += gravity * featherFallGravityScale * Time.deltaTime;
        if (featherFallMaxSpeed > 0f && verticalVelocity < -featherFallMaxSpeed)
            verticalVelocity = -featherFallMaxSpeed;

        _flightVerticalNow = verticalVelocity;
        characterController.Move(Vector3.up * verticalVelocity * Time.deltaTime);
    }

    // Teletransporta instantáneamente a una posición (blink de Golpe mortal).
    // Desactiva el CharacterController un instante para mover el transform sin
    // que el propio CC lo bloquee, y orienta al personaje hacia faceDir. Debe
    // correr en el proceso DUEÑO (el CC es client-authoritative) — lo dispara
    // NetworkAbilitySystemComponent.TargetExecuteTeleport.
    // turnCamera: girar también la cámara hacia faceDir. Sin eso el cuerpo gira pero
    // la cámara no, y como el personaje sigue a la cámara, termina mirando hacia donde
    // miraba antes del teletransporte.
    public void TeleportTo(Vector3 position, Vector3 faceDir, bool turnCamera = false)
    {
        characterController.enabled = false;
        transform.position          = position;
        characterController.enabled = true;
        if (_wallActive) StopWall();   // antes de poner en cero: StopWall deja la velocidad que traía
        verticalVelocity            = 0f;
        _knockbackActive            = false;
        _flightFalling              = false;
        _diving                     = false;
        if (_rushActive) StopRush(false);

        faceDir.y = 0;
        if (faceDir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(faceDir.normalized);

        if (turnCamera && _orbitCam != null) _orbitCam.FaceDirection(faceDir);

        // Si llegó en el aire frente a una pared (el Paso de las sombras apunta a paredes),
        // se engancha solo en los próximos frames. Ver TryGraceAttach.
        _wallGraceDir   = faceDir.sqrMagnitude > 0.0001f ? faceDir.normalized : Vector3.zero;
        _wallGraceUntil = Time.time + 0.3f;
    }

    // Excluye (o restaura) capas de colisión del CharacterController. Lo usa el
    // dash para atravesar a otros jugadores sin dejar de chocar con las paredes:
    // se excluye la capa de jugadores durante el impulso y se restaura al terminar
    // (ver GA_Dash / NetworkAbilitySystemComponent.TargetExecuteDash).
    public void SetCollisionExclusion(LayerMask mask, bool exclude)
    {
        if (exclude) characterController.excludeLayers |=  mask;
        else         characterController.excludeLayers &= ~mask;
    }
}
