using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// BotController — LA PELEA (qué habilidad usar y cuándo)
//
// Una parte de BotController (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/BotController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class BotController
{

    // =========================================================
    // PELEA
    // =========================================================

    private void Fight()
    {
        if (_netASC == null) return;

        // Canalizando algo propio (la Bola de fuego, la Recarga de maná del Mago): pedir otra
        // habilidad la cortaría. Se espera a que termine; moverse sí puede.
        if (Time.time < _castingUntil) return;

        // LLEVANDO LA CARGA lo primero es llegar: gastar la
        // habilidad de movimiento para llegar antes al punto de entrega. Es lo que haría
        // cualquiera con la carga en la mano. Ya sobre la plataforma, no: un vuelo o un salto lo
        // sacarían de la zona de entrega, que es pegada al piso.
        if (_delivering)
        {
            MercenariesGameMode gm = MercenariesGameMode.Instance;
            MercTeamBase home = gm != null ? gm.GetBase(_teamId) : null;
            bool onPad = home != null && home.IsInDeliveryZone(transform.position);

            if (!onPad && Time.time >= _nextAbilityAt &&
                Activate(EAbilityInput.Movement, _destination + Vector3.up, DirectionTo(_destination)))
                _nextAbilityAt = Time.time + AbilityInterval;

            // Y si hay alguien a quien pegarle (ver Think), sigue abajo como cualquier pelea, pero sin
            // habilidades de movimiento (lo sacarían de la plataforma o del camino) ni la definitiva
            // (con la carga, ese botón la suelta).
            if (_target == null) return;
        }

        // Revivir va antes que pelear (ver FindBodyToRevive). Y volviendo a curarse sin objetivo, el
        // Trampero deja una trampa detrás para el que lo persigue.
        if (Time.time >= _nextAbilityAt && (TryRevive() || TryTrapWhileRetreating()))
        {
            _nextAbilityAt = Time.time + AbilityInterval;
            return;
        }

        // Sin nadie a quien pegarle y lejos de donde va (la pelea, el Objetivo, su base para
        // curarse): el Mago vuela hasta allá.
        if (_target == null && Time.time >= _nextAbilityAt && TryFlyToDestination())
        {
            _nextAbilityAt = Time.time + AbilityInterval;
            return;
        }

        // Retirándose con alguien encima: se lo saca de encima con su dash o su teletransporte (ver TryEscapeChaser).
        if (_target == null && _retreating && Time.time >= _nextAbilityAt && TryEscapeChaser())
        {
            _nextAbilityAt = Time.time + AbilityInterval;
            return;
        }

        if (_target == null) return;
        if (_target.HasTag(EGameplayTag.State_Dead)) { _target = null; return; }
        if (!CanPerceive(_target))   // se volvió invisible: lo pierdo (y el Maestro de bestias saca al Búho)
        {
            _lostToInvisibleAt = Time.time;
            _target = null;
            return;
        }

        float dist = Vector3.Distance(transform.position, _target.transform.position);

        // Recién lo vio: todavía no reaccionó (ver ReactToTargetChange). Una persona no pega en el mismo instante.
        if (StillReacting) return;

        // Punto de mira: el pecho, no los pies. Las habilidades que trazan un rayo desde
        // el personaje fallarían contra el piso. Con el error de su puntería (ver AimError).
        Vector3 aim     = _target.transform.position + Vector3.up * 1.2f + AimError(dist);
        Vector3 moveDir = transform.forward;

        EBotRole role = ResolveRole();

        if (Time.time >= _nextAbilityAt && dist <= VisionRadius)
        {
            // Lejos prueban más seguido: así castigan mientras se acercan en vez de
            // llegar con todo intacto y recién ahí soltarlo. Persiguiendo al portador, lo
            // mismo — cada segundo cuenta.
            bool approaching = _chasing || dist > MeleeRange * 1.6f;

            if (TryAbilities(role, dist, aim, moveDir))
                _nextAbilityAt = Time.time + (approaching ? ApproachAbilityInterval : AbilityInterval);
        }

        // El básico: cuerpo a cuerpo, en contacto; el que pega de lejos (el Mago), a distancia de
        // tiro y solo si lo ve (tirar contra una pared es regalar el ritmo).
        if (Time.time >= _nextPrimaryAt &&
            (FightsFromAfar(role) ? dist <= RangedAttackRange && CanSee(_target) : dist <= MeleeRange * 1.3f))
        {
            if (Activate(EAbilityInput.PrimaryAttack, aim, moveDir))
                _nextPrimaryAt = Time.time + PrimaryInterval;
        }
    }

    private Vector3 DirectionTo(Vector3 point)
    {
        Vector3 d = point - transform.position;
        d.y = 0f;
        return d.sqrMagnitude > 0.01f ? d.normalized : transform.forward;
    }

    private static EAbilityKind Classify(GameplayAbility a)
    {
        // Un cargado o un lanzamiento que se apunta (GA_ChargedAttack) se juzga por lo que
        // sale al soltar: su última etapa (el hacha, las dagas, los conos del Maestro).
        if (a is GA_ChargedAttack charged && charged.Stages.Count > 0)
        {
            GameplayAbility last = charged.Stages[charged.Stages.Count - 1].Ability;
            if (last != null) return Classify(last);
        }

        // Un combo cuyos pasos son TODOS a distancia (las kunais del Shinobi) es un ataque a
        // distancia. Hasta el 29 de octubre de 2026 caía en "cuerpo a cuerpo": el Shinobi bot
        // peleaba por la espalda y solo tiraba las kunais a 3 m. Un combo mixto sigue siendo
        // cuerpo a cuerpo.
        if (a is GA_ComboSequence combo && combo.Sequence != null && combo.Sequence.Count > 0)
        {
            bool allRanged = true;
            foreach (GA_ComboSequence.ComboStep step in combo.Sequence)
                if (step.AbilityToCast == null || step.AbilityToCast == a ||
                    Classify(step.AbilityToCast) != EAbilityKind.Ranged) { allRanged = false; break; }
            if (allRanged) return EAbilityKind.Ranged;
        }

        // Un switch cuya habilidad normal es de MOVIMIENTO (el Shift del Monje: la Patada voladora, o
        // la del dragón con Ki) es un movimiento; los demás switches (las posturas, el Ki), un buff.
        if (a is GA_TagSwitch sw && sw.DefaultAbility != null && sw.DefaultAbility != a &&
            Classify(sw.DefaultAbility) == EAbilityKind.Movement)
            return EAbilityKind.Movement;

        if (a is GA_SelfBuff || a is GA_TagSwitch || a is GA_ShieldBlock) return EAbilityKind.Buff;

        // El Golpe final del Inmortal no hereda de ninguna genérica —es su propia clase—
        // pero barre un área por delante: se juzga como área, o sea con varios enemigos
        // juntos. El Molinete sí hereda de GA_ContinuousAoE y entra solo más abajo.
        if (a is GA_FinalBlow) return EAbilityKind.Aoe;

        // La embestida (la Patada voladora del Monje) y el teletransporte (el Destello del Clérigo)
        // también: hasta el 23 de octubre de 2026 el Destello caía en "cuerpo a cuerpo" y el Clérigo se
        // teletransportaba encima del que le pegaba.
        if (a is GA_Dash || a is GA_LeapAttack || a is GA_Blink || a is GA_HeroicInterception ||
            a is GA_RushAttack || a is GA_Teleport)
            return EAbilityKind.Movement;

        if (a is GA_ProjectileShoot || a is GA_HitscanShot) return EAbilityKind.Ranged;

        // Lo que se pone en el piso con una zona (el Muro de fuego y la Tormenta del Mago) se
        // juzga como un área: donde están los enemigos.
        if (a is GA_InstantAoE || a is GA_ContinuousAoE || a is GA_ConeAttack || a is GA_LineAttack ||
            a is GA_SpawnSummon)
            return EAbilityKind.Aoe;

        return EAbilityKind.Melee;
    }

    // Recorre TODO el kit y usa lo que tenga sentido AHORA. La regla de cada habilidad sale
    // de dos cosas: qué es (Classify) y qué rol juega este bot.
    private bool TryAbilities(EBotRole role, float dist, Vector3 aim, Vector3 moveDir)
    {
        bool immune = IsImmune(_target);

        foreach (EAbilityInput slot in CombatSlots)
        {
            GameplayAbility ability = FindAbility(slot);
            if (ability == null) continue;

            // Llevando el Objetivo: ni moverse (ver Fight) ni la definitiva (con la carga, ese botón la suelta).
            if (_delivering && (slot == EAbilityInput.Movement || slot == EAbilityInput.Action3)) continue;

            if (ability == null) continue;

            if (immune && !UsefulAgainstImmune(ability, slot, role, dist)) continue;
            if (!ShouldUse(ability, slot, role, dist)) continue;
            if (Activate(slot, aim, moveDir)) return true;
        }

        return false;
    }

    // El criterio, habilidad por habilidad. Está escrito como lo diría un jugador.
    private bool ShouldUse(GameplayAbility ability, EAbilityInput slot, EBotRole role, float dist)
    {
        bool engaged = dist <= MeleeRange * 1.6f;

        // CASOS CON NOMBRE PROPIO. Van primero porque su momento no se deduce del tipo: son
        // habilidades pensadas para una jugada concreta, y tratarlas como "un área más" o
        // "un buff más" las desperdicia.
        switch (ability)
        {
            // Un SWITCH cuyas variantes son habilidades enteras (cada una cobra y canaliza lo
            // suyo: la Q del Mago, que es otra según el hechizo elegido) se juzga por la que
            // saldría AHORA. Los demás switches (las posturas, el Ki) siguen siendo un buff.
            case GA_TagSwitch sw when sw.VariantsPayOwnCost:
            {
                GameplayAbility variant = sw.CurrentVariant;
                return variant != null && variant != ability && ShouldUse(variant, slot, role, dist);
            }

            // Una rueda que DEJA UN ESTADO (el libro de hechizos del Mago): se cambia cuando
            // conviene otra opción (ver DesiredChoice). Activate usa la que eligió.
            case GA_RadialChoice choice:
            {
                _pendingChoice = DesiredChoice(choice, dist);
                return _pendingChoice >= 0 && _pendingChoice != choice.CurrentIndex;
            }

            // El compañero del Maestro de bestias (la rueda del Lobo, el Oso y el Búho): se cambia
            // según la pelea (ver DesiredCompanion). Activate usa el que eligió.
            case GA_SwitchCompanion _:
                _pendingCompanion = DesiredCompanion(dist);
                return _pendingCompanion >= 0;

            // Un buff que se termina apretándolo otra vez (la Tumba helada): ya puesto, apretarlo
            // lo sacaría antes. Se deja correr.
            case GA_SelfBuff selfBuff when selfBuff.EndTag != EGameplayTag.None && _asc.HasTag(selfBuff.EndTag):
                return false;

            // La auto-revivida del Inmortal la dispara la MUERTE (ver
            // PlayerController.HandlePlayerDeath). Gastarla a mano lo deja muriendo de
            // verdad la próxima vez.
            case GA_ImmortalWrath _:
                return false;

            // Una definitiva que alcanza a todo el mapa (GA_InstantAoE "Todo el mapa"; 19 de noviembre de
            // 2026): no importa a cuántos tenga cerca, sale apenas pelea.
            case GA_InstantAoE global when global.Global:
                return _target != null;

            // El señuelo del Ilusionista: se planta cuando alguien está por pegarle, para
            // que le peguen a la copia. Antes salía en cualquier momento y la arena se
            // llenaba de señuelos parados.
            case GA_ExactCopy _:
                return _target != null && dist <= MeleeRange * 1.4f;

            // La fiesta de copias rinde con el equipo junto: cada compañero cerca es una
            // copia más.
            case GA_CopyParty _:
                return AlliesNear(transform.position, AoeRadius * 1.6f) >= 2;

            // El cañoneo del Pirata es daño en área: contra dos o más.
            case GA_CannonBarrage _:
                return EnemiesNear(_target.transform.position, AoeRadius) >= 2;

            // Revivir (la Resurrección del Clérigo de la vida o cualquier GA_Revive) va aparte, antes de
            // pelear (TryRevive): apunta al cuerpo de un aliado muerto, no al enemigo. Por acá nunca
            // encontraría cuerpo.
            case GA_Revive _:
                return false;

            // La Presa del Cazador marca a un JUGADOR (no a un NPC) a distancia: va sobre el que
            // pelea apenas lo ve y está a tiro. Hasta el 28 de octubre de 2026 caía en "cuerpo a
            // cuerpo" y el Cazador, que pelea de lejos, casi nunca la usaba.
            case GA_Prey prey:
                return _target != null && _target.GetComponent<PlayerController>() != null &&
                       dist <= prey.MaxRange && CanSee(_target);

            // Las trampas del Trampero: entre él y el enemigo que se le viene encima (TrapPoint). Al
            // retirarse, una detrás para el que lo persigue (TryTrapWhileRetreating).
            case GA_PlaceTrap trap:
                return _target != null && _targetApproaching &&
                       dist > MeleeRange * 2f && dist <= trap.MaxPlaceRange;

            // La Ejecución del Guardián REMATA (su daño crece con la vida que le falta al otro, y si
            // mata se reinicia): cuerpo a cuerpo, contra uno con poca vida. Antes se juzgaba como un
            // área y esperaba a tener dos enemigos juntos.
            case GA_Execute _:
                return _target != null && dist <= MeleeRange * 1.4f && HealthFraction(_target) <= ExecuteBelowHealth;

            // Las MARCAS que protegen al equipo (Aturdir del Clérigo del orden, Enemigo jurado del
            // Paladín de la venganza): sobre el enemigo que le está pegando a un aliado (o a mí), a su
            // alcance. Antes solo salían trabado cuerpo a cuerpo, y un soporte casi nunca lo está.
            case GA_CommandHalt halt:
                _markTarget = FindEnemyOnAlly(halt.MaxRange);
                return _markTarget != null;

            case GA_SwornEnemy sworn:
                _markTarget = FindEnemyOnAlly(sworn.MaxRange);
                return _markTarget != null;

            // La Furia elemental del Chamán pone los tótems y el tornado ALREDEDOR SUYO: con varios
            // juntos, y él metido en la pelea (antes salía aunque estuviera lejos).
            case GA_ElementalFury _:
                return _target != null && engaged && EnemiesNear(_target.transform.position, AoeRadius) >= 2;

            // La marca del Asesino es para REMATAR: un jugador con poca vida en el punto.
            case GA_MarkedForDeath _:
                return _target != null
                    && _target.GetComponent<PlayerController>() != null
                    && HealthFraction(_target) <= ExecuteBelowHealth;

            // EL ESCUDO se levanta para DEFENDER, no porque sí. Bloquear también cura, así
            // que es la forma de sostener al equipo sin dejar de estar delante — pero
            // mientras nadie corre peligro conviene PEGAR, porque sus ataques son los que
            // curan. Y además hay que tener al enemigo encima: un escudo contra nadie no
            // hace nada y queda en cooldown.
            case GA_ShieldBlock _:
                return SomeoneInDanger() && dist <= MeleeRange * 1.8f;

            // La Proyección arcana (la E de la Abjuración) DURA: se pone una vez, sobre el aliado más
            // cercano, y no se relanza mientras siga (moverla a cada rato le tiraba el escudo).
            case GA_ArcaneProjection projection:
                return !HasOwnProjection() && ChooseAllyFor(projection) != null;

            // Una definitiva de vuelo (la Sobrecarga de hechizos de la Evocación): levita a FlightHeight
            // con la resistencia, el maná y las Q al instante (ver VUELO). Va en plena pelea, a su
            // distancia.
            case GA_Flight _ when slot == EAbilityInput.Action3:
                return _target != null && CanSee(_target) &&
                       dist <= (FightsFromAfar(role) ? RangedAttackRange : MeleeRange * 3f);

            // EL VUELO del Shift (el Mago, 29 de octubre de 2026): despega para escapar (volviendo a
            // curarse), para pelear desde arriba (con el enemigo a tiro y a la vista) y para alcanzar
            // al que está lejos (persiguiendo al que lleva el Objetivo). Volando, no se reactiva; bajo
            // un techo, no despega. Sin nadie a quien pegarle lo decide TryFlyToDestination.
            case GA_Flight _:
                if (_flying || !HasHeadroom(FlightHeight)) return false;
                if (_retreating) return true;
                return _target != null &&
                       ((CanSee(_target) && dist <= RangedAttackRange) || dist > FlyTravelDistance);

            // Las de OBJETIVO dicen a quién apuntan, así que la regla sale del asset:
            // a un ALIADO son curaciones, escudos y protecciones —van cuando alguien está
            // herido—; a un ENEMIGO son marcas, y van sobre el que estoy peleando.
            case GA_Target t:
            {
                if (t.Targets != GA_Target.ETargetSide.Allies) return _target != null;

                // Un escudo o una curación se ponen ANTES (50 %); la definitiva se guarda
                // para la emergencia de verdad (30 %).
                float threshold = slot == EAbilityInput.Action3 ? AllyDangerHealth : AllySupportHealth;
                return SomeoneBelow(threshold);
            }
        }

        // UN ATURDIMIENTO vale contra UNO SOLO: frenar a alguien es tan útil como pegarle,
        // y guardarlo esperando a que se junten dos es desperdiciarlo. Se detecta por el
        // efecto que aplica, no por el nombre de la habilidad.
        if (AppliesStun(ability)) return _target != null;

        // LA DEFINITIVA (R) se juzga por ROL: cada clase la tiene para algo distinto y
        // usarla apenas sale de cooldown es tirarla.
        if (slot == EAbilityInput.Action3)
        {
            switch (role)
            {
                case EBotRole.Support:  return SomeoneInDanger();
                // Una definitiva que es un BUFF del que pega de lejos (la Protección máxima de la
                // Abjuración, la Ira primal del Explorador) va en plena pelea, a su distancia; las de
                // área, contra varios juntos (abajo).
                case EBotRole.Ranged when Classify(ability) == EAbilityKind.Buff:
                    return CanSee(_target) && dist <= RangedAttackRange;
                // Una definitiva de ÁREA de un asesino (el Baile final del Filo danzante) vale contra
                // varios juntos, o para rematar; las demás, solo para rematar.
                case EBotRole.Assassin:
                    return HealthFraction(_target) <= ExecuteBelowHealth ||
                           (Classify(ability) == EAbilityKind.Aoe && EnemiesNear(_target.transform.position, AoeRadius) >= 2);
                default:                return EnemiesNear(_target.transform.position, AoeRadius) >= 2;
            }
        }

        switch (Classify(ability))
        {
            case EAbilityKind.Buff:
                return ShouldUseBuff(ability, engaged, dist);

            case EAbilityKind.Ranged:
                // Antes del contacto, o cuando el otro se está yendo: es justo cuando un
                // ataque a distancia rinde y el cuerpo a cuerpo no llega. Y con poca vida,
                // siempre — pegar de lejos es lo único que se puede hacer sin morirse.
                return !engaged || _targetFleeing || HealthFraction(_asc) < 0.4f;

            case EAbilityKind.Aoe:
                // El que pega de lejos (el Mago) apunta sus áreas al piso donde está el que
                // pelea, aunque esté solo: el Levantamiento, el Muro, la Tormenta son su daño.
                if (FightsFromAfar(role) && ability is IGroundTargetAbility ground && ground.UsesGroundTarget)
                    return _target != null && dist <= ground.MaxTargetRange && CanSee(_target);

                // Vale la pena con dos o más juntos; con uno solo, el básico alcanza.
                return EnemiesNear(_target.transform.position, AoeRadius) >= 2;

            case EAbilityKind.Movement:
                return ShouldUseMovement(ability, role, dist);

            default:
                return engaged;   // lo cuerpo a cuerpo, en contacto
        }
    }

    // No todos los buffs se usan en el mismo momento, y NO hace falta marcarlos a mano: se
    // mira lo que el efecto HACE.
    //
    //  · Da Status_Invisible  → es un sigilo: se usa ANTES de entrar, para acercarse. Con
    //    la regla general (trabado en combate) el Asesino no lo usaba nunca, que es
    //    justamente lo contrario de para qué existe.
    //  · Sube la vida         → es una curación de emergencia: solo con poca vida.
    //  · Da Status_Immunity   → es un salvavidas (la Tumba helada): solo en apuros, con
    //    alguien encima.
    //  · Devuelve maná        → una recarga (la del Mago): con poco maná y nadie encima.
    //  · Cualquier otro       → buff de pelea: trabado y después de un momento, para no
    //    gastarlo caminando apenas sale de cooldown.
    private bool ShouldUseBuff(GameplayAbility ability, bool engaged, float dist)
    {
        // Lo que se aplica a sí mismo al lanzarla (las entradas "al activarse → el lanzador").
        _selfBuffEffects.Clear();
        if (ability is GA_SelfBuff) ability.GetEffects(EEffectWhen.OnActivate, EEffectTarget.Self, _selfBuffEffects);

        if (AnyGrants(_selfBuffEffects, EGameplayTag.Status_Invisible))
            return !engaged && _target != null;

        if (AnyGrants(_selfBuffEffects, EGameplayTag.Status_Immunity))
            return InTrouble(dist);

        foreach (GameplayEffect effect in _selfBuffEffects)
            if (RestoresMana(effect))
                return ManaFraction() <= LowManaFraction && (_target == null || dist > MeleeRange * 3f);

        foreach (GameplayEffect effect in _selfBuffEffects)
            if (HealsHealth(effect))
                return HealthFraction(_asc) <= 0.45f;

        // Un buff de pelea se usa cuando HAY pelea: con un enemigo cerca, no despues de
        // aguantar un rato trabado. El Castigo del Paladin es justo eso — carga el proximo
        // ataque, asi que llega tarde si espera a estar peleando hace segundo y medio.
        // "Cerca" depende de cómo pelea: el que pega de lejos (el Mago: la Memoria perfecta) lo usa
        // a su distancia de tiro.
        float near = FightsFromAfar(ResolveRole()) ? RangedDistance * 1.5f : MeleeRange * 3f;
        if (_target != null && Vector3.Distance(transform.position, _target.transform.position) <= near)
            return true;

        return engaged && Time.time - _engagedSince >= BuffAfterSeconds;
    }

    // ¿Estoy en apuros? Con poca vida y el que peleo encima (o cerca: el Mago no espera a que
    // lo toquen).
    private bool InTrouble(float dist)
        => _target != null && HealthFraction(_asc) <= EmergencyHealth && dist <= RangedDistance;

    // Cuánto maná le queda (1 si la clase no usa maná).
    private float ManaFraction()
    {
        float max = _asc.GetAttributeValue(EAttributeType.MaxMana);
        return max > 0f ? _asc.GetAttributeValue(EAttributeType.Mana) / max : 1f;
    }

    private static bool RestoresMana(GameplayEffect effect)
    {
        if (effect == null || effect.Modifiers == null) return false;

        foreach (Modifier m in effect.Modifiers)
            if (m != null && m.Attribute == EAttributeType.Mana &&
                (m.Magnitude > 0f || (m.UseAttributeScaling && m.AttributeCoefficient > 0f)))
                return true;

        return false;
    }
}
