using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — APLICAR LOS EFECTOS
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
//
// La lista Effects de cada habilidad: a quién (por afiliación) y cuándo (al golpear, al primer
// golpe, al activarse, al matar). ApplyHitEffects es la puerta de entrada.
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // APLICAR UNA LISTA DE EFECTOS
    // =========================================================

    // Aplica una lista de GameplayEffect a un objetivo (usando al dueño como
    // fuente), ignorando entradas nulas. Atajo para las habilidades que aplican
    // efectos "extra" además de su daño principal (ralentizar, marcar, heridas,
    // etc.) — ver el campo AdditionalEffects de cada una.
    protected void ApplyEffectsTo(List<GameplayEffect> effects, AbilitySystemComponent target)
    {
        if (effects == null || target == null) return;
        GameplayAbility previousContext = AbilitySystemComponent.DamageContext;
        AbilitySystemComponent.DamageContext = this;
        try
        {
            foreach (GameplayEffect effect in effects)
                if (effect != null) target.ApplyGameplayEffect(effect, OwnerASC);
        }
        finally { AbilitySystemComponent.DamageContext = previousContext; }
    }

    // =========================================================
    // REPARTO POR AFILIACIÓN
    // =========================================================

    // Aplica a 'target' lo que corresponda según su AFILIACIÓN con el dueño, y
    // devuelve true si el objetivo era válido (o sea, si hubo que hacerle algo).
    //
    // Es el punto único que reparte "esto le pasa a los enemigos" vs "esto a los
    // aliados", para que cada habilidad no repita el if. El daño va aparte porque
    // cada habilidad lo dispara con su propio campo (DamageEffect) y necesita saber
    // si el golpe conectó (para el VFX de impacto y la carga de ultimate).
    //
    // POR QUÉ allyEffects VIENE POR PARÁMETRO Y NO ES UN CAMPO DE ACÁ: de las dos
    // docenas de habilidades del juego, solo un puñado le hace algo a los aliados que
    // alcanza. Con la lista en la base, TODAS mostraban en el inspector un campo que
    // nunca iban a usar. Ahora cada habilidad que sí lo necesita declara la suya (ver
    // TargetEffects en GA_ConeAttack, GA_LineAttack y GA_ProjectileShoot).
    //
    // Lista vacía o nula = los aliados se saltean por completo, que es el
    // comportamiento clásico de un ataque normal.
    protected bool ApplyAffiliationEffects(AbilitySystemComponent target, GameplayEffect enemyDamage,
                                           List<GameplayEffect> allyEffects)
    {
        if (target == null) return false;

        if (IsEnemy(target))
        {
            if (enemyDamage != null) ApplyEffectsTo(new List<GameplayEffect> { enemyDamage }, target);
            return true;
        }

        // Aliado (incluido uno mismo): solo cuenta como objetivo si la habilidad
        // tiene efectos configurados para él.
        if (allyEffects != null && allyEffects.Count > 0 && IsAlly(target))
        {
            ApplyEffectsTo(allyEffects, target);
            return true;
        }

        return false;
    }

    // =========================================================
    // LA LISTA DE EFECTOS (Effects)
    // =========================================================

    // ¿Hay alguna entrada con efecto para este momento?
    public bool HasEffects(EEffectWhen when)
    {
        if (Effects == null) return false;
        foreach (AbilityEffect e in Effects)
            if (e.Effect != null && e.When == when) return true;
        return false;
    }

    // ¿Le hace algo a los ALIADOS que alcanza? Con eso un ataque empieza a tenerlos en
    // cuenta (la estela del Castigo divino, que daña enemigos y cura aliados a su paso).
    // Sin nada para ellos, los aliados se saltean: el comportamiento clásico.
    public bool HasAllyHitEffects
    {
        get
        {
            if (Effects == null) return false;
            foreach (AbilityEffect e in Effects)
                if (e.Effect != null && IsHitTiming(e.When) &&
                    (e.ApplyTo == EEffectTarget.Allies || e.ApplyTo == EEffectTarget.Everyone))
                    return true;
            return false;
        }
    }

    // El DAÑO PRINCIPAL: la primera entrada "al golpear, a enemigos" sin condición. Lo usa
    // la barrera para medir cuánto frenó un proyectil.
    public GameplayEffect PrimaryDamageEffect
    {
        get
        {
            if (Effects == null) return null;
            foreach (AbilityEffect e in Effects)
                if (e.Effect != null && e.When == EEffectWhen.OnHit && e.ApplyTo == EEffectTarget.Enemies &&
                    e.OnlyIfTargetHas == EGameplayTag.None)
                    return e.Effect;
            return null;
        }
    }

    // ¿Algún efecto de la lista otorga este tag? Lo usan los bots para entender qué hace
    // una habilidad mirando lo que aplica, no su nombre (¿aturde? ¿me vuelve invisible?).
    public bool AnyEffectGrants(EGameplayTag tag, EEffectWhen? when = null, EEffectTarget? applyTo = null)
    {
        if (Effects == null) return false;
        foreach (AbilityEffect e in Effects)
        {
            if (e.Effect == null || e.Effect.GrantedTags == null) continue;
            if (when.HasValue && e.When != when.Value) continue;
            if (applyTo.HasValue && e.ApplyTo != applyTo.Value) continue;
            if (e.Effect.GrantedTags.Contains(tag)) return true;
        }
        return false;
    }

    // Los efectos de un momento y un destino, en orden (los bots, y quien necesite mirarlos).
    public void GetEffects(EEffectWhen when, EEffectTarget applyTo, List<GameplayEffect> into)
    {
        if (Effects == null || into == null) return;
        foreach (AbilityEffect e in Effects)
            if (e.Effect != null && e.When == when && e.ApplyTo == applyTo) into.Add(e.Effect);
    }

    private static bool IsHitTiming(EEffectWhen when)
        => when == EEffectWhen.OnHit || when == EEffectWhen.OnFirstHit;

    // =========================================================
    // AL ACTIVARSE, AL GOLPEAR Y AL MATAR
    // =========================================================

    private static bool PassesCondition(AbilityEffect e, AbilitySystemComponent subject)
        => e.OnlyIfTargetHas == EGameplayTag.None || (subject != null && subject.HasTag(e.OnlyIfTargetHas));

    // Si CommitAbility aplica solo los efectos "al activarse". La Ira inmortal lo apaga: se
    // cobra estando muerta, y un buff aplicado antes de revivir se perdería al revivir.
    protected virtual bool ApplyActivationEffectsOnCommit => true;

    // Los efectos "al activarse": al lanzador. Una entrada "al activarse" para enemigos o
    // aliados no tiene a quién ir (todavía no golpeó a nadie): el Inspector la marca.
    protected void ApplyActivationEffects()
    {
        if (OwnerASC == null || Effects == null) return;

        foreach (AbilityEffect e in Effects)
        {
            if (e.Effect == null || e.When != EEffectWhen.OnActivate || e.ApplyTo != EEffectTarget.Self) continue;
            if (!PassesCondition(e, OwnerASC)) continue;

            int times = StackApplications(e, out float duration);
            ApplyScaled(OwnerASC, e.Effect, OwnerASC, times, duration);
        }
    }

    // Aplica a 'target' lo que le toca por haber sido ALCANZADO por esta habilidad, y
    // devuelve true si era un objetivo válido:
    //   · un enemigo, siempre;
    //   · un aliado (o uno mismo), solo si la lista tiene algo para los aliados.
    //
    // firstHit: es el primer objetivo de esta activación (lo decide cada habilidad, que
    // sabe en qué orden alcanza): recibe además las entradas "al primer golpe".
    //
    // Va en el orden de la lista, así el aturdido de primer golpe puede ir antes del daño
    // si así está cargado. Las entradas "al lanzador" se aplican una vez por cada ENEMIGO
    // golpeado; las "al matar", cuando el golpe deja muerto a un enemigo que estaba vivo.
    protected bool ApplyHitEffects(AbilitySystemComponent target, bool firstHit = false)
        => ApplyHitEffects(target, firstHit, OwnerASC);

    // La versión completa, para quien golpea "en nombre" de la habilidad (el proyectil):
    //   · source: quién figura como autor del golpe (el que lo devolvió, tras un parry).
    //   · replacements: el efecto que se aplica EN LUGAR de cada uno de la lista (el
    //     proyectil devuelto pone sus copias congeladas con los stats de quien lo tiró:
    //     ver GameplayEffect.CreateFrozenCopy). Los que no están, van tal cual.
    //   · onlyHostile: solo lo que va a los enemigos — nada para aliados, ni para el autor,
    //     ni de primer golpe (un proyectil devuelto no premia al que lo devolvió con los
    //     efectos de otra clase).
    public bool ApplyHitEffects(AbilitySystemComponent target, bool firstHit, AbilitySystemComponent source,
                                IReadOnlyDictionary<GameplayEffect, GameplayEffect> replacements = null,
                                bool onlyHostile = false)
    {
        // Mientras se aplican, el daño queda anotado a nombre de esta habilidad (el resumen de muerte).
        GameplayAbility previousContext = AbilitySystemComponent.DamageContext;
        AbilitySystemComponent.DamageContext = this;
        try { return ApplyHitEffectsCore(target, firstHit, source, replacements, onlyHostile); }
        finally { AbilitySystemComponent.DamageContext = previousContext; }
    }

    private bool ApplyHitEffectsCore(AbilitySystemComponent target, bool firstHit, AbilitySystemComponent source,
                                     IReadOnlyDictionary<GameplayEffect, GameplayEffect> replacements,
                                     bool onlyHostile)
    {
        if (target == null || source == null) return false;

        // Algo del mapa que se golpea (la campana): se le avisa quién pegó y nada más. False: para quien
        // llama no es un golpe (sin carga de definitiva, ni VFX, ni "primer golpe").
        if (target.Strikeable != null)
        {
            target.Strikeable.Strike(source, this);
            return false;
        }

        bool enemy = source.IsEnemyOf(target);
        bool ally  = !enemy && !onlyHostile && source.IsAllyOf(target, includeSelf: true);

        if (!enemy && !(ally && HasAllyHitEffects)) return false;

        if (enemy && firstHit && !onlyHostile && !IsEcho && StopsOnImpact)
        {
            NetworkAbilitySystemComponent net = source.GetComponent<NetworkAbilitySystemComponent>();
            if (net != null) net.ServerImpactStop();
        }

        if (Effects == null) return true;

        bool wasAlive = !target.HasTag(EGameplayTag.State_Dead);
        bool healed = false;   // para los VFX "al curar"

        // Para las entradas "al terminar de golpear": un enemigo más y sus puntos, ANTES de que las
        // de abajo los gasten.
        if (enemy && !onlyHostile) TallyForAfterHits(target);

        foreach (AbilityEffect e in Effects)
        {
            if (e.Effect == null) continue;

            bool timing = e.When == EEffectWhen.OnHit ||
                          (e.When == EEffectWhen.OnFirstHit && firstHit && !onlyHostile);
            if (!timing) continue;

            // ¿Le toca a este objetivo? (el lanzador cobra lo suyo por cada ENEMIGO golpeado)
            bool reaches;
            switch (e.ApplyTo)
            {
                case EEffectTarget.Enemies:  reaches = enemy;                 break;
                case EEffectTarget.Allies:   reaches = ally;                  break;
                case EEffectTarget.Everyone: reaches = enemy || ally;         break;
                default:                     reaches = enemy && !onlyHostile; break;   // Self
            }
            if (!reaches || !PassesCondition(e, target)) continue;

            // Con las acumulaciones leídas: sin ellas, lo que escala no se aplica.
            int times = StackApplications(e, out float duration);
            if (times <= 0) continue;

            // Con los puntos del OBJETIVO (los puntos arcanos del Mago): la duración o las veces.
            int targetStacks = 0;
            if (e.TargetScaling != ETargetStackScaling.None && e.TargetStacksTag != EGameplayTag.None &&
                e.ApplyTo != EEffectTarget.Self)
            {
                targetStacks = target.GetTagCount(e.TargetStacksTag);
                if (!ScaleByTargetPoints(e, targetStacks, ref times, ref duration)) continue;
            }

            // El filtro del primer golpe va al final: puede ANOTAR algo (el proyectil anota
            // cuándo se puede volver a aturdir a ese enemigo), y no tiene que anotar nada que
            // después no se aplique.
            if (e.When == EEffectWhen.OnFirstHit && !CanApplyFirstHitEffect(target, e.Effect)) continue;

            // Espera por enemigo: al MISMO objetivo, una vez cada tanto (la anota al aplicarse).
            if (e.PerTargetCooldown > 0f && e.ApplyTo != EEffectTarget.Self &&
                !target.TryStartEffectCooldown(CooldownKey, e.Effect, source, e.PerTargetCooldown)) continue;

            GameplayEffect effect = e.Effect;
            if (replacements != null && replacements.TryGetValue(effect, out GameplayEffect replacement) &&
                replacement != null)
                effect = replacement;
            effect = AsMagicWhileTagged(effect);   // la Espada mágica del Filo: el daño pasa a mágico
            effect = ScaledForDamage(effect);      // las copias de los ecos: la parte de su daño

            if (e.ApplyTo == EEffectTarget.Self) ApplyScaled(source, effect, source, times, duration);
            else                                 ApplyScaled(target, effect, source, times, duration);

            // Gastarlos (el aturdido del Terremoto): las entradas de abajo ya los ven en 0.
            if (e.ConsumeTargetStacks && targetStacks > 0) target.RemoveEffectsWithTag(e.TargetStacksTag);

            if (ally && e.ApplyTo != EEffectTarget.Self && effect.HealsHealth) healed = true;
        }

        if (enemy && !onlyHostile && wasAlive && target.HasTag(EGameplayTag.State_Dead))
            ApplyKillEffects(target, source);

        // Los VFX "al curar": sobre el aliado curado, una vez por golpe, sin importar cuántas
        // entradas lo curaron. Cada habilidad elige el suyo (o ninguno) en su lista de VFX.
        if (healed) BroadcastHealVFX(target);

        return true;
    }

    // Las entradas "al matar": al lanzador (las demás no tienen sentido: el Inspector las
    // marca). 'victim' es el que murió, para la condición de tag.
    private void ApplyKillEffects(AbilitySystemComponent victim, AbilitySystemComponent source)
    {
        foreach (AbilityEffect e in Effects)
        {
            if (e.Effect == null || e.When != EEffectWhen.OnKill || e.ApplyTo != EEffectTarget.Self) continue;
            if (!PassesCondition(e, victim)) continue;

            int times = StackApplications(e, out float duration);
            ApplyScaled(source, e.Effect, source, times, duration);
        }
    }

    // Filtro extra para las entradas "al primer golpe". El proyectil lo usa para no
    // re-aturdir al mismo enemigo con cada disparo (FirstHitCooldownPerTarget).
    protected virtual bool CanApplyFirstHitEffect(AbilitySystemComponent target, GameplayEffect effect) => true;

    // ¿Qué momentos acepta esta habilidad? Para que el Inspector marque las entradas que
    // nunca se van a aplicar. "Al activarse" lo aceptan todas (lo hace CommitAbility).
    public virtual bool SupportsEffectTiming(EEffectWhen when)
        => when == EEffectWhen.OnActivate ||
           (when == EEffectWhen.AfterHits ? SupportsAfterHits && UsesHitEffects : UsesHitEffects);

    // ¿Qué VFX acepta? "Al lanzar" todas; "al golpear" las que golpean; "en el impacto" las
    // que tienen un punto donde caen.
    public virtual bool SupportsVisualTiming(EVisualWhen when)
        => when == EVisualWhen.OnCast ||
           ((when == EVisualWhen.OnHit || when == EVisualWhen.OnHeal) && UsesHitEffects);
}
