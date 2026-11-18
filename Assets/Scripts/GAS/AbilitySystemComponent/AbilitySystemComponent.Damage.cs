using UnityEngine;
using System.Collections.Generic;
using System;

// ============================================================
// AbilitySystemComponent — EL CAMINO DEL DAÑO
//
// Una parte de AbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/AbilitySystemComponent.cs; ahí está también la lista de las partes.
//
// Lo que pasa cuando un efecto INSTANTÁNEO le llega a este personaje (ExecuteInstantEffect):
// magnitud y escalados, crítico, bloqueo, defensas, escudo, vida, robo de vida y ejecutar.
// Lo que cambia el número en el camino (defensas, modificadores, espalda) está en
// DamageModifiers. Lo vigilan las pruebas de Scripts/Editor/Tests/GasDamagePipelineTests.cs.
// ============================================================
public partial class AbilitySystemComponent
{
    // =========================================================
    // EL CAMINO DEL DAÑO (y de la curación): un efecto instantáneo sobre ESTE personaje
    // =========================================================

    // Aplica de una vez los Modifiers de un efecto SIN duración (daño,
    // curación, etc.). Calcula escalado con stats del atacante, resuelve
    // el escudo antes que la vida, y dispara robo de vida si corresponde.
    private void ExecuteInstantEffect(GameplayEffect effect, object source = null, bool isPeriodicTick = false)
    {
        AbilitySystemComponent sourceASC = source as AbilitySystemComponent;

        bool wasDamagingHit = false;

        // Daño que de verdad llegó a la VIDA en este golpe (después de bloqueo,
        // defensas y escudo). Se acumula sobre todos los modificadores del efecto y
        // viaja en OnDealtDamage, para las pasivas que curan/escalan con lo hecho
        // (Aura de protección del Paladín).
        float damageToHealth = 0f;

        // Para la carga de la definitiva por rol (ver OnDamageEndured / OnHealedAlly).
        float damageEndured = 0f;
        float healthHealed  = 0f;

        // Si algún golpe de este efecto fue crítico: el número sale amarillo y la X del
        // que pegó también.
        bool anyCritical = false;

        foreach (var mod in effect.Modifiers)
        {
            if (!Attributes.ContainsKey(mod.Attribute)) continue;

            float calculatedMagnitude = CalculateBaseMagnitude(mod, sourceASC);
            float traceBase = calculatedMagnitude;   // para el depurador: el número antes de crítico y bonos
            bool traceCrit = false;

            // Pipeline de modificadores de daño SALIENTE del atacante (backstab del
            // Pícaro, crítico mejorado del Asesino, futuros bonos...). Cada pasiva
            // registra su IDamageModifier en el ASC de su dueño; acá recorremos esa
            // lista y resolvemos el crítico. Solo aplica a daño directo a la vida.
            if (sourceASC != null && mod.Attribute == EAttributeType.Health && calculatedMagnitude < 0)
            {
                // "Crítico si el objetivo tiene" (GameplayEffect.CritIfTargetHas): lo decide el efecto.
                bool forceCrit = effect.CritIfTargetHas != EGameplayTag.None && HasTag(effect.CritIfTargetHas);
                calculatedMagnitude = sourceASC.ResolveOutgoingDamage(this, calculatedMagnitude, isPeriodicTick,
                                                                      out bool critical, forceCrit);
                // Un crítico que no entra (inmunidad) no se festeja: se anota al ver
                // que el golpe pasó, más abajo.
                if (critical && !HasTag(EGameplayTag.Status_Immunity)) anyCritical = true;
                traceCrit = critical;
            }

            // Lo mismo para las CURACIONES que reparte el autor (Médico bendecido del
            // Clérigo de la Vida). Ver IHealModifier.
            if (sourceASC != null && mod.Attribute == EAttributeType.Health && calculatedMagnitude > 0)
                calculatedMagnitude = sourceASC.ResolveOutgoingHeal(this, calculatedMagnitude, isPeriodicTick);

            // Y la curación que RECIBE este personaje (Faro de esperanza del Clérigo de la
            // Luz: +15 % dentro del aura). Vale para toda curación que llegue por un efecto,
            // tenga autor o no (un botiquín también).
            if (mod.Attribute == EAttributeType.Health && calculatedMagnitude > 0)
            {
                float bonus = GetAttributeValue(EAttributeType.HealingReceived);
                if (bonus != 0f) calculatedMagnitude *= Mathf.Max(0f, 1f + bonus);
            }

            if (mod.Attribute == EAttributeType.Health && calculatedMagnitude < 0)
            {
                // INMUNIDAD TOTAL (Status_Immunity, ej. Protección divina del Paladín):
                // el golpe no entra y punto. Se saltea el modificador entero, así que
                // tampoco consume escudo, ni anota al atacante, ni rompe invisibilidad,
                // ni dispara las pasivas de "al golpear".
                //
                // Es distinto de Status_Immortal, que NO es inmunidad: ahí el daño entra
                // normal y lo único que pasa es que la vida no puede bajar de 1.
                //
                // Solo se saltea este modificador: un efecto que además ralentice sigue
                // ralentizando. Lo que la inmunidad niega es el DAÑO.
                if (HasTag(EGameplayTag.Status_Immunity)) continue;

                // DEFENSA DE AGUA (Status_DamageToHeal, el Maestro elemental): el golpe no daña,
                // cura lo mismo que iba a hacer. No cuenta como golpe recibido.
                if (HasTag(EGameplayTag.Status_DamageToHeal))
                {
                    float before = GetAttributeValue(EAttributeType.Health);
                    SetCurrentAttributeValue(EAttributeType.Health, before + Mathf.Abs(calculatedMagnitude));
                    float healed = GetAttributeValue(EAttributeType.Health) - before;
                    if (healed > 0f) ShowCombatNumber(healed, ECombatNumber.Heal);
                    continue;
                }

                wasDamagingHit = true;

                // Registrar al atacante para atribuir la baja (EXP al matador). Se
                // guarda antes de resolver escudo/vida: aunque este golpe no sea el
                // letal, deja anotado quién fue el último en pegar.
                if (sourceASC != null && !ReferenceEquals(sourceASC, this))
                {
                    LastAttacker = sourceASC;
                    _lastHitBy[sourceASC] = Time.time;
                }

                // EL TIPO DE DANO SALE DEL ATRIBUTO DEL QUE ESCALA EL MODIFICADOR.
                //
                // Si escala de MagicDamage, es magico; de cualquier otra cosa (Attack, o un
                // valor plano sin escalado), fisico. No hace falta declararlo aparte: el
                // atributo YA lo dice, y tener las dos cosas invitaba a que se contradijeran.
                //
                // Antes se sumaba el MagicDamage del atacante a TODO golpe que hiciera dano,
                // asi que un personaje con ese stat pegaba mitad magico incluso dando un
                // bastonazo, y ninguna habilidad podia ser puramente fisica.
                bool isMagic = mod.UseAttributeScaling && mod.SourceAttribute == EAttributeType.MagicDamage;

                float rawDamage      = Mathf.Abs(calculatedMagnitude);
                float physicalDamage = isMagic ? 0f : rawDamage;
                float magicDamage    = isMagic ? rawDamage : 0f;

                // Bloqueo DIRECCIONAL antes que nada (escudo del Paladín y compañía):
                // lo que la barrera frena no llega siquiera a las defensas. Ver
                // ResolveIncomingDamage / IIncomingDamageModifier.
                ResolveIncomingDamage(sourceASC, ref physicalDamage, ref magicDamage, isPeriodicTick);
                float traceBlocked = physicalDamage + magicDamage;

                // Defensas del que RECIBE, ya con TODO el daño entrante sumado
                // (físico + mágico) y antes de tocar el escudo. Ver ApplyDefenses.
                ApplyDefenses(ref physicalDamage, ref magicDamage);

                float currentShield      = GetAttributeValue(EAttributeType.Shield);
                float damageBeforeShield = physicalDamage + magicDamage;
                damageEndured           += damageBeforeShield;

                if (currentShield > 0)
                {
                    if (magicDamage > 0)
                    {
                        float effectiveMagic = magicDamage * 2f;
                        if (currentShield >= effectiveMagic) { currentShield -= effectiveMagic; magicDamage = 0f; }
                        else { magicDamage -= currentShield / 2f; currentShield = 0f; }
                    }
                    if (currentShield > 0 && physicalDamage > 0)
                    {
                        if (currentShield >= physicalDamage) { currentShield -= physicalDamage; physicalDamage = 0f; }
                        else { physicalDamage -= currentShield; currentShield = 0f; }
                    }
                    SetCurrentAttributeValue(EAttributeType.Shield, currentShield);
                }

                // ESCUDO QUE CURA (Status_HealShield — "Cubrir con escudo" del Juramento
                // de la conquista, y más adelante la defensa mejorada del Monje): lo que
                // el escudo alcanzó a frenar no solo no duele, además vuelve como VIDA.
                //
                // El escudo se gasta igual, así que es un INTERCAMBIO: un escudo de 50
                // contra un golpe de 60 cura 50, se rompe, y los 10 que sobran entran
                // normal (ya pasados por bloqueo y defensas, que corren más arriba).
                //
                // Se cura el DAÑO NEUTRALIZADO, no los puntos de escudo gastados. Solo
                // se diferencian con daño MÁGICO, que cuesta el doble de escudo: 50 de
                // escudo frenan 25 de magia, y son 25 los que se curan.
                //
                // Esto es A PROPÓSITO, no un descuido de la conversión: el escudo es
                // mágico, así que las clases que pegan con daño mágico son su contra
                // natural. Le rompen el doble de escudo por punto de daño y, al frenarse
                // menos daño, el dueño se cura menos. Es el mismo hecho visto dos veces,
                // y hace que un mago sea la respuesta a un Paladín de la Conquista.
                //
                // El tag lo trae el mismo GE que otorga el escudo, así que cuando el
                // escudo se agota la curación deja de sumar sola (absorbed queda en 0)
                // aunque al tag le quede duración.
                //
                // Cuánto de lo frenado vuelve lo dice ese GE ("Escudo que cura: vida por lo
                // frenado", desde el 29 de octubre de 2026: con todo, el escudo de la Conquista
                // valía el doble de vida y curaba de más).
                float absorbedByShield = damageBeforeShield - (physicalDamage + magicDamage);
                if (TraceDamage)
                    RecordTrace(effect, sourceASC, isPeriodicTick, traceCrit, isMagic, Mathf.Abs(traceBase), rawDamage,
                                traceBlocked, damageBeforeShield, absorbedByShield, physicalDamage + magicDamage);
                if (absorbedByShield > 0f && HasTag(EGameplayTag.Status_HealShield))
                {
                    float before = GetAttributeValue(EAttributeType.Health);
                    SetCurrentAttributeValue(EAttributeType.Health, before + absorbedByShield * HealShieldReturn());
                    healthHealed += Mathf.Max(0f, GetAttributeValue(EAttributeType.Health) - before);
                }

                calculatedMagnitude = -(physicalDamage + magicDamage);
                damageToHealth     += physicalDamage + magicDamage;
            }

            float healthBefore = GetAttributeValue(EAttributeType.Health);

            float newValue = CalculateModifiedValue(Attributes[mod.Attribute].CurrentValue, mod, calculatedMagnitude);
            SetCurrentAttributeValue(mod.Attribute, newValue);
            HandleLifeSteal(mod, calculatedMagnitude, sourceASC);

            // Curación EFECTIVA: se mide la vida antes y después, así lo que se pasaba
            // de la máxima (y se descartó) no cuenta como curado.
            if (mod.Attribute == EAttributeType.Health && calculatedMagnitude > 0f)
                healthHealed += Mathf.Max(0f, GetAttributeValue(EAttributeType.Health) - healthBefore);
        }

        // Carga de la definitiva por ROL. Va aparte del bloque de abajo porque ese
        // saltea los ticks periódicos, y acá sí cuentan. Solo si el efecto vino de
        // OTRO personaje: el daño de una zona del mapa, la vida que te da un botiquín
        // o tu propio robo de vida no son hacer tu rol.
        if (sourceASC != null && !ReferenceEquals(sourceASC, this))
        {
            if (damageEndured > 0f) OnDamageEndured?.Invoke(damageEndured);
            if (damageEndured > 0f) CreditOf(sourceASC).NotifyDamageDone(this, damageEndured);

            // Vuelo libre: cualquier daño de otro lo baja (cae lento, ver PlayerController). Con
            // vuelo firme (la Sobrecarga de la Evocación) no.
            if (damageEndured > 0f && HasTag(EGameplayTag.Status_Flying) && !HasTag(EGameplayTag.Status_SteadyFlight))
                RemoveEffectsWithTag(EGameplayTag.Status_Flying);
            if (healthHealed > 0f && !sourceASC.IsEnemyOf(this))
            {
                _lastHealedBy[sourceASC] = Time.time;
                sourceASC.NotifyHealedAlly(this, healthHealed);
            }
        }
        else if (ReferenceEquals(sourceASC, this) && healthHealed > 0f)
        {
            OnHealedSelf?.Invoke(healthHealed);
        }

        // "Se va solo" (GameplayEffect.RemoveOn): al recibir daño (de cualquiera menos de sí mismo, también un veneno o una
        // zona sin autor) y, al que lo hizo, al hacer daño. Lo que frenó un escudo también cuenta.
        if (damageEndured > 0f && !ReferenceEquals(sourceASC, this))
        {
            RemoveEffectsOnEvent(GameplayEffect.ERemoveOnEvent.TakeDamage);
            if (sourceASC != null) sourceASC.RemoveEffectsOnEvent(GameplayEffect.ERemoveOnEvent.DealDamage);
        }

        // EVENTOS DE JUEGO (ver AbilitySystemComponent.Events y GameplayEvents.cs): cada uno desde los ojos de a quién le pasó.
        if (damageEndured > 0f && !ReferenceEquals(sourceASC, this))
        {
            SendGameplayEvent(EGameplayEvent.DamageTaken, sourceASC, damageEndured, isPeriodicTick);
            if (anyCritical) SendGameplayEvent(EGameplayEvent.CriticalTaken, sourceASC, damageEndured, isPeriodicTick);
            if (sourceASC != null)
            {
                sourceASC.SendGameplayEvent(EGameplayEvent.DamageDealt, this, damageEndured, isPeriodicTick);
                if (anyCritical) sourceASC.SendGameplayEvent(EGameplayEvent.CriticalDealt, this, damageEndured, isPeriodicTick);
                if (!isPeriodicTick && IsBackstab(sourceASC)) sourceASC.SendGameplayEvent(EGameplayEvent.BackstabDealt, this, damageEndured);
            }
        }
        if (healthHealed > 0f)
        {
            SendGameplayEvent(EGameplayEvent.HealReceived, sourceASC, healthHealed, isPeriodicTick);
            if (sourceASC != null && !ReferenceEquals(sourceASC, this) && !sourceASC.IsEnemyOf(this))
                sourceASC.SendGameplayEvent(EGameplayEvent.HealDealt, this, healthHealed, isPeriodicTick);
        }

        // El resumen de muerte: todo daño recibido, venga de quien venga (también de una zona sin autor).
        if (damageEndured > 0f)
            RecordDamageTaken(sourceASC != null ? CreditOf(sourceASC) : null, damageEndured, isPeriodicTick);

        // Los números flotantes, sobre este personaje. El daño es lo que pasó bloqueo y
        // defensas (lo que frenó un escudo también cuenta: se lo pegaron igual); la
        // curación, lo que subió la vida de verdad.
        if (damageEndured > 0f)
        {
            ECombatNumber kind = anyCritical   ? ECombatNumber.CriticalDamage
                               : isPeriodicTick ? ECombatNumber.DamageOverTime
                                                : ECombatNumber.Damage;
            ShowCombatNumber(damageEndured, kind, shielded: damageToHealth <= 0f);
        }
        if (healthHealed > 0f) ShowCombatNumber(healthHealed, ECombatNumber.Heal);
        // (el escudo de la Conquista que cura lo que frena ya sumó arriba en healthHealed)

        // Rotura de invisibilidad: un golpe de daño delata tanto a quien lo RECIBE
        // (this) como a quien lo REPARTE (sourceASC).
        //
        // Solo en golpes DIRECTOS (no ticks de DoT): un tick periódico corre desde
        // ProcessActiveEffects, que está iterando ActiveEffects — y BreakInvisibility
        // remueve efectos de esa misma lista, lo que reventaría la iteración. Además,
        // el caso "estoy invisible y me tickea un veneno viejo" es marginal.
        if (wasDamagingHit && !isPeriodicTick)
        {
            BreakInvisibility(); // el objetivo se vuelve visible al ser golpeado

            if (sourceASC != null && !ReferenceEquals(sourceASC, this))
            {
                sourceASC.BreakInvisibility();       // el atacante se delata al golpear
                sourceASC.NotifyDealtDamage(this, damageToHealth); // pasivas "al golpear" (Cuchillas ilusorias, Aura del Paladín)
                NotifyTookDamage(sourceASC);         // reacciones "al ser golpeado" (ej. Copia exacta)

                // Lo que VEN los dos: la X en la retícula del que pegó y el arco rojo en
                // la pantalla del que recibió. Acá es el único lugar donde se sabe todo
                // junto —quién, a quién y cuánto entró de verdad— así que de acá sale.
                if (damageToHealth > 0f) ReportCombatFeedback(sourceASC, anyCritical);
            }
        }

        // Con un efecto que ejecuta encima (el Corte final), este golpe puede rematarlo.
        if (wasDamagingHit) CheckExecute();
    }

    // =========================================================
    // LA MAGNITUD DE UN MODIFICADOR (fijo + escalados) y su estimación sin aplicarlo
    // =========================================================

    // Calcula la magnitud final de un modificador: su valor fijo + el escalado
    // con un atributo del ATACANTE + el escalado con la vida del OBJETIVO (this).
    // No incluye el crítico por la espalda (eso es exclusivo del daño a la vida).
    //
    // El escalado por vida del objetivo sirve tanto para daño (Golpe mortal del
    // Pícaro: % de la vida faltante del enemigo) como para otorgar (escudo del
    // Berserker: % de su PROPIA vida faltante, porque el efecto se lo aplica a sí
    // mismo). El SIGNO del coeficiente marca la dirección, igual que Magnitude:
    // negativo = quita (daño), positivo = otorga (curación/escudo).
    private float CalculateBaseMagnitude(Modifier mod, AbilitySystemComponent sourceASC)
    {
        float magnitude = mod.Magnitude;

        if (mod.UseAttributeScaling && sourceASC != null)
            magnitude += sourceASC.GetAttributeValue(mod.SourceAttribute) * mod.AttributeCoefficient;

        if (mod.UseTargetHealthScaling)
        {
            float portion;
            switch (mod.TargetHealthMode)
            {
                case Modifier.ETargetHealthMode.CurrentHealth: portion = GetAttributeValue(EAttributeType.Health); break;
                case Modifier.ETargetHealthMode.MaxHealth:     portion = GetAttributeValue(EAttributeType.MaxHealth); break;
                default:                                        portion = GetAttributeValue(EAttributeType.MaxHealth) - GetAttributeValue(EAttributeType.Health); break;
            }
            magnitude += portion * mod.TargetHealthCoefficient;
        }

        // Piso del resultado, en VALOR ABSOLUTO: "que nunca de menos de X", aplicado en la
        // direccion que el modificador ya tiene. Poner 1 en un efecto de dano significa
        // "al menos 1 de dano", no "+1 de vida".
        //
        // La direccion NO se saca del valor calculado: puede haber dado exactamente 0 (un
        // escalado por vida faltante a vida llena) y ahi no hay signo del que agarrarse.
        // Se saca de como esta CONFIGURADO el modificador.
        if (mod.MinMagnitude != 0f)
        {
            float floor = Mathf.Abs(mod.MinMagnitude);

            if (Mathf.Abs(magnitude) < floor)
            {
                float intent = mod.Magnitude;
                if (Mathf.Approximately(intent, 0f) && mod.UseAttributeScaling)    intent = mod.AttributeCoefficient;
                if (Mathf.Approximately(intent, 0f) && mod.UseTargetHealthScaling) intent = mod.TargetHealthCoefficient;

                magnitude = intent < 0f ? -floor : floor;
            }
        }

        return magnitude;
    }

    // Cuánto daño a la VIDA le haría a ESTE personaje un efecto instantáneo lanzado
    // por 'sourceASC', si se aplicara ahora. Devuelve un número positivo (0 si el
    // efecto no hace daño).
    //
    // Es una ESTIMACIÓN a propósito: incluye la magnitud base, el escalado por stats
    // del atacante, el escalado por la vida de este objetivo y el daño mágico plano —
    // o sea, todo lo que se puede saber sin llegar a aplicar el golpe. Lo que NO
    // incluye es lo que solo se resuelve al impactar: críticos, bloqueo, defensas y
    // escudo.
    //
    // Existe para los casos en los que un golpe se FRENA antes de resolverse y aun así
    // hace falta saber cuánto valía. Hoy: un proyectil que muere contra una barrera
    // (ver Entity_ShieldBarrier.NotifyProjectileBlocked), donde el escudo tiene que
    // reportar cuánto daño evitó para que las pasivas que escalan con eso —la curación
    // del aura del Paladín— funcionen igual que con un golpe normal.
    public float EstimateInstantDamage(GameplayEffect effect, AbilitySystemComponent sourceASC)
    {
        // Solo los instantáneos: un efecto con duración no "pega" al aplicarse.
        if (effect == null || effect.HasDuration) return 0f;

        float total = 0f;

        foreach (var mod in effect.Modifiers)
        {
            if (mod.Attribute != EAttributeType.Health) continue;

            float magnitude = CalculateBaseMagnitude(mod, sourceASC);
            if (magnitude < 0f) total += -magnitude;   // negativo = daño
        }

        return total;
    }

    // =========================================================
    // ESCUDO QUE CURA Y ROBO DE VIDA
    // =========================================================

    // Qué parte de lo que frena el escudo vuelve como vida (Status_HealShield): la del GE
    // que da el tag ("Escudo que cura: vida por lo frenado"); con dos, la mayor. Sin
    // ninguno a la vista (un tag puesto a mano), todo.
    private float HealShieldReturn()
    {
        float best = -1f;
        foreach (var active in ActiveEffects)
            if (active.Definition.GrantedTags.Contains(EGameplayTag.Status_HealShield))
                best = Mathf.Max(best, active.Definition.HealShieldReturn);
        return best < 0f ? 1f : Mathf.Clamp01(best);
    }

    // Cura al atacante (sourceASC) un porcentaje del daño infligido, según
    // su stat LifeSteal. Solo aplica si el modificador bajó la Vida.
    private void HandleLifeSteal(Modifier mod, float magnitude, AbilitySystemComponent sourceASC)
    {
        if (mod.Attribute != EAttributeType.Health || magnitude >= 0 || sourceASC == null) return;
        float ls = sourceASC.GetAttributeValue(EAttributeType.LifeSteal);
        if (ls <= 0) return;
        float heal = Mathf.Abs(magnitude) * ls;
        float cur  = sourceASC.GetAttributeValue(EAttributeType.Health);
        float max  = sourceASC.GetAttributeValue(EAttributeType.MaxHealth);
        sourceASC.SetCurrentAttributeValue(EAttributeType.Health, Mathf.Clamp(cur + heal, 0, max));

        // Número verde sobre el que se curó (se junta con los demás golpes del momento,
        // ver ServerShowCombatNumber).
        sourceASC.ShowCombatNumber(Mathf.Clamp(cur + heal, 0, max) - cur, ECombatNumber.Heal);

        // La columna "Curación" de la tabla sí lo cuenta (13 de noviembre de 2026, pedido de Gustavo). Va por su
        // propio aviso y no por OnHealedSelf: ese lo escuchan pasivas (la Bendición) que no esperan el robo de vida.
        sourceASC.OnLifeStolen?.Invoke(Mathf.Clamp(cur + heal, 0, max) - cur);
    }

    // =========================================================
    // EJECUTAR (GameplayEffect.ExecuteBelowHealth — el Corte final del Samurái)
    // =========================================================

    // Mientras tenga un efecto que ejecuta, si su vida quedó en ese porcentaje de su vida
    // máxima o menos, muere. Se mira al recibir el efecto y después de cada golpe. La baja
    // es de quien le puso el efecto (LastAttacker). No mata a un inmune, y a un inmortal
    // (Preservar vida) SetCurrentAttributeValue lo deja en 1.
    private void CheckExecute()
    {
        if (HasTag(EGameplayTag.State_Dead) || HasTag(EGameplayTag.Status_Immunity)) return;

        float max    = GetAttributeValue(EAttributeType.MaxHealth);
        float health = GetAttributeValue(EAttributeType.Health);
        if (max <= 0f || health <= 0f) return;

        bool execute = false;
        AbilitySystemComponent executor = null;
        foreach (ActiveGameplayEffect active in ActiveEffects)
        {
            float threshold = active.Definition != null ? active.Definition.ExecuteBelowHealth : 0f;
            if (threshold <= 0f || health > max * threshold) continue;

            execute  = true;
            executor = active.Source as AbilitySystemComponent;
            break;
        }
        if (!execute) return;

        if (executor != null && !ReferenceEquals(executor, this)) LastAttacker = executor;
        SetCurrentAttributeValue(EAttributeType.Health, 0f);
    }
}
