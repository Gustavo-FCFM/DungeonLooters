using UnityEngine;
using System.Collections.Generic;
using System;

// ============================================================
// AbilitySystemComponent — APLICAR EFECTOS
//
// Una parte de AbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/AbilitySystemComponent.cs; ahí está también la lista de las partes.
//
// La entrada de todo efecto (ApplyGameplayEffect), el empuje, la resistencia al control y
// los efectos CON duración: sus modificadores, el escudo temporal, las pasivas de la clase
// y el paso de cada frame (ticks y vencimientos). Lo instantáneo sigue en Damage.
// ============================================================
public partial class AbilitySystemComponent
{
    // =========================================================
    // GAMEPLAY EFFECTS
    // =========================================================

    // Espera por enemigo de las entradas de efectos (AbilityEffect.PerTargetCooldown): hasta
    // cuándo ESTA habilidad (key), de ESTE lanzador, no le puede volver a aplicar ESTE efecto
    // a este personaje. Solo en el servidor, que es quien aplica los efectos.
    private readonly Dictionary<(UnityEngine.Object key, GameplayEffect effect, AbilitySystemComponent source), float> _effectCooldownUntil =
        new Dictionary<(UnityEngine.Object, GameplayEffect, AbilitySystemComponent), float>();

    // true = se puede aplicar ahora, y queda anotada la espera; false = todavía no.
    public bool TryStartEffectCooldown(UnityEngine.Object key, GameplayEffect effect, AbilitySystemComponent source, float seconds)
    {
        var id = (key, effect, source);
        if (_effectCooldownUntil.TryGetValue(id, out float until) && Time.time < until) return false;
        _effectCooldownUntil[id] = Time.time + seconds;
        return true;
    }

    // Punto de entrada para aplicarle cualquier GameplayEffect a este
    // personaje (daño, curación, buffs, cooldowns...). Si el efecto no
    // tiene duración lo ejecuta una sola vez; si tiene duración lo agrega
    // a ActiveEffects respetando su StackingPolicy (Refresh/Stack/Override).
    // durationOverride pisa la duración del asset (usado por cooldowns
    // dinámicos, ej. basados en velocidad de ataque).
    //
    // Los encadenados que dispare (por ejemplo, una limpieza que quita una marca con "Si se lo quitan antes")
    // esperan a que este termine de aplicarse: ver ENCADENADOS.
    public void ApplyGameplayEffect(GameplayEffect effect, object source = null, float durationOverride = -1f)
    {
        BeginHold();
        try { ApplyGameplayEffectNow(effect, source, durationOverride); }
        finally { EndHold(); }
    }

    private void ApplyGameplayEffectNow(GameplayEffect effect, object source, float durationOverride)
    {
        if (effect == null) return;

        // Condiciones por tag e inmunidades (ver "Quitar efectos e inmunidad" del GE): antes que nada, así un efecto
        // que no entra tampoco empuja, ni quita, ni nada.
        if (!PassesApplicationTags(effect) || IsImmuneTo(effect)) return;

        // Limpiar / purgar / quitar por tag, y lo que ya tenía y ahora es inmune: lo quita al aplicarse.
        RemoveOnApply(effect, durationOverride > 0f ? durationOverride : effect.EffectiveDuration);

        // Repeler / atraer: pasa en el instante de aplicarse, aparte de lo que haga el
        // efecto con sus modificadores (ver TryApplyKnockback).
        if (effect.KnockbackDistance > 0f) TryApplyKnockback(effect, source);

        // Inmunidad a debuffs (Imparable del Pirata): mientras Status_Unstoppable esté
        // activo, los efectos marcados como Debuff CON DURACIÓN (CC, DoT) no entran. El
        // daño instantáneo (Duration 0) sí — no queremos hacerlo invulnerable, solo a CC.
        if (effect.EffectType == GameplayEffect.EEffectType.Debuff && effect.HasDuration &&
            HasTag(EGameplayTag.Status_Unstoppable))
            return;

        float finalDuration = (durationOverride > 0) ? durationOverride : effect.EffectiveDuration;   // +∞ si es infinito

        // Resistencia al control: recorta (o alarga) lo que dura el CC que entra.
        // Si el objetivo lo resiste del todo, el efecto NO se aplica en absoluto.
        if (finalDuration > 0f && effect.CountsAsCrowdControl)
        {
            finalDuration = ResolveCrowdControlDuration(finalDuration);
            if (finalDuration <= 0f) return;
        }

        if (finalDuration <= 0)
        {
            ExecuteInstantEffect(effect, source);
        }
        else
        {
            // Exclusión mutua por grupo (jerarquía): dentro de un mismo
            // EffectGroup solo vive el de mayor Priority. Si ya hay uno igual o
            // superior activo, este (inferior) no se aplica; si este es
            // superior, remueve a los inferiores del grupo. El MISMO efecto
            // (misma Definition) se saltea — su re-aplicación la maneja la
            // StackingPolicy de abajo.
            if (effect.EffectGroup != EGameplayTag.None)
            {
                for (int i = ActiveEffects.Count - 1; i >= 0; i--)
                {
                    GameplayEffect otro = ActiveEffects[i].Definition;
                    if (otro == effect || otro.EffectGroup != effect.EffectGroup) continue;

                    // ReplacesGroup (posturas): el nuevo SIEMPRE saca a los otros del grupo.
                    if (!effect.ReplacesGroup && otro.Priority >= effect.Priority) return;   // ya hay uno igual/superior
                    RemoveActiveEffect(ActiveEffects[i], EEffectEnd.Replaced);   // este es superior (o reemplaza) → quitar el otro
                }
            }

            if (effect.StackingPolicy == GameplayEffect.EStackingType.Refresh)
            {
                foreach (var existing in ActiveEffects)
                {
                    if (existing.Definition == effect)
                    {
                        existing.SetDuration(finalDuration);
                        existing.AppliedFrame    = Time.frameCount;
                        existing.AppliedPosition = transform.position;
                        RevealToSourceTeam(effect, finalDuration, source);
                        OnActiveEffectAddedCallback?.Invoke(effect, finalDuration);
                        return;
                    }
                }
            }
            else if (effect.StackingPolicy == GameplayEffect.EStackingType.Override)
            {
                for (int i = ActiveEffects.Count - 1; i >= 0; i--)
                    if (ActiveEffects[i].Definition == effect)
                        RemoveActiveEffect(ActiveEffects[i], EEffectEnd.Replaced);
            }
            else if (effect.StackingPolicy == GameplayEffect.EStackingType.Stack && effect.MaxStacks > 0)
            {
                int stacks = 0;
                foreach (var existing in ActiveEffects)
                    if (existing.Definition == effect) stacks++;

                // Explosión al tope (Heridas del Ilusionista): si al sumar ESTA
                // acumulación se llega al máximo y hay un OnMaxStacksEffect, se
                // consumen TODAS las acumulaciones y se aplica ese efecto (el daño
                // masivo). La fuente se conserva para el crédito de la muerte / robo
                // de vida.
                if (effect.OnMaxStacksEffect != null && stacks + 1 >= effect.MaxStacks)
                {
                    RemoveEffectsByDefinition(effect, EEffectEnd.Replaced);
                    ApplyGameplayEffect(effect.OnMaxStacksEffect, source);
                    return;
                }

                // Reloj COMPARTIDO: cada nueva acumulación refresca la duración de
                // TODAS las existentes. Antes cada instancia tenía su propio reloj, así
                // que la acumulación más vieja expiraba primero (5→4 "de la nada")
                // aunque el icono mostrara el reloj de la última. Con esto, seguir
                // golpeando mantiene vivo el stack entero y todas expiran juntas
                // 'finalDuration' después del último golpe.
                foreach (var existing in ActiveEffects)
                {
                    if (existing.Definition != effect) continue;
                    existing.SetDuration(finalDuration);
                    existing.AppliedFrame    = Time.frameCount;
                    existing.AppliedPosition = transform.position;
                }

                // Tope normal (sin explosión): al llegar al máximo ya refrescamos
                // arriba, no agregamos otra instancia.
                if (stacks >= effect.MaxStacks)
                {
                    RevealToSourceTeam(effect, finalDuration, source);
                    OnActiveEffectAddedCallback?.Invoke(effect, finalDuration);
                    return;
                }
            }

            WarnInertPoolModifiers(effect);

            ActiveGameplayEffect newEffect = new ActiveGameplayEffect(effect, finalDuration, source);
            newEffect.SourceAbility = DamageContext;   // para el resumen de muerte: los ticks saben de qué habilidad son
            newEffect.AppliedFrame    = Time.frameCount;      // "Se va solo": un evento de este frame no cuenta
            newEffect.AppliedPosition = transform.position;   // "Al moverse": desde acá
            ActiveEffects.Add(newEffect);
            CountImmunities(effect, +1);

            // El escudo se otorga ANTES que los demás modificadores: si el mismo
            // efecto también sube MaxHealth (ej. Enfurecer), no queremos que eso
            // infle la "vida faltante" con la que escala el escudo.
            newEffect.GrantedShield = GrantTemporaryShield(effect, source);

            ApplyEffectModifiers(effect, true);

            if (effect.GrantedTags != null)
            {
                foreach (EGameplayTag tag in effect.GrantedTags) AddTag(tag);

                // Imparable: al otorgarse el tag de inmunidad, limpia los debuffs ya
                // activos (la inmunidad de arriba se encarga de bloquear los futuros).
                if (effect.GrantedTags.Contains(EGameplayTag.Status_Unstoppable))
                    RemoveAllDebuffs();
            }

            RevealToSourceTeam(effect, finalDuration, source);
            OnActiveEffectAddedCallback?.Invoke(effect, finalDuration);

            // Un efecto que ejecuta remata en el acto al que ya estaba por debajo del umbral.
            if (effect.ExecuteBelowHealth > 0f) CheckExecute();
        }
    }

    // "Revela a quien lo tiene ante el equipo del que lo puso" (GameplayEffect.RevealsToSourceTeam, 18 de noviembre de 2026):
    // el equipo del autor lo ve a través de las paredes por lo que dura (al entrar y en cada refresco). Un infinito, una
    // hora. Solo en el servidor y con red (NetworkAbilitySystemComponent.ServerRevealToTeam).
    private void RevealToSourceTeam(GameplayEffect effect, float duration, object source)
    {
        if (!effect.RevealsToSourceTeam || !(source is AbilitySystemComponent by) || by == null || by.TeamID <= 0) return;
        NetworkAbilitySystemComponent net = GetComponent<NetworkAbilitySystemComponent>();
        if (net == null || !net.IsServerInitialized) return;
        net.ServerRevealToTeam(by.TeamID, float.IsPositiveInfinity(duration) ? 3600f : duration);
    }

    // PROVOCADO (Status_Taunted, 19 de noviembre de 2026): ¿quién lo obliga a atacarlo? La fuente del efecto de provocar
    // MÁS NUEVO que siga viva. null = no está provocado (o el que lo provocó murió o ya no está). Lo preguntan la IA de los
    // NPC (MercEnemyAI.FindTarget) y la de los bots (BotController.FindBestTarget). Solo en el servidor (efectos activos).
    public AbilitySystemComponent GetTaunter()
    {
        for (int i = ActiveEffects.Count - 1; i >= 0; i--)
        {
            ActiveGameplayEffect active = ActiveEffects[i];
            if (!GameplayTagTree.AnyMatches(active.Definition.GrantedTags, EGameplayTag.Status_Taunted)) continue;
            if (active.Source is AbilitySystemComponent by && by != null && by != this && !by.HasTag(EGameplayTag.State_Dead))
                return by;
        }
        return null;
    }

    // =========================================================
    // REPELER / ATRAER (GameplayEffect.KnockbackDistance)
    // =========================================================

    // Mueve a ESTE personaje al recibir un efecto con desplazamiento. Es control: un
    // Imparable no se mueve y la Resistencia al control recorta la distancia (con el mismo
    // tope que la duración de un CC). El escudo NO lo frena: el escudo mitiga daño, no
    // control. Lo ejecuta NetworkAbilitySystemComponent.ServerApplyKnockback (el dueño si
    // es un jugador, el servidor si es un bot o un NPC).
    private void TryApplyKnockback(GameplayEffect effect, object source)
    {
        AbilitySystemComponent sourceASC = source as AbilitySystemComponent;
        if (sourceASC == null || sourceASC == this) return;
        if (HasTag(EGameplayTag.State_Dead) || HasTag(EGameplayTag.Status_Unstoppable)) return;

        NetworkAbilitySystemComponent netAsc = GetComponent<NetworkAbilitySystemComponent>();
        if (netAsc == null || !netAsc.IsServerInitialized) return;

        float distance   = effect.KnockbackDistance;
        float resistance = Mathf.Min(GetAttributeValue(EAttributeType.CCResistance), MaxCCResistance);
        distance *= 1f - resistance;

        Vector3 from = sourceASC.transform.position;
        Vector3 to   = transform.position;
        Vector3 dir;
        switch (effect.KnockbackDirection)
        {
            case GameplayEffect.EKnockbackDirection.TowardSource:
                dir = from - to;
                // Se detiene antes de llegar: nunca lo atraviesa ni lo pasa de largo.
                float gap = new Vector3(dir.x, 0f, dir.z).magnitude - effect.PullStopDistance;
                distance = Mathf.Min(distance, Mathf.Max(0f, gap));
                break;
            case GameplayEffect.EKnockbackDirection.SourceForward:
                dir = sourceASC.transform.forward;
                break;
            default:
                dir = to - from;
                // Encimados: lo manda hacia donde mira quien empuja.
                if (new Vector3(dir.x, 0f, dir.z).sqrMagnitude < 0.01f) dir = sourceASC.transform.forward;
                break;
        }

        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f || distance < 0.05f) return;

        netAsc.ServerApplyKnockback(dir.normalized * distance, Mathf.Max(0.05f, effect.KnockbackDuration),
                                    effect.KnockbackUpVelocity);
    }

    // =========================================================
    // RESISTENCIA AL CONTROL (CC)
    // =========================================================

    // Tope de la resistencia: por más fuentes que se apilen, un CC nunca se recorta más
    // allá de esto. Sin tope, dos auras juntas darían inmunidad total al control por la
    // puerta de atrás — y para eso ya está Status_Unstoppable, que es explícito y se ve
    // venir. Mismo criterio que el tope de Resistance en ApplyDefenses.
    private const float MaxCCResistance = 0.9f;

    // Por debajo de esto, un CC deja de tener sentido y se descarta entero.
    //
    // El umbral NO es cosmético: si dejáramos que la duración cayera a 0,
    // ApplyGameplayEffect lo tomaría como efecto INSTANTÁNEO y le aplicaría los
    // modificadores una vez. O sea que "resistir un aturdimiento por completo" te
    // aplicaría igual lo que ese aturdimiento trajera. Devolviendo 0 acá, quien llama
    // corta antes de llegar a esa rama.
    private const float MinCCDuration = 0.05f;

    // Duración final de un efecto de control sobre ESTE personaje, según su
    // CCResistance. Devuelve 0 si lo resiste del todo.
    //
    // La resistencia negativa (un aura enemiga que te la baja) alarga el CC en vez de
    // acortarlo, y eso es a propósito: es lo que deja expresar "reduce la resistencia
    // al aturdimiento de los enemigos" sin un segundo atributo.
    private float ResolveCrowdControlDuration(float duration)
    {
        float resistance = Mathf.Min(GetAttributeValue(EAttributeType.CCResistance), MaxCCResistance);
        if (Mathf.Approximately(resistance, 0f)) return duration;

        float scaled = duration * (1f - resistance);
        return scaled < MinCCDuration ? 0f : scaled;
    }

    // =========================================================
    // EFECTOS CON DURACIÓN: avisos de configuración, pasivas de la clase, escudo temporal,
    // sus modificadores y el paso de cada frame
    // =========================================================

    // Efectos ya avisados, para no repetir el mismo warning de config cada vez
    // que se aplican.
    private static readonly HashSet<GameplayEffect> _warnedInertEffects = new HashSet<GameplayEffect>();

    // Avisa si un efecto CON duración tiene modificadores sobre "pools" que el
    // sistema de modificadores no puede aplicar y quedarían INERTES en silencio.
    // Los pools (Health/Mana/Energy) tienen su propio valor actual, así que
    // RecalculateAllAttributes los saltea: para tocarlos con un efecto de duración
    // hay que usar Period > 0 (por tick). Shield es la excepción soportada, vía
    // GrantTemporaryShield.
    private void WarnInertPoolModifiers(GameplayEffect effect)
    {
        if (effect.Period > 0 || _warnedInertEffects.Contains(effect)) return;

        foreach (var mod in effect.Modifiers)
        {
            if (mod.Attribute != EAttributeType.Health &&
                mod.Attribute != EAttributeType.Mana &&
                mod.Attribute != EAttributeType.Energy) continue;

            Debug.LogWarning($"[GAS] '{effect.name}' tiene duración y un modificador sobre {mod.Attribute}, " +
                             $"que es un 'pool': NO se aplica por el sistema de modificadores y queda inerte. " +
                             $"Usá Period > 0 para que actúe por ticks (DoT/regeneración), o Shield si querés " +
                             $"un escudo temporal.");
            _warnedInertEffects.Add(effect);
            return;
        }
    }

    // Las pasivas de la clase son efectos activos como cualquier otro (así se ven en
    // la barra), pero NO son estado de combate: sobreviven a la muerte. Se anotan acá
    // al aplicarlas (PlayerController.ApplyClassPassives) y se olvidan al cambiar de
    // clase (RemoveAllActiveEffects).
    private readonly HashSet<GameplayEffect> _classPassives = new HashSet<GameplayEffect>();

    public void ApplyClassPassive(GameplayEffect passive)
    {
        if (passive == null) return;
        _classPassives.Add(passive);
        ApplyGameplayEffect(passive, this);
    }

    // Termina la invisibilidad de este personaje quitando los efectos que la
    // otorgan. Lo dispara ExecuteInstantEffect al atacar o al recibir daño, y
    // también lo puede llamar una habilidad a mano.
    public void BreakInvisibility()
    {
        if (HasTag(EGameplayTag.Status_Invisible))
            RemoveEffectsWithTag(EGameplayTag.Status_Invisible);
    }

    // Otorga el ESCUDO de un efecto CON duración y devuelve cuánto otorgó.
    // Shield es un "pool" (tiene su propio valor actual que el daño consume), así
    // que no puede pasar por el sistema de modificadores/RecalculateAllAttributes
    // —que lo saltea a propósito— sino que se suma acá al aplicar el efecto y se
    // resta al expirar (ver RemoveActiveEffect). Así el escudo se puede ir de las
    // dos formas: consumido por el daño, o retirado al terminar la duración.
    private float GrantTemporaryShield(GameplayEffect effect, object source)
    {
        AbilitySystemComponent sourceASC = source as AbilitySystemComponent;
        float total = 0f;

        foreach (var mod in effect.Modifiers)
        {
            if (mod.Attribute != EAttributeType.Shield) continue;
            // Para un pool solo tiene sentido sumar una cantidad.
            if (mod.Type != Modifier.EModificationType.Add) continue;

            float amount = CalculateBaseMagnitude(mod, sourceASC);
            if (amount > 0f) total += amount;
        }

        if (total > 0f)
            SetCurrentAttributeValue(EAttributeType.Shield, GetAttributeValue(EAttributeType.Shield) + total);

        return total;
    }

    // Suma/resta los modificadores Add/Multiply de un efecto CON duración
    // a los atributos afectados (apply=true al agregarlo, false al
    // quitarlo) y recalcula todo. No toca daño instantáneo.
    private void ApplyEffectModifiers(GameplayEffect effect, bool apply)
    {
        float sign = apply ? 1f : -1f;
        foreach (var mod in effect.Modifiers)
        {
            // Shield es un "pool": no pasa por el sistema de modificadores (que
            // RecalculateAllAttributes saltea igual). Su alta/baja la manejan
            // GrantTemporaryShield y RemoveActiveEffect.
            if (mod.Attribute == EAttributeType.Shield) continue;

            // Si el personaje todavía no tiene ese atributo, se CREA en 0 y el
            // modificador entra igual.
            //
            // Antes acá había un `continue` y era un agujero grande y SILENCIOSO: si el
            // atributo no estaba listado en el AttributeSetDefinition de la clase, el
            // modificador se descartaba sin aviso. Como ninguna clase declaraba
            // Resistance, el Aura de protección del Paladín y el buff de la Intercepción
            // heroica no hacían absolutamente nada, y no había forma de notarlo salvo
            // midiendo el daño a mano.
            //
            // Crear en 0 es lo coherente con el resto del sistema: GetAttributeValue ya
            // devuelve 0 para un atributo ausente, o sea que "no declarado" siempre
            // significó "vale 0". Los POOLS quedan afuera porque un modificador con
            // duración sobre ellos es inerte por diseño (ver WarnInertPoolModifiers).
            if (!Attributes.TryGetValue(mod.Attribute, out AttributeValue attr))
            {
                if (IsPoolAttribute(mod.Attribute)) continue;

                attr = new AttributeValue(0f);
                Attributes[mod.Attribute] = attr;
            }
            if (mod.Type == Modifier.EModificationType.Add)
                attr.AdditiveModifier += mod.Magnitude * sign;
            else if (mod.Type == Modifier.EModificationType.Multiply)
            {
                // Los multiplicadores se MULTIPLICAN entre sí (ver AttributeValue.Multipliers).
                if (apply) attr.AddMultiplier(mod.Magnitude);
                else       attr.RemoveMultiplier(mod.Magnitude);
            }
        }
        RecalculateAllAttributes();
    }

    // Quita un efecto activo: revierte sus modificadores, le saca los tags
    // que otorgaba, y notifica OnActiveEffectRemovedCallback (usado tanto
    // para remoción explícita como para expiración natural). 'why' decide si dispara sus encadenados.
    private void RemoveActiveEffect(ActiveGameplayEffect effect, EEffectEnd why = EEffectEnd.Removed)
    {
        ApplyEffectModifiers(effect.Definition, false);

        // Escudo temporal: retiramos exactamente lo que este efecto otorgó, sin
        // bajar de 0 (si el daño ya se lo comió, no le sacamos escudo de otros).
        if (effect.GrantedShield > 0f)
        {
            float current = GetAttributeValue(EAttributeType.Shield);
            SetCurrentAttributeValue(EAttributeType.Shield, Mathf.Max(0f, current - effect.GrantedShield));
        }

        foreach (EGameplayTag tag in effect.Definition.GrantedTags) RemoveTag(tag);
        ActiveEffects.Remove(effect);
        CountImmunities(effect.Definition, -1);
        OnActiveEffectRemovedCallback?.Invoke(effect.Definition);

        QueueChainedEffects(effect, why);
    }

    // Le hace bajar un tick a la duración (y al período, si es
    // periódico) de todos los efectos activos, ejecuta los ticks que
    // corresponda, y limpia los que ya expiraron. Se llama una vez por
    // frame desde Update(). Los encadenados de lo que venció salen al terminar el barrido.
    private void ProcessActiveEffects(float deltaTime)
    {
        if (ActiveEffects.Count == 0) return;
        BeginHold();
        try
        {
            RemoveEffectsOnEvent(GameplayEffect.ERemoveOnEvent.Move);   // "Se va solo: al moverse"
            ProcessActiveEffectsNow(deltaTime);
        }
        finally { EndHold(); }
    }

    private void ProcessActiveEffectsNow(float deltaTime)
    {

        // Recorremos una COPIA, no la lista viva: un tick puede bajar la vida a 0 y
        // disparar Die() → OnDeath, y cualquier handler que aplique o quite un efecto
        // sobre ESTE mismo personaje modificaría ActiveEffects en plena iteración
        // (InvalidOperationException). Con la copia, esos cambios son seguros.
        _tickBuffer.Clear();
        _tickBuffer.AddRange(ActiveEffects);

        // Un cadáver no recibe ticks: pegarle a un muerto no hace nada y evita
        // re-entrar en la muerte. Las duraciones SÍ siguen corriendo, así que los
        // efectos vencen normalmente aunque el personaje esté esperando revivir.
        bool isDead = HasTag(EGameplayTag.State_Dead);

        List<ActiveGameplayEffect> expired = null;
        List<ActiveGameplayEffect> broken  = null;   // vínculos cortados por distancia (ver LinkBroken)

        foreach (var active in _tickBuffer)
        {
            // Pudo haberlo quitado un handler durante este mismo barrido.
            if (!ActiveEffects.Contains(active)) continue;

            active.DurationRemaining -= deltaTime;

            if (!isDead && active.Definition.Period > 0)
            {
                active.PeriodRemaining -= deltaTime;
                if (active.PeriodRemaining <= 0)
                {
                    // Le pasamos la fuente original para que el tick tenga robo de
                    // vida / escalado por el atacante. isPeriodicTick evita que un
                    // tick de DoT dispare el crítico por la espalda (no queremos que
                    // una herida pegue más fuerte por dónde está parado el atacante).
                    GameplayAbility previousContext = DamageContext;
                    DamageContext = active.SourceAbility;
                    ExecuteInstantEffect(active.Definition, active.Source, true);
                    DamageContext = previousContext;
                    active.PeriodRemaining = active.Definition.Period;
                }
            }

            if (active.IsExpired) (expired ??= new List<ActiveGameplayEffect>()).Add(active);
            else if (LinkBroken(active)) (broken ??= new List<ActiveGameplayEffect>()).Add(active);
        }

        // Un vínculo cortado se QUITA (no vence): sus "Si se lo quitan antes" encadenan.
        if (broken != null)
            foreach (var b in broken)
                if (ActiveEffects.Contains(b)) RemoveActiveEffect(b, EEffectEnd.Removed);

        // Solo se aloca la lista si de verdad venció algo (antes se creaba una
        // lista nueva CADA frame, para todos los personajes).
        if (expired == null) return;

        // Contains: un tick de este mismo barrido pudo matarlo, y morir ya se llevó sus buffs; quitarlo otra vez le
        // revertiría los modificadores dos veces.
        foreach (var e in expired)
            if (ActiveEffects.Contains(e)) RemoveActiveEffect(e, EEffectEnd.Expired);
    }

    // =========================================================
    // ENCADENADOS (18 de noviembre de 2026; los campos están en GameplayEffect: "Al terminar (encadenados)")
    // =========================================================
    //
    // Cuando un efecto con duración se va, se le aplican a quien lo tenía sus "Al vencer, aplica" (si venció) o sus
    // "Si se lo quitan antes, aplica" (si lo quitaron), firmados por quien se lo puso y con la habilidad de origen.
    // No van en el momento: esperan a que termine lo que está recorriendo ActiveEffects (una limpieza, el barrido de
    // cada frame, un ApplyGameplayEffect), para no cambiar la lista en plena vuelta. Un muerto no encadena.

    // Por qué se va un efecto activo.
    private enum EEffectEnd
    {
        Removed,    // lo quitaron antes de tiempo (limpieza, purga, inmunidad, una habilidad): "Si se lo quitan antes"
        Expired,    // se le acabó el tiempo: "Al vencer"
        Replaced,   // volver a aplicarlo lo sacó (pisar, el grupo, la explosión al tope): nada
        Cleared,    // la limpieza de morir, revivir o cambiar de clase: nada
    }

    // Un encadenado pendiente: un efecto a aplicarle a este personaje o (Explosion) un estallido en 'At' a nombre de Source.
    private struct PendingChain
    {
        public GameplayEffect Effect; public object Source; public GameplayAbility Ability;
        public GA_InstantAoE Explosion; public Vector3 At;
    }

    private readonly List<PendingChain> _pendingChains = new List<PendingChain>();
    private int _chainHold;   // > 0: hay una vuelta sobre ActiveEffects en curso; los encadenados esperan

    // Una cadena que dispara otra que dispara otra...: más de esto en la misma pila es casi seguro un círculo.
    private const int MaxChainDepth = 8;
    private static int s_chainDepth;
    private static bool s_warnedChainDepth;

    private void QueueChainedEffects(ActiveGameplayEffect gone, EEffectEnd why)
    {
        List<GameplayEffect> list = why == EEffectEnd.Expired ? gone.Definition.OnExpireEffects
                                  : why == EEffectEnd.Removed ? gone.Definition.OnRemovedEffects : null;

        // La explosión de una marca (marcar y detonar): al quitarla y, con la casilla, al vencer.
        GA_InstantAoE explosion = gone.Definition.ExplosionOnRemoved;
        bool explodes = explosion != null &&
                        (why == EEffectEnd.Removed || (why == EEffectEnd.Expired && gone.Definition.ExplodesOnExpire));

        if ((list == null || list.Count == 0) && !explodes) return;
        if (HasTag(EGameplayTag.State_Dead)) return;

        // El que lo puso ya no está (salió de la partida, una invocación que murió): sin firma, solo lo fijo.
        object source = gone.Source;
        if (source is UnityEngine.Object o && o == null) source = null;

        if (list != null)
            foreach (GameplayEffect next in list)
                if (next != null) _pendingChains.Add(new PendingChain { Effect = next, Source = source, Ability = gone.SourceAbility });

        // Sin quien la puso no hay a nombre de quién estallar.
        if (explodes && source is AbilitySystemComponent)
            _pendingChains.Add(new PendingChain { Explosion = explosion, Source = source, Ability = gone.SourceAbility,
                                                  At = transform.position });

        if (_chainHold == 0) FlushChainedEffects();
    }

    private void EndChainHold()
    {
        _chainHold--;
        if (_chainHold == 0) FlushChainedEffects();
    }

    private void FlushChainedEffects()
    {
        if (_pendingChains.Count == 0 || _chainHold > 0) return;
        if (s_chainDepth >= MaxChainDepth)
        {
            if (!s_warnedChainDepth)
            {
                s_warnedChainDepth = true;
                Debug.LogWarning($"[GAS] Efectos encadenados que se disparan entre sí más de {MaxChainDepth} veces seguidas " +
                                 $"(en '{name}', con '{_pendingChains[0].Effect.name}'): se cortó la cadena. Revisar sus " +
                                 "\"Al vencer\" / \"Si se lo quitan antes\".");
            }
            _pendingChains.Clear();
            return;
        }

        var batch = new List<PendingChain>(_pendingChains);
        _pendingChains.Clear();
        s_chainDepth++;
        try
        {
            foreach (PendingChain c in batch)
            {
                if (c.Explosion != null)
                {
                    // El estallido no depende de que este siga vivo: es un área (la detonación que lo mató también salpica).
                    GA_InstantAoE area = GA_InstantAoE.DeployableFor(c.Explosion, c.Source as AbilitySystemComponent);
                    if (area != null) area.DetonateAt(c.At);
                    continue;
                }
                if (HasTag(EGameplayTag.State_Dead)) break;
                GameplayAbility previousContext = DamageContext;
                DamageContext = c.Ability;
                try { ApplyGameplayEffect(c.Effect, c.Source); }
                finally { DamageContext = previousContext; }
            }
        }
        finally { s_chainDepth--; }
    }

    // Lista en vivo de los efectos activos (buffs/debuffs/cooldowns). Solo
    // está poblada de verdad en la copia servidor — ver notas en
    // NetworkAbilitySystemComponent y UI_EffectContainer.
    public List<ActiveGameplayEffect> GetActiveEffects() => ActiveEffects;
}
