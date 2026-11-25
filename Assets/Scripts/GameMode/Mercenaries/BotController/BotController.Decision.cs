using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// BotController — EL CICLO Y LA DECISIÓN (adónde ir)
//
// Una parte de BotController (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/BotController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class BotController
{

    // =========================================================
    // CICLO
    // =========================================================

    private void Update()
    {
        if (!_ready || _pc == null || _asc == null) return;

        // Muerto: se queda quieto hasta que el respawn lo levante. El agente se frena
        // para que el cadáver no siga empujando a los vivos.
        if (_asc.HasTag(EGameplayTag.State_Dead))
        {
            if (_agent != null && _agent.enabled && _agent.isOnNavMesh) _agent.isStopped = true;
            DriveAnimator(Vector3.zero);
            return;
        }

        // Si NO estoy en pleno salto o dash, el agente tiene que estar encendido. Ver
        // RestoreAgent: es la red que atrapa cualquier camino que lo haya dejado apagado.
        if (!_dashing) RestoreAgent();

        if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
        {
            // Aturdido o enraizado (o adentro de la Tumba helada) no camina, igual que un jugador:
            // hasta el 21 de octubre el agente seguía andando con un aturdido encima.
            _agent.isStopped = _asc.HasTag(EGameplayTag.State_Stunned) || _asc.HasTag(EGameplayTag.State_Rooted);
            _agent.speed     = ResolveMoveSpeed();   // cambia con buffs y con la carga
        }

        ReleaseHeldAbility();
        TryChooseSubclass();

        _thinkTimer -= Time.deltaTime;
        if (_thinkTimer <= 0f)
        {
            _thinkTimer = ThinkInterval * Random.Range(0.85f, 1.15f);   // no todos al mismo compás

            // Contención, antes de decidir nada: si ya está fuera del mundo, decidir a
            // quién pegarle no sirve de nada. Se saltea en pleno salto o dash, que es
            // cuando estar en el aire —lejos del NavMesh— es legítimo.
            // La red de contención, solo para el que se perdió DE VERDAD: hay que estar
            // fuera de lugar varias decisiones seguidas. Con un chequeo instantáneo, un bot
            // que pasaba un instante por un borde raro terminaba en su base sin motivo.
            if (_dashing || IsWhereItShouldBe()) _lostChecks = 0;
            else if (++_lostChecks >= LostChecksBeforeRescue) { _lostChecks = 0; RecoverIfLost(); }

            Think();
        }

        if (!_dashing) Steer();   // durante el dash manda el impulso, no el agente
        Fight();
        DriveAnimator(_flying ? _airVelocity : _agent != null && _agent.enabled ? _agent.velocity : Vector3.zero, _flying);
    }

    // Suelta la habilidad de MANTENER que esté abierta. Un jugador la suelta al soltar el
    // botón; un bot la sostiene el tiempo que diga HoldSeconds y la cierra solo. Sin esto
    // el escudo del Paladín se quedaba levantado para siempre y no volvía a salir.
    private void ReleaseHeldAbility()
    {
        if (_held == null || Time.time < _heldUntil) return;

        if (_held.IsHolding) _held.EndHold();
        _held = null;
    }

    // Al llegar al nivel máximo, un jugador elige subclase en un menú. El bot elige una
    // AL AZAR entre las que tenga su clase — que es lo que se pidió, y de paso hace que
    // dos partidas grabadas no se vean iguales.
    private void TryChooseSubclass()
    {
        if (_subclassChosen || _pc.CurrentClassDef == null) return;

        List<CharacterClassDefinition> subs = _pc.CurrentClassDef.AvailableSubclasses;
        if (subs == null || subs.Count == 0) return;

        if (_asc.GetAttributeValue(EAttributeType.Level) < _asc.MaxLevel) return;

        CharacterClassDefinition chosen = subs[Random.Range(0, subs.Count)];
        if (chosen == null) return;

        _subclassChosen = true;
        _pc.ServerEquipClassAsBot(chosen);
        Debug.Log($"[Bots] {name} evolucionó a {chosen.ClassName}.");
    }

    // =========================================================
    // DECISIÓN
    // =========================================================

    // Lo que quiere hacer: a quién pegarle y adónde ir. Lo llama Think (BotController.Instinct), que después lo corrige
    // con lo que pasa alrededor (esquivar, salir de una zona).
    private void DecideGoal()
    {
        MercenariesGameMode gm  = MercenariesGameMode.Instance;
        MercObjective       obj = gm != null ? gm.ActiveObjective : MercObjective.Instance;

        // 0 · PREPARACIÓN: nadie sale de su base.
        //
        // A un jugador lo frenan las rejas, porque su CharacterController choca con
        // ellas. Un bot camina sobre el NavMesh y las atraviesa como si no existieran
        // —se los veía saliendo al mapa con el reloj todavía en preparación—, así que la
        // regla hay que aplicarla acá.
        if (gm != null && gm.State == EMatchState.Warmup)
        {
            MercTeamBase home = gm.GetBase(_teamId);
            _destination = home != null ? home.SafeRoomWorldCenter : transform.position;
            _target      = null;
            return;
        }

        _retreating = EvaluateRetreat();
        _target     = FindBestTarget();
        TrackCombatContext();

        // 1 · El herido se va a curar y no se distrae con nada (cuándo se va cada uno: EvaluateRetreat). Al botiquín
        //     más cercano si le queda más a mano que la base. Salvo que lleve el Objetivo: con la bolsa no puede entrar
        //     a su sala, y la entrega queda en el mismo camino, así que primero entrega.
        bool carryingObjective = _asc != null && _asc.HasTag(EGameplayTag.Status_Carrying_Objective);
        if (_retreating && !carryingObjective)
        {
            _destination = RetreatDestination(gm);
            _target      = null;
            return;
        }

        // 2 · La carga manda sobre todo lo demás. Es la única forma de ganar, así que
        //     cuando está en juego el resto de la partida deja de importar.
        _chasing = false;

        if (obj != null)
        {
            if (obj.IsCarried)
            {
                AbilitySystemComponent carrier = obj.ServerCarrier;

                if (carrier == _asc)
                {
                    // La llevo YO: derecho al punto de entrega, sin desviarme por nadie. Pero sin
                    // quedarse de brazos cruzados (18 de noviembre de 2026: se plantaba en la entrega y
                    // dejaba que le pegaran sin contestar). Pelea SIN MOVERSE de su camino: en la
                    // plataforma, con el que se acerque a cortarle la entrega; corriendo, solo con el que
                    // tenga encima. Atacar no corta la entrega (la corta recibir daño), y matar o espantar
                    // al que pega sí la salva. Ver Fight.
                    MercTeamBase home = gm != null ? gm.GetBase(_teamId) : null;
                    if (home != null)
                    {
                        bool onPad = home.IsInDeliveryZone(transform.position);
                        _destination = home.DeliveryWorldPoint;
                        _target      = FindEnemyNear(transform.position, onPad ? CarrierPadFightRadius : MeleeRange * 1.5f);
                        _delivering  = true;
                        return;
                    }
                }
                else if (obj.CarrierTeam == _teamId)
                {
                    // La lleva un COMPAÑERO: escoltarlo, y pelear con lo que se le acerque
                    // A ÉL, no con lo que me quede cómodo a mí. Es la diferencia entre un
                    // equipo que protege al portador y tres bots que van cada uno a lo suyo.
                    if (carrier != null)
                    {
                        AbilitySystemComponent threat = FindEnemyNear(carrier.transform.position,
                                                                     SupportFollowDistance * 2f);
                        if (threat != null)
                        {
                            _target      = threat;
                            _chasing     = true;
                            _destination = DecideCombatPosition(threat);
                            return;
                        }

                        _destination = PositionNear(carrier.transform.position, SupportFollowDistance);
                        return;
                    }
                }
                else if (carrier != null)
                {
                    // La lleva un ENEMIGO: es EL objetivo, por encima de cualquier otro y a
                    // cualquier distancia. Nada de rodear ni despegarse esperando el
                    // cooldown: acá se le va encima y se lo intercepta.
                    _target      = carrier;
                    _chasing     = true;
                    _destination = PositionNear(carrier.transform.position, MeleeRange * 0.7f);
                    return;
                }
            }
            else
            {
                // Libre en el piso: se levanta con solo acercarse.
                _destination = obj.WorldPosition;
                return;
            }
        }

        _delivering = false;

        // 2b · El Clérigo de la vida con la Resurrección lista y un aliado muerto cerca: va
        //      hasta el cuerpo (Fight lo revive al llegar). Un compañero vale más que un golpe.
        if (FindBodyToRevive(out Vector3 body, out _, out _))
        {
            _destination = body;
            return;
        }

        // 2c · Un orbe de definitiva cerca y nadie encima: tomarlo (se canaliza solo, quedándose al lado).
        if (FindOrbToTake(out Vector3 orb) &&
            (_target == null || Vector3.Distance(transform.position, _target.transform.position) > 8f))
        {
            _destination = orb;
            return;
        }

        // 2d · Un santuario activo: a la zona, y dentro pelear con lo que haya. Se gana estando dentro,
        //      así que un enemigo lejos de ahí no lo saca de la zona.
        MatchShrine shrine = MatchEvent_Shrines.Instance != null ? MatchEvent_Shrines.Instance.ActiveShrine : null;
        if (shrine != null)
        {
            bool targetClose = _target != null &&
                               Vector3.Distance(transform.position, _target.transform.position) <= ShrineFightRadius;
            if (targetClose && shrine.Contains(_target.transform.position))
            {
                _destination = DecideCombatPosition(_target);
                return;
            }
            if (!shrine.Contains(transform.position) || !targetClose)
            {
                _destination = InsideShrine(shrine);
                return;
            }
            _destination = DecideCombatPosition(_target);
            return;
        }

        // 2e · Herido, sin nadie encima y con un botiquín a mano: pasar a buscarlo (20 de noviembre de 2026).
        if (HealthFraction(_asc) < 0.6f &&
            (_target == null || Vector3.Distance(transform.position, _target.transform.position) > 12f) &&
            FindHealthPack(12f, out Vector3 pack))
        {
            _destination = pack;
            return;
        }

        // 3 · Sin carga en juego: pelear con lo que haya.
        if (_target != null)
        {
            _destination = DecideCombatPosition(_target);
            return;
        }

        // 3b · Nadie a la vista y el jefe del evento vivo: ir por él (su bendición vale la pena).
        MercEnemyAI boss = HuntEventBoss && MatchEvent_Boss.Instance != null ? MatchEvent_Boss.Instance.ActiveBoss : null;
        if (boss != null)
        {
            _destination = PositionNear(boss.transform.position, RangedDistance * 0.6f);
            return;
        }

        // 4 · Nada a la vista. El Paladín se queda con los suyos; el resto va al centro.
        if (ResolveRole() == EBotRole.Support)
        {
            AbilitySystemComponent ally = FindNearestAlly();
            if (ally != null)
            {
                _destination = PositionNear(ally.transform.position, SupportFollowDistance * 0.5f);
                return;
            }
        }

        _destination = ResolveIdleDestination(gm);
    }

    // Retirarse y volver tienen umbrales DISTINTOS a propósito: con uno solo, el Pícaro
    // entraba y salía sin parar bailando alrededor del valor exacto.
    private bool EvaluateRetreat()
    {
        float hp = HealthFraction(_asc);

        // Si LO ESTÁN CURANDO, se queda. Antes bajaba al 35 %, decidía volver a la base, y
        // seguía yendo aunque el Paladín le devolviera la vida en el camino: la decisión
        // solo se revisaba contra el umbral de vuelta (75 %). Que la vida SUBA ya es la
        // señal de que no hace falta irse.
        bool healing = _lastHealth >= 0f && hp > _lastHealth + 0.005f;
        _lastHealth  = hp;

        // Cuándo se va: el que pega por la espalda o de lejos, antes; el Bárbaro y el soporte, que aguantan, más tarde
        // (20 de noviembre de 2026: antes solo se retiraban el Pícaro y el Mago, y los demás peleaban hasta morir).
        EBotRole role = ResolveRole();
        float below = role == EBotRole.Assassin || role == EBotRole.Ranged ? AssassinRetreatBelowHealth
                                                                            : FrontlineRetreatBelowHealth;

        if (_retreating && healing && hp > below) return false;

        // Retirarse y volver tienen umbrales DISTINTOS a propósito: con uno solo, el Pícaro
        // entraba y salía sin parar bailando alrededor del valor exacto.
        if (_retreating) return hp < AssassinReturnAboveHealth;

        // No se va si el que tiene al lado está por caer: lo remata primero.
        if (AboutToFinish()) return false;

        // Superado en número y herido, también se repliega.
        if (hp <= OutnumberedRetreatHealth && Outnumbered()) return true;
        return hp <= below;
    }

    // Actualiza lo que las reglas de habilidad consultan: si estoy trabado en combate desde
    // hace rato, y si el objetivo se está escapando.
    private void TrackCombatContext()
    {
        if (_target == null)
        {
            _engagedSince       = Time.time;   // sin objetivo, el reloj de "trabado" arranca de cero
            _targetFleeing      = false;
            _lastTargetDistance = -1f;
            return;
        }

        float dist = Vector3.Distance(transform.position, _target.transform.position);

        if (dist > MeleeRange * 1.6f) _engagedSince = Time.time;

        // Se compara contra la medición anterior: si se está alejando, es momento de lo
        // que llega de lejos.
        _targetFleeing      = _lastTargetDistance >= 0f && dist > _lastTargetDistance + 0.25f;
        _targetApproaching  = _lastTargetDistance >= 0f && dist < _lastTargetDistance - 0.25f;
        _lastTargetDistance = dist;
    }


    // Dónde se cura: su propia sala segura, que es donde la vida vuelve al máximo. Es la
    // misma regla que para un jugador — el que está herido vuelve a la base.
    private Vector3 ResolveHealSpot(MercenariesGameMode gm)
    {
        MercTeamBase home = gm != null ? gm.GetBase(_teamId) : null;
        return home != null ? home.SafeRoomWorldCenter : transform.position;
    }

    // Dónde pararse para pelear con este objetivo. Es lo único que distingue a un rol de
    // otro en el movimiento.
    private Vector3 DecideCombatPosition(AbilitySystemComponent target)
    {
        Vector3 targetPos = target.transform.position;

        // Un escudo enemigo le tapa al objetivo: a un costado desde donde no lo tape (ver TryFlankShield).
        float flankKeep = FightsFromAfar(ResolveRole()) ? RangedDistance * 0.8f : MeleeRange * 0.8f;
        if (TryFlankShield(target, flankKeep, out Vector3 flank)) return flank;

        switch (ResolveRole())
        {
            case EBotRole.Support:
            {
                // El Paladín se pone ENTRE su compañero y quien lo está atacando: así el
                // cuerpo (y el escudo) le tapan el camino, y como sus ataques son los que
                // curan, queda igual a distancia de pegar. Este no rodea: su posición es
                // el punto, no un adorno.
                //
                // El Soporte que pega DE LEJOS (la Abjuración) se pone del mismo lado, pero a distancia
                // de tiro: detrás de su compañero, que es a quien proyecta y protege. Sin verlo, se acerca.
                bool fromAfar = PrimaryIsRanged();
                float keep    = fromAfar ? RangedDistance : MeleeRange * 0.85f;
                if (fromAfar && !CanSee(target)) return PositionNear(targetPos, RangedDistance * 0.5f);

                AbilitySystemComponent ally = FindNearestAlly();
                if (ally == null) return OrbitPoint(targetPos, keep);

                Vector3 toAlly = ally.transform.position - targetPos;
                toAlly.y = 0f;
                if (toAlly.sqrMagnitude < 0.01f) return OrbitPoint(targetPos, keep);

                return targetPos + toAlly.normalized * keep;
            }

            case EBotRole.Ranged:
            {
                // El Mago pelea DE LEJOS: a distancia de tiro, rodeando para no ser un blanco
                // quieto, y alejándose si se le vienen encima (el anillo es el mismo: si está más
                // cerca, el punto queda más afuera). Sin verlo (una pared en medio) se acerca
                // hasta tenerlo a tiro. Persiguiendo al portador de la carga, se acerca a la mitad
                // de la distancia: le tira mientras lo alcanza.
                if (_chasing) return PositionNear(targetPos, RangedDistance * 0.6f);
                if (!CanSee(target)) return PositionNear(targetPos, RangedDistance * 0.5f);
                return OrbitPoint(targetPos, RangedDistance);
            }

            case EBotRole.Assassin:
            {
                // El Pícaro pega POR DETRÁS: su daño a la espalda es mayor (ver
                // BackstabDamageModifier), así que rodear hasta quedar atrás del objetivo
                // no es un adorno, es su forma de pelear.
                if (_chasing) return PositionNear(targetPos, MeleeRange * 0.7f);

                if (!PrimaryReady()) return OrbitPoint(targetPos, MeleeRange * BackOffRangeMult);

                Vector3 behind = targetPos - target.transform.forward * (MeleeRange * 0.75f);
                return NavMesh.SamplePosition(behind, out NavMeshHit hit, 3f, NavMesh.AllAreas)
                     ? hit.position
                     : OrbitPoint(targetPos, MeleeRange * 0.8f);
            }

            default:
            {
                // Bárbaro: encima cuando puede pegar, y AFUERA mientras el ataque está en
                // cooldown. Quedarse plantado comiendo golpes sin poder responder es lo
                // que los hacía ver como muñecos.
                // Persiguiendo al portador de la carga NO se rodea ni se espera el
                // cooldown: se lo intercepta. Bailar alrededor mientras se escapa con la
                // carga es perder la partida con estilo.
                if (_chasing) return PositionNear(targetPos, MeleeRange * 0.7f);

                float ring = PrimaryReady() ? MeleeRange * 0.8f : MeleeRange * BackOffRangeMult;
                return OrbitPoint(targetPos, ring);
            }
        }
    }

    private bool CanSee(AbilitySystemComponent target)
        => target != null && !Physics.Linecast(transform.position + Vector3.up * 1.4f,
                                               target.transform.position + Vector3.up * 1.2f,
                                               SightMask, QueryTriggerInteraction.Ignore);

    // ¿Puede pegar ya? Se mira el cooldown REAL de la habilidad, no mi propio
    // temporizador: así el "entrar y salir" coincide con lo que el jugador ve pasar.
    private bool PrimaryReady()
    {
        GameplayAbility primary = _pc != null ? _pc.PrimaryAttackAbility : null;
        return primary != null && Time.time >= _nextPrimaryAt && primary.CanActivate();
    }

    // Un punto a "radius" del centro, pero RODEANDO: el ángulo avanza un poco en cada
    // decisión en vez de quedarse en la línea recta enemigo-bot.
    //
    // Sin esto los nueve bots se paraban de frente, quietos, en fila — que es exactamente
    // lo que delata a una máquina. Cada uno rodea para su lado y cambia de sentido cada
    // tantos segundos, así que dos bots contra el mismo enemigo no se mueven en bloque.
    private Vector3 OrbitPoint(Vector3 center, float radius)
    {
        if (Time.time >= _strafeFlipAt)
        {
            _strafeSign   = Random.value < 0.5f ? -1f : 1f;
            _strafeFlipAt = Time.time + StrafeFlipSeconds * Random.Range(0.6f, 1.6f);
            _orbitScale   = Random.Range(0.85f, 1.15f);   // y un anillo un poco distinto: nadie pelea a una distancia fija
        }

        Vector3 away = transform.position - center;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = -transform.forward;

        away = Quaternion.Euler(0f, _strafeSign * StrafeDegreesPerThink, 0f) * away.normalized;
        return center + away * (radius * _orbitScale);
    }

    // Un punto a "distance" del centro, del lado en el que ya estoy. Sin rodeo: lo usan
    // el que escolta al portador y el Paladín que sigue a un compañero, donde girar
    // alrededor no aporta nada.
    // Corrige un destino que caiga dentro de la sala segura de OTRO equipo.
    //
    // Ahí adentro el enemigo es intocable y se cura, así que entrar no sirve de nada — y
    // desde que las bases expulsan intrusos, un bot que insista queda rebotando contra el
    // borde. Mejor que se plante justo afuera y espere a que salga.
    //
    // Con el Objetivo encima, la sala PROPIA también está prohibida (la base expulsa al
    // portador): un Pícaro herido que se vaya a curar ahí quedaría rebotando en la puerta.
    private Vector3 AvoidEnemySafeRooms(Vector3 destination)
    {
        MercenariesGameMode gm = MercenariesGameMode.Instance;
        if (gm == null) return destination;

        bool carrying = _asc != null && _asc.HasTag(EGameplayTag.Status_Carrying_Objective);

        for (int team = 1; team <= MercenariesGameMode.TeamCount; team++)
        {
            if (team == _teamId && !carrying) continue;

            MercTeamBase b = gm.GetBase(team);
            if (b == null || !b.IsInsideSafeRoom(destination)) continue;

            // Se queda en el borde, del lado por el que venía.
            Vector3 away = destination - b.SafeRoomWorldCenter;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = transform.position - b.SafeRoomWorldCenter;
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;

            float radius = Mathf.Max(b.SafeRoomSize.x, b.SafeRoomSize.z) * 0.5f + 2f;
            return b.SafeRoomWorldCenter + away.normalized * radius;
        }

        return destination;
    }

    private Vector3 PositionNear(Vector3 center, float distance)
    {
        Vector3 away = transform.position - center;
        away.y = 0f;

        if (away.sqrMagnitude < 0.01f) away = transform.forward;
        return center + away.normalized * distance;
    }

    // El orbe de definitiva activo más cercano, si me sirve (la definitiva no está llena) y está a mano.
    private bool FindOrbToTake(out Vector3 point)
    {
        point = default;
        MatchEvent_Orbs orbs = MatchEvent_Orbs.Instance;
        if (orbs == null || orbs.ActiveCount == 0 || _pc == null || !_pc.ServerCanGainUltimateCharge()) return false;

        float best = OrbSeekRadius * OrbSeekRadius;
        bool found = false;
        IReadOnlyList<OrbSpot> spots = OrbSpot.All;
        for (int i = 0; i < spots.Count; i++)
        {
            if (spots[i] == null || !orbs.IsActive(i)) continue;
            Vector3 p = spots[i].OrbPoint;
            float d = (p - transform.position).sqrMagnitude;
            if (d >= best) continue;
            best  = d;
            point = p;
            found = true;
        }

        // El orbe flota sobre el sarcófago: el destino es el piso de al lado.
        if (found && NavMesh.SamplePosition(point, out NavMeshHit hit, 3f, NavMesh.AllAreas)) point = hit.position;
        return found;
    }

    // Un punto dentro de la zona del santuario, rodeando el centro (no todos los bots al mismo lugar).
    private Vector3 InsideShrine(MatchShrine shrine)
    {
        Vector3 p = OrbitPoint(shrine.transform.position, shrine.Radius * 0.55f);
        return NavMesh.SamplePosition(p, out NavMeshHit hit, 4f, NavMesh.AllAreas) ? hit.position : shrine.transform.position;
    }

    private Vector3 ResolveIdleDestination(MercenariesGameMode gm)
    {
        if (gm != null && gm.ObjectiveSpawnPoint != null) return gm.ObjectiveSpawnPoint.position;
        return Vector3.zero;
    }
}
