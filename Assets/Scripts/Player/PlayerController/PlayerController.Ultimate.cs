using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — CARGA DE LA DEFINITIVA
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    // Decide con cuánta definitiva sale la clase recién equipada. La llama
    // EquipCharacterClass, del lado del servidor, con lo que tenía la clase anterior.
    private void ResolveUltimateCharge(bool hadUltimate, float previousCharge)
    {
        // Se deja una clase con definitiva: se guarda la parte que se conserva. Si la
        // anterior no tenía (una clase base), no hay nada nuevo que guardar y lo que
        // estuviera guardado sigue ahí — cambiar de base a base no recorta dos veces.
        if (hadUltimate)
            _carriedUltimateCharge = previousCharge * UltimateKeptOnClassChange;

        // La nueva no tiene definitiva (una clase base): nada que poner.
        if (AbilityR == null) return;

        float start;
        if (StartSubclassWithFullUltimate) start = 1f;
        else if (_carriedUltimateCharge >= 0f) start = _carriedUltimateCharge;
        else start = 0f;

        _carriedUltimateCharge = -1f;
        SetUltimateCharge(start);
    }

    // Deja la definitiva con una carga exacta, de 0 (recién usada) a 1 (lista).
    // Pone el cooldown entero y le descuenta la parte ya cargada: así la barra del HUD
    // muestra el total real de 180 s y no un cooldown más corto.
    private void SetUltimateCharge(float charge)
    {
        if (AbilityR == null || AbilityR.CooldownEffect == null) return;

        ASC.RemoveEffectsWithTag(EGameplayTag.Ability_Cooldown_Ultimate);

        charge = Mathf.Clamp01(charge);
        if (charge >= 1f) return;   // lista: sin cooldown puesto

        float total = AbilityR.CooldownDurationSeconds;
        if (total <= 0f) return;

        ASC.ApplyGameplayEffect(AbilityR.CooldownEffect, AbilityR, total);
        ASC.ReduceCooldownByTag(EGameplayTag.Ability_Cooldown_Ultimate, total * charge);
    }

    // --- La carga por hacer el rol ---

    private void OnDamageEnduredForRole(float damage)
    {
        _lastDamagedAt = Time.time;   // para el desatascarse: no se usa en combate
        ChargeUltimateForRole(EClassRole.Tank, damage * TankChargePerDamage);
    }

    private void OnHealedAllyForRole(AbilitySystemComponent ally, float healed)
        => ChargeUltimateForRole(EClassRole.Support, healed * SupportChargePerHeal);

    // La llama la VÍCTIMA al morir (HandlePlayerDeath), sobre quien la mató.
    public void NotifyKilledCharacter()
        => ChargeUltimateForRole(EClassRole.Damage, DamageChargePerKill);

    private void ChargeUltimateForRole(EClassRole role, float seconds)
    {
        if (seconds <= 0f || ASC == null) return;
        if (IsSpawned && !IsServerInitialized) return;
        if (CurrentClassDef == null || CurrentClassDef.Role != role) return;

        ASC.ReduceCooldownByTag(EGameplayTag.Ability_Cooldown_Ultimate, seconds);
    }

    // --- La carga de los orbes del mapa (MatchEvent_Orbs, 11 de noviembre de 2026) ---

    // ¿Le sirve más carga? False si ya la tiene llena (así el orbe queda para otro). Una clase base, que
    // no tiene definitiva, mira lo que guarda para su subclase.
    public bool ServerCanGainUltimateCharge()
    {
        if (ASC == null) return false;
        if (AbilityR == null) return _carriedUltimateCharge < 1f;
        return ASC.GetUltimateCharge() < 1f;
    }

    // Suma una parte (0 a 1) de la definitiva entera. Una clase base la guarda para la subclase, igual
    // que lo que se conserva al cambiar de clase.
    public void ServerAddUltimateCharge(float fraction)
    {
        if (fraction <= 0f || ASC == null) return;

        if (AbilityR == null)
        {
            _carriedUltimateCharge = Mathf.Clamp01(Mathf.Max(0f, _carriedUltimateCharge) + fraction);
            return;
        }

        float total = AbilityR.CooldownDurationSeconds;
        if (total > 0f) ASC.ReduceCooldownByTag(EGameplayTag.Ability_Cooldown_Ultimate, total * fraction);
    }

    // Cambia la clase del personaje: limpia estado anterior, actualiza
    // visuales/armas, otorga las nuevas habilidades por slot, recarga
    // atributos base, y (si sos el dueño) sincroniza el cambio por red y
    // refresca el HUD.
    // resetProgress=true reinicia nivel/exp a 1/0 (empezás la clase nueva desde
    // cero) — lo usa el cambio manual de clase desde el menú. En false (por
    // defecto) conserva el progreso, que es lo correcto al evolucionar a subclase
    // o al equipar la clase inicial (que ya está en nivel 1).
    public void EquipCharacterClass(CharacterClassDefinition newClass, bool resetProgress = false)
    {
        if (newClass == null || ASC == null) return;

        // ANTES de tocar nada: cerrar los mantenidos en curso. Sus instancias están a
        // punto de descartarse, y una habilidad de mantener que se va sin cerrarse
        // deja su corutina viva y al jugador trabado. Ver EndActiveHolds.
        EndActiveHolds();

        // Cuánta definitiva tenía la clase que se va, leída ANTES de que la línea de
        // abajo borre todos los efectos — el cooldown de la definitiva incluido. Ver
        // "CARGA DE LA DEFINITIVA".
        bool  serverSide     = !IsSpawned || IsServerInitialized;
        bool  hadUltimate    = AbilityR != null;
        float previousCharge = serverSide && hadUltimate ? ASC.GetUltimateCharge() : 0f;

        ASC.RemoveAllActiveEffects();
        if (_wallActive) StopWall();   // la clase nueva puede no engancharse a las paredes
        CurrentClassDef  = newClass;
        ASC.CurrentClass = newClass;
        CharacterIcon    = newClass.ClassIcon;

        // Comportamientos de código propios de la clase (PassiveBehaviorsPrefab):
        // se instancian como hijo del jugador y se destruyen al cambiar de clase.
        SetupPassiveBehaviors(newClass);

        // Armas equipadas + animator override de la clase. Esta llamada se había
        // perdido: por eso el dueño/host no veía las armas ni le cambiaban las
        // animaciones (ej. el bárbaro). Corre para el dueño y para la copia del
        // servidor de un remoto; los observadores puros lo reciben por
        // OnNetClassIndexChanged.
        UpdateVisuals(newClass);

        ASC.ClearGrantedAbilities();
        MovementAbility = AbilityQ = AbilityE = AbilityR =
            PrimaryAttackAbility = AimAbility = null;

        foreach (var assignment in newClass.Abilities)
        {
            if (assignment.Ability == null) continue;
            GameplayAbility inst = ASC.GrantAbility(assignment.Ability);
            switch (assignment.InputSlot)
            {
                case EAbilityInput.PrimaryAttack:   PrimaryAttackAbility = inst; inst.IsInterruptible = true; inst.IsBasicAttack = true; break;
                case EAbilityInput.SecondaryAttack: AimAbility           = inst; break;
                case EAbilityInput.Action1:         AbilityQ             = inst; break;
                case EAbilityInput.Action2:         AbilityE             = inst; break;
                case EAbilityInput.Action3:         AbilityR             = inst; inst.IsUltimate = true; break;
                case EAbilityInput.Movement:        MovementAbility      = inst; break;
            }
        }

        if (newClass.BaseAttributes != null)
        {
            ASC.CharacterRoleDefinition = newClass.BaseAttributes;
            ASC.InitializeAttributes(keepProgress: !resetProgress);
        }

        // Pasivas de la clase (GEs siempre activos, ej: Ataque Furtivo del Pícaro).
        // Se aplican DESPUÉS de InitializeAttributes (que resetea los modificadores)
        // y solo en el lado autoritativo: el servidor los aplica y sus tags/stats
        // se sincronizan a los clientes por los canales normales del NetworkASC. En
        // una escena sin red (IsSpawned=false) se aplican localmente.
        if (serverSide)
        {
            ApplyClassPassives(newClass);
            ResolveUltimateCharge(hadUltimate, previousCharge);
        }

        if (IsOwner)
        {
            int idx = GetClassIndex(newClass);
            if (idx >= 0) ServerSetClass(idx, resetProgress);
            else
                // La clase no está en la lista plana (no es alcanzable desde
                // MainBaseClasses vía AvailableSubclasses). Sin sincronizar,
                // el servidor se queda con la clase vieja y después tira
                // "No se encontró habilidad en slot ..." al activar una
                // habilidad nueva de esta clase. Revisar que newClass sea
                // subclase (directa o indirecta) de alguna de MainBaseClasses.
                Debug.LogError($"[PlayerController] '{newClass.ClassName}' no está en " +
                               $"MainBaseClasses ni en sus subclases — no se puede sincronizar " +
                               $"la clase al servidor. Las habilidades de esta clase fallarán en red.");
            UpdateHUD();
        }
    }

    // Cierra cualquier habilidad de MANTENER que siga activa, justo antes de descartar
    // las instancias otorgadas (cambio de clase / evolución a subclase).
    //
    // Corre en TODOS los peers, y hace falta en los dos lados por motivos distintos:
    //
    //  · En el SERVIDOR: un GA_ShieldBlock a medio mantener tiene su corutina de
    //    vigilancia corriendo sobre el ASC. Si simplemente descartamos la instancia,
    //    esa corutina queda viva para siempre girando en el vacío, y el efecto de
    //    bloqueo nunca se retira por la vía normal.
    //
    //  · En el DUEÑO: libera el estado de input. Se hace acá y no con el pedido normal
    //    al servidor porque para este punto CurrentClass ya apunta a la clase NUEVA,
    //    así que resolver la habilidad por slot daría cualquier cosa. Recorremos las
    //    instancias directamente, que es lo único fiable.
    private void EndActiveHolds()
    {
        if (ASC != null)
        {
            foreach (var granted in ASC.GrantedAbilities)
                if (granted is IHoldAbility hold && hold.IsHolding) hold.EndHold();
        }

        // Suelta _holdAbility, baja la animación de mantenido y libera isAttacking.
        FinishAttack();
    }

    // Aplica los GEs pasivos de la clase (CharacterClassDefinition.PassiveEffects).
    // RemoveAllActiveEffects() al inicio de EquipCharacterClass ya limpió las
    // pasivas de la clase anterior, así que no se acumulan al evolucionar.
    private void ApplyClassPassives(CharacterClassDefinition cls)
    {
        if (cls.PassiveEffects == null) return;
        // ApplyClassPassive y no ApplyGameplayEffect: las deja anotadas como pasivas de
        // clase, que es lo que las salva de la limpieza de buffs al morir.
        foreach (var passive in cls.PassiveEffects)
            if (passive != null) ASC.ApplyClassPassive(passive);
    }

    // Destruye los comportamientos de código de la clase anterior e instancia los
    // de la nueva (ver CharacterClassDefinition.PassiveBehaviorsPrefab). Corre en
    // TODOS los peers (igual que otorgar habilidades): así el servidor tiene la
    // lógica que reacciona a sus eventos (ej. IllusoryBladesPassive escuchando
    // OnDealtDamage) y cada cliente la suya si la necesita. Los componentes del
    // prefab llegan al ASC del jugador con GetComponentInParent, ya que quedan como
    // hijos de este GameObject.
    private void SetupPassiveBehaviors(CharacterClassDefinition cls)
    {
        if (currentPassiveBehaviors != null)
        {
            Destroy(currentPassiveBehaviors);
            currentPassiveBehaviors = null;
        }

        if (cls.PassiveBehaviorsPrefab != null)
        {
            currentPassiveBehaviors = Instantiate(cls.PassiveBehaviorsPrefab, transform);
            currentPassiveBehaviors.transform.localPosition = Vector3.zero;
            currentPassiveBehaviors.transform.localRotation = Quaternion.identity;
        }
    }

    // Sincroniza el índice de clase elegido al SyncVar y, en la copia del
    // SERVIDOR de un jugador remoto, equipa la clase completa (habilidades
    // incluidas). Sin ese re-equip en el servidor, este seguía con las
    // habilidades de la clase vieja y tiraba "No se encontró habilidad en
    // slot ..." al intentar activar una habilidad de la subclase nueva.
    [ServerRpc]
    private void ServerSetClass(int classIndex, bool resetProgress)
    {
        _netClassIndex.Value = classIndex;

        // Si somos el dueño (host), ya lo equipamos localmente en
        // EquipCharacterClass. Para un jugador remoto, esta copia server-side
        // necesita el equip completo (otorga las habilidades que después
        // FindAbilityBySlot busca). resetProgress viaja igual que en el dueño para
        // que el reinicio a nivel 1 sea autoritativo (los atributos son
        // server-authoritative y se sincronizan de vuelta).
        if (!IsOwner)
        {
            CharacterClassDefinition def = GetClassByIndex(classIndex);
            if (def != null) EquipCharacterClass(def, resetProgress);
        }
    }

    // En los CLIENTES-observadores puros, aplica los visuales de la clase que
    // el dueño equipó. El dueño ya equipó localmente (se saltea con IsOwner) y
    // el servidor lo hizo en ServerSetClass (se saltea con IsServerInitialized,
    // así no duplicamos el UpdateVisuals ahí).
    private void OnNetClassIndexChanged(int prev, int next, bool asServer)
    {
        if (IsOwner || IsServerInitialized) return;
        CharacterClassDefinition def = GetClassByIndex(next);
        if (def != null) UpdateVisuals(def);
    }
    private List<CharacterClassDefinition> AllClasses
    {
        get
        {
            if (_allClasses == null)
            {
                _allClasses = new List<CharacterClassDefinition>();
                if (MainBaseClasses != null)
                    foreach (var c in MainBaseClasses) AddClassRecursive(c);
            }
            return _allClasses;
        }
    }

    // Agrega una clase y sus subclases (en profundidad) a _allClasses, sin
    // duplicar. El orden es determinístico, así que el índice es estable
    // entre peers.
    private void AddClassRecursive(CharacterClassDefinition c)
    {
        if (c == null || _allClasses.Contains(c)) return;
        _allClasses.Add(c);
        if (c.AvailableSubclasses != null)
            foreach (var sub in c.AvailableSubclasses) AddClassRecursive(sub);
    }

    // Índice de una clase en la lista plana (para mandarlo por red). -1 si no
    // está (no debería pasar si la clase deriva de alguna base).
    private int GetClassIndex(CharacterClassDefinition def) => AllClasses.IndexOf(def);

    // Inverso: resuelve un índice recibido por red de vuelta a la clase real.
    public CharacterClassDefinition GetClassByIndex(int idx)
    {
        if (idx < 0 || idx >= AllClasses.Count) return null;
        return AllClasses[idx];
    }

    // Índice de clase SINCRONIZADO de este jugador. Vale en todos los peers (es un
    // SyncVar). Lo usan las Copias exactas para saber a qué jugador copiar el arma/anim.
    public int VisualClassIndex => _netClassIndex.Value;

    // Resuelve un índice de clase → definición, en cualquier peer. Como AllClasses se
    // arma igual en todos (desde MainBaseClasses del prefab), un peer puede resolver la
    // clase de OTRO jugador usando su propia lista. Lo usa Entity_PlayerCopy para
    // copiar el arma/animator del jugador origen sin depender de su CurrentClassDef
    // (que puede no estar seteado en un observador puro).
    public CharacterClassDefinition ResolveClassByIndex(int idx) => GetClassByIndex(idx);

    // El modelo de una clase: el suyo, o el de la clase de la que sale (una subclase comparte el
    // personaje de su clase base salvo que traiga uno propio). Null = el Dummy.
    public GameObject ResolveClassModel(CharacterClassDefinition cls)
    {
        if (cls == null) return null;
        if (cls.ClassModel != null) return cls.ClassModel;

        foreach (CharacterClassDefinition parent in AllClasses)
            if (parent != cls && parent.AvailableSubclasses != null && parent.AvailableSubclasses.Contains(cls))
                return ResolveClassModel(parent);
        return null;
    }

    // Reemplaza el animator override y las armas equipadas según la
    // clase dada. La llaman tanto el dueño (al equipar) como los
    // observadores remotos (al recibir el cambio de clase por red).
    private void UpdateVisuals(CharacterClassDefinition newClass)
    {
        ClassModelSwap.Apply(characterAnimator, ResolveClassModel(newClass));
        RebuildRuntimeAnimator(newClass);

        if (currentMainWeapon != null) Destroy(currentMainWeapon);
        if (currentOffWeapon  != null) Destroy(currentOffWeapon);

        if (newClass.MainHandWeaponPrefab != null && MainHandSocket != null)
        {
            currentMainWeapon = Instantiate(newClass.MainHandWeaponPrefab, MainHandSocket);
            _mainHandStowed   = false;   // nace en la mano; TickStanceVisuals la guarda si hace falta
            // La pose sale de la CLASE, no del prefab del arma: los dos sockets tienen
            // orientaciones distintas, así que un mismo modelo necesita ajustes según
            // en qué mano vaya. En cero queda pegado al hueso, como antes.
            currentMainWeapon.transform.SetLocalPositionAndRotation(
                newClass.MainHandPositionOffset,
                Quaternion.Euler(newClass.MainHandRotationOffset));

            // Un modelo compartido a otro tamaño (el bastón del Clérigo es la lanza, más chica).
            if (newClass.MainHandScale > 0f && !Mathf.Approximately(newClass.MainHandScale, 1f))
                currentMainWeapon.transform.localScale *= newClass.MainHandScale;

            Transform trail = currentMainWeapon.transform.Find("WeaponTrail");
            if (trail != null)
            {
                currentWeaponTrail = trail.gameObject;
                currentWeaponTrail.SetActive(false);
            }
        }

        if (newClass.OffHandWeaponPrefab != null && OffHandSocket != null)
        {
            currentOffWeapon = Instantiate(newClass.OffHandWeaponPrefab, OffHandSocket);
            _offHandStowed = false;   // nace en la mano; TickStanceVisuals la guarda si hace falta
            currentOffWeapon.transform.SetLocalPositionAndRotation(
                newClass.OffHandPositionOffset,
                Quaternion.Euler(newClass.OffHandRotationOffset));

            // El escudo grande del Guardián: el mismo modelo, escalado.
            if (newClass.OffHandScale > 0f && !Mathf.Approximately(newClass.OffHandScale, 1f))
                currentOffWeapon.transform.localScale *= newClass.OffHandScale;
        }
    }
}
