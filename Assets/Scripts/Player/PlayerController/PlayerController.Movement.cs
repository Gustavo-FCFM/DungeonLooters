using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — MOVIMIENTO, IMPULSO Y EMBESTIDA
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    // =========================================================
    // MOVIMIENTO
    // =========================================================

    // Lo único que se mueve con el input bloqueado: la gravedad. Antes Update salía
    // antes de HandleMovementInput, así que un menú modal abierto en pleno salto
    // dejaba al personaje SUSPENDIDO en el aire — y un jugador flotando con un menú
    // abierto no lo podía bajar nadie. Acá cae, se planta, y la animación pasa a
    // idle. Sin avance horizontal: cualquier inercia de habilidad se corta.
    private void SettleWithoutInput()
    {
        if (characterController == null || !characterController.enabled) return;

        if (_wallActive) StopWall();
        _inertiaVelocity = Vector3.zero;
        _knockbackActive = false;

        if (characterController.isGrounded && verticalVelocity < 0) verticalVelocity = -2f;
        else                                                        verticalVelocity += gravity * Time.deltaTime;

        characterController.Move(Vector3.up * verticalVelocity * Time.deltaTime);
        UpdateAnimations();
    }

    // Mueve el CharacterController según el input WASD (o el impulso de
    // una habilidad, si hay uno activo) más gravedad. Es el único lugar
    // que llama Move() — solo corre para el dueño (ver Update).
    private void HandleMovementInput()
    {
        if (ASC.HasTag(EGameplayTag.State_Rooted))
        {
            // Enraizado en pleno vuelo: queda quieto en el aire.
            if (IsFlying) { verticalVelocity = 0f; _flightVerticalNow = 0f; return; }

            _inertiaVelocity  = Vector3.zero;
            verticalVelocity += gravity * Time.deltaTime;
            characterController.Move(Vector3.up * verticalVelocity * Time.deltaTime);
            return;
        }

        if (characterController.isGrounded && verticalVelocity < 0)
            verticalVelocity = -2f;

        float baseSpeed = ASC.GetAttributeValue(EAttributeType.MovSpeed);
        if (baseSpeed <= 0) baseSpeed = 5f;

        // Con la rueda abierta se sigue caminando: con control se elige con el stick DERECHO
        // (ver UI_RadialMenu), como con el mouse mientras el WASD mueve.
        Vector2 moveInput = _input.MoveValue;
        Vector3 inputVec  = GetWASDInputVector(moveInput.x, moveInput.y);

        // Third-person shooter: el cuerpo SIEMPRE mira hacia donde apunta la
        // cámara, sin importar en qué dirección apunten las WASD. El
        // desplazamiento en 8 direcciones lo resuelve el blend tree 2D de la
        // animación (parámetros MoveX/MoveY, ver UpdateAnimations), NO la
        // rotación del personaje. Mientras se ejecuta una habilidad no tocamos
        // la rotación: varias (salto, dash, ataques) orientan al personaje a
        // propósito hacia el punto de mira.
        //
        // EXCEPCIÓN: las habilidades de MANTENER (escudo). Ahí isAttacking está en
        // true todo el rato que sostenés el botón, y sin esta excepción el cuerpo
        // quedaba clavado mirando a donde apuntabas al levantar el escudo — o sea
        // que no podías APUNTARLO, que es justamente de lo que se trata.
        if (!isAttacking || IsHoldingAbility) FaceCameraForward();

        // Dash 3D (Dash siniestro): movimiento recto en la dirección apuntada,
        // incluyendo arriba/abajo, SIN gravedad durante el impulso. Se atenúa
        // solo (inercia) hasta que NetworkASC.DashRoutine llama ClearDashVelocity.
        // Sigue chocando con paredes/entorno (el CharacterController colisiona;
        // solo se excluye la capa de jugadores en GA_Dash).
        if (_dashActive)
        {
            _dashVelocity   = Vector3.Lerp(_dashVelocity, Vector3.zero, Time.deltaTime);
            Vector3 steer   = inputVec * baseSpeed;
            Vector3 dashMove = _dashVelocity + new Vector3(steer.x, 0, steer.z);
            characterController.Move(dashMove * Time.deltaTime);
            return;
        }

        // Vuelo libre (ver VUELO LIBRE): reemplaza al caminar, al salto y a la gravedad.
        if (IsFlying)
        {
            Camera cam = MainCamera;
            Vector3 dir = Vector3.zero;
            if (cam != null)
            {
                Transform camT = cam.transform;
                dir = camT.forward * moveInput.y + camT.right * moveInput.x;
                if (dir.sqrMagnitude > 1f) dir.Normalize();
            }

            float flySpeed = baseSpeed * _flightSpeedMultiplier;
            if (moveInput.y > 0.5f && Mathf.Abs(moveInput.x) < 0.3f) flySpeed *= _flightForwardBoost;

            float climb = 0f;
            if (_input.Jump.IsPressed()) climb += 1f;
            if (_input.DescendHeld)      climb -= 1f;

            // Quieto en el aire (sin WASD ni subir/bajar) va cayendo despacio, cada vez un poco
            // más rápido hasta _flightIdleSink: para no caer hay que moverse. Moverse lo corta.
            bool idle = dir.sqrMagnitude < 0.01f && Mathf.Approximately(climb, 0f);
            _flightSinkNow = idle ? Mathf.MoveTowards(_flightSinkNow, _flightIdleSink, _flightIdleSink * Time.deltaTime) : 0f;

            _flightLaunch *= Mathf.Exp(-3f * Time.deltaTime);
            if (_flightLaunch.sqrMagnitude < 0.0025f) _flightLaunch = Vector3.zero;

            Vector3 flyVelocity = dir * flySpeed + _flightLaunch +
                                  Vector3.up * (climb * _flightVerticalSpeed - _flightSinkNow);
            _flightVerticalNow = flyVelocity.y;
            verticalVelocity   = 0f;
            _inertiaVelocity   = Vector3.zero;

            characterController.Move(flyVelocity * Time.deltaTime);
            return;
        }

        // Inercia que dejó el último dash: se disipa sola y se SUMA al movimiento
        // normal, así que el jugador nunca pierde el control mientras se le va.
        if (_inertiaVelocity.sqrMagnitude > 0.01f)
            _inertiaVelocity *= Mathf.Exp(-_inertiaDamping * Time.deltaTime);
        else
            _inertiaVelocity = Vector3.zero;

        // Caída de pluma (ult del Ángel vengador): cae más lento y puede volver a
        // impulsarse en el aire tantas veces como quiera, batiendo las alas. No es
        // volar libre: no hay ascenso sostenido, solo aleteos contra una gravedad floja.
        bool feathered = ASC.HasTag(EGameplayTag.Status_Feather_Fall);

        Vector3 horizontal;

        if (_abilityVelocityActive)
        {
            _abilityVelocity = Vector3.Lerp(_abilityVelocity, Vector3.zero, Time.deltaTime);
            horizontal        = _abilityVelocity + inputVec * baseSpeed;
        }
        else
        {
            // Recién saltó de una pared: el WASD no puede empujar de vuelta contra ella (de
            // costado sí). Sin esto, con W apretado hacia la pared el salto se anulaba.
            if (Time.time < _wallJumpLockUntil)
            {
                float into = Vector3.Dot(inputVec, -_wallJumpAwayNormal);
                if (into > 0f) inputVec += _wallJumpAwayNormal * into;
            }

            horizontal = inputVec * baseSpeed + _inertiaVelocity;

            // En el piso es un salto normal; en el aire, solo con la caída de pluma
            // activa, y con el impulso más chico del aleteo.
            if (_input.Jump.WasPressedThisFrame() && (characterController.isGrounded || feathered))
            {
                bool fromGround = characterController.isGrounded;
                verticalVelocity = fromGround ? jumpForce : flapForce;
                if (AudioLibrary.Instance != null) AudioManager.Play(AudioLibrary.Instance.Jump, transform.position);
                if (fromGround) OnOwnerJumped();
            }
        }

        // Después de volar también cae lento (sin aleteos: eso es solo de la caída de pluma). En
        // PICADA no: cae rápido hasta el piso (ver StartDive).
        bool slowFall = !_diving && (feathered || _flightFalling);
        if (_diving) verticalVelocity = Mathf.Min(verticalVelocity, -_diveSpeed);
        verticalVelocity += gravity * (slowFall ? featherFallGravityScale : 1f) * Time.deltaTime;

        if (slowFall && featherFallMaxSpeed > 0f && verticalVelocity < -featherFallMaxSpeed)
            verticalVelocity = -featherFallMaxSpeed;

        Vector3 finalMove = new Vector3(horizontal.x, 0, horizontal.z)
                            + Vector3.up * verticalVelocity;
        characterController.Move(finalMove * Time.deltaTime);

        // En el aire, yendo contra una pared: el Pícaro se pega, el Monje corre por ella.
        // Tocar el piso habilita volver a pegarse a cualquier pared, y corta el impulso que
        // dejó la pared (ver _inertiaFromWall).
        if (characterController.isGrounded)
        {
            _hasLastWall = false;
            if (_inertiaFromWall)
            {
                _inertiaFromWall = false;
                _inertiaVelocity = Vector3.zero;
            }
        }
        else TryStartWall(inputVec);   // también en medio de un salto de habilidad (la Retirada)
    }
    private Camera MainCamera
    {
        get
        {
            if (_mainCamera == null || !_mainCamera.isActiveAndEnabled) _mainCamera = Camera.main;
            return _mainCamera;
        }
    }

    // Convierte el input horizontal/vertical crudo en un vector de mundo
    // relativo a hacia dónde mira la cámara del dueño.
    private Vector3 GetWASDInputVector(float h, float v)
    {
        Camera cam = MainCamera;
        if (cam == null) return Vector3.zero;

        Transform camT = cam.transform;
        Vector3 f = camT.forward; f.y = 0; f.Normalize();
        Vector3 r = camT.right;   r.y = 0; r.Normalize();
        return (f * v + r * h).normalized;
    }

    // Orienta el cuerpo hacia el frente horizontal (yaw) de la cámara. Es el
    // núcleo del giro estilo third-person shooter: el personaje siempre "ve"
    // hacia donde apunta la cámara y las WASD solo deciden en qué dirección se
    // desliza (lo anima el blend tree 2D de strafe). Solo yaw — nunca inclina
    // el cuerpo hacia arriba/abajo.
    //
    // Si en el futuro querés que el cuerpo gire SOLO cuando te movés o apuntás
    // (y no mientras estás quieto orbitando la cámara), envolvé la llamada en
    // HandleMovementInput con algo como: if (inputVec != Vector3.zero) ...
    private void FaceCameraForward()
    {
        Camera cam = MainCamera;
        if (cam == null) return;
        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) return;

        Quaternion target = Quaternion.LookRotation(fwd.normalized);
        transform.rotation = Quaternion.Slerp(
            transform.rotation, target, aimTurnSpeed * Time.deltaTime);
    }

    // =========================================================
    // IMPULSO DE HABILIDAD (genérico — salto, dash, empujón, etc.)
    //
    // Este script NO sabe qué habilidad lo usa. GA_LeapAttack (vía
    // NetworkAbilitySystemComponent.TargetExecuteLeap, que corre en la
    // conexión DUEÑA) le calcula la dirección con SU cámara y llama acá.
    // Quien llama es también responsable de detectar el aterrizaje
    // (IsGrounded) y de avisarle al servidor cuándo resolver cualquier
    // efecto asociado — este script solo mueve el CharacterController.
    // =========================================================

    // True si el CharacterController está tocando el piso ahora mismo.
    public bool IsGrounded => characterController.isGrounded;

    // True mientras está enganchado a una pared (pegado o corriendo).
    public bool IsOnWall => _wallActive;

    // Toma control temporal del movimiento: aplica un impulso horizontal
    // (que se va atenuando solo) y uno vertical instantáneo. Funciona esté o no
    // en el piso (se puede usar en el aire — ej. el Salto Furioso o el dash).
    public bool ApplyAbilityVelocity(Vector3 horizontalVelocity, float verticalImpulse)
    {
        if (_wallActive) StopWall();   // un salto de habilidad lo despega de la pared
        _abilityVelocity       = horizontalVelocity;
        verticalVelocity       = verticalImpulse;
        _abilityVelocityActive = true;

        Vector3 flatDir = new Vector3(horizontalVelocity.x, 0, horizontalVelocity.z);
        // Encara hacia donde salta, salvo que salte hacia ATRÁS (la Retirada): ahí sigue mirando
        // al frente.
        if (flatDir != Vector3.zero && Vector3.Dot(flatDir, transform.forward) >= 0f)
            transform.forward = flatDir.normalized;

        return true;
    }

    // Devuelve el movimiento al control normal del input del jugador.
    public void ClearAbilityVelocity()
    {
        _abilityVelocityActive = false;
        _abilityVelocity       = Vector3.zero;
    }

    // Toma control del movimiento para un DASH en 3D: impulso recto en cualquier
    // dirección (incluida la vertical) que se atenúa solo, SIN gravedad mientras
    // dura. Funciona en el aire. El fin lo maneja quien lo disparó llamando
    // ClearDashVelocity (ver NetworkAbilitySystemComponent.DashRoutine).
    public void ApplyDashVelocity(Vector3 velocity, bool faceVelocity = true)
    {
        if (_wallActive) StopWall();   // el dash sale desde la pared y la suelta
        _dashVelocity    = velocity;
        _dashActive      = true;
        _inertiaVelocity = Vector3.zero;   // el impulso nuevo pisa el resto del anterior
        // Durante el dash no hay gravedad; al terminar, la caída arranca de 0.
        verticalVelocity = 0f;

        // En la esquiva direccional (faceVelocity=false) NO giramos el cuerpo:
        // mantiene la orientación hacia la mira que RequestAbility ya fijó, así la
        // esquiva lateral/hacia atrás no te da vuelta.
        if (!faceVelocity) return;

        Vector3 flatDir = new Vector3(velocity.x, 0, velocity.z);
        if (flatDir.sqrMagnitude > 0.0001f) transform.forward = flatDir.normalized;
    }

    // Termina el dash 3D y devuelve el movimiento al control normal (con gravedad).
    //
    // retainPercent conserva una fracción de la velocidad que traía el dash en vez de
    // frenar en seco: la parte HORIZONTAL pasa a _inertiaVelocity (se disipa sola,
    // sumándose al input del jugador) y la VERTICAL vuelve a verticalVelocity, así un
    // dash hacia arriba sigue subiendo un poco antes de caer en vez de desplomarse
    // apenas termina el impulso.
    //
    // En 0 (el default) el comportamiento es exactamente el de antes.
    public void ClearDashVelocity(float retainPercent = 0f, float damping = 3f)
    {
        Vector3 leftover = _dashVelocity * Mathf.Clamp01(retainPercent);

        _dashActive   = false;
        _dashVelocity = Vector3.zero;

        _inertiaDamping  = Mathf.Max(0.01f, damping);
        _inertiaVelocity = new Vector3(leftover.x, 0f, leftover.z);
        _inertiaFromWall = false;
        verticalVelocity = leftover.y;
    }

    // Repeler / atraer: lo llama NetworkAbilitySystemComponent.TargetExecuteKnockback en
    // el DUEÑO. displacement es el recorrido horizontal entero; upVelocity lo levanta un
    // poco del piso. Pisa cualquier impulso que traía (dash, salto, inercia).
    public void ApplyKnockback(Vector3 displacement, float duration, float upVelocity)
    {
        if (_rushActive) StopRush(true);   // empujado, la embestida se corta
        if (_wallActive) StopWall();       // y se cae de la pared

        _knockbackDisplacement = new Vector3(displacement.x, 0f, displacement.z);
        _knockbackDuration     = Mathf.Max(0.05f, duration);
        _knockbackElapsed      = 0f;
        _knockbackProgress     = 0f;
        _knockbackActive       = true;

        _inertiaVelocity = Vector3.zero;
        _abilityVelocity = Vector3.zero;
        _dashVelocity    = Vector3.zero;
        if (upVelocity > 0f) verticalVelocity = upVelocity;
    }

    // Arranca rápido y frena al final (la fracción del recorrido hecha a la fracción t
    // del tiempo). La comparte el empuje de los NPCs en el servidor.
    public static float KnockbackEase(float t) => 1f - (1f - t) * (1f - t);

    // Avanza el empuje un frame. Choca con las paredes (CharacterController) y sigue
    // cayendo con gravedad. false = no hay empuje en curso.
    private bool TickKnockback()
    {
        if (!_knockbackActive) return false;
        if (characterController == null || !characterController.enabled)
        {
            _knockbackActive = false;
            return false;
        }

        _knockbackElapsed += Time.deltaTime;
        float t     = Mathf.Min(1f, _knockbackElapsed / _knockbackDuration);
        float eased = KnockbackEase(t);
        Vector3 step = _knockbackDisplacement * (eased - _knockbackProgress);
        _knockbackProgress = eased;

        // Parte del control normal encima del empuje: puede corregir, no anularlo.
        Vector3 steer = Vector3.zero;
        if (!ASC.HasTag(EGameplayTag.State_Stunned) && !ASC.HasTag(EGameplayTag.State_Rooted))
        {
            float speed = ASC.GetAttributeValue(EAttributeType.MovSpeed);
            if (speed <= 0f) speed = 5f;
            Vector2 move = _input.MoveValue;
            steer = GetWASDInputVector(move.x, move.y) * speed * knockbackSteerControl;
            if (!isAttacking || IsHoldingAbility) FaceCameraForward();
        }

        if (IsFlying) verticalVelocity = 0f;   // volando, el empuje no lo hace caer
        else
        {
            if (characterController.isGrounded && verticalVelocity < 0) verticalVelocity = -2f;
            verticalVelocity += gravity * Time.deltaTime;
        }

        characterController.Move(step + (steer + Vector3.up * verticalVelocity) * Time.deltaTime);

        if (t >= 1f) _knockbackActive = false;
        return true;
    }

    // =========================================================
    // EMBESTIDA (GA_RushAttack)
    // =========================================================

    // Lo llama NetworkAbilitySystemComponent.TargetStartRush en el DUEÑO. velocity es la
    // entera (dirección × velocidad); trae componente vertical si la habilidad apunta arriba
    // o abajo (GA_RushAttack.AimVertical).
    public void StartRush(Vector3 velocity, float duration, float strafeSpeed, int excludeMask, bool cancelable)
    {
        if (_rushActive) StopRush(false);
        if (_wallActive) StopWall();   // la patada sale desde la pared y la suelta

        _rushVelocity    = velocity;
        _rushEndsAt      = Time.time + Mathf.Max(0.05f, duration);
        _rushStrafeSpeed = strafeSpeed;
        _rushExcludeMask = excludeMask;
        _rushCancelable  = cancelable;
        _rushSlot        = _runningSlot != EAbilityInput.None ? _runningSlot : EAbilityInput.Movement;
        _rushActive      = true;

        verticalVelocity = 0f;
        _inertiaVelocity = Vector3.zero;
        SetCollisionExclusion(excludeMask, true);

        Vector3 face = new Vector3(_rushVelocity.x, 0f, _rushVelocity.z);
        if (face.sqrMagnitude > 0.0001f) transform.forward = face.normalized;
    }

    // Termina la embestida. notifyServer: la cortó el dueño (volvió a apretar o chocó con
    // una pared), así el servidor deja de buscar a quién golpear.
    public void StopRush(bool notifyServer)
    {
        if (!_rushActive) return;
        _rushActive = false;

        SetCollisionExclusion(_rushExcludeMask, false);
        _inertiaVelocity = new Vector3(_rushVelocity.x, 0f, _rushVelocity.z) * 0.2f;   // no frena en seco
        _inertiaDamping  = 4f;
        _inertiaFromWall = false;

        if (notifyServer && NetASC != null) NetASC.ServerRequestStopRush();
    }

    // Avanza la embestida un frame. false = no hay ninguna en curso.
    private bool TickRush()
    {
        if (!_rushActive) return false;

        if (Time.time >= _rushEndsAt || characterController == null || !characterController.enabled)
        {
            StopRush(false);
            return false;
        }

        // Volver a apretar el mismo botón la corta (por si iba derecho a un precipicio).
        UnityEngine.InputSystem.InputAction action = ActionForSlot(_rushSlot);
        if (_rushCancelable && action != null && action.WasPressedThisFrame())
        {
            StopRush(true);
            return true;
        }

        // Izquierda / derecha la corren de costado, sin girarla: sigue mirando y avanzando
        // hacia el mismo lado.
        Vector3 forward = new Vector3(_rushVelocity.x, 0f, _rushVelocity.z);
        Vector3 step = _rushVelocity;
        if (forward.sqrMagnitude > 0.0001f)
        {
            forward.Normalize();
            transform.forward = forward;
            if (_rushStrafeSpeed > 0f)
                step += Vector3.Cross(Vector3.up, forward) * (_input.MoveValue.x * _rushStrafeSpeed);
        }

        // Sin gravedad mientras dura, como el dash. Una pared la termina.
        CollisionFlags flags = characterController.Move(step * Time.deltaTime);
        if ((flags & CollisionFlags.Sides) != 0) StopRush(true);

        return true;
    }

    // La acción de input de cada ranura (para cortar la embestida con su mismo botón).
    private UnityEngine.InputSystem.InputAction ActionForSlot(EAbilityInput slot)
    {
        switch (slot)
        {
            case EAbilityInput.Movement:        return _input.MovementAbility;
            case EAbilityInput.Action1:         return _input.Ability1;
            case EAbilityInput.Action2:         return _input.Ability2;
            case EAbilityInput.Action3:         return _input.Ability3;
            case EAbilityInput.SecondaryAttack: return _input.Secondary;
            case EAbilityInput.PrimaryAttack:   return _input.PrimaryAttack;
            default:                            return null;
        }
    }
}
