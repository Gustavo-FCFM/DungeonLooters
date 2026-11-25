using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ============================================================
// BotController — REVIVIR, TRAMPAS, MARCAS Y EL COMPAÑERO
//
// Una parte de BotController (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/BotController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class BotController
{

    private bool FindBodyToRevive(out Vector3 body, out GA_Revive revive, out EAbilityInput slot)
    {
        body = Vector3.zero;
        revive = null;
        slot = EAbilityInput.None;
        foreach (EAbilityInput s in s_reviveSlots)
            if (FindAbility(s) is GA_Revive r && r.IsReady) { revive = r; slot = s; break; }
        if (revive == null) return false;

        float best = ReviveSearchRadius * ReviveSearchRadius;
        bool found = false;
        foreach (PlayerController pc in PlayerController.All)
        {
            AbilitySystemComponent asc = pc.GetComponent<AbilitySystemComponent>();
            if (asc == null || asc == _asc || !asc.HasTag(EGameplayTag.State_Dead)) continue;
            if (!_asc.IsAllyOf(asc, includeSelf: false)) continue;

            Vector3 p = GA_Revive.BodyPosition(pc);
            float d = (p - transform.position).sqrMagnitude;
            if (d >= best) continue;
            best  = d;
            body  = p;
            found = true;
        }
        return found;
    }

    // Ya al lado del cuerpo (a su alcance, con margen): lo revive, apuntándole al cuerpo.
    private bool TryRevive()
    {
        if (!FindBodyToRevive(out Vector3 body, out GA_Revive revive, out EAbilityInput slot)) return false;
        if (Vector3.Distance(transform.position, body) > revive.MaxRange * 0.85f) return false;
        return Activate(slot, body, DirectionTo(body));
    }

    // Volviendo a curarse (sin objetivo: Think se lo saca), el Trampero deja una trampa detrás
    // para el que lo viene persiguiendo.
    private bool TryTrapWhileRetreating()
    {
        if (!_retreating || !(FindAbility(EAbilityInput.Action2) is GA_PlaceTrap)) return false;

        AbilitySystemComponent chaser = FindEnemyNear(transform.position, RangedDistance * 1.5f);
        if (chaser == null) return false;

        _trapThreat = chaser;
        bool placed = Activate(EAbilityInput.Action2, chaser.transform.position, DirectionTo(chaser.transform.position));
        _trapThreat = null;
        return placed;
    }

    // Dónde va la trampa: en la línea entre el enemigo y yo. Si me retiro, cerca mío (que la pise
    // persiguiéndome); si se me viene encima, a mitad de camino.
    private Vector3 TrapPoint()
    {
        AbilitySystemComponent threat = _trapThreat != null ? _trapThreat : _target;
        if (threat == null) return transform.position;

        Vector3 to = threat.transform.position - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist < 0.01f) return transform.position;

        float along = _retreating ? 2.5f : Mathf.Max(2.5f, dist * 0.5f);
        return transform.position + to / dist * Mathf.Min(along, dist);
    }

    // El enemigo que le está pegando a un aliado (o a mí): el que está pegado a uno de los míos,
    // dentro de mi alcance y a la vista. null si nadie está en esa.
    private AbilitySystemComponent FindEnemyOnAlly(float maxRange)
    {
        // Primero los aliados (yo incluido), copiados: FindEnemyNear usa el mismo buffer _hits.
        _allyScratch.Clear();
        _allyScratch.Add(_asc);
        int count = Physics.OverlapSphereNonAlloc(
            transform.position, maxRange + MeleeRange * 2f, _hits, CharacterLayer, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent other = _hits[i].GetComponentInParent<AbilitySystemComponent>();
            if (other == null || other == _asc || other.HasTag(EGameplayTag.State_Dead)) continue;
            if (!_asc.IsAllyOf(other, includeSelf: false) || other.GetComponent<PlayerController>() == null) continue;
            if (!_allyScratch.Contains(other)) _allyScratch.Add(other);
        }

        AbilitySystemComponent best = null;
        float bestDist = float.MaxValue;
        foreach (AbilitySystemComponent ally in _allyScratch)
        {
            AbilitySystemComponent enemy = FindEnemyNear(ally.transform.position, MeleeRange * 2f);
            if (enemy == null || IsImmune(enemy)) continue;

            float d = Vector3.Distance(transform.position, enemy.transform.position);
            if (d > maxRange || d >= bestDist || !CanSee(enemy)) continue;
            bestDist = d;
            best     = enemy;
        }
        return best;
    }

    // A dónde va un teletransporte de movimiento: en apuros (o volviendo a curarme), LEJOS del que
    // peleo, a todo su alcance; un Soporte que va a socorrer, al aliado herido; si no, a donde
    // apuntaba (hacia el enemigo, o el punto de entrega con la carga).
    private Vector3 TeleportAim(GA_Teleport teleport, Vector3 aim)
    {
        if (_target != null &&
            (_retreating || InTrouble(Vector3.Distance(transform.position, _target.transform.position))))
        {
            Vector3 away = transform.position - _target.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = -transform.forward;
            return transform.position + away.normalized * teleport.MaxRange;
        }

        if (ResolveRole() == EBotRole.Support)
        {
            AbilitySystemComponent ally = FindNearestAlly();
            if (ally != null && HealthFraction(ally) < 0.6f) return ally.transform.position;
        }

        return aim;
    }

    // ¿Puedo ver a este enemigo? Un invisible no, salvo que lo hayan revelado a mi equipo (la Marca
    // del cazador, el Faro de esperanza): lo mismo que hacen los NPCs. Hasta el 23 de octubre de 2026
    // los bots perseguían a los invisibles como si nada, y el sigilo no servía contra ellos.
    private bool CanPerceive(AbilitySystemComponent other)
    {
        if (other == null || !other.IsHiddenFromEnemies) return other != null;

        NetworkAbilitySystemComponent net = other.GetComponent<NetworkAbilitySystemComponent>();
        return net != null && net.IsRevealedToTeam(_asc.TeamID);
    }

    // ¿Es INMUNE? (Status_Immunity: la Tumba helada, la Protección divina, Muerte silenciosa, el que
    // está en su sala segura). El golpe no le entra.
    private static bool IsImmune(AbilitySystemComponent other)
        => other != null && other.HasTag(EGameplayTag.Status_Immunity);

    // Contra un INMUNE no se gasta nada CONTRA él: no le hace nada, y la habilidad queda en cooldown para
    // cuando ya no lo es. Sí lo que no va contra él: protegerse o curarse, escapar, sostener a un aliado,
    // elegir hechizo o compañero. El básico sigue (Fight). Pedido de Gustavo, 23 de octubre de 2026.
    private bool UsefulAgainstImmune(GameplayAbility ability, EAbilityInput slot, EBotRole role, float dist)
    {
        // Un switch de habilidades enteras (la Q del Mago) se juzga por la que saldría.
        if (ability is GA_TagSwitch sw && sw.VariantsPayOwnCost)
            return sw.CurrentVariant != null && sw.CurrentVariant != ability &&
                   UsefulAgainstImmune(sw.CurrentVariant, slot, role, dist);

        switch (ability)
        {
            case GA_RadialChoice _:
            case GA_SwitchCompanion _:
            case GA_ShieldBlock _:
            case GA_ExactCopy _:
                return true;
            case GA_Target t:
                return t.Targets == GA_Target.ETargetSide.Allies;
        }

        // La definitiva: solo la de un soporte, que es para salvar a los suyos.
        if (slot == EAbilityInput.Action3) return role == EBotRole.Support;

        switch (Classify(ability))
        {
            case EAbilityKind.Buff:
                return IsDefensiveBuff(ability);

            case EAbilityKind.Movement:
            {
                // Para irse o para socorrer a un aliado, no para entrarle.
                GameplayAbility moving = ability is GA_TagSwitch msw && msw.CurrentVariant != null ? msw.CurrentVariant : ability;
                return _retreating || role == EBotRole.Support || (moving is GA_Teleport && InTrouble(dist));
            }

            default:
                return false;
        }
    }

    // Un buff que protege o recupera (la Tumba helada, una curación, la recarga de maná), no uno de pelea.
    private bool IsDefensiveBuff(GameplayAbility ability)
    {
        if (!(ability is GA_SelfBuff)) return false;

        _selfBuffEffects.Clear();
        ability.GetEffects(EEffectWhen.OnActivate, EEffectTarget.Self, _selfBuffEffects);
        if (AnyGrants(_selfBuffEffects, EGameplayTag.Status_Immunity)) return true;

        foreach (GameplayEffect effect in _selfBuffEffects)
            if (HealsHealth(effect) || RestoresMana(effect)) return true;

        return false;
    }

    // =========================================================
    // EL COMPAÑERO DEL MAESTRO DE BESTIAS
    //
    // Cuál conviene, según la pelea (pedido de Gustavo, 23 de octubre de 2026; antes lo cambiaba al
    // azar cada tanto), de más urgente a menos:
    //   1. Se le hizo invisible un enemigo hace poco → el que caza invisibles (el Búho).
    //   2. En apuros → el que se pone delante (el Oso).
    //   3. Peleando contra un NPC → el que atrae a los fantasmas (el Oso).
    //   4. Si no, el inicial: el de pelear (el Lobo).
    // Se queda con cada uno CompanionKeepSeconds; si el que tiene se muere, cambia enseguida por el que
    // convenga de los que no están esperando para volver. No sabe nombres: mira lo que hace cada uno en
    // su prefab (Entity_Companion).
    // =========================================================

    // El índice del compañero que conviene sacar ahora, o -1 si se queda con el que tiene.
    private int DesiredCompanion(float dist)
    {
        CompanionManager m = FindCompanions();
        if (m == null || m.Companions.Count == 0 || m.FuryActive) return -1;

        int active = m.ActiveIndex;
        bool activeDown = active < 0 || m.IsOnCooldown(active);   // se le murió: espera para volver
        if (!activeDown && Time.time - _companionChosenAt < CompanionKeepSeconds) return -1;

        int start = Mathf.Clamp(m.StartIndex, 0, m.Companions.Count - 1);
        bool vsNpc = _target != null && _target.GetComponent<PlayerController>() == null;

        int want = -1;
        if (Time.time - _lostToInvisibleAt <= InvisibleMemorySeconds) want = CompanionWith(m, c => c.HuntsInvisible);
        if (want < 0 && InTrouble(dist))                               want = CompanionWith(m, c => c.GuardOwner);
        if (want < 0 && vsNpc)                                         want = CompanionWith(m, c => c.TauntsNpcs);
        if (want < 0 && !m.IsOnCooldown(start))                        want = start;
        if (want < 0 && activeDown)                                    want = CompanionWith(m, c => true);

        return want >= 0 && want != active ? want : -1;
    }

    // El primero de la rueda que hace eso y no está esperando para volver. -1 si no hay.
    private static int CompanionWith(CompanionManager m, System.Func<Entity_Companion, bool> does)
    {
        for (int i = 0; i < m.Companions.Count; i++)
        {
            GameObject prefab = m.Companions[i].Prefab;
            Entity_Companion companion = prefab != null ? prefab.GetComponent<Entity_Companion>() : null;
            if (companion != null && does(companion) && !m.IsOnCooldown(i)) return i;
        }
        return -1;
    }

    // El CompanionManager del Maestro de bestias (en su PassiveBehaviorsPrefab). Al cambiar de clase se
    // destruye y se vuelve a buscar.
    private CompanionManager FindCompanions()
    {
        if (_companions == null) _companions = GetComponentInChildren<CompanionManager>();
        return _companions;
    }

    // ¿Ya tengo a alguien proyectado con la E (la Abjuración)? La proyección dura: no se relanza.
    private bool HasOwnProjection()
    {
        ArcaneProtectionPassive passive = GetComponentInChildren<ArcaneProtectionPassive>();
        return passive != null && passive.HasProjectionFromE;
    }

    // ¿Pelea de lejos? El que pega de lejos (el Mago, el Explorador) y el Soporte cuyo básico es a
    // distancia (la Abjuración): se queda a distancia de tiro en vez de meterse cuerpo a cuerpo.
    private bool FightsFromAfar(EBotRole role)
        => role == EBotRole.Ranged || (role == EBotRole.Support && PrimaryIsRanged());

    // Un punto EN EL SUELO hacia donde apunto, recortado al alcance de la habilidad. Es lo
    // que espera cualquier habilidad apuntada al piso: zonas, tótems, muros.
    private Vector3 GroundAimPoint(Vector3 aim, float maxRange)
    {
        Vector3 flat = aim - transform.position;
        flat.y = 0f;

        if (maxRange > 0f && flat.magnitude > maxRange) flat = flat.normalized * maxRange;

        Vector3 wanted = transform.position + flat;
        return NavMesh.SamplePosition(wanted, out NavMeshHit hit, 4f, NavMesh.AllAreas)
             ? hit.position
             : wanted;
    }

    // Las instancias vivas están en el PlayerController, que es donde EquipCharacterClass
    // las deja repartidas por slot.
    private GameplayAbility FindAbility(EAbilityInput slot)
    {
        switch (slot)
        {
            case EAbilityInput.PrimaryAttack:   return _pc.PrimaryAttackAbility;
            case EAbilityInput.SecondaryAttack: return _pc.AimAbility;
            case EAbilityInput.Action1:         return _pc.AbilityQ;
            case EAbilityInput.Action2:         return _pc.AbilityE;
            case EAbilityInput.Action3:         return _pc.AbilityR;
            case EAbilityInput.Movement:        return _pc.MovementAbility;
            default:                            return null;
        }
    }
}
