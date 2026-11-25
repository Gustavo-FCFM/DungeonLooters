using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Connection;

// ============================================================
// NetworkAbilitySystemComponent — LO QUE SE ESPEJA EN RED (atributos, tags, cooldowns, cargas, efectos, cuerda, equipos)
//
// Una parte de NetworkAbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en Network/NetworkAbilitySystemComponent.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class NetworkAbilitySystemComponent
{

    // =========================================================
    // ATRIBUTOS EN RED
    // =========================================================

    // El servidor refleja cada cambio de atributo del ASC en el SyncVar
    // correspondiente.
    private void HandleAttributeChanged(EAttributeType type, float value)
    {
        if (!IsServerInitialized) return;

        switch (type)
        {
            case EAttributeType.Health:    _netHealth.Value    = value; break;
            case EAttributeType.MaxHealth: _netMaxHealth.Value = value; break;
            case EAttributeType.Mana:      _netMana.Value      = value; break;
            case EAttributeType.MaxMana:   _netMaxMana.Value   = value; break;
            case EAttributeType.Energy:    _netEnergy.Value    = value; break;
            case EAttributeType.Shield:    _netShield.Value    = value; break;
            case EAttributeType.Level:     _netLevel.Value     = value; break;
            case EAttributeType.Exp:       _netExp.Value       = value; break;

            // Cualquier otro stat (AtkSpeed, MovSpeed, Atq, Def, CritChance...)
            // va por el diccionario genérico — así los buffs sobre stats
            // derivados también llegan a los clientes sin un SyncVar por cada uno.
            default:                       NetStats[type]      = value; break;
        }
    }

    // En los clientes, cada callback de SyncVar aplica el nuevo valor al
    // ASC local (asServer=true significa que lo escribimos nosotros como
    // servidor, así que lo ignoramos para no reescribirnos a nosotros mismos).
    private void OnNetHealthChanged(float prev, float next, bool asServer)
    {
        if (_asc == null) return;

        // En el HOST este callback llega dos veces, y la de cliente (asServer false) trae un valor que puede ser
        // VIEJO: si entre que el servidor lo escribió y que llegó el callback le pegaron otra vez, escribirlo
        // le devolvía esa vida. Pasaba en el host con cada golpe seguido (18 de noviembre de 2026: la medición
        // del balance lo vio como curación de "OnNetHealthChanged", ~10 % de lo que se curaba cada uno). En el
        // host el ASC es el mismo objeto que el del servidor: ya tiene la vida buena.
        if (!asServer && !IsServerInitialized) _asc.SetCurrentAttributeValue(EAttributeType.Health, next);

        // Sonido y reacción de golpe: acá, y no en el pipeline de daño, porque este
        // callback corre en TODOS los peers y es el único lugar donde "bajó la vida"
        // llega igual a todos. Nada viaja de más.
        //
        // La vida anterior la llevamos NOSOTROS (_lastSeenHealth), no confiamos en
        // 'prev': en el host el callback llega dos veces (servidor y cliente) y en la
        // segunda 'prev' ya puede ser igual a 'next' — la reacción se perdía en la mitad
        // de los golpes. Con la vida anterior propia, la primera llamada reacciona y la
        // segunda ve que no cambió nada.
        if (!_healthSeen) { _healthSeen = true; _lastSeenHealth = next; return; }

        float before = _lastSeenHealth;
        _lastSeenHealth = next;
        if (next < before) ReactToDamage(before, next);
    }

    // Un golpe recibido: suena (Hurt, o Death si lo mató) y el personaje se sacude
    // (PlayerController.PlayHitReaction). Con un mínimo de daño y un respiro entre
    // reacciones, para que una quemadura de 3 por segundo no lo tenga temblando.
    //
    // HurtCooldown es el respiro del SONIDO. La ANIMACIÓN tiene el suyo aparte, en
    // PlayerController.HitReactionCooldown: son ritmos distintos, porque el "ay" en cada
    // golpe se lee bien y la sacudida en cada golpe se ve como un temblor.
    private void ReactToDamage(float prev, float next)
    {
        AudioLibrary lib = AudioLibrary.Instance;
        Vector3 at = transform.position + Vector3.up;

        if (next <= 0f)
        {
            if (lib != null) AudioManager.Play(lib.Death, at);
            return;
        }

        float minDamage = lib != null ? Mathf.Max(_netMaxHealth.Value, 1f) * lib.HurtMinFraction : 1f;
        float cooldown  = lib != null ? lib.HurtCooldown : 0.45f;
        if (prev - next < minDamage || Time.time - _lastHurtAt < cooldown) return;
        _lastHurtAt = Time.time;

        if (lib != null) AudioManager.Play(lib.Hurt, at);

        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null) pc.PlayHitReaction();
    }
    private void OnNetMaxHealthChanged(float prev, float next, bool asServer)
    {
        if (!asServer && _asc != null) _asc.SetCurrentAttributeValue(EAttributeType.MaxHealth, next);
    }
    private void OnNetManaChanged(float prev, float next, bool asServer)
    {
        if (!asServer && _asc != null) _asc.SetCurrentAttributeValue(EAttributeType.Mana, next);
    }
    private void OnNetMaxManaChanged(float prev, float next, bool asServer)
    {
        if (!asServer && _asc != null) _asc.SetCurrentAttributeValue(EAttributeType.MaxMana, next);
    }
    private void OnNetEnergyChanged(float prev, float next, bool asServer)
    {
        if (!asServer && _asc != null) _asc.SetCurrentAttributeValue(EAttributeType.Energy, next);
    }
    private void OnNetShieldChanged(float prev, float next, bool asServer)
    {
        if (!asServer && _asc != null) _asc.SetCurrentAttributeValue(EAttributeType.Shield, next);
    }
    private void OnNetTeamIDChanged(int prev, int next, bool asServer)
    {
        if (!asServer && _asc != null) _asc.TeamID = next;
    }
    private void OnNetLevelChanged(float prev, float next, bool asServer)
    {
        if (asServer || _asc == null) return;
        _asc.SetCurrentAttributeValue(EAttributeType.Level, next);

        // Subir de nivel suena solo en la pantalla del que subió (es SU momento; para
        // el equipo está el aviso TeamLevelUp).
        if (next > prev && IsOwner && AudioLibrary.Instance != null)
            AudioManager.PlayUI(AudioLibrary.Instance.LevelUp);
    }
    private void OnNetExpChanged(float prev, float next, bool asServer)
    {
        if (!asServer && _asc != null) _asc.SetCurrentAttributeValue(EAttributeType.Exp, next);
    }

    // Vuelca todos los atributos actuales del ASC a sus SyncVars de una
    // sola vez (ej: para forzar una resincronización completa).
    [Server]
    public void SyncAllAttributesToNet()
    {
        if (_asc == null) return;

        _netHealth.Value    = _asc.GetAttributeValue(EAttributeType.Health);
        _netMaxHealth.Value = _asc.GetAttributeValue(EAttributeType.MaxHealth);
        _netMana.Value      = _asc.GetAttributeValue(EAttributeType.Mana);
        _netMaxMana.Value   = _asc.GetAttributeValue(EAttributeType.MaxMana);
        _netEnergy.Value    = _asc.GetAttributeValue(EAttributeType.Energy);
        _netShield.Value    = _asc.GetAttributeValue(EAttributeType.Shield);
        _netLevel.Value     = _asc.GetAttributeValue(EAttributeType.Level);
        _netExp.Value       = _asc.GetAttributeValue(EAttributeType.Exp);
        _netTeamID.Value    = _asc.TeamID;
    }

    // =========================================================
    // TAGS EN RED
    // =========================================================

    private void HandleTagAdded(EGameplayTag tag)
    {
        if (!IsServerInitialized) return;
        NetTags.Add(tag);
    }

    private void HandleTagRemoved(EGameplayTag tag)
    {
        if (!IsServerInitialized) return;
        NetTags.Remove(tag);
    }

    // NetTags se llenaba del lado servidor (HandleTagAdded/HandleTagRemoved)
    // pero nada aplicaba esos cambios de vuelta al ASC local del cliente.
    // Sin esto, HasTag() en la copia del dueño remoto nunca se entera de
    // Stunned/Rooted/Silenced/Dead/cooldowns — por eso el UI de cooldown
    // nunca se llenaba para el jugador 2.
    //
    // IMPORTANTE: solo los clientes PUROS deben espejar NetTags. En el host,
    // el servidor ya maneja GameplayTags directo (AddTag/RemoveTag desde el
    // sistema de efectos), así que re-aplicarlos acá es redundante Y peligroso:
    // el SyncHashSet entrega su OnChange de forma asíncrona, y para un tag de
    // vida corta (ej. el cooldown melee = ~0.16s) el 'Add' puede llegar DESPUÉS
    // de que el servidor ya removió el tag → lo vuelve a agregar sin ningún
    // efecto detrás → queda huérfano/pegado para siempre. Por eso salteamos
    // también cuando este peer ES servidor (IsServerInitialized).
    private void OnNetTagsChanged(SyncHashSetOperation op, EGameplayTag item, bool asServer)
    {
        if (asServer || IsServerInitialized || _asc == null) return;

        switch (op)
        {
            case SyncHashSetOperation.Add:
                _asc.AddTag(item);
                break;
            case SyncHashSetOperation.Remove:
                _asc.RemoveTag(item);

                // State_Dead tiene DOS fuentes en un cliente: este espejo y la muerte local
                // (la vida en 0 llega por su SyncVar y SetCurrentAttributeValue llama a Die).
                // Si la vida llegaba antes que el tag, el conteo quedaba en 2 y al revivir
                // bajaba a 1: el cliente seguía "muerto" en la base, sin poder moverse, aunque
                // el servidor ya lo había revivido (los desatascos del 29 de octubre de 2026:
                // State_Dead en su lado y no en el del servidor). Si el servidor dice que no
                // está muerto, no lo está.
                if (item == EGameplayTag.State_Dead)
                    while (_asc.HasTag(item)) _asc.RemoveTag(item);
                break;
        }
    }

    // En los clientes, aplica al ASC local cada stat derivado que cambió en
    // el servidor (ver NetStats). Así la UI (MaxHealth), el movimiento
    // (MovSpeed) y las animaciones (AtkSpeed) del jugador reflejan sus buffs.
    // El guard de IsServerInitialized es el MISMO que el de OnNetTagsChanged, y por el
    // mismo motivo. En un HOST el ASC es UNO SOLO: servidor y cliente comparten objeto.
    // Sin ese chequeo, la copia cliente del callback vuelve a ESCRIBIR en el ASC
    // autoritativo un valor que el servidor ya cambió, y como SetCurrentAttributeValue
    // CREA el atributo si no existe, resucita atributos que el servidor acababa de
    // borrar.
    //
    // El caso real: los AttributeSet del Bárbaro y sus subclases traen LifeSteal 0.3 como
    // stat BASE. Al cambiar de clase, InitializeAttributes vacía el diccionario y el
    // Pícaro queda sin robo de vida — pero el eco de NetStats se lo volvía a meter con el
    // 0.3 del Bárbaro, y como el daño se resuelve en el servidor, el Pícaro seguía
    // curándose al pegar. No hacía falta morir: bastaba con cambiar de clase.
    //
    // No se pierde nada salteándolo: en el host el servidor ya escribió ese valor en el
    // ASC de primera mano, y este callback solo repetía lo que él mismo acababa de mandar.
    private void OnNetStatChanged(SyncDictionaryOperation op, EAttributeType key, float value, bool asServer)
    {
        if (asServer || IsServerInitialized || _asc == null) return;

        if (op == SyncDictionaryOperation.Add || op == SyncDictionaryOperation.Set)
            _asc.SetCurrentAttributeValue(key, value);
    }

    // =========================================================
    // COOLDOWNS EN RED
    // =========================================================

    // Publica el tick de inicio y la duración total del cooldown vigente
    // de una habilidad (o limpia la entrada si ya no está en cooldown).
    [Server]
    private void SyncCooldownForSlot(EAbilityInput slot)
    {
        GameplayAbility ability = FindAbilityBySlot(slot);
        if (ability == null) return;

        if (_asc.GetCooldownStatus(ability, out float remaining, out float total))
        {
            // BUG QUE ARREGLAMOS: acá antes se mandaba "remaining" como si
            // fuera la duración TOTAL y "ahora" como tick de inicio. Como
            // esto se re-sincroniza cada CooldownSyncInterval, el "total"
            // (denominador de la barra en la UI) se achicaba a lo que
            // quedaba en cada barrido, y el tick de inicio se reiniciaba a
            // "ahora" — la barra saltaba a casi llena y se vaciaba de nuevo
            // cada 0.25s en vez de vaciarse una sola vez a lo largo de todo
            // el cooldown real (se veía como si "recargara" varias veces).
            //
            // Mandamos la duración TOTAL real (constante durante todo el
            // cooldown) y reconstruimos el tick de inicio a partir de cuánto
            // ya transcurrió (total - remaining), no "ahora". Así, si algo
            // reduce el cooldown a mitad de camino (ChargeUltimate /
            // ReduceCooldownByTag, que solo tocan el remaining, no el total),
            // el próximo barrido corrige el tiempo restante sin resetear la
            // barra visualmente.
            uint elapsedTicks = TimeManager.TimeToTicks(Mathf.Max(0f, total - remaining));
            NetCooldownStartTick[slot] = TimeManager.Tick > elapsedTicks ? TimeManager.Tick - elapsedTicks : 0;
            NetCooldownDuration[slot]  = total;
        }
        else if (NetCooldownDuration.ContainsKey(slot))
        {
            NetCooldownStartTick.Remove(slot);
            NetCooldownDuration.Remove(slot);
        }
    }

    // Usado por la UI (UI_AbilitySlot / UI_UltimateSlot) para leer el
    // cooldown real sin depender del ASC local (que en el cliente remoto
    // nunca tiene ActiveEffects poblado).
    public bool TryGetNetCooldown(EAbilityInput slot, out float remaining, out float total)
    {
        remaining = 0f;

        if (!NetCooldownDuration.TryGetValue(slot, out total)) { total = 0f; return false; }
        if (!NetCooldownStartTick.TryGetValue(slot, out uint startTick)) return false;

        uint   elapsedTicks   = TimeManager.Tick > startTick ? (TimeManager.Tick - startTick) : 0;
        double elapsedSeconds = TimeManager.TicksToTime(elapsedTicks);

        remaining = Mathf.Max(0f, total - (float)elapsedSeconds);
        return remaining > 0f;
    }

    // =========================================================
    // CARGAS EN RED (habilidades con sistema de cargas, ej: el dash)
    // =========================================================

    // El servidor publica cuántas cargas le quedan a una habilidad. Resuelve el
    // slot de la habilidad (por nombre, igual que FindAbilityBySlot) y lo guarda
    // en NetCharges para que la UI del dueño lo muestre. Lo llama GA_Dash cada
    // vez que sus cargas cambian (usar, recargar, reembolsar).
    [Server]
    public void ServerReportCharges(GameplayAbility ability, int charges)
    {
        if (_asc == null || _asc.CurrentClass == null || ability == null) return;

        EAbilityInput slot = EAbilityInput.None;
        foreach (var assignment in _asc.CurrentClass.Abilities)
        {
            if (assignment.Ability != null && assignment.Ability.AbilityName == ability.AbilityName)
            {
                slot = assignment.InputSlot;
                break;
            }
        }
        if (slot == EAbilityInput.None) return;

        NetCharges[slot] = Mathf.Max(0, charges);
    }

    // Lee las cargas sincronizadas de un slot. Devuelve false si ese slot no es
    // una habilidad con cargas (o todavía no reportó ninguna).
    public bool TryGetNetCharges(EAbilityInput slot, out int charges)
    {
        return NetCharges.TryGetValue(slot, out charges);
    }

    // =========================================================
    // EFECTOS ACTIVOS EN RED (buffs/debuffs)
    // =========================================================

    private void HandleActiveEffectAdded(GameplayEffect effect, float totalDuration) => SyncActiveEffect(effect);
    private void HandleActiveEffectRemoved(GameplayEffect effect)                    => SyncActiveEffect(effect);

    // Recalcula y publica el estado de UN efecto agregando TODAS sus instancias
    // (stacks): sincroniza el stack que expira ÚLTIMO (para la barra de progreso)
    // y cuántos hay en total.
    //
    // BUG QUE ARREGLAMOS: antes esto se escribía directo desde el add/remove. Como
    // el diccionario está indexado por EFECTO y no por instancia, los stacks se
    // pisaban entre sí: cada nueva aplicación reiniciaba el reloj de la UI, y al
    // expirar el PRIMER stack se borraba la entrada entera → el icono desaparecía
    // aunque quedaran stacks vivos (se veía como si el tiempo fuera solo el de la
    // primera aplicación). Recontar la lista real en cada cambio lo resuelve.
    private void SyncActiveEffect(GameplayEffect effect)
    {
        if (!IsServerInitialized || _asc == null || effect == null) return;

        SyncLinkPartner(effect);   // la cuerda de un vínculo, aunque el efecto sea "Hidden"

        // Los efectos "Hidden" son mecánicas internas (ej: el propio CooldownEffect,
        // que ya se sincroniza aparte) — no muestran icono en la barra.
        if (effect.EffectType == GameplayEffect.EEffectType.Hidden) return;

        int index = ResolveEffectIndex(effect);
        if (index < 0) return;

        int   stacks           = 0;
        float longestRemaining = 0f;
        float longestTotal     = 0f;

        // Por su original (Template): la copia congelada de un proyectil devuelto comparte
        // índice con él, y si cada una contara solo lo suyo se pisarían.
        foreach (var active in _asc.GetActiveEffects())
        {
            if (active.Definition.Template != effect.Template) continue;
            stacks++;
            if (active.DurationRemaining > longestRemaining)
            {
                longestRemaining = active.DurationRemaining;
                longestTotal     = active.TotalDuration;
            }
        }

        if (stacks == 0)
        {
            NetActiveEffectStartTick.Remove(index);
            NetActiveEffectDuration.Remove(index);
            NetActiveEffectStacks.Remove(index);
            return;
        }

        // Sin reloj (infinito, o "Es de aura": lo que un aura refresca cada medio segundo). Duración 0 = "sin tiempo"
        // para la barra, y solo se escribe lo que CAMBIÓ: un refresco que no cambia nada no viaja por la red
        // (antes, cada aliado dentro de un aura reenviaba su entrada dos veces por segundo).
        if (effect.Template.HidesTime)
        {
            if (!NetActiveEffectStartTick.ContainsKey(index)) NetActiveEffectStartTick[index] = TimeManager.Tick;
            if (!NetActiveEffectDuration.TryGetValue(index, out float shownTotal) || shownTotal != 0f) NetActiveEffectDuration[index] = 0f;
            if (!NetActiveEffectStacks.TryGetValue(index, out int shownStacks) || shownStacks != stacks) NetActiveEffectStacks[index] = stacks;
            return;
        }

        // Reconstruimos el tick de inicio desde lo ya transcurrido (igual que con
        // los cooldowns), para no reiniciar la barra en cada resincronización.
        uint elapsedTicks = TimeManager.TimeToTicks(Mathf.Max(0f, longestTotal - longestRemaining));
        NetActiveEffectStartTick[index] = TimeManager.Tick > elapsedTicks ? TimeManager.Tick - elapsedTicks : 0;
        NetActiveEffectDuration[index]  = longestTotal;
        NetActiveEffectStacks[index]    = stacks;
    }

    // =========================================================
    // LA CUERDA DE UN VÍNCULO (GameplayEffect "Cuerda"; 20 de noviembre de 2026)
    // =========================================================

    // Servidor: anota (o borra) quién es el otro extremo del vínculo 'effect', con el más nuevo si hay varios.
    private void SyncLinkPartner(GameplayEffect effect)
    {
        if (effect.Template.LinkTetherPrefab == null) return;
        int index = ResolveEffectIndex(effect);
        if (index < 0) return;

        NetworkObject partner = null;
        foreach (var active in _asc.GetActiveEffects())
        {
            if (active.Definition.Template != effect.Template) continue;
            AbilitySystemComponent source = active.Source as AbilitySystemComponent;
            if (source != null && source != _asc) partner = source.GetComponent<NetworkObject>();
        }

        if (partner == null) { NetLinkPartner.Remove(index); return; }
        if (!NetLinkPartner.TryGetValue(index, out NetworkObject shown) || shown != partner) NetLinkPartner[index] = partner;
    }

    private void OnNetLinkPartnerChanged(SyncDictionaryOperation op, int effectIndex, NetworkObject partner, bool asServer)
    {
        if (asServer) return;
        switch (op)
        {
            case SyncDictionaryOperation.Add:
            case SyncDictionaryOperation.Set:    DespawnTether(effectIndex); SpawnTether(effectIndex, partner); break;
            case SyncDictionaryOperation.Remove: DespawnTether(effectIndex); break;
            case SyncDictionaryOperation.Clear:  DespawnAllTethers(); break;
        }
    }

    private void SpawnTether(int effectIndex, NetworkObject partner)
    {
        if (partner == null || _tethers.ContainsKey(effectIndex)) return;
        GameplayEffectRegistry registry = GameplayEffectRegistry.Instance;
        GameplayEffect definition = registry != null ? registry.GetEffect(effectIndex) : null;
        if (definition == null || definition.LinkTetherPrefab == null) return;
        LinkTether tether = LinkTether.Create(definition.LinkTetherPrefab, transform, partner.transform);
        if (tether != null) _tethers[effectIndex] = tether;
    }

    private void DespawnTether(int effectIndex)
    {
        if (_tethers.TryGetValue(effectIndex, out LinkTether tether) && tether != null) Destroy(tether.gameObject);
        _tethers.Remove(effectIndex);
    }

    private void DespawnAllTethers()
    {
        foreach (var kvp in _tethers)
            if (kvp.Value != null) Destroy(kvp.Value.gameObject);
        _tethers.Clear();
    }

    // Busca el índice de un GameplayEffect en GameplayEffectRegistry (ver
    // esa clase). Logea un warning si el registro no existe o el efecto
    // no está registrado — en ambos casos, el efecto sigue funcionando en
    // gameplay, solo no sincroniza su icono.
    private int ResolveEffectIndex(GameplayEffect effect)
    {
        GameplayEffectRegistry registry = GameplayEffectRegistry.Instance;
        if (registry == null)
        {
            Debug.LogWarning("[NetworkASC] No se encontró GameplayEffectRegistry en Resources — la barra de buffs/debuffs no se va a sincronizar a los clientes remotos.");
            return -1;
        }

        int index = registry.GetIndex(effect);
        if (index < 0)
            Debug.LogWarning($"[NetworkASC] '{effect.name}' no está en GameplayEffectRegistry — su icono no se sincronizará a los clientes remotos.");

        return index;
    }

    // Usado por UI_EffectContainer para saber qué efectos (índices de
    // GameplayEffectRegistry) están activos ahora mismo en este ASC.
    public IEnumerable<int> GetActiveEffectIndices() => NetActiveEffectDuration.Keys;

    // Cuántos stacks de un efecto están activos ahora mismo. Devuelve false si ese
    // efecto no está activo (la UI lo trata como 1). Ver SyncActiveEffect.
    public bool TryGetNetActiveEffectStacks(int effectIndex, out int stacks)
    {
        return NetActiveEffectStacks.TryGetValue(effectIndex, out stacks);
    }

    // Igual que TryGetNetCooldown pero para un efecto activo (buff/debuff)
    // identificado por su índice en GameplayEffectRegistry.
    public bool TryGetNetActiveEffect(int effectIndex, out float remaining, out float total)
    {
        remaining = 0f;

        if (!NetActiveEffectDuration.TryGetValue(effectIndex, out total)) { total = 0f; return false; }
        if (!NetActiveEffectStartTick.TryGetValue(effectIndex, out uint startTick)) return false;

        uint   elapsedTicks   = TimeManager.Tick > startTick ? (TimeManager.Tick - startTick) : 0;
        double elapsedSeconds = TimeManager.TicksToTime(elapsedTicks);

        remaining = Mathf.Max(0f, total - (float)elapsedSeconds);
        return remaining > 0f;
    }

    private void OnNetActiveEffectVfxChanged(SyncDictionaryOperation op, int effectIndex,
                                             float duration, bool asServer)
    {
        if (asServer) return;

        switch (op)
        {
            case SyncDictionaryOperation.Add:
            case SyncDictionaryOperation.Set:
                SpawnEffectVfx(effectIndex);
                break;
            case SyncDictionaryOperation.Remove:
                DespawnEffectVfx(effectIndex);
                break;
            case SyncDictionaryOperation.Clear:
                DespawnAllEffectVfx();
                break;
        }
    }

    // Instancia el TargetVFX del efecto como HIJO de este personaje, así lo sigue a
    // donde vaya. Idempotente a propósito: un efecto que se refresca o suma un stack
    // vuelve a disparar Set, y no queremos dos partículas encimadas.
    private void SpawnEffectVfx(int effectIndex)
    {
        if (_activeEffectVfx.ContainsKey(effectIndex)) return;

        GameplayEffectRegistry registry = GameplayEffectRegistry.Instance;
        GameplayEffect definition = registry != null ? registry.GetEffect(effectIndex) : null;
        if (definition == null) return;

        // El sonido va antes que el VFX a propósito: un efecto puede tener sonido sin
        // tener partícula (un stun que solo suena), y viceversa.
        AudioManager.Play(definition.TargetSound, transform.position + Vector3.up);

        // Sin partícula igual se anota (con null): si no, cada refresco del efecto
        // volvería a pasar por acá y el sonido se repetiría. Los Despawn toleran el null.
        if (definition.TargetVFX == null) { _activeEffectVfx[effectIndex] = null; return; }

        Vector3    pos = transform.position + transform.TransformDirection(definition.TargetVFXOffset);
        Quaternion rot = transform.rotation * Quaternion.Euler(definition.TargetVFXRotation);

        GameObject vfx = Instantiate(definition.TargetVFX, pos, rot, transform);
        if (definition.TargetVFXScale != Vector3.zero)
            vfx.transform.localScale = definition.TargetVFXScale;

        // Los VFX de un solo disparo se repiten mientras dure el efecto (ver
        // GameplayEffect.TargetVFXLoop). Sin acción al terminar: un Destroy propio del
        // prefab se lo llevaría antes de tiempo.
        if (definition.TargetVFXLoop)
            foreach (ParticleSystem ps in vfx.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = ps.main;
                main.loop = true;
                main.stopAction = ParticleSystemStopAction.None;
            }

        _activeEffectVfx[effectIndex] = vfx;
    }

    private void DespawnEffectVfx(int effectIndex)
    {
        if (!_activeEffectVfx.TryGetValue(effectIndex, out GameObject vfx)) return;
        FadeOutEffectVfx(vfx);
        _activeEffectVfx.Remove(effectIndex);
    }

    private void DespawnAllEffectVfx()
    {
        foreach (var kvp in _activeEffectVfx)
            FadeOutEffectVfx(kvp.Value);

        _activeEffectVfx.Clear();
    }

    private static void FadeOutEffectVfx(GameObject vfx)
    {
        if (vfx == null) return;

        ParticleSystem[] systems = vfx.GetComponentsInChildren<ParticleSystem>();
        if (systems.Length == 0) { Destroy(vfx); return; }

        // Un VFX que junta partículas con un modelo (las alas del Ángel vengador): el
        // modelo se apaga en el acto, solo las partículas se desvanecen.
        foreach (Renderer r in vfx.GetComponentsInChildren<Renderer>())
            if (!(r is ParticleSystemRenderer)) r.enabled = false;

        float longest = 0f;
        foreach (ParticleSystem ps in systems)
        {
            ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            ParticleSystem.MinMaxCurve life = ps.main.startLifetime;
            float seconds = life.mode == ParticleSystemCurveMode.Constant     ? life.constant
                          : life.mode == ParticleSystemCurveMode.TwoConstants ? life.constantMax
                          :                                                     life.curveMultiplier;
            longest = Mathf.Max(longest, seconds);
        }

        Destroy(vfx, Mathf.Min(longest, MaxEffectVfxFade));
    }

    // =========================================================
    // EQUIPOS
    // =========================================================

    // Asigna el equipo de este personaje (lo llama NetworkGameManager al
    // spawnearlo) y lo sincroniza a los clientes.
    [Server]
    public void AssignTeam(int teamID)
    {
        if (_asc != null) _asc.TeamID = teamID;
        _netTeamID.Value = teamID;
        Debug.Log($"{gameObject.name} → Equipo {teamID}");
    }
    public string PlayerName => _netPlayerName.Value;

    // La llama NetworkGameManager al spawnear, con el nombre que mandó el cliente.
    [Server]
    public void AssignPlayerName(string playerName)
    {
        _netPlayerName.Value = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
    }
}
