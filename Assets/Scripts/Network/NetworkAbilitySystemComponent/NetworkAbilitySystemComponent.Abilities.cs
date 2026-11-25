using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Connection;

// ============================================================
// NetworkAbilitySystemComponent — ACTIVAR HABILIDADES POR RED (y sus animaciones)
//
// Una parte de NetworkAbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en Network/NetworkAbilitySystemComponent.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class NetworkAbilitySystemComponent
{

    // =========================================================
    // ACTIVACIÓN DE HABILIDADES POR RED
    // El PlayerController llama ServerRequestActivateAbility()
    // como ServerRpc. El servidor valida y ejecuta.
    // =========================================================

    // Valida y ejecuta la habilidad de un slot, y replica su animación a los
    // observadores. El dueño ya la disparó por predicción (o, en el host, vía
    // Activate()); ObserversPlayAbilityAnimation la reproduce en los demás.
    // placementYaw: el giro que el dueño le dio con la rueda a lo que pone (el Muro de fuego del
    // Mago), en grados sobre "atravesado a la mira". 0 en todo lo demás.
    [ServerRpc]
    public void ServerRequestActivateAbility(EAbilityInput inputSlot, Vector3 aimPoint, Vector3 moveDir,
                                             Vector3 aimOrigin, float placementYaw)
        => ServerActivateAbility(inputSlot, aimPoint, moveDir, aimOrigin, placementYaw);

    // Valida y ejecuta la habilidad de un slot con autoridad de servidor, y
    // replica su animación a los observadores. Separado del ServerRpc de arriba
    // para que código que YA corre en el servidor (ej. la auto-revivida de la
    // clase Inmortal en HandlePlayerDeath) pueda activar directo, sin pasar por
    // el [ServerRpc] de dueño —que el servidor NO puede invocar sobre un cliente
    // remoto (fallaría por RequireOwnership) y dejaba al Inmortal remoto tirado—.
    [Server]
    public void ServerActivateAbility(EAbilityInput inputSlot, Vector3 aimPoint, Vector3 moveDir = default,
                                      Vector3 aimOrigin = default, float placementYaw = 0f)
    {
        if (_asc == null) return;

        GameplayAbility ability = FindAbilityBySlot(inputSlot);

        if (ability == null)
        {
            // Incluimos la clase server-side: si no coincide con la que el
            // dueño cree tener, es un desync de clase/subclase (el servidor se
            // quedó en la clase vieja porque ServerSetClass no sincronizó — ej.
            // GetClassIndex devolvió -1). Ese dato hace obvio el diagnóstico.
            string serverClass = _asc.CurrentClass != null ? _asc.CurrentClass.ClassName : "null";
            Debug.LogWarning($"[Server] No se encontró habilidad en slot {inputSlot} " +
                             $"(clase server-side: {serverClass}). Posible desync de clase con el dueño.");
            // El dueño ya puso isAttacking=true por predicción; si no le
            // avisamos, queda trabado hasta que salte el watchdog y no puede
            // usar NINGUNA habilidad mientras tanto. Lo destrabamos ya.
            NotifyOwnerAbilityRejected();
            return;
        }

        // El dueño calculó este punto con SU cámara y nos lo mandó; lo dejamos
        // disponible en el PlayerController para que RotateToAim()/GetAimPoint()
        // lo usen en vez de intentar leer Camera.main en el servidor.
        //
        // VA ANTES DE CanActivate() a propósito: hay habilidades que validan si tienen
        // un objetivo A TIRO (Enemigo jurado, las de GA_Target), y para eso necesitan la
        // mira de ESTE pedido. Con la asignación después, el servidor las validaba
        // contra la mira de la activación ANTERIOR y podía rechazar un tiro bueno.
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null)
        {
            pc.NetworkAimPoint     = aimPoint;
            // Dirección de movimiento del dueño al activar (para la esquiva
            // direccional del dash). Igual que NetworkAimPoint: el servidor no
            // puede leer el input del dueño, así que este la envía con el pedido.
            pc.NetworkMoveDirection = moveDir;

            // Desde dónde miraba. Cero = no vino (una activación server-side, ej. la
            // auto-revivida del Inmortal); ahí se deja el último que haya, que es mejor
            // que un punto en el origen del mundo.
            if (aimOrigin != Vector3.zero) pc.NetworkAimOrigin = aimOrigin;

            // El giro de lo que pone (la rueda, mientras apuntaba el muro), y desde dónde lo pidió (lo
            // que canaliza antes de salir cae donde soltó aunque se mueva).
            pc.NetworkPlacementYaw    = placementYaw;
            pc.NetworkRequestPosition = pc.transform.position;
        }

        if (!ability.CanActivate())
        {
            Debug.Log($"[Server] Habilidad {ability.AbilityName} bloqueada.");
            NotifyOwnerAbilityRejected(ability);
            return;
        }

        // CON UN MANTENIDO ARRIBA (el escudo) NO ENTRA NINGUNA OTRA. El cliente ya no deja
        // apretar nada mientras mantiene, pero si su estado se desincroniza (pasó: creía
        // haber bajado el escudo), acá está la verdad: sin este corte, un Paladín podía
        // atacar con el clic izquierdo y tener el escudo arriba a la vez, para siempre.
        //
        // La excepción son las marcadas UsableWhileHolding (la Carga defensiva del
        // Guerrero). Esas entran, pero sin animación propia (ver más abajo).
        bool duringHold = IsAnotherHoldActive(ability);
        if (duringHold && !ability.CanUseWhileHolding)
        {
            NotifyOwnerAbilityRejected(ability);
            return;
        }

        // Empezar CUALQUIER habilidad corta las interrumpibles que estén corriendo: el
        // swing a medias no pega, el lanzamiento a medias no sale. Ver
        // GameplayAbility.IsInterruptible.
        //
        // El ataque básico también corta, y no se corta a sí mismo porque el cliente no
        // deja ni leer el botón mientras corre su propio combo (ver
        // PlayerController.HandleAbilityInput). Y aunque llegara igual, el combo nuevo
        // captura el contador DESPUÉS de este incremento: moriría el viejo, no él.
        _asc.CancelInterruptibleAbilities();

        ability.CommittedThisActivation = false;
        ability.StartedThisActivation   = false;
        ability.WasTriggered            = false;   // esta vez la pidió el dueño (ver "Se activa sola")

        // CANALIZA (sección Canalizar del GA): primero la canalización, y la habilidad sale
        // al terminar (o se repite mientras dura). Las animaciones las manda ella: el bucle a
        // todos y, al soltar, el clip de la habilidad (ver GameplayAbility.CastRoutine).
        if (ability.UsesCast)
        {
            ability.BeginCast();
            return;
        }

        ability.Activate();

        // Una habilidad de MANTENER no anima con un clip one-shot sino con un estado
        // sostenido: su Activate() ya mandó ServerBroadcastHoldAnimation. Mandar acá
        // además el clip suelto pisaría ese estado apenas se levanta.
        if (HoldInput.IsHold(ability)) return;

        // Usada con el escudo arriba: su clip suelto pisaría la pose sostenida del escudo
        // en las demás pantallas. Se ve como una carga con el escudo al frente.
        if (duringHold) return;

        // Un combo manda la animación de CADA paso por su cuenta. La del padre llegaría
        // justo después de la del primer paso y la pisaría: en la pantalla de los demás
        // el primer golpe no se veía, solo el segundo.
        if (ability.BroadcastsOwnAnimation) return;

        // La habilidad se plantó sola: entró, vio que no había nada que hacer y salió
        // sin comprometer costo ni cooldown (el Golpe mortal del Pícaro sin nadie a
        // tiro es el caso típico). Entonces tampoco se anima — animar algo que no pasó
        // le miente al que está mirando, y encima delata la posición del que falló.
        // La excepción son las que cobran después (un lanzamiento cobra al soltar): esas
        // avisan con StartedThisActivation que sí arrancaron.
        if (!ability.CommittedThisActivation && !ability.StartedThisActivation) return;

        // De qué habilidad sale la animación: normalmente ella misma, pero las
        // envoltorio (ataque que cambia según un tag) delegan en su variante — y hay
        // que resolverlo DESPUÉS de Activate(), cuando el tag ya se consumió o no.
        GameplayAbility animSource = ability.ResolveAnimationSource() ?? ability;

        // Mandamos también el índice del registro: con él, cada observador resuelve el
        // asset y puede usar su AnimationClip (esquema nuevo). Sin índice válido, el
        // trigger + ActionID alcanzan para el esquema viejo.
        int animIndex = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(animSource) : -1;
        // La velocidad se calcula ACÁ (el servidor tiene el dueño y sus stats); en el
        // observador la habilidad es el template del registro y no podría resolverla.
        ObserversPlayAbilityAnimation(animIndex, animSource.AnimationTriggerName, animSource.AnimationID,
                                      animSource.ResolveAnimationSpeed(), false);
    }

    // Una habilidad que SE ACTIVA SOLA (GameplayAbility "Se activa sola", 18 de noviembre de 2026): la pide el ASC al pasar su
    // evento, ya en el servidor. Es ServerActivateAbility sin botón ni pedido del dueño: la mira apunta al otro del evento
    // ("Apunta al otro"), no corta lo que el dueño estaba haciendo, y la animación va a TODOS, incluido el dueño remoto,
    // que no la predijo. Si no puede (cooldown, costo, un bloqueo) no sale, sin avisar.
    [Server]
    public void ServerActivateTriggered(GameplayAbility ability, GameplayEventData data)
    {
        if (_asc == null || ability == null) return;

        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null && ability.TriggerAimsAtOther && data.Other != null && data.Other != _asc)
        {
            pc.NetworkAimOrigin       = transform.position + Vector3.up * 1.6f;
            pc.NetworkAimPoint        = data.Other.transform.position + Vector3.up * 1f;
            pc.NetworkMoveDirection   = Vector3.zero;
            pc.NetworkPlacementYaw    = 0f;
            pc.NetworkRequestPosition = pc.transform.position;
        }

        if (!ability.CanActivate()) return;
        bool duringHold = IsAnotherHoldActive(ability);
        if (duringHold && !ability.CanUseWhileHolding) return;

        ability.CommittedThisActivation = false;
        ability.StartedThisActivation   = false;
        if (ability.UsesCast) { ability.BeginCast(); return; }

        ability.Activate();

        if (HoldInput.IsHold(ability) || duringHold || ability.BroadcastsOwnAnimation) return;
        if (!ability.CommittedThisActivation && !ability.StartedThisActivation) return;

        GameplayAbility animSource = ability.ResolveAnimationSource() ?? ability;
        int animIndex = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(animSource) : -1;
        ObserversPlayAbilityAnimation(animIndex, animSource.AnimationTriggerName, animSource.AnimationID,
                                      animSource.ResolveAnimationSpeed(), true);
    }

    // Las animaciones de este juego NO viajan por el NetworkAnimator del prefab: ese
    // componente está inerte (su campo Animator quedó vacío y el Animator vive en un
    // hijo, así que no lo encuentra). Hasta la locomoción se manda a mano — ver
    // "LOCOMOCIÓN EN RED" en PlayerController. Y los SetTrigger no los sincronizaría
    // igual ni estando vivo: se consumen antes de que pueda muestrearlos.
    // Por eso la animación de ataque se propaga a los observadores con este
    // ObserversRpc explícito. Al dueño se la salteamos: él ya la disparó por
    // predicción (cliente remoto) o vía Activate() (host). Setea el Animator
    // directo, sin pasar por PlayerController.PlayAnimation, porque ese método
    // tiene un guard de IsOwner que justamente bloquea las copias ajenas.
    [ObserversRpc]
    // includeOwner: la activó el servidor solo ("Se activa sola"), así que el dueño REMOTO tampoco la predijo. En el host
    // el dueño ya se animó en su propio Activate(): ese se saltea igual.
    private void ObserversPlayAbilityAnimation(int abilityIndex, string trigger, int actionID,
                                               float animationSpeed, bool includeOwner)
    {
        if (IsOwner && (!includeOwner || IsServerInitialized)) return;

        // Esquema nuevo: si la habilidad trae su propio AnimationClip, se lo pedimos a
        // PlayerController (que lo mete en la ranura genérica de acción). Va por
        // ApplyAbilityAnimation, la variante SIN guard de dueño — acá justamente
        // estamos en copias que no son el dueño.
        GameplayAbility ability = abilityIndex >= 0
            ? GameplayAbilityRegistry.Instance?.GetAbility(abilityIndex) : null;

        if (ability != null && ability.AnimationClip != null)
        {
            PlayerController pc = GetComponent<PlayerController>();
            if (pc != null) { pc.ApplyAbilityAnimation(ability, animationSpeed); return; }
        }

        // Esquema viejo: seteamos el Animator directo.
        Animator anim = GetComponentInChildren<Animator>();
        if (anim == null || string.IsNullOrEmpty(trigger)) return;

        anim.SetInteger("ActionID", actionID);
        anim.SetTrigger(trigger);
    }

    // Permite que otras rutas server-side (ej. habilidades de menú radial en
    // PlayerController.ServerRequestRadialAbility) también repliquen su
    // animación a los observadores.
    [Server]
    public void ServerBroadcastAbilityAnimation(string trigger, int actionID)
    {
        ObserversPlayAbilityAnimation(-1, trigger, actionID, -1f, false);
    }

    // =========================================================
    // HABILIDADES DE MANTENER (IHoldAbility — escudo del Paladín, etc.)
    //
    // La ACTIVACIÓN va por el camino normal (ServerRequestActivateAbility). Lo que
    // hace falta acá es el otro extremo: avisarle al servidor que el dueño SOLTÓ el
    // botón, y replicar el levantar/bajar de la animación a los observadores (que
    // no reciben nada por ObserversPlayAbilityAnimation, porque un mantenido no es
    // un clip one-shot sino un estado con bool).
    // =========================================================

    // El dueño soltó el botón. Se busca la habilidad del slot y se le pide que
    // termine; EndHold es idempotente, así que un pedido duplicado (soltar justo
    // cuando el servidor ya la cortó por falta de energía) no hace nada.
    [ServerRpc]
    public void ServerRequestEndHoldAbility(EAbilityInput inputSlot)
    {
        GameplayAbility ability = FindAbilityBySlot(inputSlot);
        if (ability is IHoldAbility hold) hold.EndHold();
    }

    // Replica una fase de la animación de mantenido a los observadores. La habilidad
    // viaja por índice de registro para que cada peer resuelva SUS clips desde su
    // propia copia del asset.
    [Server]
    public void ServerBroadcastHoldAnimation(GameplayAbility ability, EHoldAnimationPhase phase)
    {
        int index = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(ability) : -1;

        if (index < 0 && phase != EHoldAnimationPhase.Stop)
            Debug.LogWarning($"[NetworkASC] '{ability?.AbilityName}' no está en GameplayAbilityRegistry — " +
                             $"su animación de mantenido no se replicará a los clientes remotos.");

        ObserversPlayHoldAnimation(index, phase);
    }

    [ObserversRpc]
    private void ObserversPlayHoldAnimation(int abilityIndex, EHoldAnimationPhase phase)
    {
        // Levantar y bajar los salteamos en el DUEÑO: ya los disparó por predicción
        // (cliente remoto) o vía Activate()/ProcessHoldRelease (host).
        //
        // El IMPACTO no: es un evento que nace en el SERVIDOR (lo dispara la barrera al
        // frenar daño), así que el dueño remoto no lo predijo y sí necesita recibirlo.
        // Solo se saltea el personaje del HOST, donde el propio evento ya lo animó —
        // mismo criterio que ObserversPlayComboStepAnimation.
        bool skip = phase == EHoldAnimationPhase.Impact
            ? (IsServerInitialized && IsOwner)
            : IsOwner;
        if (skip) return;

        PlayerController pc = GetComponent<PlayerController>();
        if (pc == null) return;

        // Bajar solo necesita el bool, así que no depende de resolver la habilidad:
        // si el índice no estaba registrado, al menos el escudo no queda pegado.
        if (phase == EHoldAnimationPhase.Stop) { pc.ApplyStopHoldAnimation(); return; }

        GameplayAbility ability = abilityIndex >= 0
            ? GameplayAbilityRegistry.Instance?.GetAbility(abilityIndex) : null;
        if (ability == null) return;

        if (phase == EHoldAnimationPhase.Start) pc.ApplyHoldAnimation(ability);
        else if (ability is IHoldAbility hold)  pc.ApplyHoldImpactAnimation(hold.HoldImpactClip);
    }

    // =========================================================
    // ANIMACIÓN DE CANALIZADO (molinete y compañía)
    //
    // Igual que la del mantenido, con UNA diferencia clave: acá NO se saltea al dueño.
    // Un mantenido lo dispara el input del jugador, así que el dueño ya lo predijo
    // localmente; un canalizado lo dispara Activate() en el SERVIDOR, así que el dueño
    // tampoco lo vio y necesita recibir el RPC como cualquier observador.
    // =========================================================

    [Server]
    public void ServerPlayChannelAnimation(GameplayAbility ability, bool start)
    {
        int index = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(ability) : -1;

        if (index < 0 && start)
            Debug.LogWarning($"[NetworkASC] '{ability?.AbilityName}' no está en GameplayAbilityRegistry — " +
                             $"su animación de canalizado no se va a ver en ningún peer.");

        ObserversPlayChannelAnimation(index, start);
    }

    [ObserversRpc]
    private void ObserversPlayChannelAnimation(int abilityIndex, bool start)
    {
        PlayerController pc = GetComponent<PlayerController>();
        if (pc == null) return;

        // Cortar no depende de resolver la habilidad: si el índice no estaba
        // registrado, al menos el personaje no queda girando para siempre.
        if (!start) { pc.ApplyStopChannelAnimation(); return; }

        GameplayAbility ability = abilityIndex >= 0
            ? GameplayAbilityRegistry.Instance?.GetAbility(abilityIndex) : null;
        if (ability == null) return;

        if (ability is IChanneledAbility channel)
        {
            pc.ApplyChannelAnimation(channel.ChannelStartClip, channel.ChannelLoopClip, channel.ChannelEndClip);
            pc.SetModelSpin(channel.SpinSpeed);
            return;
        }

        // Cualquier otra que canaliza por su sección Canalizar.
        pc.ApplyChannelAnimation(ability.CastStartAnimation, ability.CastLoopAnimation, ability.CastReleaseAnimation);
    }

    // =========================================================
    // DESTELLO DE LA BARRERA AL BLOQUEAR
    //
    // El bloqueo se resuelve SOLO en el servidor (es donde corre el pipeline de daño),
    // así que el feedback visual del escudo necesita su propia réplica: sin esto, el
    // que dispara nunca vería que su tiro pegó contra un escudo, que es justamente la
    // información que tiene que leer para dejar de disparar y rodearlo.
    //
    // Solo viaja el punto de impacto: el resto (colores, duración, prefab del efecto)
    // lo resuelve cada peer con su propia copia del ShieldBlockFlash del prefab.
    // =========================================================

    [Server]
    public void ServerBroadcastShieldBlockFlash(Vector3 hitPoint)
    {
        ObserversPlayShieldBlockFlash(hitPoint);
    }

    [ObserversRpc]
    private void ObserversPlayShieldBlockFlash(Vector3 hitPoint)
    {
        // El servidor ya lo reprodujo local en Entity_ShieldBarrier.BroadcastFlash
        // (importa para el host, que renderiza esa misma copia).
        if (IsServerInitialized) return;

        // Puede haber dos (el Monje: el bloqueo y la cápsula): destella la que esté arriba.
        foreach (Entity_ShieldBarrier barrier in GetComponentsInChildren<Entity_ShieldBarrier>(true))
            barrier.PlayFlash(hitPoint);
    }

    // =========================================================
    // VFX DE UNA PASIVA SOBRE OTRO PERSONAJE (ver IPassiveTargetVFX)
    //
    // La curación del aura del Paladín se resuelve solo en el servidor, así que su
    // destello sobre cada curado necesita su propia réplica, igual que el del escudo.
    // Viaja solo el objetivo: el prefab y los ajustes los pone la pasiva de cada peer.
    // =========================================================

    [Server]
    public void ServerPlayPassiveVFXOn(AbilitySystemComponent target)
    {
        if (target == null) return;

        // El servidor lo dibuja local (importa para el host, que renderiza esa copia).
        GetComponentInChildren<IPassiveTargetVFX>()?.PlayPassiveVFX(target);

        NetworkObject targetNob = target.GetComponent<NetworkObject>();
        if (targetNob != null) ObserversPlayPassiveVFXOn(targetNob);
    }

    [ObserversRpc]
    private void ObserversPlayPassiveVFXOn(NetworkObject target)
    {
        if (IsServerInitialized || target == null) return;

        AbilitySystemComponent targetAsc = target.GetComponent<AbilitySystemComponent>();
        if (targetAsc != null) GetComponentInChildren<IPassiveTargetVFX>()?.PlayPassiveVFX(targetAsc);
    }

    // =========================================================
    // ANIMACIÓN DE UN PASO DE COMBO
    //
    // Un combo (GA_ComboSequence / GA_AlternatingCombo) transmite su PROPIA animación
    // al activarse, pero sus pasos corren después, dentro de una corutina server-side.
    // Cada paso llama a pc.PlayAnimation(this), que tiene guard de dueño — o sea que
    // en el host se ve, pero para un cliente remoto (dueño u observador) no se veía
    // NADA: se quedaban con la animación del combo padre, que normalmente ni siquiera
    // tiene clip propio. Esto replica cada paso a todos los demás peers.
    //
    // Va por COORDENADAS (índice del combo en el registro + qué secuencia + qué paso)
    // en vez de por la habilidad del paso: el paso puede traer un AnimationClipOverride
    // distinto al clip de su propio asset, y resolver la habilidad en el registro daría
    // el clip equivocado. Ver GameplayAbility.GetStepAnimationClip.
    // =========================================================

    [Server]
    public void ServerBroadcastComboStepAnimation(GameplayAbility parent, int sequenceIndex,
                                                  int stepIndex, GameplayAbility stepInstance)
    {
        if (parent == null || stepInstance == null) return;

        int parentIndex = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(parent) : -1;

        if (parentIndex < 0)
            Debug.LogWarning($"[NetworkASC] '{parent.AbilityName}' no está en GameplayAbilityRegistry — " +
                             $"las animaciones de sus pasos no se replicarán a los clientes remotos.");

        // La velocidad se resuelve ACÁ: el paso ya trae el AnimationSpeedOverride que le
        // impuso el combo, y en el observador es solo un clip suelto sin dueño.
        ObserversPlayComboStepAnimation(parentIndex, sequenceIndex, stepIndex,
                                        stepInstance.AnimationTriggerName, stepInstance.AnimationID,
                                        stepInstance.ResolveAnimationSpeed());
    }

    [ObserversRpc]
    private void ObserversPlayComboStepAnimation(int parentIndex, int sequenceIndex, int stepIndex,
                                                 string trigger, int actionID, float animationSpeed)
    {
        // Salteamos SOLO donde el Activate() del paso ya animó por su cuenta: en el
        // servidor Y siendo el dueño, o sea el personaje propio del host. Repetirla ahí
        // dispararía el trigger dos veces sobre el mismo Animator y el segundo queda
        // buffeado, reproduciendo el ataque de nuevo solo.
        //
        // Las dos condiciones importan:
        //  · IsOwner solo (sin IsServerInitialized) saltearía al dueño REMOTO, que sí
        //    necesita esto: su predicción local cubrió el combo padre, no los pasos.
        //  · IsServerInitialized solo saltearía, en el host, a los jugadores AJENOS —
        //    y ahí Activate() no animó nada, porque pc.PlayAnimation tiene guard de
        //    dueño y esas copias no son del host. Sus combos se veían mudos.
        if (IsServerInitialized && IsOwner) return;

        PlayerController pc = GetComponent<PlayerController>();
        if (pc == null) return;

        GameplayAbility parent = parentIndex >= 0
            ? GameplayAbilityRegistry.Instance?.GetAbility(parentIndex) : null;

        // Si no se puede resolver el clip, PlayActionClip cae solo al esquema viejo.
        AnimationClip clip = parent != null ? parent.GetStepAnimationClip(sequenceIndex, stepIndex) : null;
        pc.PlayActionClip(clip, animationSpeed, trigger, actionID);
    }

    // Igual que ServerBroadcastComboStepAnimation, pero saltea a TODOS los dueños: para
    // lo que el dueño ya animó por su cuenta en su pantalla (soltar un lanzamiento
    // apuntado, ver GA_ProjectileShoot.PredictOwnerReleaseVisuals).
    [Server]
    public void ServerBroadcastStepAnimationToOthers(GameplayAbility parent, int sequenceIndex,
                                                     int stepIndex, GameplayAbility stepInstance)
    {
        if (parent == null || stepInstance == null) return;

        int parentIndex = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(parent) : -1;

        ObserversPlayStepAnimationToOthers(parentIndex, sequenceIndex, stepIndex,
                                           stepInstance.AnimationTriggerName, stepInstance.AnimationID,
                                           stepInstance.ResolveAnimationSpeed());
    }

    [ObserversRpc]
    private void ObserversPlayStepAnimationToOthers(int parentIndex, int sequenceIndex, int stepIndex,
                                                    string trigger, int actionID, float animationSpeed)
    {
        if (IsOwner) return;

        PlayerController pc = GetComponent<PlayerController>();
        if (pc == null) return;

        GameplayAbility parent = parentIndex >= 0
            ? GameplayAbilityRegistry.Instance?.GetAbility(parentIndex) : null;

        AnimationClip clip = parent != null ? parent.GetStepAnimationClip(sequenceIndex, stepIndex) : null;
        pc.PlayActionClip(clip, animationSpeed, trigger, actionID);
    }

    // Igual que la anterior pero a partir de la habilidad, para que los observadores
    // puedan usar su AnimationClip. Preferí esta cuando tengas el asset a mano.
    [Server]
    public void ServerBroadcastAbilityAnimation(GameplayAbility ability)
    {
        if (ability == null) return;
        int index = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(ability) : -1;
        ObserversPlayAbilityAnimation(index, ability.AnimationTriggerName, ability.AnimationID,
                                      ability.ResolveAnimationSpeed(), false);
    }

    // Igual que la anterior pero TAMBIÉN en el dueño. Para un golpe que sale cuando lo
    // decide el servidor y no al apretar (el mandoble del Golpe final, al terminar la
    // carga): el dueño no lo pudo anticipar, así que tiene que recibirlo como los demás.
    [Server]
    public void ServerPlayAbilityAnimationOnAll(GameplayAbility ability)
    {
        if (ability == null) return;
        int index = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(ability) : -1;
        ObserversPlayAbilityAnimationOnAll(index, ability.ResolveAnimationSpeed());
    }

    [ObserversRpc]
    private void ObserversPlayAbilityAnimationOnAll(int abilityIndex, float animationSpeed)
    {
        GameplayAbility ability = abilityIndex >= 0
            ? GameplayAbilityRegistry.Instance?.GetAbility(abilityIndex) : null;
        PlayerController pc = GetComponent<PlayerController>();
        if (ability != null && pc != null) pc.ApplyAbilityAnimation(ability, animationSpeed);
    }

    // La animación de SOLTAR de una habilidad canalizada (sección Canalizar), al terminar de
    // canalizar. Va también al dueño (no la anticipó al apretar: lo que se ve al apretar es
    // el bucle), pero NO al personaje del host: ahí Activate() ya la hizo con PlayAnimation.
    [Server]
    public void ServerPlayAbilityAnimationAfterCast(GameplayAbility ability)
    {
        if (ability == null) return;
        int index = GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(ability) : -1;
        ObserversPlayAbilityAnimationAfterCast(index, ability.ResolveAnimationSpeed());
    }

    [ObserversRpc]
    private void ObserversPlayAbilityAnimationAfterCast(int abilityIndex, float animationSpeed)
    {
        if (IsServerInitialized && IsOwner) return;

        GameplayAbility ability = abilityIndex >= 0
            ? GameplayAbilityRegistry.Instance?.GetAbility(abilityIndex) : null;
        PlayerController pc = GetComponent<PlayerController>();
        if (ability != null && pc != null) pc.ApplyAbilityAnimation(ability, animationSpeed);
    }

    // =========================================================
    // FIN DE HABILIDAD — avisar al dueño real (isAttacking es local,
    // no un SyncVar, así que si no le avisamos por red al dueño remoto,
    // se queda trabado en "atacando" para siempre).
    // =========================================================

    // Todos los avisos de fin llevan CUÁL habilidad terminó (su índice en el registro).
    // Hace falta por el escudo: mientras se mantiene, puede terminar OTRA habilidad (el
    // básico que el escudo cortó al subir), y un aviso sin nombre le hacía creer al dueño
    // que el escudo se había bajado. Ver PlayerController.FinishAttack(GameplayAbility).
    [Server]
    public void ServerNotifyAbilityEnded(GameplayAbility ability = null)
    {
        ObserversFinishAttack(RegistryIndex(ability));
    }

    private static int RegistryIndex(GameplayAbility ability)
        => ability != null && GameplayAbilityRegistry.Instance != null
            ? GameplayAbilityRegistry.Instance.GetIndex(ability) : -1;

    private static GameplayAbility RegistryAbility(int index)
        => GameplayAbilityRegistry.Instance != null ? GameplayAbilityRegistry.Instance.GetAbility(index) : null;

    // ¿Hay un mantenido (el escudo) arriba que NO sea 'requested'?
    private bool IsAnotherHoldActive(GameplayAbility requested)
    {
        foreach (GameplayAbility granted in _asc.GrantedAbilities)
            if (granted is IHoldAbility hold && hold.IsHolding && granted != requested)
                return true;
        return false;
    }

    // El servidor rechazó una activación (no encontró la habilidad, o estaba
    // en cooldown). El dueño ya había puesto isAttacking=true por predicción,
    // así que hay que destrabarlo YA — si no, queda bloqueado sin poder usar
    // ninguna habilidad hasta que salte el watchdog de PlayerController.
    [Server]
    private void NotifyOwnerAbilityRejected(GameplayAbility ability = null)
    {
        // Host: el dueño es este mismo proceso; reseteamos directo (el
        // TargetRpc de abajo se saltea solo con el guard de IsServerInitialized).
        if (IsOwner)
        {
            PlayerController pc = GetComponent<PlayerController>();
            if (pc != null) pc.FinishAttack(ability);
        }
        // Dueño remoto: se lo avisamos solo a su conexión. Un BOT no tiene ninguna, y esto
        // corre al terminar CADA habilidad: sin el corte, FishNet avisaba "Target is not an
        // observer" decenas de veces por partida.
        if (!HasOwnerConnection()) return;
        TargetFinishAttack(Owner, RegistryIndex(ability));
    }

    [TargetRpc]
    private void TargetFinishAttack(NetworkConnection conn, int abilityIndex)
    {
        // En el host, el dueño ya se reseteó en NotifyOwnerAbilityRejected.
        if (IsServerInitialized) return;

        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.FinishAttack(RegistryAbility(abilityIndex));
    }

    [ObserversRpc]
    private void ObserversFinishAttack(int abilityIndex)
    {
        // El host ya se resetea directo en GameplayAbility.EndAbility()
        // (server == dueño ahí); esto es sobre todo para el dueño remoto.
        if (IsServerInitialized) return;

        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.FinishAttack(RegistryAbility(abilityIndex));
    }
}
