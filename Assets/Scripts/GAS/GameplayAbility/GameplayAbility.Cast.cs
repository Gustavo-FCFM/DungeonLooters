using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — CANALIZAR
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
//
// Cómo corre la canalización de cualquier habilidad. La sección del Inspector, con sus campos
// (CastMode, CastSeconds, los clips...), está en GameplayAbility.cs.
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // CANALIZAR — cómo corre (la sección del Inspector, con sus campos, está en GameplayAbility.cs)
    // =========================================================

    // De dónde salen los datos de la canalización (modo, segundos, clips): de la propia
    // habilidad, salvo un ENVOLTORIO que canaliza con los de la que va a ejecutar (GA_TagSwitch
    // con "Las variantes cobran y canalizan lo suyo": la Bola de fuego del Mago canaliza 0.5 s y
    // los misiles nada, en el mismo botón). Se resuelve en el dueño y en el servidor por los
    // tags, que los dos ven.
    public virtual GameplayAbility CastSettingsSource => this;

    // ¿Pasa por la canalización? Las que tienen su propio ciclo no.
    public bool UsesCast
    {
        get
        {
            GameplayAbility s = CastSettingsSource ?? this;
            return s.CastMode != ECastMode.None && s.CastSeconds > 0f &&
                   !(s is IChanneledAbility) && !(s is IRadialMenuAbility) && !HoldInput.IsHold(s) &&
                   !(this is IChanneledAbility) && !(this is IRadialMenuAbility) && !HoldInput.IsHold(this);
        }
    }

    // Cuánto dura la canalización que haría ahora (la de su fuente).
    public float CastDuration => (CastSettingsSource ?? this).CastSeconds;

    // Lo que canaliza un MANTENIDO (el lanzamiento apuntado: el Haz guía): mantener el botón
    // canaliza, y soltar antes de que se llene no lanza todavía — sale apenas se llena. Lo
    // resuelve el propio mantenido (GA_ProjectileShoot) y, del lado del dueño,
    // PlayerController.ProcessHoldRelease. 0 = no canaliza.
    // Un switch de mantener lo toma de la variante que va a salir (la Flecha helada del Mago).
    public float HoldCastSeconds
    {
        get
        {
            GameplayAbility s = CastSettingsSource ?? this;
            return s.CastMode != ECastMode.None && s.CastSeconds > 0f && HoldInput.IsHold(this) ? s.CastSeconds : 0f;
        }
    }

    // Cobra su costo y su cooldown como si se hubiera lanzado. La usa un switch para cobrar la
    // variante que iba a salir cuando le cortan la canalización (ver GA_TagSwitch.PayCutCast).
    public void PayAsCast() => PayCostAndCooldown();

    // Mientras canaliza un CONTINUO, sus repeticiones no animan (la pose en bucle sigue). También
    // la variante que un switch corre como repetición (ActivateAsCastTick). Lo mira
    // PlayerController.PlayAnimation.
    public bool SuppressesOwnAnimation
        => _castTicking || (_castRunning && (_castSource ?? this).CastMode == ECastMode.Continuous);

    // Para un ENVOLTORIO que canaliza con los datos de su variante (GA_TagSwitch): si esta
    // activación es una repetición de su continuo, si ya cobró, y con qué variante empezó.
    protected bool IsCastTick   => _castTicking;
    protected bool CastTickPaid => _castPaid;
    protected GameplayAbility CastSource => _castSource;

    // El envoltorio cobró a través de su variante (en la primera repetición): al terminar o
    // cortarse la canalización no se cobra otra vez.
    protected void MarkCastPaid()
    {
        if (_castRunning) _castPaid = true;
    }

    // Corre Activate como una repetición del continuo de OTRA habilidad: el switch que canaliza
    // con los datos de esta variante (la Recarga de maná en la Q del Mago). Sin mirar el cooldown
    // ni la canalización, sin animar, y gratis si 'paid' (la primera cobra). Servidor.
    public void ActivateAsCastTick(bool paid)
    {
        bool wasTicking = _castTicking, wasPaid = _castPaid;
        _castTicking = true;
        _castPaid    = paid;
        try     { Activate(); }
        finally { _castTicking = wasTicking; _castPaid = wasPaid; }
    }

    // Servidor: canalizando ahora / ya cobrado en esta canalización (ver CommitAbility).
    [System.NonSerialized] private bool _castRunning;
    [System.NonSerialized] private GameplayAbility _castSource;   // de dónde salieron los datos de esta canalización
    [System.NonSerialized] private bool _castPaid;
    [System.NonSerialized] private bool _castTicking;   // dentro de una repetición del continuo

    // La llama ServerActivateAbility en vez de Activate() (ya pasó CanActivate).
    public void BeginCast()
    {
        if (OwnerASC == null) return;

        _castPaid             = false;
        StartedThisActivation = true;
        OwnerASC.StartAbilityCoroutine(CastRoutine());
    }

    private IEnumerator CastRoutine()
    {
        AbilitySystemComponent        asc = OwnerASC;
        PlayerController              pc  = asc.GetComponent<PlayerController>();
        NetworkAbilitySystemComponent net = asc.GetComponent<NetworkAbilitySystemComponent>();

        // Los datos, de su fuente (la variante de un switch), fijos para toda esta canalización:
        // si su tag cambia a mitad, sigue con lo que empezó.
        GameplayAbility src = CastSettingsSource ?? this;
        _castSource = src;
        bool  continuous = src.CastMode == ECastMode.Continuous;
        float seconds    = src.CastSeconds;
        bool  blocks     = src.CastBlocksOthers;
        bool  breaks     = src.CastBreaksOnDamage;
        bool  loops      = src.CastLoopAnimation != null;

        // Lo que se apunta en el piso (el Muro, el Levantamiento, la Tormenta del Mago) cae donde
        // el dueño SOLTÓ, aunque mire a otro lado mientras canaliza: la mira no se actualiza en vivo.
        bool lockAim = this is IGroundTargetAbility ground && ground.UsesGroundTarget;
        // Y se guarda la mira del pedido: un botón apretado mientras canaliza (que el servidor rechaza)
        // igual escribe su mira encima; al salir se vuelve a la de cuando soltó.
        Vector3 lockedAim  = pc != null ? pc.NetworkAimPoint : Vector3.zero;
        Vector3 lockedFrom = pc != null ? pc.NetworkRequestPosition : Vector3.zero;
        float   lockedYaw  = pc != null ? pc.NetworkPlacementYaw : 0f;

        _castRunning = true;
        if (blocks) asc.AddTag(EGameplayTag.Status_Channeling);
        if (loops && net != null) net.ServerPlayChannelAnimation(src, true);
        ShowCastBar(seconds, channel: continuous, shown: src);
        if (pc != null && !lockAim) pc.ServerStreamAimFor(seconds + 0.5f);
        if (pc != null && lockAim)  pc.LockAim(lockedAim, seconds + 0.5f);   // también el host lee el punto donde soltó

        bool damaged = false;
        System.Action<AbilitySystemComponent> onDamaged = _ => damaged = true;
        if (breaks) asc.OnTookDamage += onDamaged;
        int cancelSerial = asc.CancelSerial;

        float elapsed  = 0f;
        float nextTick = 0f;
        bool  interrupted = false;

        while (elapsed < seconds)
        {
            if (CastInterrupted(asc, cancelSerial, damaged)) { interrupted = true; break; }

            if (continuous && elapsed >= nextTick)
            {
                _castTicking = true;
                try     { Activate(); }   // la primera cobra (CommitAbility); las demás salen gratis
                finally { _castTicking = false; }
                nextTick += Mathf.Max(0.05f, src.CastTickSeconds);
            }

            yield return null;
            elapsed += Time.deltaTime;
        }

        if (breaks) asc.OnTookDamage -= onDamaged;
        if (blocks) asc.RemoveTag(EGameplayTag.Status_Channeling);
        _castRunning = false;
        _castSource  = null;
        if (loops && net != null) net.ServerPlayChannelAnimation(src, false);
        HideCastBar(interrupted);

        // Cortada (o un continuo que terminó): no sale nada más. Cortada cuenta como lanzada.
        if (interrupted || continuous)
        {
            if (lockAim && pc != null) pc.UnlockAim();
            if (!_castPaid)
            {
                PayCostAndCooldown();
                if (interrupted) PayCutCast();
                _castPaid = true;
            }
            EndAbility();
            yield break;
        }

        // Se lanza al terminar: la habilidad de siempre, ahora.
        if (lockAim && pc != null)
        {
            pc.NetworkAimPoint        = lockedAim;
            pc.NetworkRequestPosition = lockedFrom;
            pc.NetworkPlacementYaw    = lockedYaw;
        }
        CommittedThisActivation = false;
        StartedThisActivation   = false;
        Activate();
        if (lockAim && pc != null) pc.UnlockAim();

        // Se plantó sola (la de objetivo único sin nadie a tiro): igual se canalizó, cuenta
        // como lanzada.
        if (!CommittedThisActivation && !StartedThisActivation)
        {
            PayCostAndCooldown();
            EndAbility();
            yield break;
        }

        // La animación de soltar, a todas las pantallas: el dueño no la anticipó al apretar
        // (ver PlayerController.ProcessAbilityPress). El personaje del host ya la hizo en
        // Activate(); la RPC lo saltea.
        if (net != null && !BroadcastsOwnAnimation)
            net.ServerPlayAbilityAnimationAfterCast(ResolveAnimationSource() ?? this);
    }

    // ¿Algo corta la canalización? Lo que la bloquea (el aturdido, el silencio si es magia),
    // morir, otra habilidad si es interrumpible y, si se marcó, recibir daño.
    protected bool CastInterrupted(AbilitySystemComponent asc, int cancelSerial, bool damaged)
    {
        if (asc == null || asc.HasTag(EGameplayTag.State_Dead)) return true;

        if (ActivationBlockedTags != null)
            foreach (EGameplayTag tag in ActivationBlockedTags)
                if (asc.HasTag(tag)) return true;

        if (IsInterruptible && asc.CancelSerial != cancelSerial) return true;
        return (_castSource ?? CastSettingsSource ?? this).CastBreaksOnDamage && damaged;
    }

    // Una canalización CORTADA ya cobró su costo y su cooldown (PayCostAndCooldown). Un
    // envoltorio cobra además lo de la variante que iba a salir (GA_TagSwitch: el maná del
    // hechizo del Mago): cortada cuenta como lanzada.
    protected virtual void PayCutCast() { }

    // Barra de canalizar del DUEÑO (UI_CastBar), estilo WoW. channel = se vacía mientras
    // dura (un canalizado, el molinete); si no, se llena hasta que la habilidad sale (una
    // carga, el Golpe final). Llamarlas en el servidor; HideCastBar(true) la pone roja.
    // 'shown': la habilidad cuyo nombre e ícono muestra la barra (la variante que canaliza un
    // switch). Null = esta.
    protected void ShowCastBar(float duration, bool channel, GameplayAbility shown = null)
    {
        NetworkAbilitySystemComponent netAsc = OwnerASC != null ? OwnerASC.GetComponent<NetworkAbilitySystemComponent>() : null;
        if (netAsc != null && netAsc.IsServerInitialized) netAsc.ServerShowCastBar(shown ?? this, duration, channel);
    }

    protected void HideCastBar(bool interrupted)
    {
        NetworkAbilitySystemComponent netAsc = OwnerASC != null ? OwnerASC.GetComponent<NetworkAbilitySystemComponent>() : null;
        if (netAsc != null && netAsc.IsServerInitialized) netAsc.ServerHideCastBar(interrupted);
    }
}
