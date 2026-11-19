using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — EL CICLO DE VIDA
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
//
// Otorgarla, si puede activarse, comprometerla (cobrar, cooldown, efectos y VFX "al activarse"),
// terminarla; la duración del cooldown y la carga de la definitiva.
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // CICLO DE VIDA
    // =========================================================

    // True si ESTA activación llegó a CommitAbility, o sea si de verdad se ejecutó.
    // Una habilidad que se planta sola —el Golpe mortal del Pícaro sin nadie a tiro—
    // sale de Activate() sin haber comprometido nada, y entonces tampoco tiene que
    // animarse: la reinicia ServerActivateAbility antes de cada Activate().
    [System.NonSerialized] public bool CommittedThisActivation;

    // True si ESTA activación arrancó de verdad aunque cobre MÁS TARDE. Un lanzamiento
    // (GA_ProjectileShoot) cobra al soltar el proyectil, no al empezar la animación: al
    // salir de Activate() todavía no comprometió nada, y sin esta marca el servidor
    // creía que se había plantado y no les mandaba la animación a los demás (el hacha
    // del Bárbaro y la daga del Pícaro volaban sin que se viera el brazo). La reinicia
    // ServerActivateAbility junto con la de arriba.
    [System.NonSerialized] public bool StartedThisActivation;

    // Para no repetir el aviso de "CooldownEffect sin tag" en cada activación.
    [System.NonSerialized] private bool _warnedNoCooldownTag;

    // La llama AbilitySystemComponent.GrantAbility() al otorgar esta
    // instancia a un personaje.
    public void Initialize(AbilitySystemComponent asc)
    {
        OwnerASC = asc;
    }

    // Valida si la habilidad se puede activar ahora mismo: dueño vivo,
    // sin tags bloqueantes, sin cooldown activo, y con costo pagable.
    // Cada subclase puede sobreescribirla para agregar condiciones extra
    // (ver GA_ImmortalWrath, que solo se activa estando muerto).
    public virtual bool CanActivate()
    {
        if (OwnerASC == null) return false;

        // Una repetición de un canalizado continuo: lo que la cortaría ya lo mira la
        // canalización en cada frame, y el cooldown (ya cobrado) o el tag de canalizar la
        // frenarían sin razón. Lo propio de cada habilidad (tener a alguien a tiro) sigue.
        if (_castTicking) return true;

        if (OwnerASC.HasTag(EGameplayTag.State_Dead)) return false;

        // Ya canalizándose (el cooldown recién se cobra al soltar): no se arranca otra vez.
        if (_castRunning) return false;

        if (ActivationBlockedTags != null)
            foreach (EGameplayTag tag in ActivationBlockedTags)
                if (OwnerASC.HasTag(tag)) return false;

        // Canalizado en curso: solo pasan las marcadas como usables durante uno. El
        // molinete del bárbaro deja seguir usando Frenzy y el salto, y nada más.
        if (!UsableWhileChanneling && OwnerASC.HasTag(EGameplayTag.Status_Channeling)) return false;

        // Desarmado, silenciado y enraizado NO se resuelven acá: cada habilidad dice en su
        // ActivationBlockedTags qué la bloquea (1 de octubre, pedido de Gustavo):
        //   · State_Disarmed  → las acciones de ARMA (golpear con ella o lanzarla).
        //   · State_Silenced  → las de MAGIA o fantasía.
        //   · State_Rooted    → las de MOVIMIENTO.
        //   · State_Stunned   → todas.
        // Antes el desarme bloqueaba "el ataque básico" de cualquier clase desde acá, pero
        // eso no distinguía un tajo de un hechizo.

        if (ActivationRequiredTags != null)
            foreach (EGameplayTag tag in ActivationRequiredTags)
                if (!OwnerASC.HasTag(tag)) return false;

        if (CooldownEffect != null && CooldownEffect.GrantedTags.Count > 0)
            if (OwnerASC.HasTag(CooldownEffect.GrantedTags[0])) return false;

        if (CostEffect != null && !OwnerASC.CanAffordGameplayEffect(CostEffect)) return false;

        // Sin cargas disponibles no se puede activar. Red de seguridad más que gate
        // real: el bloqueo de cara al jugador lo hace el TAG del cooldown (que sí se
        // sincroniza al dueño), y ese tag solo se aplica al gastar la última carga —
        // ver CommitAbility. En un cliente el conteo arranca "lleno" porque el estado
        // es server-only, así que su predicción sigue siendo permisiva, igual que antes.
        if (UsesCharges && ChargesRemaining <= 0) return false;

        return true;
    }

    // Lógica concreta de la habilidad (detección, daño, movimiento...).
    // Cada subclase la implementa; debe empezar con "if (!IsServer) return;"
    // y llamar CommitAbility()/EndAbility() en los momentos correctos.
    public abstract void Activate();

    // ¿Esta habilidad mueve al personaje DESDE EL DUEÑO?
    //
    // El transform del jugador es client-authoritative, así que saltos, dashes y
    // teletransportes los ejecuta la conexión dueña: el servidor le manda un TargetRpc y
    // el impulso lo aplica el Update() del PlayerController de ese cliente.
    //
    // UN BOT NO TIENE DUEÑO. Si activa una de estas, FishNet avisa "Target is not an
    // observer", el bot paga el cooldown y NO SE MUEVE. Marcarlas acá deja que el
    // BotController simplemente no las proponga, en vez de tener que enumerar tipos.
    //
    // Marcala en true si tu habilidad llama a ServerStartLeap, ServerStartDash,
    // ServerTeleportOwnerTo, ApplyAbilityVelocity o ApplyDashVelocity.
    public virtual bool MovesThroughOwner => false;

    // ¿Al conectar, el que ataca se congela un instante (parón de impacto, ver CombatFeel)? Lo
    // piden los golpes de arma cuerpo a cuerpo: uno por golpe, con el primer enemigo alcanzado.
    protected virtual bool StopsOnImpact => false;

    // Descuenta el costo, aplica el cooldown, y arranca la secuencia
    // visual automática si la habilidad tiene una configurada. Cada
    // Activate() concreto la llama una vez al confirmar que sí se va a
    // ejecutar.
    protected void CommitAbility()
    {
        if (OwnerASC == null) return;

        CommittedThisActivation = true;
        ResetAfterHitsTally();   // una activación nueva cuenta sus golpeados desde cero

        // "Se va solo: al usar una habilidad" (un sigilo que se rompe al lanzar). Antes de los efectos "al activarse" (y
        // lo de este mismo frame no cuenta): el buff que pone ESTA habilidad no se va en el acto. Un eco no cuenta.
        if (!IsEcho) OwnerASC.RemoveEffectsOnEvent(GameplayEffect.ERemoveOnEvent.UseAbility);
        if (!IsEcho) OwnerASC.SendGameplayEvent(EGameplayEvent.AbilityUsed, null, 0f, false, this);   // evento de juego

        // Canalizado CONTINUO ya cobrado: cada repetición sale gratis (el costo y el cooldown
        // se cobran una vez, al empezar). Sus efectos "al activarse" sí se repiten (la Recarga de
        // maná del Mago suma maná en cada pulso), y los VFX "al lanzar" marcan cada pulso.
        if (_castPaid)
        {
            if (ApplyActivationEffectsOnCommit) ApplyActivationEffects();
            PlayCastVisuals();
            return;
        }

        // Las acumulaciones, ANTES de los efectos "al activarse": esos ya pueden escalar.
        ReadStacks();

        PayCostAndCooldown();
        if (_castRunning) _castPaid = true;

        // Los efectos "al activarse" (un buff propio al lanzar). Una habilidad que tiene que
        // aplicarlos en otro momento (la Ira inmortal, que primero revive) los pide ella.
        if (ApplyActivationEffectsOnCommit) ApplyActivationEffects();

        PlayCastVisuals();
    }

    // Una activación GRATIS: sin costo, sin cooldown y sin gastar carga (la Memoria perfecta de la
    // Evocación: la próxima Q). Solo vale para lo que cobra DENTRO de Activate (el muro, el
    // levantamiento, la tumba); un lanzamiento que cobra al soltar cobraría igual. Servidor.
    [System.NonSerialized] private bool _freeActivation;

    public void ActivateFree()
    {
        bool was = _freeActivation;
        _freeActivation = true;
        try     { Activate(); }
        finally { _freeActivation = was; }
    }

    // Para un envoltorio que cobra él mismo (GA_TagSwitch): su propio CommitAbility, gratis.
    protected void SetFreeActivation(bool on) => _freeActivation = on;

    // Gasta una carga, cobra el costo y pone el cooldown. Es lo de CommitAbility que también
    // se cobra cuando una canalización se corta (cuenta como lanzada).
    protected void PayCostAndCooldown()
    {
        if (_freeActivation) return;   // la activación gratis (ver ActivateFree)

        // Gasta una carga y arranca la recarga. Con MaxCharges <= 1 no hace nada:
        // la habilidad se comporta como siempre (cooldown en cada uso).
        bool spentLastCharge = ConsumeCharge();

        if (CostEffect != null)
            OwnerASC.ApplyGameplayEffect(CostEffect, CostSource);

        // Con cargas, el cooldown se aplica SOLO al gastar la última: mientras queden
        // cargas no debe haber tag, o el jugador no podría encadenarlas.
        if (CooldownEffect != null && spentLastCharge)
        {
            // Sin GrantedTags, CanActivate() no tiene con qué bloquear la
            // reactivación (ver el guard de GrantedTags.Count ahí): la habilidad
            // quedaría SIN cooldown real, en silencio. Avisamos una sola vez.
            if (!_warnedNoCooldownTag && (CooldownEffect.GrantedTags == null || CooldownEffect.GrantedTags.Count == 0))
            {
                _warnedNoCooldownTag = true;
                Debug.LogWarning($"[{AbilityName}] Su CooldownEffect '{CooldownEffect.name}' no tiene GrantedTags: " +
                                 $"CanActivate no puede bloquear la reactivación, así que la habilidad no va a tener " +
                                 $"cooldown real. Agregale un tag de cooldown al GE.");
            }

            OwnerASC.ApplyGameplayEffect(CooldownEffect, this, ResolveCooldownDuration());

            // Todas las definitivas comparten este tag: es lo que las distingue de las demás.
            if (CooldownEffect.GrantedTags != null && CooldownEffect.GrantedTags.Contains(EGameplayTag.Ability_Cooldown_Ultimate))
            {
                NetworkAbilitySystemComponent net = OwnerASC.GetComponent<NetworkAbilitySystemComponent>();
                if (net != null) net.ServerUltimateCast();
            }
        }
    }

    // Los VFX "al lanzar" de la lista, en todas las pantallas. Lo hace CommitAbility; las
    // habilidades que no pasan por ahí (el escudo, que cobra a mano) lo llaman ellas.
    protected void PlayCastVisuals()
    {
        if (OwnerASC == null) return;

        if (HasVisuals(EVisualWhen.OnCast))
        {
            // Instantiate() dentro de PlayVisualsSequence() corre en el
            // proceso que llama a CommitAbility() (el servidor) — un cliente
            // remoto nunca vería estos VFX. ServerPlayAbilityVisualsSequence
            // corre la secuencia acá mismo Y le pide a cada cliente que
            // corra SU PROPIA copia de la misma corutina (mismos delays,
            // offsets, etc. — el resultado es idéntico en todos los peers
            // sin necesidad de sincronizar nada más).
            NetworkAbilitySystemComponent netAsc = OwnerASC.GetComponent<NetworkAbilitySystemComponent>();
            if (netAsc != null) netAsc.ServerPlayAbilityVisualsSequence(this);
            else OwnerASC.StartAbilityCoroutine(PlayVisualsSequence()); // fallback sin red
        }
    }

    // Cierra la activación: libera el estado "atacando" del dueño y le
    // avisa al servidor que la habilidad terminó (para que replique el
    // fin del ataque al dueño remoto, si lo hay). Cada Activate() concreto
    // la llama al finalizar su secuencia (con o sin delay).
    public virtual void EndAbility()
    {
        // Las acumulaciones que se gastan "al terminar" (ver ReadStacks).
        if (_consumeStacksOnEnd)
        {
            _consumeStacksOnEnd = false;
            if (OwnerASC != null) OwnerASC.RemoveEffectsWithTag(StacksTag);
        }

        // Una repetición de un canalizado continuo: el fin de verdad lo avisa la
        // canalización al terminar (ver CastRoutine). Si no, el dueño quedaría libre a mitad.
        // También la variante que un switch corre como repetición (ActivateAsCastTick).
        if (_castRunning || _castTicking) return;

        // Salió SOLA ("Se activa sola"): el dueño no la pidió ni puso su ataque en marcha, así que no hay nada que
        // destrabarle. Avisarle "terminó" le soltaría el ataque que SÍ está haciendo (pasaría a mitad de un golpe).
        if (WasTriggered) { WasTriggered = false; return; }

        // Esto corre en el servidor (Activate() ya lo garantiza). Si el dueño
        // es un cliente remoto (no el host), pc.FinishAttack() de acá solo
        // resetea isAttacking en la copia del servidor — la copia real del
        // dueño nunca se entera y queda trabada en isAttacking = true para
        // siempre (no puede volver a atacar, ni girar al moverse).
        // Por eso también avisamos por red al dueño.
        PlayerController pc = OwnerASC?.GetComponent<PlayerController>();
        // Con CUÁL terminó: si el dueño está manteniendo el escudo, que termine otra
        // habilidad (el básico que el escudo cortó) no lo baja. Ver FinishAttack.
        //
        // Una variante de un GA_TagSwitch avisa con el nombre del SWITCH (ReportEndAs):
        // el dueño apretó el switch, no la variante. Sin esto, un mantenido dentro de un
        // switch (el clic derecho del Maestro de batalla) que el servidor corta por su
        // cuenta avisaba con un nombre que el dueño no reconocía, y quedaba trabado.
        GameplayAbility reported = ReportEndAs != null ? ReportEndAs : this;
        if (pc != null) pc.FinishAttack(reported);

        NetworkAbilitySystemComponent netASC = OwnerASC?.GetComponent<NetworkAbilitySystemComponent>();
        if (netASC != null) netASC.ServerNotifyAbilityEnded(reported);
    }

    // =========================================================
    // EL COOLDOWN Y LA CARGA DE LA DEFINITIVA
    // =========================================================

    // El cooldown que el HUD muestra para esta habilidad. Casi siempre es el propio; un
    // GA_HoldTagSwitch muestra el de la variante que se dispararía ahora.
    public virtual GameplayEffect CooldownEffectForDisplay => CooldownEffect;

    // Resuelve la duración del cooldown en segundos, con prioridad:
    // AtkSpeed dinámico > CooldownDuration (del GA) > Duration del CooldownEffect.
    // La usan tanto el cooldown normal (CommitAbility) como el tiempo de recarga
    // por carga de las habilidades con cargas (ver GA_Dash) — así un mismo valor
    // define el cooldown Y cuánto tarda en volver cada carga.
    protected float ResolveCooldownDuration()
    {
        if (UseAttackSpeedAsCooldown && OwnerASC != null)
        {
            float spd = OwnerASC.GetAttributeValue(EAttributeType.AtkSpeed);
            if (spd > 0) return spd;
        }
        if (CooldownDuration > 0) return CooldownDuration;
        return CooldownEffect != null ? CooldownEffect.Duration : 0f;
    }

    // La misma duración, para quien la necesita desde afuera: el PlayerController la
    // usa para poner la definitiva en un porcentaje exacto al cambiar de clase.
    public float CooldownDurationSeconds => ResolveCooldownDuration();

    // Adelanta el cooldown de la ultimate del dueño en UltimateChargeAmount.
    // Cada habilidad que "carga" la ultimate la llama al conectar un golpe.
    protected void ChargeUltimate()
    {
        if (UltimateChargeAmount > 0 && OwnerASC != null && !IsEcho)
            OwnerASC.ReduceCooldownByTag(EGameplayTag.Ability_Cooldown_Ultimate, UltimateChargeAmount);
    }
}
