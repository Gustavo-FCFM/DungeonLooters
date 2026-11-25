using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — ANIMACIÓN (y la locomoción en red)
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    // =========================================================
    // ANIMACIÓN
    // =========================================================

    // Actualiza los parámetros del Animator (velocidad, salto,
    // multiplicador de velocidad de ataque) según el movimiento actual.
    // Solo corre en el dueño (ver Update).
    void UpdateAnimations()
    {
        if (characterAnimator == null) return;

        Vector3 flatVel = new Vector3(
            characterController.velocity.x, 0,
            characterController.velocity.z);
        float speed = flatVel.magnitude;

        characterAnimator.SetFloat("Speed", speed, 0.1f, Time.deltaTime);

        // Strafe en 8 direcciones: tomamos la velocidad REAL del personaje y la
        // pasamos a su espacio LOCAL (relativo a hacia dónde mira: la cámara).
        // - MoveX  > 0 = se desliza a la derecha,  < 0 = a la izquierda
        // - MoveY  > 0 = hacia adelante,           < 0 = hacia atrás
        // La normalizamos por la velocidad máxima para que quede en ~[-1, 1] y
        // así encaje con las posiciones del blend tree 2D (Freeform Directional).
        // El body no rota con las WASD (siempre mira a la cámara), por eso el
        // blend tree es lo que muestra caminar de lado / de espaldas / diagonal.
        float maxSpeed = ASC.GetAttributeValue(EAttributeType.MovSpeed);
        if (maxSpeed <= 0) maxSpeed = 5f;
        Vector3 localVel = transform.InverseTransformDirection(flatVel) / maxSpeed;
        characterAnimator.SetFloat("MoveX", localVel.x, 0.1f, Time.deltaTime);
        characterAnimator.SetFloat("MoveY", localVel.z, 0.1f, Time.deltaTime);

        // IsJumping se sostiene en TRUE desde que arranca un salto de habilidad hasta
        // que el personaje despega de verdad.
        //
        // POR QUÉ: cuando la habilidad dispara la animación, el personaje TODAVÍA está
        // en el piso — el impulso lo aplica un TargetRpc que llega uno o más frames
        // después (ver GA_LeapAttack/ServerStartLeap). Sin esta retención, el Animator
        // entraba al bucle aéreo y la transición de salida (IsJumping == false) se
        // cumplía en el primer frame: solo se veía el aterrizaje, nunca el bucle.
        //
        // El latch se suelta en cuanto isGrounded da false una vez; de ahí en más manda
        // la física como siempre, así que el aterrizaje se detecta normal.
        // Volando nunca "aterriza": aunque roce el piso sigue en la pose del aire.
        bool airborne = !characterController.isGrounded || IsFlying;
        if (_leapTakeoffPending)
        {
            // Se suelta al despegar, o por TIEMPO: si el impulso nunca llega (RPC perdido,
            // salto bloqueado por un root), el personaje no puede quedarse clavado en el
            // bucle aereo para siempre.
            if (airborne || Time.time > _leapTakeoffDeadline) _leapTakeoffPending = false;
            else                                              airborne = true;
        }

        // Corriendo por una pared (Monje) se ven las piernas corriendo, no la pose del aire.
        WallMovementProfile wall = WallProfile;
        if (_wallActive && wall != null && wall.Mode == WallMovementProfile.EWallMode.Run) airborne = false;

        characterAnimator.SetBool("IsJumping", airborne);

        // VELOCIDAD VERTICAL, para que el Animator pueda distinguir SUBIR de CAER.
        //
        // Con IsJumping solo no alcanza: dice "esta en el aire", no si va para arriba o
        // para abajo. Un dash hacia arriba se queda con la pose de despegue todo el
        // trayecto, incluida la bajada.
        //
        // Durante un dash la componente vertical NO vive en verticalVelocity (se pone en
        // cero y la gravedad se apaga): vive en _dashVelocity. Por eso se reporta la que
        // de verdad esta moviendo al personaje en cada momento.
        float verticalNow = IsFlying ? _flightVerticalNow : _dashActive ? _dashVelocity.y
                          : _wallActive ? _wallVertical : verticalVelocity;

        if (!string.IsNullOrEmpty(VerticalSpeedParam))
            characterAnimator.SetFloat(VerticalSpeedParam, verticalNow);

        float spd = ASC.GetAttributeValue(EAttributeType.AtkSpeed);
        if (spd > 0)
            characterAnimator.SetFloat("AttackSpeedMult", 1f / spd);

        // Inclinado hacia afuera de la pared (lo aplica LateUpdate, suave).
        _leanTargetLocal = WallLeanUpLocal();

        // Y que lo vean los demás (ver "LOCOMOCIÓN EN RED").
        BroadcastLocomotion(speed, localVel.x, localVel.z, airborne, verticalNow,
                            _leanTargetLocal.x, _leanTargetLocal.z);
    }

    // =========================================================
    // LOCOMOCIÓN EN RED
    //
    // Caminar, correr, saltar y caer: los parámetros del Animator que los dibujan
    // (Speed, MoveX, MoveY, IsJumping, VerticalSpeed) los escribe UN SOLO peer —
    // UpdateAnimations en el dueño, BotController en el servidor si es un bot— y desde
    // acá viajan al resto.
    //
    // POR QUÉ NO LO HACE EL NetworkAnimator, que es para esto: el del prefab tiene su
    // campo Animator VACÍO y el Animator está en un hijo (el modelo), así que su
    // GetComponent no lo encuentra y el componente queda INERTE. Sin un solo warning:
    // FishNet lo tiene comentado. Probado el 22 de septiembre de 2026 con dos ventanas —
    // los demás jugadores se deslizaban en idle en vez de caminar.
    //
    // Se podría reflotar asignándole el Animator a mano, pero eso prende de golpe la
    // sincronización de TODOS los parámetros, encima de las RPC del NetworkASC que ya
    // mandan las animaciones de habilidad, golpe, stun y muerte. Mandar los cinco de
    // caminar nosotros es más barato y no toca nada de lo que ya funciona.
    //
    // Canal NO CONFIABLE a 15 por segundo, y solo cuando cambia algo: parado no gasta un
    // paquete, y uno perdido lo corrige el siguiente.
    // =========================================================

    // ¿Esta copia escribe su propio Animator, o lo recibe? El dueño manda en un jugador;
    // en un bot (que se spawnea sin dueño) manda el servidor. IsBot solo es true en el
    // servidor —es [NonSerialized] y lo pone ServerSpawnBot—, así que en un cliente esto
    // da false para todos menos para el personaje propio, que es justo lo que queremos.
    private bool DrivesOwnAnimator => IsOwner || (IsServerInitialized && IsBot);

    // leanX / leanZ: hacia dónde apunta el "arriba" del modelo en el espacio de la raíz (la
    // inclinación en una pared, ver "MOVIMIENTO EN PAREDES"). 0 y 0 = derecho. Viajan en un
    // byte cada uno (−1..1 → −127..127): sobra para unos grados de inclinación.
    public void BroadcastLocomotion(float speed, float moveX, float moveY, bool jumping, float verticalSpeed,
                                    float leanX = 0f, float leanZ = 0f)
    {
        if (!IsSpawned || !DrivesOwnAnimator) return;
        if (Time.time < _nextLocomotionSend) return;

        sbyte lx = LeanToByte(leanX), lz = LeanToByte(leanZ);

        // Nada que contar: quieto y en el piso no se manda nada.
        bool changed = !_locomotionSent
                    || jumping != _sentJumping
                    || lx != _sentLeanX || lz != _sentLeanZ
                    || Mathf.Abs(speed    - _sentSpeed)    > 0.05f
                    || Mathf.Abs(moveX    - _sentMoveX)    > 0.02f
                    || Mathf.Abs(moveY    - _sentMoveY)    > 0.02f
                    || Mathf.Abs(verticalSpeed - _sentVertical) > 0.25f;
        if (!changed) return;

        if (IsOwner) ServerSetLocomotion(speed, moveX, moveY, jumping, verticalSpeed, lx, lz, Channel.Unreliable);
        else         ObserversSetLocomotion(speed, moveX, moveY, jumping, verticalSpeed, lx, lz, Channel.Unreliable);

        _sentSpeed = speed; _sentMoveX = moveX; _sentMoveY = moveY;
        _sentJumping = jumping; _sentVertical = verticalSpeed;
        _sentLeanX = lx; _sentLeanZ = lz;
        _locomotionSent = true;
        _nextLocomotionSend = Time.time + 1f / LocomotionSendRate;
    }

    private static sbyte LeanToByte(float v) => (sbyte)Mathf.Clamp(Mathf.RoundToInt(v * 127f), -127, 127);

    // El "arriba" del modelo a partir de los dos bytes: la componente vertical se deduce.
    private static Vector3 LeanFromBytes(sbyte x, sbyte z)
    {
        if (x == 0 && z == 0) return Vector3.up;
        float fx = x / 127f, fz = z / 127f;
        return new Vector3(fx, Mathf.Sqrt(Mathf.Max(0f, 1f - fx * fx - fz * fz)), fz).normalized;
    }

    [ServerRpc]
    private void ServerSetLocomotion(float speed, float moveX, float moveY, bool jumping, float verticalSpeed,
                                     sbyte leanX, sbyte leanZ, Channel channel = Channel.Unreliable)
    {
        StoreLocomotion(speed, moveX, moveY, jumping, verticalSpeed, leanX, leanZ);
        ObserversSetLocomotion(speed, moveX, moveY, jumping, verticalSpeed, leanX, leanZ, Channel.Unreliable);
    }

    // Al dueño se lo salteamos: su Animator ya lo escribe él mismo.
    [ObserversRpc(ExcludeOwner = true)]
    private void ObserversSetLocomotion(float speed, float moveX, float moveY, bool jumping, float verticalSpeed,
                                        sbyte leanX, sbyte leanZ, Channel channel = Channel.Unreliable)
    {
        StoreLocomotion(speed, moveX, moveY, jumping, verticalSpeed, leanX, leanZ);
    }

    private void StoreLocomotion(float speed, float moveX, float moveY, bool jumping, float verticalSpeed,
                                 sbyte leanX, sbyte leanZ)
    {
        _netSpeed = speed; _netMoveX = moveX; _netMoveY = moveY;
        _netJumping = jumping; _netVertical = verticalSpeed;
        _netLeanX = leanX; _netLeanZ = leanZ;
    }

    // En las copias que NO escriben su Animator, lleva los valores recibidos al Animator
    // cada frame. El suavizado es el mismo que usa el dueño (SetFloat con damping), así
    // que el blend tree se mueve igual de suave y tapa el escalón entre paquetes.
    private void TickRemoteAnimations()
    {
        if (characterAnimator == null || !characterAnimator.enabled) return;

        characterAnimator.SetFloat("Speed", _netSpeed, 0.1f, Time.deltaTime);
        characterAnimator.SetFloat("MoveX", _netMoveX, 0.1f, Time.deltaTime);
        characterAnimator.SetFloat("MoveY", _netMoveY, 0.1f, Time.deltaTime);
        characterAnimator.SetBool("IsJumping", _netJumping);

        if (!string.IsNullOrEmpty(VerticalSpeedParam))
            characterAnimator.SetFloat(VerticalSpeedParam, _netVertical);

        // La inclinación en una pared (la aplica LateUpdate, suave).
        _leanTargetLocal = LeanFromBytes(_netLeanX, _netLeanZ);

        // La velocidad de ataque NO viaja acá: el atributo ya está sincronizado por el
        // NetworkASC, así que cada copia lo lee de su propio ASC. Sin esto, los ataques
        // de los demás se veían siempre a velocidad 1, ignorando sus buffs.
        float spd = ASC != null ? ASC.GetAttributeValue(EAttributeType.AtkSpeed) : 0f;
        if (spd > 0f) characterAnimator.SetFloat("AttackSpeedMult", 1f / spd);
    }

    // =========================================================
    // INCLINACIÓN DEL TORSO (AimPitch)
    //
    // El ángulo lo calcula UpperBodyAim en el DUEÑO (es el único que sabe hacia
    // dónde mira su cámara) y acá solo lo transportamos: dueño → servidor →
    // observadores.
    //
    // POR QUÉ NO VIAJA POR EL NetworkAnimator, como decía el plan original: el
    // NetworkAnimator del prefab tiene su campo Animator VACÍO y el Animator vive en
    // un hijo (el modelo), así que su GetComponent no lo encuentra y el componente
    // queda inerte — sin un solo warning, porque FishNet lo tiene comentado. Antes de
    // apoyarse en él habría que asignarlo a mano, y eso prendería la sincronización
    // de TODOS los parámetros de golpe, encima de las RPCs que ya los mandan.
    //
    // Va por canal NO CONFIABLE y a ~15 por segundo: es un valor que se reemplaza
    // solo, un paquete perdido lo arregla el siguiente. Un byte alcanza — el ángulo
    // está topeado a ±50°.
    // =========================================================

    // Último ángulo recibido, en grados. Lo leen las copias que NO son del dueño.
    public float NetworkAimPitch { get; private set; }

    public void SendAimPitch(float degrees)
    {
        if (!IsOwner) return;
        ServerSetAimPitch((sbyte)Mathf.Clamp(Mathf.RoundToInt(degrees), -127, 127), Channel.Unreliable);
    }

    [ServerRpc]
    private void ServerSetAimPitch(sbyte pitch, Channel channel = Channel.Unreliable)
    {
        NetworkAimPitch = pitch;          // la copia del servidor también lo dibuja
        ObserversSetAimPitch(pitch, Channel.Unreliable);
    }

    // Al dueño se lo salteamos: él ya tiene el valor de primera mano, sin el retardo
    // de la ida y vuelta.
    [ObserversRpc(ExcludeOwner = true)]
    private void ObserversSetAimPitch(sbyte pitch, Channel channel = Channel.Unreliable)
    {
        NetworkAimPitch = pitch;
    }

    [Server]
    public void ServerStreamAimFor(float seconds)
    {
        // El host lee su cámara en vivo; un bot no tiene dueño (su IA escribe la mira).
        if (IsOwner || !Owner.IsValid) return;
        TargetStreamAim(Owner, seconds);
    }

    [TargetRpc]
    private void TargetStreamAim(FishNet.Connection.NetworkConnection conn, float seconds)
    {
        _aimStreamUntil  = Time.time + seconds;
        _nextAimStreamAt = 0f;
    }

    private void TickAimStream()
    {
        if (Time.time >= _aimStreamUntil || Time.time < _nextAimStreamAt) return;
        _nextAimStreamAt = Time.time + 1f / AimStreamRate;
        ServerSetStreamedAim(GetAimPoint(), Channel.Unreliable);
    }

    [ServerRpc]
    private void ServerSetStreamedAim(Vector3 aimPoint, Channel channel = Channel.Unreliable)
    {
        NetworkAimPoint = aimPoint;
    }

    private void OnOwnerJumped()
    {
        if (!IsOwner || NetASC == null) return;

        GameplayEffectRegistry registry = GameplayEffectRegistry.Instance;
        if (registry == null) return;

        foreach (int index in NetASC.GetActiveEffectIndices())
        {
            GameplayEffect effect = registry.GetEffect(index);
            if (effect == null || effect.JumpVFX == null) continue;

            Vector3 at = transform.position;
            SpawnJumpVFX(effect, at);
            ServerJumpVFX(index, at);
            return;   // uno por salto: dos humos encimados no dicen nada más
        }
    }

    [ServerRpc]
    private void ServerJumpVFX(int effectIndex, Vector3 position)
    {
        // Solo si el servidor también le ve el efecto puesto.
        if (NetASC == null || !NetASC.NetActiveEffectDuration.ContainsKey(effectIndex)) return;
        ObserversJumpVFX(effectIndex, position);
    }

    [ObserversRpc(ExcludeOwner = true)]
    private void ObserversJumpVFX(int effectIndex, Vector3 position)
    {
        GameplayEffectRegistry registry = GameplayEffectRegistry.Instance;
        GameplayEffect effect = registry != null ? registry.GetEffect(effectIndex) : null;
        if (effect != null) SpawnJumpVFX(effect, position);
    }

    private static void SpawnJumpVFX(GameplayEffect effect, Vector3 position)
    {
        if (effect.JumpVFX == null) return;
        Destroy(Instantiate(effect.JumpVFX, position, Quaternion.identity), JumpVFXLifetime);
    }

    // (Re)arma la copia de runtime del Animator: parte del controller BASE y le
    // aplica encima los overrides de la clase. Se hace así —y no asignando el
    // override de clase directo— para poder intercambiar el clip de la ranura de
    // acción sin modificar ningún asset.
    //
    // Importante: la copia se construye sobre el controller BASE a propósito. Si la
    // construyéramos sobre el override de la clase, las claves de los overrides
    // serían los clips DE ESA CLASE (distintos en cada una) y el nombre de la ranura
    // dejaría de servir. Sobre el base, la clave es siempre el mismo placeholder.
    private void RebuildRuntimeAnimator(CharacterClassDefinition cls)
    {
        if (characterAnimator == null) return;

        AnimatorOverrideController built = BuildClassAnimator(cls, characterAnimator.runtimeAnimatorController);
        if (built == null) return;
        _runtimeAnimator = built;

        _currentActionClip = null; // la ranura vuelve al placeholder
        characterAnimator.runtimeAnimatorController = _runtimeAnimator;

        // Cambiar de controller devuelve las capas a su peso por defecto: la pose de la
        // mano secundaria se prende recién ahora (ver OffHandPose en la clase).
        _offHandPoseClass = cls;
        ApplyOffHandPoseWeight(characterAnimator, cls, asc: ASC);
        _offHandPoseOn = WantsOffHandPose(cls, ASC);
        PrepareStanceAnimator(cls);

        // ¿El controller base tiene realmente la ranura de acción? Si el estado
        // genérico todavía no se creó en el Animator, el esquema nuevo no puede
        // funcionar y hay que seguir con el viejo (ver ApplyAbilityAnimation).
        //
        // La búsqueda ignora mayúsculas/minúsculas por comodidad, pero guardamos el
        // nombre EXACTO del clip: el indexador del AnimatorOverrideController sí
        // distingue mayúsculas y, si no coincide al carácter, la asignación no hace
        // nada EN SILENCIO (el ataque se quedaría con el clip placeholder).
        var slots = new List<KeyValuePair<AnimationClip, AnimationClip>>();
        _runtimeAnimator.GetOverrides(slots);

        _actionSlotKey = ResolveSlotKey(slots, ActionClipSlotName);

        // Ranuras de mantener (escudo y compañía). Se resuelven una por una y por
        // separado a propósito: todas menos el bucle son opcionales, así que la que
        // no exista simplemente queda en null y se saltea al animar.
        _holdLoopKey   = ResolveSlotKey(slots, HoldLoopSlotName);
        _holdStartKey  = ResolveSlotKey(slots, HoldStartSlotName);
        _holdEndKey    = ResolveSlotKey(slots, HoldEndSlotName);
        _holdImpactKey = ResolveSlotKey(slots, HoldImpactSlotName);
        _currentHoldStart = _currentHoldLoop = _currentHoldEnd = _currentHoldImpact = null;

        // Ranuras del salto de habilidad (leap). Mismo criterio que las de mantener:
        // el bucle es lo imprescindible, el remate es opcional y queda en null si el
        // estado no existe en el Animator.
        _airStartKey = ResolveSlotKey(slots, AirStartSlotName);
        _airLoopKey = ResolveSlotKey(slots, AirLoopSlotName);
        _airLandKey = ResolveSlotKey(slots, AirLandSlotName);
        _currentAirStart = _currentAirLoop = _currentAirLand = null;

        _deathSlotKey = ResolveSlotKey(slots, DeathClipSlotName);
        ResolveHitTrigger();
        _lastStepPos = transform.position;
        _spawnedAt   = Time.time;
    }

    // =========================================================
    // MUERTE Y ATURDIMIENTO (animación / ragdoll)
    // =========================================================

    // La llama NetworkAbilitySystemComponent en todos los peers al morir, con la
    // dirección del golpe que mató (del atacante hacia acá). Ragdoll si hay; si no, la
    // animación.
    public void PlayDeath(Vector3 hitDirection)
    {
        if (_ragdoll == null) _ragdoll = GetComponent<RagdollController>();

        if (_ragdoll != null && _ragdoll.IsReady)
        {
            _ragdoll.Activate(hitDirection);

            // Solo el dueño tiene cámara propia; los demás ven el muñeco desde la suya.
            if (CameraFollowsRagdoll && _orbitCam != null) _orbitCam.FollowOverride = _ragdoll.Hips;
            return;
        }

        if (characterAnimator == null) return;

        // El brazo del libro cae con el resto del cuerpo, no se queda sosteniéndolo.
        ApplyOffHandPoseWeight(characterAnimator, _offHandPoseClass, off: true);

        if (!string.IsNullOrEmpty(_deathSlotKey) && DeathClips != null && DeathClips.Length > 0)
        {
            AnimationClip clip = DeathClips[Random.Range(0, DeathClips.Length)];
            if (clip != null && _runtimeAnimator != null) _runtimeAnimator[_deathSlotKey] = clip;
        }

        if (_hasDeadParam) characterAnimator.SetBool(DeadParam, true);
    }

    public void PlayRevive()
    {
        if (_orbitCam != null) _orbitCam.FollowOverride = null;
        if (_ragdoll != null && _ragdoll.IsActive) _ragdoll.Deactivate();
        if (characterAnimator != null && _hasDeadParam) characterAnimator.SetBool(DeadParam, false);
        ApplyOffHandPoseWeight(characterAnimator, _offHandPoseClass, asc: ASC);
    }

    // El aturdido se lee del tag en cada copia: los tags viajan sincronizados, así que
    // no hace falta ningún RPC para que todos vean el bucle.
    private void TickStunAnimation()
    {
        if (!_hasStunnedParam || characterAnimator == null || ASC == null) return;

        bool stunned = ASC.HasTag(EGameplayTag.State_Stunned) && !ASC.HasTag(EGameplayTag.State_Dead);
        if (characterAnimator.GetBool(StunnedParam) != stunned) characterAnimator.SetBool(StunnedParam, stunned);

        // Los BRAZOS también. El aturdido vive en la capa Base, y la capa del torso
        // (UpperBody: el ataque a medias, el escudo levantado, la reacción al golpe) se
        // dibuja encima: con un escudo arriba o un golpe en curso, el aturdido solo se veía
        // en las piernas, y en las clases de escudo parecía que no lo tenían. Mientras dura,
        // esa capa se apaga (la del brazo del escudo la apaga TickStanceVisuals).
        int upper = characterAnimator.GetLayerIndex(UpperBodyLayerName);
        if (upper < 0) return;

        if (stunned)
        {
            if (characterAnimator.GetLayerWeight(upper) > 0f) characterAnimator.SetLayerWeight(upper, 0f);
            _stunHidUpperBody = true;
        }
        else if (_stunHidUpperBody)
        {
            characterAnimator.SetLayerWeight(upper, 1f);
            _stunHidUpperBody = false;
        }
    }

    // ¿El Animator tiene un parámetro con ese nombre y tipo? Se mira una vez, al armar
    // el controller en runtime, y no cada golpe.
    private bool HasAnimatorParam(string name, AnimatorControllerParameterType type)
    {
        if (characterAnimator == null || string.IsNullOrEmpty(name)) return false;
        foreach (AnimatorControllerParameter p in characterAnimator.parameters)
            if (p.type == type && p.name == name) return true;
        return false;
    }
}
