using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// BotController — LA RUEDA DE ESTADOS (el libro de hechizos del Mago)
//
// Una parte de BotController (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/BotController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class BotController
{

    // =========================================================
    // RUEDA DE ESTADOS (el libro de hechizos del Mago)
    //
    // Un jugador elige el hechizo según lo que pasa; el bot lo decide así, de más urgente a
    // menos:
    //   1. En apuros: la opción que vuelve SALVAVIDAS a otra de sus habilidades (en el Mago, el
    //      hielo: la Q pasa a ser la Tumba helada), si esa habilidad está lista.
    //   2. Con poco maná: la opción normal (los misiles, que no gastan; y la Q recarga).
    //   3. Cada tanto (ChoiceSeconds), otra de las que le alcanzan, al azar: así usa todo el
    //      libro y todas las Q.
    // No sabe nada del Mago: mira los estados que da cada opción y las variantes de sus switches.
    // =========================================================

    private int DesiredChoice(GA_RadialChoice choice, float dist)
    {
        if (choice.Options == null || choice.Options.Count == 0) return -1;
        int current = choice.CurrentIndex;

        if (InTrouble(dist))
        {
            int rescue = RescueChoice(choice);
            if (rescue >= 0) return rescue;
        }

        int normal = choice.Options.FindIndex(o => o.Effect == null);
        if (normal >= 0 && ManaFraction() <= LowManaFraction) return normal;

        if (Time.time < _nextChoiceAt) return current;
        _nextChoiceAt = Time.time + ChoiceSeconds * Random.Range(0.7f, 1.3f);

        var candidates = new List<int>();
        for (int i = 0; i < choice.Options.Count; i++)
            if (i != current && choice.IsRadialOptionSelectable(i)) candidates.Add(i);
        return candidates.Count > 0 ? candidates[Random.Range(0, candidates.Count)] : current;
    }

    // La opción cuyo estado hace que otra habilidad del kit (un switch) salga como salvavidas:
    // un buff propio que da Status_Immunity, listo para usarse. -1 si no hay.
    private int RescueChoice(GA_RadialChoice choice)
    {
        foreach (EAbilityInput slot in CombatSlots)
        {
            if (!(FindAbility(slot) is GA_TagSwitch sw) || sw.Variants == null) continue;

            foreach (GA_TagSwitch.TagVariant variant in sw.Variants)
            {
                if (!(variant.Ability is GA_SelfBuff buff)) continue;

                _selfBuffEffects.Clear();
                buff.GetEffects(EEffectWhen.OnActivate, EEffectTarget.Self, _selfBuffEffects);
                if (!AnyGrants(_selfBuffEffects, EGameplayTag.Status_Immunity)) continue;
                if (!sw.CanActivate()) continue;   // la Q en cooldown: elegir el hielo no salva a nadie

                for (int i = 0; i < choice.Options.Count; i++)
                    if (GrantsTag(choice.Options[i].Effect, variant.RequiredTag) && choice.IsRadialOptionSelectable(i))
                        return i;
            }
        }
        return -1;
    }

    // ¿Hay alguien de los míos en problemas? Es lo que hace que el Paladín deje de pegar y
    // pase a sostener: sus curaciones y escudos no se gastan "por si acaso".
    private bool SomeoneInDanger() => SomeoneBelow(AllyDangerHealth);

    // ¿Yo o el compañero más cercano estamos por debajo de esta fracción de vida? El
    // umbral lo pone quien pregunta: sostener empieza antes que la emergencia.
    private bool SomeoneBelow(float fraction)
    {
        if (HealthFraction(_asc) <= fraction) return true;

        AbilitySystemComponent ally = FindNearestAlly();
        return ally != null && HealthFraction(ally) <= fraction;
    }

    // ¿Esta habilidad ATURDE? Se mira el efecto que aplica, no el nombre: así la Presencia
    // conquistadora entra sola, y cualquier aturdimiento nuevo también.
    private static bool AppliesStun(GameplayAbility ability)
    {
        switch (ability)
        {
            case GA_InstantAoE _:
            case GA_Target _:
                return ability.AnyEffectGrants(EGameplayTag.State_Stunned, EEffectWhen.OnHit);
            default:
                return false;
        }
    }

    private static bool AnyGrants(List<GameplayEffect> effects, EGameplayTag tag)
    {
        if (effects == null) return false;

        foreach (GameplayEffect e in effects)
            if (GrantsTag(e, tag)) return true;

        return false;
    }

    private static bool GrantsTag(GameplayEffect effect, EGameplayTag tag)
        => effect != null && effect.GrantedTags != null && effect.GrantedTags.Contains(tag);

    private static bool HealsHealth(GameplayEffect effect)
    {
        if (effect == null || effect.Modifiers == null) return false;

        foreach (Modifier m in effect.Modifiers)
            if (m != null && m.Attribute == EAttributeType.Health && m.Magnitude > 0f) return true;

        return false;
    }

    // Cuántos COMPAÑEROS jugadores hay alrededor de un punto. Lo usa la fiesta de copias.
    private int AlliesNear(Vector3 center, float radius)
    {
        int count = Physics.OverlapSphereNonAlloc(
            center, radius, _hits, CharacterLayer, QueryTriggerInteraction.Ignore);

        int allies = 0;
        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i].GetComponentInParent<AbilitySystemComponent>();
            if (other == null || other == _asc) continue;
            if (other.HasTag(EGameplayTag.State_Dead)) continue;
            if (!_asc.IsAllyOf(other, includeSelf: false)) continue;
            if (other.GetComponent<PlayerController>() == null) continue;

            allies++;
        }

        return allies;
    }

    // La habilidad de movimiento es la que más cambia de sentido según la clase.
    private bool ShouldUseMovement(GameplayAbility ability, EBotRole role, float dist)
    {
        // ¿HAY TECHO? Un salto sube unos 11 metros con los números del Bárbaro. Bajo el
        // techo de una base, o de cualquier cosa con altura, el bot se estampa contra él y
        // termina encajado entre el piso y lo de arriba — que es exactamente lo que pasó
        // cuando el equipo entero saltó al abrirse las puertas.
        if (ability is GA_LeapAttack leap && !HasHeadroomFor(leap)) return false;

        // Escapar mientras me retiro a curarme, y llegar antes con la carga: eso vale para
        // cualquiera (lo de la carga lo dispara Fight aparte).
        if (_retreating) return true;

        // Una embestida (la Patada voladora) solo si llega: con el otro más lejos que su recorrido,
        // se gastaba corriendo hacia la nada.
        GameplayAbility moving = ability is GA_TagSwitch msw && msw.CurrentVariant != null ? msw.CurrentVariant : ability;
        if (moving is GA_RushAttack rush && dist > rush.Distance * 1.2f) return false;

        // Un teletransporte en apuros sirve para escapar (lo apunta TeleportAim lejos del enemigo).
        if (moving is GA_Teleport && InTrouble(dist)) return true;

        switch (role)
        {
            case EBotRole.Support:
            {
                // La Intervención existe para llegar hasta el compañero en problemas y
                // ponerse delante. Sin alguien a quien socorrer, no se gasta.
                AbilitySystemComponent ally = FindNearestAlly();
                if (ally == null) return false;

                return HealthFraction(ally) < 0.6f
                    && Vector3.Distance(transform.position, ally.transform.position) > SupportFollowDistance;
            }

            case EBotRole.Assassin:
                // Acercarse de golpe para entrar, o rematar al que se escapa.
                return dist > MeleeRange * 2.5f || _targetFleeing;

            default:
            {
                // El salto del Bárbaro es un ataque en área con desplazamiento: se usa para
                // CAER SOBRE VARIOS, o para entrar cuando el otro está a tiro.
                //
                // El tope de distancia importa: sin él, apenas se abrían las puertas TODOS
                // saltaban a la vez hacia un enemigo que estaba en la otra punta del mapa.
                float reach = ability is GA_LeapAttack l ? LeapReach(l) : MeleeRange * 6f;
                if (dist > reach) return false;

                return EnemiesNear(_target.transform.position, AoeRadius) >= 2
                    || dist > MeleeRange * 3f;
            }
        }
    }

    // Hasta dónde llega ese salto: velocidad de avance por el tiempo que dura en el aire.
    private static float LeapReach(GA_LeapAttack leap)
    {
        float gravity = Mathf.Abs(Physics.gravity.y);
        return leap.ForwardForce * (2f * leap.JumpVelocity / gravity);
    }

    // ¿Tiene lugar arriba para el arco completo? Se mide contra la altura máxima real del
    // salto, no contra un número inventado.
    private bool HasHeadroomFor(GA_LeapAttack leap)
    {
        float gravity = Mathf.Abs(Physics.gravity.y);
        float apex    = leap.JumpVelocity * leap.JumpVelocity / (2f * gravity);

        return !Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.up,
                                apex, CollisionMask, QueryTriggerInteraction.Ignore);
    }


    // Cuántos enemigos hay alrededor de un punto. Es lo que distingue "vale la pena el área"
    // de "gastar la definitiva contra un fantasma suelto".
    private int EnemiesNear(Vector3 center, float radius)
    {
        int count = Physics.OverlapSphereNonAlloc(
            center, radius, _hits, CharacterLayer, QueryTriggerInteraction.Ignore);

        int enemies = 0;
        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i].GetComponentInParent<AbilitySystemComponent>();
            if (other == null || other == _asc) continue;
            if (other.HasTag(EGameplayTag.State_Dead)) continue;
            if (_asc.IsAllyOf(other, includeSelf: false)) continue;
            if (other.Strikeable != null) continue;   // algo del mapa (la campana): no es a quién pelear
            if (!CanPerceive(other)) continue;   // invisible y sin revelar: no lo veo
            if (IsImmune(other)) continue;       // inmune: un área no le hace nada

            enemies++;
        }

        return enemies;
    }

    // Pide una habilidad por el MISMO camino que un jugador de verdad: el servidor valida
    // cooldown, costo y tags, y replica la animación a todos. Si el slot está vacío o la
    // habilidad no se puede usar, no pasa nada.
    private bool Activate(EAbilityInput slot, Vector3 aim, Vector3 moveDir)
    {
        GameplayAbility ability = FindAbility(slot);
        if (ability == null) return false;

        // Con algo MANTENIDO abierto (el hacha apuntada, el escudo) no se aprieta otro mantenido, ni el
        // mismo otra vez. Un lanzamiento cobra recién al soltar, así que mientras se apunta sigue
        // "disponible": reactivarlo le reiniciaba la cuenta para soltar, y el bot se quedaba apuntando
        // el hacha sin tirarla hasta el corte de seguridad (23 de octubre de 2026).
        if (_held != null && _held.IsHolding && (ReferenceEquals(_held, ability) || HoldInput.IsHold(ability)))
            return false;

        // La que va a salir ahora: la variante de un switch (el clic izquierdo del Mago), o ella misma.
        GameplayAbility effective = ability is GA_TagSwitch sw && sw.CurrentVariant != null ? sw.CurrentVariant : ability;

        // Lo único que un bot todavía no puede ejecutar. Ver GameplayAbility.MovesThroughOwner. La
        // excepción: el vuelo (GA_Flight), que lo mueve este cerebro (ver VUELO).
        if (ability.MovesThroughOwner && !(ability is GA_Flight)) return false;

        // Una de ALIADO (la Proyección arcana, la curación apuntada del Clérigo) se apunta al aliado,
        // no al enemigo: si no, no lo encuentra en la mira. Sin aliado y sin autocasteo, no sale.
        if (effective is GA_Target ally && ally.Targets == GA_Target.ETargetSide.Allies)
        {
            AbilitySystemComponent friend = ChooseAllyFor(ally);
            if (friend != null) aim = friend.transform.position + Vector3.up * 1.2f;
            else if (!ally.SelfIfNoTarget) return false;
            else aim = transform.position;   // a mis pies: sin nadie en la mira, sale sobre mí
        }

        // Una marca que protege al equipo va al enemigo que le pega a un aliado (lo eligió ShouldUse).
        if ((effective is GA_CommandHalt || effective is GA_SwornEnemy) && _markTarget != null)
            aim = _markTarget.transform.position + Vector3.up * 1.2f;

        // Un dash (el corte avanzando del Filo danzante, el del Pícaro) va derecho, no hacia el pecho:
        // desde los pies, apuntar al pecho lo tiraba en diagonal hacia arriba.
        if (effective is GA_Dash) aim.y = transform.position.y + 0.3f;

        // Un proyectil a su objetivo, adelantado a lo que corre (como apunta una persona; ver LeadFor).
        if (effective is GA_ProjectileShoot shot && AimsAtTarget(aim)) aim += LeadFor(shot.LaunchForce);

        // Un teletransporte de movimiento (el Destello del Clérigo) va a donde convenga, no encima del
        // enemigo (ver TeleportAim).
        if (effective is GA_Teleport teleport && slot == EAbilityInput.Movement) aim = TeleportAim(teleport, aim);

        // La mira de ESTE pedido, ya antes de preguntar si se puede: las de objetivo (GA_Target)
        // validan que haya alguien a tiro con ella.
        if (_pc != null)
        {
            _pc.NetworkAimPoint  = aim;
            _pc.NetworkAimOrigin = transform.position + Vector3.up * 1.6f;
        }

        if (!ability.CanActivate()) return false;

        // Una rueda que deja un estado (el libro del Mago): la opción que eligió ShouldUse
        // (DesiredChoice), no una al azar.
        if (ability is GA_RadialChoice choice)
        {
            int pick = _pendingChoice;
            _pendingChoice = -1;
            if (pick < 0 || pick == choice.CurrentIndex || !choice.IsRadialOptionSelectable(pick)) return false;

            choice.ActivateWithSelection(pick, transform.position);
            _netASC.ServerBroadcastAbilityAnimation(ability);
            return true;
        }

        // El compañero (el Maestro de bestias): el que eligió ShouldUse (DesiredCompanion). Hasta el 23
        // de octubre de 2026 caía en la rueda de abajo y lo cambiaba al azar cada tanto.
        if (ability is GA_SwitchCompanion companion)
        {
            int pick = _pendingCompanion;
            _pendingCompanion = -1;
            CompanionManager m = FindCompanions();
            if (m == null || pick < 0 || !companion.IsRadialOptionAvailable(pick)) return false;

            companion.ActivateWithSelection(pick, transform.position);
            if (m.ActiveIndex != pick) return false;   // no cambió (la Ira primal, el que ya estaba)

            _companionChosenAt = Time.time;
            _netASC.ServerBroadcastAbilityAnimation(ability);
            return true;
        }

        // Las de RUEDA (los tótems del Chamán) no se activan con Activate(): esperan que
        // alguien elija una opción del menú circular. Un bot no abre menús, así que elige
        // una al azar — que además hace que dos partidas no se vean iguales.
        //
        // Sin esto los chamanes nunca invocaban nada: la habilidad se activaba, no pasaba
        // nada, y el cooldown se pagaba igual.
        if (ability is IRadialMenuAbility radial)
        {
            // Con su propio ritmo, mucho más lento: ver SummonInterval.
            if (Time.time < _nextSummonAt) return false;

            int options = radial.RadialIcons != null ? radial.RadialIcons.Length : 0;
            if (options <= 0) return false;

            _nextSummonAt = Time.time + SummonInterval;

            // Una trampa va entre el enemigo y yo (ver TrapPoint); un tótem, donde apunto.
            Vector3 point = ability is GA_PlaceTrap ? TrapPoint() : aim;
            radial.ActivateWithSelection(Random.Range(0, options), GroundAimPoint(point, radial.MaxRadialRange));

            // La animación no viaja sola por este camino (el del jugador la manda aparte).
            _netASC.ServerBroadcastAbilityAnimation(ability);
            return true;
        }

        // Las de ZONA leen el punto de mira como cualquier otra, pero ese punto tiene que
        // estar EN EL SUELO y dentro de su alcance: apuntando al pecho del enemigo a 30 m,
        // la zona caía en el aire o fuera de rango.
        if (ability is IGroundTargetAbility ground && ground.UsesGroundTarget)
            aim = GroundAimPoint(aim, ground.MaxTargetRange);

        // Lo que va a canalizar se mira ANTES de activar (después, un switch ya resolvió otra cosa).
        float castSeconds = ability.UsesCast ? ability.CastDuration : 0f;

        _netASC.ServerActivateAbility(slot, aim, moveDir, transform.position + Vector3.up * 1.6f);

        // Si canaliza (la Bola de fuego, la Recarga de maná), no pide otra cosa hasta que
        // termine: la cortaría (ver Fight).
        if (castSeconds > 0f) _castingUntil = Time.time + castSeconds + 0.1f;

        // Una habilidad de MANTENER queda abierta hasta que alguien suelta el botón, y un
        // bot no tiene botones. Se la anota para soltarla sola (ver Update).
        if (HoldInput.IsHold(ability) && ability is IHoldAbility hold)
        {
            _held      = hold;
            _heldUntil = Time.time + HoldSecondsFor(effective);
        }

        return true;
    }

    // Cuánto sostiene un mantenido antes de soltarlo: un lanzamiento apuntado, lo que canalice más un
    // momento; un ataque cargado, hasta su última etapa (el arco cargado del todo, la Mordedura de hielo
    // que aturde); lo demás (el escudo), HoldSeconds.
    private float HoldSecondsFor(GameplayAbility ability)
    {
        if (ability is GA_ChargedAttack charged && charged.Stages != null && charged.Stages.Count > 0)
            return Mathf.Clamp(charged.Stages[charged.Stages.Count - 1].MinChargeTime + 0.15f, 0.2f, 4f);

        if (ability is GA_ProjectileShoot shot && shot.AimBeforeThrow)
            return shot.HoldCastSeconds + ThrowAimSeconds;

        return HoldSeconds;
    }

    // A qué aliado va una habilidad de aliado (GA_Target), a su alcance: el más herido; o nadie si el
    // más herido soy yo y se cura sola (sale sobre mí). La Proyección arcana: el más cercano que no
    // esté proyectado. null = no hay a quién.
    private AbilitySystemComponent ChooseAllyFor(GA_Target ability)
    {
        bool projection = ability is GA_ArcaneProjection;
        int count = Physics.OverlapSphereNonAlloc(
            transform.position, ability.MaxRange, _hits, CharacterLayer, QueryTriggerInteraction.Ignore);

        AbilitySystemComponent best = null;
        float bestScore = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i].GetComponentInParent<AbilitySystemComponent>();
            if (other == null || other == _asc || other.HasTag(EGameplayTag.State_Dead)) continue;
            if (!_asc.IsAllyOf(other, includeSelf: false) || other.GetComponent<PlayerController>() == null) continue;
            if (projection && other.HasTag(EGameplayTag.Status_ArcaneProjection)) continue;

            float score = projection ? Vector3.Distance(transform.position, other.transform.position)
                                     : HealthFraction(other);
            if (score >= bestScore) continue;
            bestScore = score;
            best      = other;
        }

        if (!projection && best != null && ability.SelfIfNoTarget && HealthFraction(_asc) <= HealthFraction(best))
            return null;
        return best;
    }
}
