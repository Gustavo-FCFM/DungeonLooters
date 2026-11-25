using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — HABILIDADES DE MANTENER (ranuras y animación)
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{
    private bool HasActionSlot => !string.IsNullOrEmpty(_actionSlotKey);

    // El BUCLE es lo único imprescindible: levantar, bajar y la reacción al golpe son
    // estados opcionales del Animator, y sin ellos el mantenido funciona igual.
    private bool HasHoldSlots   => !string.IsNullOrEmpty(_holdLoopKey);
    private bool HasHoldImpact  => !string.IsNullOrEmpty(_holdImpactKey);

    // El BUCLE es lo único imprescindible: el remate del aterrizaje es opcional.
    private bool HasAirSlots => !string.IsNullOrEmpty(_airLoopKey);

    private bool IsFlying => ASC != null && ASC.HasTag(EGameplayTag.Status_Flying);

    // Para los menús de clase (UI_ClassMenu): ¿vuela?, y aterrizar. anyFlight = también la
    // levitación de la Sobrecarga; sin eso, solo el vuelo del Shift (ver ServerRequestLand).
    public bool IsFlyingNow => IsFlying;
    public void RequestLand(bool anyFlight)
    {
        if (NetASC != null && IsFlying) NetASC.ServerRequestLand(anyFlight);
    }

    // PAREDES (WallMovementProfile de la clase): el Pícaro se pega y salta, el Monje corre
    // por ellas. Corre en el DUEÑO, como todo el movimiento. Ver "MOVIMIENTO EN PAREDES".
    private WallMovementProfile WallProfile => CurrentClassDef != null ? CurrentClassDef.WallMovement : null;

    // Desde dónde medir lo apuntado: donde estaba al pedirla (o donde está, si no hubo pedido).
    public Vector3 AimFromPosition => NetworkRequestPosition != Vector3.zero ? NetworkRequestPosition : transform.position;

    public int Kills   => _netKills.Value;
    public int Deaths  => _netDeaths.Value;
    public int Assists => _netAssists.Value;

    public int DamageDone  => _netDamageDone.Value;
    public int HealingDone => _netHealingDone.Value;

    // 0 a 100, o -1 si no tiene definitiva.
    public int UltimatePercent => _netUltimatePercent.Value == 255 ? -1 : _netUltimatePercent.Value;

    private void ServerTickUltimatePercent()
    {
        if (Time.time < _nextUltimatePercentAt || ASC == null) return;
        _nextUltimatePercentAt = Time.time + 0.25f;
        byte percent = AbilityR == null ? (byte)255 : (byte)Mathf.FloorToInt(ASC.GetUltimateCharge() * 100f);
        if (_netUltimatePercent.Value != percent) _netUltimatePercent.Value = percent;
    }

    // La cámara de la sala (la que gira en el menú). Solo las activas: una ya apagada es de
    // otro personaje tuyo que todavía no se fue, y ese la vuelve a prender al irse.
    private static GameObject FindLobbyCamera()
    {
        MenuOrbitCamera orbit = FindFirstObjectByType<MenuOrbitCamera>();
        if (orbit != null) return orbit.gameObject;
        return Camera.main != null ? Camera.main.gameObject : null;
    }

    private UI_GroundTargetIndicator GroundIndicator
    {
        get
        {
            UI_GroundTargetIndicator indicator = UI_GroundTargetIndicator.Instance;
            if (indicator == null && !_warnedNoGroundIndicator)
            {
                _warnedNoGroundIndicator = true;
                Debug.LogWarning("[PlayerController] El prefab de la cámara no tiene el marcador del piso " +
                                 "(UI_GroundTargetIndicator): las habilidades de zona se apuntan sin marcador.");
            }
            return indicator;
        }
    }

    // True mientras el dueño sostiene una habilidad de mantener.
    public bool IsHoldingAbility => _holdAbility != null;

    // Poniendo algo en el piso (el marcador a la vista, antes de soltar).
    public bool IsPlacingOnGround => _groundTargetAbility != null;

    // Lo que sostiene y desde cuándo (Time.time al apretar), para la barra de carga
    // (UI_ChargeBar). Se cuenta en el dueño: el servidor arranca casi a la vez.
    public GameplayAbility HeldAbility => _holdAbility;
    public float HoldStartedAt { get; private set; }

    // True mientras se mantiene apretada una habilidad que se apunta antes de
    // lanzarse (menú radial o zona en el suelo), o una de MANTENER. En ese estado
    // isAttacking está en true a propósito, pero HAY que seguir leyendo el input
    // para detectar cuándo se suelta el botón — y el watchdog no debe contar (un
    // escudo puede sostenerse mucho más que MaxAttackSeconds).
    private bool IsAimingAbility => isRadialMenuOpen || _groundTargetAbility != null || IsHoldingAbility;
    // La usan los menús modales para tomar/soltar el control del jugador.
    public void SetInputLocked(bool locked)
    {
        // Al ENTRAR a un menú se baja cualquier mantenido en curso: con el input
        // bloqueado nunca se detectaría que soltaste el botón, y el jugador saldría
        // del menú trabado con el escudo arriba. Ver CancelHeldAbility.
        if (locked) CancelHeldAbility();

        _inputLocked = locked;
    }

    // =========================================================
    // ANIMACIÓN DE MANTENER (IHoldAbility)
    // =========================================================

    // Arranca la animación de mantener de una habilidad en la copia DUEÑA
    // (levantar → bucle). Entrada que usan las habilidades y la predicción del input.
    public void PlayHoldAnimation(GameplayAbility ability)
    {
        // Mismo guard que PlayAnimation: Activate() corre en el servidor y no debe
        // animar copias ajenas (los observadores lo reciben por su propio RPC).
        if (IsSpawned && !IsOwner) return;
        ApplyHoldAnimation(ability);
    }

    // Termina la animación de mantener en la copia DUEÑA (bucle → bajar).
    public void StopHoldAnimation()
    {
        if (IsSpawned && !IsOwner) return;
        ApplyStopHoldAnimation();
    }

    // Igual que PlayHoldAnimation pero SIN guard de dueño: la usa la réplica a
    // observadores (NetworkAbilitySystemComponent), que corre justamente sobre
    // copias que no son el dueño.
    //
    // Los tres clips salen del asset de la habilidad, así que cada peer resuelve los
    // suyos desde el registro y no hay que mandar nada más que el índice.
    public void ApplyHoldAnimation(GameplayAbility ability)
    {
        if (characterAnimator == null || ability is not IHoldAbility hold) return;

        // Sin pose (un lanzamiento apuntado sin AimHoldClip, como el Guiding Bolt): el
        // personaje sigue con su animación normal. Entrar al bucle reproduciría el último
        // clip que quedó en esa ranura, que es de otra habilidad.
        if (hold.HoldLoopClip == null) return;

        if (!HasHoldSlots)
        {
            if (!_warnedNoHoldSlots)
            {
                _warnedNoHoldSlots = true;
                Debug.LogWarning($"[PlayerController] '{ability.AbilityName}' es una habilidad de mantener, " +
                                 $"pero el controller no tiene la ranura del bucle ('{HoldLoopSlotName}'). " +
                                 $"La habilidad funciona igual, pero sin animación: alcanza con crear UN " +
                                 $"estado con ese clip placeholder en AC_Player (ver el bloque de " +
                                 $"comentarios de HoldLoopSlotName).");
            }
            return;
        }

        SetHoldClips(hold.HoldStartClip, hold.HoldLoopClip, hold.HoldEndClip);

        // El bool va ANTES del trigger: si se seteara después, el Animator podría
        // evaluar la transición bucle → bajar con IsHolding todavía en false (del
        // mantenido anterior) y salir del bucle en el primer frame.
        if (!string.IsNullOrEmpty(HoldingParam)) characterAnimator.SetBool(HoldingParam, true);

        // El ActionID igual hay que setearlo: la transición sale de AnyState filtrada
        // por ese número (mismo esquema que la ranura de acción).
        characterAnimator.SetInteger("ActionID", HoldStateID);
        if (!string.IsNullOrEmpty(HoldTrigger)) characterAnimator.SetTrigger(HoldTrigger);
    }

    // Igual que StopHoldAnimation pero SIN guard de dueño (para observadores).
    // Basta con bajar el bool: la transición bucle → bajar la resuelve el Animator.
    public void ApplyStopHoldAnimation()
    {
        if (characterAnimator == null || string.IsNullOrEmpty(HoldingParam)) return;
        characterAnimator.SetBool(HoldingParam, false);
    }

    // Reacción al recibir un golpe MIENTRAS se bloquea (el escudo acusa el impacto).
    // Es un one-shot que sale del bucle y vuelve solo. Todo opcional: sin clip o sin
    // el estado en el Animator, no hace nada.
    //
    // Sin guard de dueño: la usan por igual el dueño y la réplica a observadores.
    public void ApplyHoldImpactAnimation(AnimationClip clip)
    {
        if (characterAnimator == null || clip == null) return;

        // La habilidad TRAE un clip de impacto pero el Animator no tiene dónde
        // ponerlo. Sin este aviso, asignar el clip en el asset no hacía absolutamente
        // nada y no había forma de darse cuenta: la reacción simplemente no se veía.
        if (!HasHoldImpact)
        {
            if (!_warnedNoHoldImpact)
            {
                _warnedNoHoldImpact = true;
                Debug.LogWarning($"[PlayerController] Hay un clip de reacción al golpe en el escudo " +
                                 $"('{clip.name}'), pero el controller no tiene la ranura " +
                                 $"'{HoldImpactSlotName}'. La reacción no se va a ver.\n" +
                                 $"Creá un estado con ese clip placeholder en AC_Player: " +
                                 $"AnyState → HoldImpact con ActionID == {HoldImpactStateID} + trigger " +
                                 $"'{HoldImpactTrigger}', y HoldImpact → HoldLoop por Exit Time con " +
                                 $"{HoldingParam} == true.");
            }
            return;
        }

        if (clip != _currentHoldImpact)
        {
            _runtimeAnimator[_holdImpactKey] = clip;
            _currentHoldImpact = clip;
        }

        characterAnimator.SetInteger("ActionID", HoldImpactStateID);
        if (!string.IsNullOrEmpty(HoldImpactTrigger)) characterAnimator.SetTrigger(HoldImpactTrigger);
    }

    // Mete los clips en sus ranuras, salteando las que no existen (levantar, bajar e
    // impacto son opcionales) y las que no cambiaron — cada override provoca un rebind
    // del Animator, así que levantar el escudo dos veces seguidas no debe tocarlo.
    private void SetHoldClips(AnimationClip start, AnimationClip loop, AnimationClip end)
    {
        if (_runtimeAnimator == null) return;

        if (loop != null && loop != _currentHoldLoop && !string.IsNullOrEmpty(_holdLoopKey))
        { _runtimeAnimator[_holdLoopKey] = loop; _currentHoldLoop = loop; }

        if (start != null && start != _currentHoldStart && !string.IsNullOrEmpty(_holdStartKey))
        { _runtimeAnimator[_holdStartKey] = start; _currentHoldStart = start; }

        if (end != null && end != _currentHoldEnd && !string.IsNullOrEmpty(_holdEndKey))
        { _runtimeAnimator[_holdEndKey] = end; _currentHoldEnd = end; }
    }

    // Setea ActionID + trigger en el Animator, sin ningún guard. Lo comparten la
    // ruta del dueño y la de observadores.
    private void TriggerAnimator(string trigger, int actionID)
    {
        if (characterAnimator == null || string.IsNullOrEmpty(trigger)) return;
        characterAnimator.SetInteger("ActionID", actionID);
        characterAnimator.SetTrigger(trigger);
    }

    // Dispara un trigger de animación con el ID de acción dado (usado por
    // las habilidades para animar el ataque correspondiente).
    public void PlayAnimation(string trigger, int actionID)
    {
        if (characterAnimator == null || string.IsNullOrEmpty(trigger)) return;

        // Este método solo dispara la animación en la copia DUEÑA. Para los
        // observadores, NetworkAbilitySystemComponent.ObserversPlayAbilityAnimation
        // la replica aparte (el NetworkAnimator no sincroniza triggers).
        //
        // El guard es clave: ability.Activate() corre en el SERVIDOR y llama a
        // PlayAnimation sobre la copia server-side del jugador. En el host, que
        // renderiza esa copia, eso se sumaba al ObserversRpc y mostraba la
        // animación del otro jugador dos veces. Con el guard, el Activate()
        // server-side no toca copias ajenas. IsSpawned lo deja pasar en escenas
        // sin red (pruebas locales sin NetworkObject spawneado).
        if (IsSpawned && !IsOwner) return;

        TriggerAnimator(trigger, actionID);
    }

    // Gancho del Animation Event que marca el frame de impacto. A propósito NO hace
    // nada: el evento sirve como MARCA DE TIEMPO dentro del clip, y quien lo usa es el
    // servidor, que lee su timestamp del asset (GameplayAbility.GetHitFrameTimes) y
    // programa el daño ahí. Así el golpe cae en el frame exacto sin depender de que
    // cada cliente reporte nada (los eventos corren local en cada peer y no serían
    // confiables para daño).
    //
    // NO borrar este método: Unity dispara igual el evento en cada peer y, sin un
    // receptor, la consola se llena de "AnimationEvent has no receiver!".
    public void AnimationEvent_HitFrame()    { }

    // Gancho de Animation Event: prende/apaga el trail del arma principal
    // en el frame exacto que marque el clip de animación.
    public void AnimationEvent_EnableTrail()
    {
        if (currentWeaponTrail == null) return;
        currentWeaponTrail.SetActive(true);

        // Limpiar cada TrailRenderer al activarlo, para que no dibuje una
        // "raya" recta desde la última posición que tenía (antes de
        // reactivarse) hasta la posición actual del arma.
        foreach (var tr in currentWeaponTrail.GetComponentsInChildren<TrailRenderer>(true))
        {
            tr.Clear();
            tr.emitting = true;
        }
    }
    // Cortar la estela NO es apagar el objeto: es dejar de EMITIR y que la cola que ya
    // está dibujada se consuma sola durante su Time.
    //
    // Apagándolo con SetActive(false) —como estaba— la estela desaparecía de golpe en
    // cuanto terminaba el arco, así que no podía durar más que el tramo entre los dos
    // eventos por más que se subiera el Time del TrailRenderer. Con emitting = false, el
    // arco se sigue viendo desvanecerse detrás del arma después del golpe, que es lo que
    // uno espera de una estela.
    //
    // Si el trail no está hecho con TrailRenderer (por ejemplo un prefab de partículas),
    // se cae al apagado de siempre: para esos, cortar la emisión es otra cosa.
    public void AnimationEvent_DisableTrail()
    {
        if (currentWeaponTrail == null) return;

        var trails = currentWeaponTrail.GetComponentsInChildren<TrailRenderer>(true);
        if (trails.Length == 0)
        {
            currentWeaponTrail.SetActive(false);
            return;
        }

        foreach (var tr in trails) tr.emitting = false;
    }
}
