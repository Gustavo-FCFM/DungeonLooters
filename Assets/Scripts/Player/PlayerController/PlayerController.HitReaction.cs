using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — REACCIÓN DE GOLPE Y PASOS
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    // =========================================================
    // REACCIÓN DE GOLPE Y PASOS (sonido)
    // =========================================================

    // Sacudón al recibir daño. La llama NetworkAbilitySystemComponent al ver bajar la
    // vida sincronizada, así que corre en TODOS los peers (dueño, host y observadores)
    // sin ningún RPC. El mínimo de DAÑO ya lo aplicó él.
    //
    // El respiro entre animaciones se aplica ACÁ y no allá porque es cosa de la
    // animación, no del audio: allá el mismo número gobernaba las dos, y subirlo para
    // que el personaje dejara de temblar también espaciaba el "ay" de cada golpe.
    public void PlayHitReaction()
    {
        if (characterAnimator == null || !_hasHitTrigger) return;

        if (HitReactionCooldown > 0f && Time.time - _lastHitReactionAt < HitReactionCooldown) return;
        if (ASC != null && ASC.HasTag(EGameplayTag.State_Dead)) return;

        // Con el escudo arriba el golpe tiene su propia animación (HoldImpact).
        if (!string.IsNullOrEmpty(HoldingParam) && characterAnimator.GetBool(HoldingParam)) return;

        // Un bucle en curso (etiqueta "Loop" en el estado) no se corta por un golpe: el
        // molinete sigue girando, el salto sigue volando. Se decide ACÁ y no en el
        // Animator, porque un trigger que no se consume queda armado y dispararía la
        // reacción tarde, al terminar el bucle.
        if (IsInStateWithTag(HitBlockedByStateTag)) return;

        // El reloj se marca RECIÉN acá, después de todos los descartes: si se marcara
        // arriba, un golpe que no llegó a animarse (con el escudo arriba, en pleno
        // molinete) igual consumiría el respiro y se comería la reacción siguiente.
        _lastHitReactionAt = Time.time;
        characterAnimator.SetTrigger(HitTrigger);
    }

    // ¿Alguna capa del Animator está (o está entrando) en un estado con esa etiqueta?
    private bool IsInStateWithTag(string tag)
    {
        if (characterAnimator == null || string.IsNullOrEmpty(tag)) return false;

        for (int layer = 0; layer < characterAnimator.layerCount; layer++)
        {
            if (characterAnimator.GetCurrentAnimatorStateInfo(layer).IsTag(tag)) return true;
            if (characterAnimator.IsInTransition(layer) &&
                characterAnimator.GetNextAnimatorStateInfo(layer).IsTag(tag)) return true;
        }
        return false;
    }

    // ¿El Animator tiene el trigger de reacción? Se mira una vez, al armar el controller
    // en runtime, y no cada golpe.
    private void ResolveHitTrigger()
    {
        _hasHitTrigger   = HasAnimatorParam(HitTrigger,   AnimatorControllerParameterType.Trigger);
        _hasDeadParam    = HasAnimatorParam(DeadParam,    AnimatorControllerParameterType.Bool);
        _hasStunnedParam = HasAnimatorParam(StunnedParam, AnimatorControllerParameterType.Bool);
        _ragdoll         = GetComponent<RagdollController>();
    }

    private void TickFootsteps()
    {
        AudioLibrary lib = AudioLibrary.Instance;
        if (lib == null || characterController == null) return;

        Vector3 pos = transform.position;

        // Primer frame (o teletransporte): no acumular un "paso" de 40 metros.
        Vector3 delta = pos - _lastStepPos;
        _lastStepPos = pos;
        if (delta.sqrMagnitude > 9f) { _stepAccum = 0f; return; }

        // Piso: el CharacterController solo sabe si está en el piso cuando lo movió
        // Move(), o sea solo en el dueño. Un rayo corto vale para todas las copias.
        bool grounded = Physics.Raycast(pos + Vector3.up * 0.3f, Vector3.down, 0.6f, ~(1 << 7),
                                        QueryTriggerInteraction.Ignore);

        if (grounded && !_wasGrounded && Time.time - _spawnedAt > 0.5f)
            AudioManager.Play(lib.Land, pos);
        _wasGrounded = grounded;

        if (!grounded) { _stepAccum = 0f; return; }

        delta.y = 0f;
        _stepAccum += delta.magnitude;
        if (_stepAccum < Mathf.Max(lib.FootstepDistance, 0.3f)) return;

        _stepAccum = 0f;
        AudioManager.Play(lib.Footsteps, pos);
    }

    // Arma el controller de runtime de una clase: parte del controller BASE, le aplica
    // los overrides de la clase y pone la pose de la mano secundaria. Lo usan el
    // jugador y las Copias exactas del Ilusionista, así se ven igual.
    public static AnimatorOverrideController BuildClassAnimator(CharacterClassDefinition cls,
                                                                RuntimeAnimatorController fallback)
    {
        RuntimeAnimatorController source = cls != null && cls.ClassAnimatorOverride != null
            ? cls.ClassAnimatorOverride
            : fallback;
        if (source == null) return null;

        // Separamos el controller base de los overrides que traiga la clase.
        RuntimeAnimatorController baseController = source;
        var classOverrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();

        if (source is AnimatorOverrideController sourceOverride)
        {
            baseController = sourceOverride.runtimeAnimatorController;
            sourceOverride.GetOverrides(classOverrides);
        }
        if (baseController == null) return null;

        var built = new AnimatorOverrideController(baseController);
        if (classOverrides.Count > 0) built.ApplyOverrides(classOverrides);

        if (cls != null && cls.OffHandPose != null)
        {
            var slots = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            built.GetOverrides(slots);
            string poseKey = ResolveSlotKey(slots, OffHandPoseSlotName);
            if (poseKey != null) built[poseKey] = cls.OffHandPose;
        }
        return built;
    }

    // Prende la capa de la pose si la clase trae una, y la apaga si no (o si 'off').
    // Si la clase pide un tag (OffHandPoseRequiredTag), solo con ese tag puesto en 'asc'.
    // Sin la capa en el Animator no hace nada.
    public static void ApplyOffHandPoseWeight(Animator animator, CharacterClassDefinition cls, bool off = false,
                                              AbilitySystemComponent asc = null)
    {
        if (animator == null || animator.runtimeAnimatorController == null) return;

        int layer = animator.GetLayerIndex(OffHandPoseLayerName);
        if (layer < 0) return;

        animator.SetLayerWeight(layer, !off && WantsOffHandPose(cls, asc) ? 1f : 0f);
    }

    private static bool WantsOffHandPose(CharacterClassDefinition cls, AbilitySystemComponent asc)
    {
        if (cls == null || cls.OffHandPose == null) return false;
        if (cls.OffHandPoseRequiredTag == EGameplayTag.None) return true;
        return asc != null && asc.HasTag(cls.OffHandPoseRequiredTag);
    }

    private void TickStanceVisuals()
    {
        CharacterClassDefinition cls = _offHandPoseClass;
        if (cls == null || ASC == null) return;

        bool dead = ASC.HasTag(EGameplayTag.State_Dead);

        // El arma principal, al revés: guardada salvo mientras tenga DrawMainHandTag.
        if (cls.DrawMainHandTag != EGameplayTag.None && currentMainWeapon != null)
        {
            bool stow = !dead && !ASC.HasTag(cls.DrawMainHandTag);
            if (stow != _mainHandStowed) PlaceMainHandWeapon(cls, stow);
        }

        if (cls.StowOffHandTag != EGameplayTag.None && currentOffWeapon != null)
        {
            bool stow = !dead && ASC.HasTag(cls.StowOffHandTag);
            if (stow != _offHandStowed) PlaceOffHandWeapon(cls, stow);
        }

        // Se compara contra el peso REAL de la capa, no contra lo último que se pidió:
        // cada vez que una habilidad cambia un clip del Animator (SetActionClip, los del
        // mantenido) Unity hace un rebind que devuelve las capas a su peso por defecto (0).
        // Mirando solo "ya la prendí", la pose se apagaba en el primer ataque y no volvía.
        if (cls.OffHandPose != null && characterAnimator != null)
        {
            int layer = characterAnimator.GetLayerIndex(OffHandPoseLayerName);
            if (layer >= 0)
            {
                // Aturdido, el brazo cae con el resto del cuerpo (ver TickStunAnimation).
                bool stunned = ASC.HasTag(EGameplayTag.State_Stunned);
                float wanted = !dead && !stunned && WantsOffHandPose(cls, ASC) ? 1f : 0f;
                _offHandPoseOn = wanted > 0f;
                if (!Mathf.Approximately(characterAnimator.GetLayerWeight(layer), wanted))
                    characterAnimator.SetLayerWeight(layer, wanted);
            }
        }

        TickGrow(cls, dead);

        // Las animaciones de la postura (ver StanceAnimatorOverride en la clase).
        if (_stanceClipsOn != null && _runtimeAnimator != null)
        {
            bool stance = ASC.HasTag(cls.StanceAnimatorTag);
            if (stance != _stanceClipsApplied)
            {
                _stanceClipsApplied = stance;
                _runtimeAnimator.ApplyOverrides(stance ? _stanceClipsOn : _stanceClipsOff);
            }
        }
    }

    private void PrepareStanceAnimator(CharacterClassDefinition cls)
    {
        _stanceClipsOn = _stanceClipsOff = null;
        _stanceClipsApplied = false;

        if (cls == null || cls.StanceAnimatorOverride == null || cls.StanceAnimatorTag == EGameplayTag.None) return;
        if (_runtimeAnimator == null) return;

        if (cls.StanceAnimatorOverride.runtimeAnimatorController != _runtimeAnimator.runtimeAnimatorController)
        {
            Debug.LogWarning($"[Postura] {cls.StanceAnimatorOverride.name} no está armado sobre el mismo Animator " +
                             $"base que la clase {cls.name}: no se usa.");
            return;
        }

        var current = new List<KeyValuePair<AnimationClip, AnimationClip>>();
        _runtimeAnimator.GetOverrides(current);
        var classClips = new Dictionary<AnimationClip, AnimationClip>();
        foreach (var pair in current)
            if (pair.Key != null) classClips[pair.Key] = pair.Value;

        var stance = new List<KeyValuePair<AnimationClip, AnimationClip>>();
        cls.StanceAnimatorOverride.GetOverrides(stance);

        _stanceClipsOn  = new List<KeyValuePair<AnimationClip, AnimationClip>>();
        _stanceClipsOff = new List<KeyValuePair<AnimationClip, AnimationClip>>();
        foreach (var pair in stance)
        {
            if (pair.Key == null || pair.Value == null) continue;
            if (pair.Key.name.StartsWith("PLACEHOLDER_", System.StringComparison.OrdinalIgnoreCase)) continue;

            classClips.TryGetValue(pair.Key, out AnimationClip classClip);
            if (classClip == pair.Value) continue;   // el mismo clip: no hace falta cambiarlo

            _stanceClipsOn.Add(pair);
            _stanceClipsOff.Add(new KeyValuePair<AnimationClip, AnimationClip>(pair.Key, classClip));
        }

        if (_stanceClipsOn.Count == 0) _stanceClipsOn = _stanceClipsOff = null;
    }

    private void TickGrow(CharacterClassDefinition cls, bool dead)
    {
        if (characterAnimator == null) return;
        Transform model = characterAnimator.transform;

        if (!_modelBaseScaleKnown)
        {
            _modelBaseScale      = model.localScale;
            _modelBaseScaleKnown = true;
        }

        bool    grow   = cls.GrowTag != EGameplayTag.None && !dead && ASC.HasTag(cls.GrowTag);
        Vector3 wanted = _modelBaseScale * (grow ? Mathf.Max(0.1f, cls.GrowScale) : 1f);

        if (model.localScale != wanted)
            model.localScale = Vector3.MoveTowards(model.localScale, wanted,
                                                   GrowPerSecond * _modelBaseScale.magnitude * Time.deltaTime);

        // La cámara del dueño acompaña el tamaño: mira desde más alto y se aleja un poco.
        if (_orbitCam != null && _modelBaseScale.x > 0.0001f)
            _orbitCam.TargetScale = model.localScale.x / _modelBaseScale.x;
    }

    // Cuelga el arma secundaria del pecho (guardada) o la devuelve a la mano.
    private void PlaceOffHandWeapon(CharacterClassDefinition cls, bool stowed)
    {
        Transform parent = stowed ? BackBone() : OffHandSocket;
        if (parent == null) return;

        _offHandStowed = stowed;
        currentOffWeapon.transform.SetParent(parent, false);
        currentOffWeapon.transform.SetLocalPositionAndRotation(
            stowed ? cls.StowedOffHandPositionOffset : cls.OffHandPositionOffset,
            Quaternion.Euler(stowed ? cls.StowedOffHandRotationOffset : cls.OffHandRotationOffset));
    }

    // Cuelga el arma principal del pecho (guardada) o la devuelve a la mano.
    private void PlaceMainHandWeapon(CharacterClassDefinition cls, bool stowed)
    {
        Transform parent = stowed ? BackBone() : MainHandSocket;
        if (parent == null) return;

        _mainHandStowed = stowed;
        currentMainWeapon.transform.SetParent(parent, false);
        currentMainWeapon.transform.SetLocalPositionAndRotation(
            stowed ? cls.StowedMainHandPositionOffset : cls.MainHandPositionOffset,
            Quaternion.Euler(stowed ? cls.StowedMainHandRotationOffset : cls.MainHandRotationOffset));
    }

    // El hueso del pecho del esqueleto humanoide (o la columna si no hay pecho).
    private Transform BackBone()
    {
        if (_backBone != null) return _backBone;
        if (characterAnimator == null || !characterAnimator.isHuman) return null;

        _backBone = characterAnimator.GetBoneTransform(HumanBodyBones.Chest);
        if (_backBone == null) _backBone = characterAnimator.GetBoneTransform(HumanBodyBones.Spine);
        return _backBone;
    }

    private static string ResolveSlotKey(List<KeyValuePair<AnimationClip, AnimationClip>> slots, string slotName)
    {
        if (string.IsNullOrEmpty(slotName)) return null;

        foreach (var pair in slots)
        {
            if (pair.Key == null) continue;
            if (string.Equals(pair.Key.name, slotName, System.StringComparison.OrdinalIgnoreCase))
                return pair.Key.name;
        }
        return null;
    }

    // Pone un clip en la ranura genérica de acción. Solo reasigna si cambió: cada
    // override provoca un rebind del Animator, así que repetir el mismo clip
    // (ej. atacar varias veces seguidas) no debe tocarlo.
    private void SetActionClip(AnimationClip clip)
    {
        if (_runtimeAnimator == null || clip == null || _currentActionClip == clip) return;
        if (!HasActionSlot) return;

        _runtimeAnimator[_actionSlotKey] = clip;
        _currentActionClip = clip;
    }

    // Reproduce la animación de una habilidad en la copia DUEÑA. Es la entrada que
    // usan las habilidades (pc.PlayAnimation(this)): elige sola entre el clip del
    // asset y el esquema viejo de ActionID.
    public void PlayAnimation(GameplayAbility ability)
    {
        if (ability == null) return;
        // Mismo guard que abajo: Activate() corre en el servidor y no debe animar
        // copias ajenas (los observadores reciben la animación por su propio RPC).
        if (IsSpawned && !IsOwner) return;
        // Las repeticiones de un canalizado continuo no animan: la pose en bucle sigue.
        if (ability.SuppressesOwnAnimation) return;

        ApplyAbilityAnimation(ability);
    }

    // Aplica la animación de una habilidad SIN guard de dueño. La usa la réplica a
    // observadores (NetworkAbilitySystemComponent), que corre justamente sobre
    // copias que no son el dueño.
    // animationSpeed: velocidad a la que reproducir el clip. -1 = calcularla acá. La
    // réplica a observadores la manda ya calculada desde el SERVIDOR, porque allá la
    // habilidad es el template del registro (sin dueño) y no podría resolverla igual.
    public void ApplyAbilityAnimation(GameplayAbility ability, float animationSpeed = -1f)
    {
        if (characterAnimator == null || ability == null) return;

        // Las habilidades "envoltorio" (el ataque principal que cambia según un tag)
        // delegan su animación en la variante que realmente se ejecuta. Para todas
        // las demás esto devuelve la misma habilidad. Ver ResolveAnimationSource.
        ability = ability.ResolveAnimationSource() ?? ability;

        // El sonido de lanzar va con la animación: este método corre en el dueño y, vía
        // ObserversPlayAbilityAnimation, en cada observador — o sea, una vez por pantalla.
        AudioManager.Play(ability.CastSound, transform.position + Vector3.up);

        // SALTO DE HABILIDAD: bucle en el aire + remate al aterrizar, en vez de un
        // clip que se reproduce una vez y termina aunque el personaje siga volando.
        //
        // Va ANTES de la ranura de acción normal a propósito: una habilidad de leap no
        // tiene que caer nunca en el camino de un solo clip. Y va acá dentro —y no en
        // un método aparte llamado desde Activate()— porque ApplyAbilityAnimation es la
        // variante SIN guard de dueño que usan las DOS rutas: la del dueño y la de
        // ObserversPlayAbilityAnimation. Poniéndolo acá, la réplica a los demás peers
        // sale gratis, sin un RPC nuevo.
        if (ability is ILeapAbility leap && leap.AirLoopClip != null)
        {
            if (ApplyLeapAnimation(ability, leap, animationSpeed)) return;
            // Sin las ranuras en el Animator se sigue de largo al esquema de siempre:
            // la habilidad se anima como antes en vez de quedarse muda.
        }

        // Camino nuevo: el clip viene en el propio asset de la habilidad. Requiere que
        // la ranura exista en el Animator; si no, seguimos con el esquema viejo (si
        // no, dispararíamos un ActionID que ninguna transición atiende y el ataque se
        // quedaría sin animación, dejando el trigger colgado).
        if (ability.AnimationClip != null && !HasActionSlot && !_warnedNoActionSlot)
        {
            _warnedNoActionSlot = true;

            // Listamos los clips que SÍ tiene el controller: casi siempre el problema
            // es que ActionClipSlotName no coincide con el nombre real del placeholder.
            string available = "(ninguno)";
            if (_runtimeAnimator != null)
            {
                var slots = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                _runtimeAnimator.GetOverrides(slots);
                var names = new List<string>();
                foreach (var pair in slots) if (pair.Key != null) names.Add(pair.Key.name);
                if (names.Count > 0) available = string.Join(", ", names);
            }

            Debug.LogWarning($"[PlayerController] '{ability.AbilityName}' tiene AnimationClip, pero el " +
                             $"controller no tiene la ranura '{ActionClipSlotName}'. Se usa el esquema viejo " +
                             $"(AnimationID). Creá el estado genérico de acción con ese clip placeholder, o " +
                             $"corregí ActionClipSlotName.\nClips disponibles en el controller: {available}");
        }

        if (ability.AnimationClip != null && HasActionSlot)
        {
            SetActionClip(ability.AnimationClip);

            // Velocidad del swing: la que hace que el clip dure lo que el ritmo de
            // ataque. Va en su propio parámetro para que UpdateAnimations no la pise.
            float speed = animationSpeed > 0f ? animationSpeed : ability.ResolveAnimationSpeed();
            if (!string.IsNullOrEmpty(ActionSpeedParam) && speed > 0f)
                characterAnimator.SetFloat(ActionSpeedParam, speed);

            // El ActionID igual hay que setearlo: las transiciones salen de AnyState
            // filtradas por ese número, así que sin esto iríamos al estado del ataque
            // anterior en vez de a la ranura genérica.
            characterAnimator.SetInteger("ActionID", ActionClipStateID);
            if (!string.IsNullOrEmpty(ActionClipTrigger)) characterAnimator.SetTrigger(ActionClipTrigger);
            return;
        }

        // Camino viejo: un estado propio por ataque, elegido por ActionID. Va por
        // TriggerAnimator (sin guard): este método también corre en observadores.
        TriggerAnimator(ability.AnimationTriggerName, ability.AnimationID);
    }

    // Mete los clips del leap en sus ranuras y manda al Animator al bucle aéreo.
    // Devuelve FALSE si el controller no tiene la ranura del bucle, para que quien la
    // llame siga con el esquema de siempre en vez de disparar un ActionID que ninguna
    // transición atiende (eso dejaría el ataque sin animación y el trigger colgado).
    //
    // No corta el bucle: de eso se encarga el Animator con IsJumping, que
    // UpdateAnimations alimenta cada frame. Por eso no hay un "StopLeapAnimation".
    private bool ApplyLeapAnimation(GameplayAbility ability, ILeapAbility leap, float animationSpeed)
    {
        if (!HasAirSlots)
        {
            if (!_warnedNoAirSlots)
            {
                _warnedNoAirSlots = true;
                Debug.LogWarning($"[PlayerController] '{ability.AbilityName}' trae clips de salto, pero el " +
                                 $"controller no tiene la ranura del bucle ('{AirLoopSlotName}'). Se anima " +
                                 $"con el esquema de siempre.\n" +
                                 $"Para activarlo, en AC_Player: un estado EN BUCLE con ese clip " +
                                 $"placeholder, AnyState → AirLoop con ActionID == {LeapStateID} + trigger " +
                                 $"'{ActionClipTrigger}', y AirLoop → AirLand con IsJumping == false.");
            }
            return false;
        }

        // Cada override provoca un rebind del Animator, así que solo se asigna lo que
        // cambió — saltar dos veces seguidas con la misma habilidad no debe tocarlo.
        // El DESPEGUE es opcional por partida doble: puede no traer clip, o el Animator
        // puede no tener el estado. Cualquiera de las dos y se entra directo al bucle.
        if (leap.AirStartClip != null && leap.AirStartClip != _currentAirStart
            && !string.IsNullOrEmpty(_airStartKey))
        {
            _runtimeAnimator[_airStartKey] = leap.AirStartClip;
            _currentAirStart = leap.AirStartClip;
        }

        if (leap.AirLoopClip != _currentAirLoop)
        {
            _runtimeAnimator[_airLoopKey] = leap.AirLoopClip;
            _currentAirLoop = leap.AirLoopClip;
        }

        // El remate es opcional por partida doble: puede no traer clip, o el Animator
        // puede no tener el estado. Cualquiera de las dos cosas y se cae del bucle
        // directo a la locomoción.
        if (leap.AirLandClip != null && leap.AirLandClip != _currentAirLand
            && !string.IsNullOrEmpty(_airLandKey))
        {
            _runtimeAnimator[_airLandKey] = leap.AirLandClip;
            _currentAirLand = leap.AirLandClip;
        }

        float speed = animationSpeed > 0f ? animationSpeed : ability.ResolveAnimationSpeed();
        if (!string.IsNullOrEmpty(ActionSpeedParam) && speed > 0f)
            characterAnimator.SetFloat(ActionSpeedParam, speed);

        // Arranca la retención del despegue: hasta que el personaje deje el piso de
        // verdad, IsJumping se fuerza en true para que el bucle no se corte en el
        // primer frame (ver UpdateAnimations).
        _leapTakeoffPending  = true;
        _leapTakeoffDeadline = Time.time + 0.5f;

        characterAnimator.SetInteger("ActionID", LeapStateID);
        if (!string.IsNullOrEmpty(ActionClipTrigger)) characterAnimator.SetTrigger(ActionClipTrigger);
        return true;
    }

    // Reproduce un CLIP suelto en la ranura genérica de acción, sin necesitar la
    // habilidad que lo trae. La usa la réplica de los pasos de un combo: ahí el clip
    // puede ser un AnimationClipOverride del paso, que no coincide con el AnimationClip
    // del asset de esa habilidad, así que el observador no lo puede resolver por la
    // habilidad — lo resuelve por las coordenadas del paso y llega acá con el clip ya
    // en mano (ver GameplayAbility.GetStepAnimationClip).
    //
    // Sin guard de dueño a propósito: corre en observadores Y en el dueño remoto.
    // Si el clip es null o falta la ranura, cae al esquema viejo con trigger/ActionID.
    public void PlayActionClip(AnimationClip clip, float animationSpeed,
                               string fallbackTrigger, int fallbackActionID)
    {
        if (characterAnimator == null) return;

        if (clip == null || !HasActionSlot)
        {
            TriggerAnimator(fallbackTrigger, fallbackActionID);
            return;
        }

        SetActionClip(clip);

        if (!string.IsNullOrEmpty(ActionSpeedParam) && animationSpeed > 0f)
            characterAnimator.SetFloat(ActionSpeedParam, animationSpeed);

        characterAnimator.SetInteger("ActionID", ActionClipStateID);
        if (!string.IsNullOrEmpty(ActionClipTrigger)) characterAnimator.SetTrigger(ActionClipTrigger);
    }
}
