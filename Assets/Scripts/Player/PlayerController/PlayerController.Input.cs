using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — INPUT Y ACTIVACIÓN DE HABILIDADES
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    private void TickQueuedHoldRelease()
    {
        if (!_holdReleaseQueued) return;
        if (_holdAbility == null) { _holdReleaseQueued = false; return; }
        if (Time.time - HoldStartedAt < _holdAbility.HoldCastSeconds) return;

        _holdReleaseQueued = false;
        ProcessHoldRelease();
    }

    private void HandleAbilityInput()
    {
        TickQueuedHoldRelease();

        // VOLANDO, el Shift ATERRIZA (29 de octubre de 2026, pedido de Gustavo): corta el vuelo
        // aunque la habilidad esté en cooldown, y cae lento. Antes para cambiar de clase había que
        // esperar a que se acabara solo. La levitación de la Sobrecarga (Status_SteadyFlight) no:
        // es la definitiva, y ahí el Shift sigue siendo el Shift.
        if (MovementAbility is GA_Flight && IsFlying && !ASC.HasTag(EGameplayTag.Status_SteadyFlight) &&
            _input.MovementAbility != null && _input.MovementAbility.WasPressedThisFrame())
        {
            RequestLand(false);
            return;
        }

        if (ASC.HasTag(EGameplayTag.State_Silenced)) return;

        // Ocupado con una habilidad: nada más entra… salvo que la que corre sea
        // INTERRUMPIBLE (el ataque básico siempre; los lanzamientos si su asset lo
        // marca). En ese caso se leen los demás botones y solo se saltea el ataque
        // principal, para que el básico no se corte a sí mismo. El corte real lo hace
        // el servidor al activar la nueva (ver GameplayAbility.IsInterruptible).
        bool busy = isAttacking && !IsAimingAbility;
        if (busy && !_attackInterruptible) return;

        CheckAbilityButton(_input.MovementAbility, MovementAbility,      EAbilityInput.Movement);
        CheckAbilityButton(_input.Ability1,        AbilityQ,             EAbilityInput.Action1);
        CheckAbilityButton(_input.Ability2,        AbilityE,             EAbilityInput.Action2);

        // MODO MERCENARIOS: con el Objetivo en las manos, el botón de la DEFINITIVA
        // deja de lanzar la definitiva y pasa a SOLTAR el Objetivo. Es la regla del
        // diseño ("se limita el uso de su Habilidad definitiva"), y se implementa acá
        // —antes de CheckAbilityButton— para que la habilidad ni se evalúe: si la
        // dejáramos pasar, el mismo click soltaría la bolsa Y tiraría la definitiva.
        if (ASC.HasTag(EGameplayTag.Status_Carrying_Objective))
        {
            if (_input.Ability3 != null && _input.Ability3.WasPressedThisFrame())
                MercObjective.RequestDropFromOwner();
        }
        else
        {
            CheckAbilityButton(_input.Ability3, AbilityR, EAbilityInput.Action3);
        }

        CheckAbilityButton(_input.Secondary,       AimAbility,           EAbilityInput.SecondaryAttack);

        // El básico también corta un lanzamiento. Que no se corte a sí mismo lo resuelve
        // CheckAbilityButton, que ya saltea el botón de lo que está corriendo.
        CheckAbilityButton(_input.PrimaryAttack,   PrimaryAttackAbility, EAbilityInput.PrimaryAttack);
        TickAutoRepeatPrimaryAttack();
    }

    private void TickAutoRepeatPrimaryAttack()
    {
        if (!AutoRepeatPrimaryAttack || PrimaryAttackAbility == null || _input.PrimaryAttack == null) return;
        if (!_input.PrimaryAttack.IsPressed() || _input.PrimaryAttack.WasPressedThisFrame()) return;
        if (isAttacking || IsAimingAbility || Time.time < _nextAutoRepeatAt) return;

        GameplayAbility ability = PrimaryAttackAbility;
        if (ability is IRadialMenuAbility || HoldInput.IsHold(ability)) return;
        // Lo que canaliza pide su clic: la Bola de fuego y la Flecha helada del Mago no se
        // repiten solas (manteniendo, se gastaría el maná sin querer); los misiles sí.
        if (ability.UsesCast) return;
        if (ability is IGroundTargetAbility ground && ground.UsesGroundTarget) return;
        if (!ability.CanActivate()) return;

        _nextAutoRepeatAt = Time.time + 0.1f;
        ProcessAbilityPress(ability, EAbilityInput.PrimaryAttack);
    }

    private void CheckAbilityButton(UnityEngine.InputSystem.InputAction action, GameplayAbility ability, EAbilityInput slot)
    {
        if (ability == null || action == null) return;

        // UNA HABILIDAD NO SE CORTA A SÍ MISMA. Interrumpible quiere decir "otra la
        // puede cortar", no "se reinicia sola". Sin esto, apretar dos veces el botón del
        // hacha reiniciaría el lanzamiento cada vez y el hacha no saldría nunca — y con
        // el cooldown empezando al soltar (ver GA_ProjectileShoot) ni siquiera habría un
        // cooldown que lo frenara.
        if (isAttacking && !IsAimingAbility && _runningSlot == slot) return;

        if (action.WasPressedThisFrame())
        {
            ProcessAbilityPress(ability, slot);
        }
        else if (action.WasReleasedThisFrame())
        {
            // Habilidad de MANTENER: soltar la TERMINA (a diferencia del radial y
            // la zona en el suelo, donde soltar es lo que recién la lanza).
            if (_holdAbility == ability)
                ProcessHoldRelease();
            else if (currentRadialAbility == ability || _groundTargetAbility == ability)
                ProcessAbilityRelease();
        }
    }

    // Una habilidad UsableWhileHolding con el escudo arriba (la Carga defensiva). Solo se
    // pide al servidor: NO se toca el estado del mantenido (isAttacking ya está en true
    // por el escudo, y _holdAbility tiene que seguir apuntándolo para que soltar el botón
    // lo baje) ni se anticipa la animación (rompería la pose del escudo). Las de rueda o
    // de zona no entran acá: necesitan su propio modo de apuntado.
    private void PressWhileHolding(GameplayAbility ability, EAbilityInput slot)
    {
        if (ability is IRadialMenuAbility) return;
        if (ability is IGroundTargetAbility ground && ground.UsesGroundTarget) return;
        if (!ability.CanActivate()) return;

        RequestAbility(slot);
    }

    // Al presionar el input de una habilidad: si es de menú radial, abre
    // el menú; si no, predice la animación localmente y pide su
    // activación al servidor.
    private void ProcessAbilityPress(GameplayAbility ability, EAbilityInput slot)
    {
        // Con una habilidad de MANTENER activa no se puede empezar ninguna otra: el
        // escudo ocupa las manos. El input se sigue leyendo igual (HandleAbilityInput
        // no corta por IsAimingAbility) porque hace falta detectar cuándo se SUELTA
        // la que está en curso — pero las demás presiones se descartan acá.
        //
        // La excepción son las marcadas UsableWhileHolding (la Carga defensiva del
        // Guerrero): esas pasan, sin tocar el estado del escudo, que sigue arriba.
        if (IsHoldingAbility)
        {
            if (ability.CanUseWhileHolding && !HoldInput.IsHold(ability)) PressWhileHolding(ability, slot);
            return;
        }

        // Apretó algo que no se puede usar: se lo dice el ícono (ver UI_AbilityDenied).
        if (!ability.CanActivate()) CombatFeel.RaiseAbilityDenied(slot);

        // Lo que arranca ahora no es (todavía) un básico interrumpible: la rueda, el
        // mantenido y la zona nunca lo son, y el camino normal lo vuelve a decidir abajo.
        _attackInterruptible = false;
        _runningSlot         = slot;

        if (ability is IRadialMenuAbility radial)
        {
            if (!ability.CanActivate()) return;
            isAttacking          = true;
            _attackStartTime     = Time.time;
            isRadialMenuOpen     = true;
            currentRadialAbility = ability;
            if (UI_RadialMenu.Instance != null) UI_RadialMenu.Instance.Show(radial);
        }
        else if (HoldInput.IsHold(ability))
        {
            // Habilidad de MANTENER: se activa por el camino NORMAL (el servidor
            // valida y arranca el estado), y queda registrada acá para que soltar el
            // botón la termine. isAttacking se sostiene todo el mantenido — bloquea
            // las demás habilidades pero no el movimiento, y IsAimingAbility saca al
            // watchdog del medio (un escudo puede durar más que MaxAttackSeconds).
            if (!ability.CanActivate()) return;

            isAttacking        = true;
            _attackStartTime   = Time.time;
            _holdAbility       = ability;
            _holdSlot          = slot;
            HoldStartedAt      = Time.time;
            _holdReleaseQueued = false;

            // Predicción local del "levantar", mismo criterio que el camino normal:
            // solo en cliente remoto (en el host la dispara el Activate del servidor).
            if (!IsServerInitialized) PlayHoldAnimation(ability);

            RequestAbility(slot);
        }
        else if (ability is IGroundTargetAbility ground && ground.UsesGroundTarget)
        {
            // Habilidad de zona: al presionar solo entramos en modo apuntado y
            // mostramos el marcador. Se lanza al SOLTAR (ver ProcessAbilityRelease).
            if (!ability.CanActivate()) return;
            isAttacking          = true;
            _attackStartTime     = Time.time;
            _groundTargetAbility = ability;
            _groundTargetSlot    = slot;
            _placementYaw        = 0f;   // el muro arranca atravesado a la mira
            _castMarkerUntil     = 0f;

            // Una franja (el Corte final) o un muro (el Muro de fuego) en vez de la zona en el
            // piso. El marcador vive en el prefab de la cámara.
            UI_GroundTargetIndicator indicator = GroundIndicator;
            if (indicator != null)
            {
                if (ability is ILineTargetAbility)                                  indicator.ShowLine();
                else if (ability is IWallTargetAbility wall && wall.UsesWallMarker) indicator.ShowWall(wall.WallWidth, wall.WallDepth, wall.WallSegments);
                else                                                                 indicator.Show(ground.TargetRadius);
            }
        }
        else
        {
            if (ability.CanActivate())
            {
                // Arrancar otra habilidad corta la anterior (ver IsInterruptible). El
                // servidor lo resuelve por su lado; acá se corta lo que el dueño había
                // anticipado, que si no se queda esperando un final que ya no llega.
                // Mismo criterio que el servidor: el ataque principal no corta nada.
                if (slot != EAbilityInput.PrimaryAttack) CancelWeaponHide();

                isAttacking = true;
                // Una que canaliza dura además su canalización: el watchdog empieza a contar
                // al terminar, no al apretar.
                _attackStartTime = Time.time + (ability.UsesCast ? ability.CastDuration : 0f);
                // Lo decide la HABILIDAD, no el slot. Cuando esto miraba además que
                // fuera el slot del ataque principal, un lanzamiento marcado como
                // interrumpible igual bloqueaba el input mientras corría: el pedido de
                // la habilidad nueva no salía nunca del cliente, así que no había nada
                // que cancelar y marcar o desmarcar la casilla daba exactamente lo mismo.
                _attackInterruptible = ability.IsInterruptible;
                _runningSlot         = slot;
                // Predicción local SOLO en un cliente remoto (no host). El
                // ObserversRpc del servidor se salta al dueño (asume que ya la
                // disparó acá), así que sin esta predicción el dueño remoto
                // nunca vería/animaría su propio ataque — de ahí que la
                // necesitemos.
                //
                // PERO en el host, servidor y dueño son el MISMO objeto:
                // ability.Activate() (que corre server-side este mismo frame)
                // ya llama PlayAnimation. Si además predijéramos acá, el
                // SetTrigger se dispararía DOS veces sobre el mismo Animator; el
                // segundo trigger queda buffeado y reproduce el ataque una
                // SEGUNDA vez solo, sin haber presionado nada.
                //
                // Y solo si la habilidad dice que tiene sentido: el Golpe mortal del
                // Pícaro sin nadie a tiro no hace nada, así que anticipar su animación
                // sería hacer el gesto en el vacío (ver CanPredictActivation).
                //
                // Tampoco si CANALIZA: al apretar se ve el bucle, que lo manda el servidor a
                // todos; la de soltar llega al terminar (GameplayAbility.CastRoutine).
                if (!IsServerInitialized && ability.CanPredictActivation() && !ability.UsesCast)
                {
                    PlayAnimation(ability);
                    // Y lo que acompaña a la animación: el hacha que sale de la mano.
                    ability.PredictOwnerVisuals(this);
                }
                RequestAbility(slot);
            }
        }
    }

    // Al soltar el input de una habilidad de menú radial: confirma la
    // selección hecha con el mouse y la manda al servidor.
    private void ProcessAbilityRelease()
    {
        if (currentRadialAbility is IRadialMenuAbility radial)
        {
            int     sel = UI_RadialMenu.Instance != null
                ? UI_RadialMenu.Instance.HideAndGetSelection() : -1;
            Vector3 pos = GetAimPoint(radial.MaxRadialRange);

            // Predicción local de la animación (mismo criterio que en
            // ProcessAbilityPress): solo en cliente remoto, porque en el host
            // la ActivateWithSelection del servidor ya la dispara. Sin esto,
            // las habilidades de menú radial no dispararían su animación en
            // ningún lado para el dueño remoto (no hay RPC de animación y el
            // Activate del servidor se saltea por el guard de PlayAnimation).
            if (sel != -1 && !IsServerInitialized)
                PlayAnimation(currentRadialAbility);

            ServerRequestRadialAbility(sel, pos);
        }
        else if (_groundTargetAbility != null)
        {
            // Zona confirmada: escondemos el marcador y la lanzamos por el camino
            // NORMAL — RequestAbility ya le manda el punto de mira al servidor
            // (NetworkAimPoint), que es justo la zona que el jugador venía viendo, y el
            // giro del muro si lo es. Si canaliza antes de salir (el Muro, el Levantamiento y
            // la Tormenta del Mago: 2 s), el marcador se queda quieto donde soltó hasta que sale.
            bool castsFirst = _groundTargetAbility.UsesCast;
            if (castsFirst)                  _castMarkerUntil = Time.time + _groundTargetAbility.CastDuration + 0.5f;
            else if (GroundIndicator != null) GroundIndicator.Hide();

            // Predicción local de la animación, mismo criterio que arriba: solo en
            // cliente remoto (en el host la dispara el propio Activate del servidor). Si
            // canaliza, no: el bucle lo manda el servidor y la de soltar llega al terminar.
            if (!IsServerInitialized && !castsFirst)
                PlayAnimation(_groundTargetAbility);

            RequestAbility(_groundTargetSlot);
            _groundTargetAbility = null;
        }

        isRadialMenuOpen     = false;
        currentRadialAbility = null;
        // Reiniciar el reloj del watchdog: recién ahora isAttacking pasa a
        // contar para el timeout (mientras se apuntaba no contaba).
        _attackStartTime     = Time.time;
    }

    // Al SOLTAR el botón de una habilidad de mantener: baja la animación en el acto
    // (feedback inmediato, sin esperar el round-trip) y le pide al servidor que
    // termine el estado real.
    //
    // isAttacking NO se libera acá: eso lo hace el servidor cuando la habilidad
    // llama EndAbility() al terminar de verdad (llega por ObserversFinishAttack).
    // Soltar antes de tiempo el bloqueo local dejaría al jugador atacar mientras el
    // servidor todavía lo tiene con el escudo arriba.
    private void ProcessHoldRelease()
    {
        // Un lanzamiento apuntado que CANALIZA (el Haz guía): soltar antes de que se llene la
        // barra no lanza todavía. Se guarda y se suelta al llenarse (TickQueuedHoldRelease),
        // con todo lo de un soltar normal: la animación de lanzar y el aviso al servidor.
        if (_holdAbility != null && Time.time - HoldStartedAt < _holdAbility.HoldCastSeconds)
        {
            _holdReleaseQueued = true;
            return;
        }

        if (!ReleaseHold(forceFinish: false)) return;

        // Recién ahora isAttacking pasa a contar para el watchdog (mientras se
        // mantenía, IsAimingAbility lo excluía).
        _attackStartTime = Time.time;
    }

    // Corta el mantenido en curso desde AFUERA del input, o sea sin que el jugador
    // haya soltado el botón. Lo llaman los menús modales al abrirse.
    //
    // Hace falta porque en esos casos el "soltar" NUNCA se llega a detectar: Update()
    // corta antes por _inputLocked. Sin esto, el jugador volvía del menú con
    // _holdAbility todavía apuntando al escudo, y como ProcessAbilityPress descarta
    // toda presión mientras haya un mantenido activo —y el watchdog excluye los
    // mantenidos a propósito, para que un escudo pueda sostenerse más de 5 s— quedaba
    // sin poder usar NINGUNA habilidad, para siempre.
    public void CancelHeldAbility() => ReleaseHold(forceFinish: true);

    // Baja el mantenido: avisa al servidor y limpia el estado local. Devuelve false si
    // no había ninguno.
    //
    // forceFinish libera además isAttacking en el acto, en vez de esperar el aviso de
    // fin del servidor. Se usa cuando ese aviso podría no llegar nunca (ver
    // EndActiveHolds: si la instancia de la habilidad ya se descartó, su EndHold no
    // hace nada y nadie manda ObserversFinishAttack).
    private bool ReleaseHold(bool forceFinish)
    {
        if (_holdAbility == null) return false;

        GameplayAbility ability = _holdAbility;
        _holdAbility = null;

        StopHoldAnimation();

        // Un lanzamiento apuntado sale al soltar: se muestra ya, sin esperar al servidor.
        // Al cortarlo desde afuera (un menú) no se lanza nada.
        if (!forceFinish && (!IsSpawned || IsOwner)) ability.PredictOwnerReleaseVisuals(this);

        if (NetASC != null) NetASC.ServerRequestEndHoldAbility(_holdSlot);
        else if (ability is IHoldAbility hold) hold.EndHold(); // fallback singleplayer

        if (forceFinish) FinishAttack();
        return true;
    }

    // Mientras se mantiene una habilidad de zona, el marcador sigue la mira
    // (recortado a su alcance, para que la vista previa coincida con lo que el
    // servidor va a hacer de verdad). Solo corre en el dueño (ver Update).
    private void UpdateGroundTargetIndicator()
    {
        // El marcador de lo que está canalizando se va solo si el fin no llegó (red de seguridad).
        if (_castMarkerUntil > 0f && _groundTargetAbility == null && Time.time >= _castMarkerUntil) HideCastMarker();

        if (_groundTargetAbility is not IGroundTargetAbility ground) return;
        UI_GroundTargetIndicator indicator = GroundIndicator;
        if (indicator == null) return;

        // La franja sale de los pies hacia la mira (la misma dirección con la que el
        // servidor va a salir: la del punto de mira que se le manda al soltar).
        if (_groundTargetAbility is ILineTargetAbility line)
        {
            indicator.UpdateLine(transform.position, GetAimPoint() - transform.position,
                                 line.LineLength, line.LineWidth, line.LineTargetLayer, ASC);
            return;
        }

        // El muro: atravesado a la línea de los pies al punto (lo mismo que hace el servidor) y
        // girado con la rueda.
        if (_groundTargetAbility is IWallTargetAbility wall && wall.UsesWallMarker)
        {
            if (wall.WallRotationStep > 0f && _input != null)
                _placementYaw = Mathf.Repeat(_placementYaw + _input.PlacementRotateStep * wall.WallRotationStep + 180f, 360f) - 180f;

            Vector3 center  = GetClampedAimPoint(ground.MaxTargetRange);
            Vector3 forward = center - transform.position; forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = new Vector3(transform.forward.x, 0f, transform.forward.z);
            indicator.UpdateWall(center, Quaternion.LookRotation(forward.normalized) * Quaternion.Euler(0f, _placementYaw, 0f));
            return;
        }

        indicator.UpdatePosition(GetClampedAimPoint(ground.MaxTargetRange));
    }

    // Punto de mira recortado a maxRange desde el jugador.
    private Vector3 GetClampedAimPoint(float maxRange)
    {
        Vector3 point  = GetAimPoint(maxRange);
        Vector3 offset = point - transform.position;
        if (offset.magnitude > maxRange) point = transform.position + offset.normalized * maxRange;
        return point;
    }

    // El giro que va con el pedido: el del muro que se estaba apuntando, o 0.
    private float CurrentPlacementYaw
        => _groundTargetAbility is IWallTargetAbility wall && wall.UsesWallMarker ? _placementYaw : 0f;

    // Calcula el punto de mira y rota al dueño hacia él ANTES de pedirle
    // al servidor que active la habilidad (para que la rotación correcta
    // llegue a tiempo — ver comentario abajo), y manda la petición.
    private void RequestAbility(EAbilityInput slot)
    {
        if (NetASC != null)
        {
            // Calculamos el punto de mira AQUÍ, en el dueño, donde Camera.main
            // sí es la cámara correcta, y lo mandamos junto con el input.
            Vector3 aimPoint = GetAimPoint();

            // Y DESDE DÓNDE la mira: sin el origen, el servidor no puede reconstruir el
            // rayo de la retícula y tiene que conformarse con la línea desde los pies
            // del personaje, que a corta distancia apunta a otro lado (ver GetAimOrigin).
            Vector3 aimOrigin = GetAimOrigin();

            // Dirección de movimiento actual (WASD/stick en mundo) para la esquiva
            // direccional del dash. El servidor no la puede leer, así que viaja con
            // el pedido (ver GetMoveDirection).
            Vector3 moveDir = GetMoveDirection();

            // Rotamos también en el cliente dueño AHORA. El NetworkTransform es
            // client-authoritative: si solo rotamos en el servidor (dentro de la
            // Ability), el próximo paquete de transform que mande este mismo
            // cliente (con su rotación vieja) lo pisa antes de que se calcule
            // el daño. Rotando acá, la rotación correcta es la que se sincroniza.
            RotateToAim(aimPoint);

            NetASC.ServerRequestActivateAbility(slot, aimPoint, moveDir, aimOrigin, CurrentPlacementYaw);
        }
        else
        {
            NetworkPlacementYaw = CurrentPlacementYaw;
            ActivateAbilityBySlot(slot); // Fallback singleplayer
        }
    }

    // Ejecuta en el servidor la habilidad de menú radial elegida y replica su
    // animación a los observadores (el dueño ya la predijo en ProcessAbilityRelease).
    [ServerRpc]
    private void ServerRequestRadialAbility(int selectedIndex, Vector3 targetPosition)
    {
        foreach (var ability in ASC.GrantedAbilities)
        {
            if (ability is IRadialMenuAbility radial)
            {
                if (!ability.CanActivate()) return;
                radial.ActivateWithSelection(selectedIndex, targetPosition);

                if (selectedIndex != -1 && NetASC != null)
                    NetASC.ServerBroadcastAbilityAnimation(ability);
                return;
            }
        }
    }

    // Activa directamente la habilidad de un slot, sin pasar por red
    // (fallback cuando no hay NetworkAbilitySystemComponent).
    private void ActivateAbilityBySlot(EAbilityInput slot)
    {
        GameplayAbility ability = slot switch
        {
            EAbilityInput.PrimaryAttack   => PrimaryAttackAbility,
            EAbilityInput.SecondaryAttack => AimAbility,
            EAbilityInput.Action1         => AbilityQ,
            EAbilityInput.Action2         => AbilityE,
            EAbilityInput.Action3         => AbilityR,
            EAbilityInput.Movement        => MovementAbility,
            _                             => null
        };
        if (ability != null && ability.CanActivate()) ability.Activate();
    }

    // Libera el estado "atacando" — lo llama GameplayAbility.EndAbility()
    // (directo o vía RPC) al terminar una habilidad.
    //
    // También cierra el estado de MANTENER si quedó abierto. Cubre los dos casos en
    // que el mantenido termina sin que el jugador suelte el botón: que el servidor
    // rechace la activación (NotifyOwnerAbilityRejected) y que la corte por su
    // cuenta (quedarse sin energía, morir). Sin esto el escudo se quedaba "pegado"
    // en el cliente: la animación de mantener seguía y soltar el botón mandaba un
    // fin de algo que ya no existía.
    // Fin de UNA habilidad concreta (los avisos del servidor dicen cuál). Si hay un
    // mantenido arriba (el escudo) y lo que terminó es OTRA habilidad, no se toca nada:
    // lo que está corriendo es el mantenido.
    //
    // POR QUÉ: subir el escudo corta el básico que venía corriendo, y ese básico avisa
    // que terminó. Con el FinishAttack() de siempre, ese aviso borraba _holdAbility: el
    // dueño creía haber bajado el escudo, el clic izquierdo sostenido volvía a pegar, y
    // al soltar el clic derecho ya no se le avisaba a nadie — el servidor dejaba el
    // escudo arriba para siempre. Era el "atacar con el escudo puesto" y el "escudo
    // eterno" de la prueba de 9.
    //
    // 'ended' null = no se sabe cuál (habilidad fuera del registro): con un mantenido
    // arriba, también se lo respeta; al soltar el botón se baja igual.
    public void FinishAttack(GameplayAbility ended)
    {
        if (_holdAbility != null && !SameAbility(ended, _holdAbility)) return;
        FinishAttack();
    }

    private static bool SameAbility(GameplayAbility a, GameplayAbility b)
    {
        if (a == null || b == null) return false;
        GameplayAbility ka = a.SourceTemplate != null ? a.SourceTemplate : a;
        GameplayAbility kb = b.SourceTemplate != null ? b.SourceTemplate : b;
        return ka == kb;
    }

    // Fin de TODO: libera el ataque y baja cualquier mantenido. Para los que cortan desde
    // afuera (el watchdog, la muerte, los menús, el desatascarse).
    // Esconde el marcador que se dejó a la vista mientras canalizaba lo apuntado.
    private void HideCastMarker()
    {
        _castMarkerUntil = 0f;
        if (_groundTargetAbility == null && GroundIndicator != null) GroundIndicator.Hide();
    }

    public void FinishAttack()
    {
        isAttacking = false;
        _attackInterruptible = false;
        _runningSlot         = EAbilityInput.None;
        _holdReleaseQueued   = false;
        if (_castMarkerUntil > 0f) HideCastMarker();   // salió (o la cortaron): el marcador se va

        if (_holdAbility == null) return;
        _holdAbility = null;
        StopHoldAnimation();
    }
}
