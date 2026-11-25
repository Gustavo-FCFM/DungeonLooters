using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet;
using FishNet.Object;

// ============================================================
// GA_ProjectileShoot — APUNTAR ANTES DE LANZAR
//
// Una parte de GA_ProjectileShoot (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameplayAbilities/GenericsAbilities/GA_ProjectileShoot.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class GA_ProjectileShoot
{

    // =========================================================
    // APUNTAR (AimBeforeThrow)
    // =========================================================

    // Apretar: el arma atrás en bucle. Nada se cobra hasta soltar.
    private void StartAiming()
    {
        if (_aiming || OwnerASC == null) return;

        _aiming        = true;
        _aimStartedAt  = Time.time;
        _releaseQueued = false;
        _castReadyAt   = Time.time + HoldCastSeconds;

        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        if (pc != null) pc.PlayHoldAnimation(this);

        NetworkAbilitySystemComponent netAsc = OwnerASC.GetComponent<NetworkAbilitySystemComponent>();
        if (netAsc != null)
            netAsc.ServerBroadcastHoldAnimation(this, NetworkAbilitySystemComponent.EHoldAnimationPhase.Start);

        // Canaliza mientras apunta: la barra del dueño se llena hasta que se puede soltar.
        if (HoldCastSeconds > 0f) ShowCastBar(HoldCastSeconds, channel: false);

        OwnerASC.StartAbilityCoroutine(AimWatch());
    }

    // Vigila el apuntado en el servidor: un aturdido o la muerte lo cortan SIN lanzar (y
    // sin cobrar), y el corte de seguridad lanza si el aviso de soltar nunca llega.
    //
    // CANALIZANDO (sección Canalizar): lo corta lo mismo que corta una canalización (lo que
    // la bloquea —el aturdido, el silencio si es magia—, morir y, si se marcó, el daño), y
    // cortado CUENTA COMO LANZADO (cobra costo y cooldown). Al llenarse la barra, si ya había
    // soltado, sale ahí mismo.
    private IEnumerator AimWatch()
    {
        bool hasCast    = HoldCastSeconds > 0f;
        bool channeling = hasCast;
        bool damaged    = false;
        System.Action<AbilitySystemComponent> onDamaged = _ => damaged = true;
        if (hasCast && CastBreaksOnDamage && OwnerASC != null) OwnerASC.OnTookDamage += onDamaged;
        int cancelSerial = OwnerASC != null ? OwnerASC.CancelSerial : 0;

        try
        {
            while (_aiming)
            {
                bool cut = hasCast
                    ? CastInterrupted(OwnerASC, cancelSerial, damaged)
                    : ShouldStop(EStopWhen.Dies | EStopWhen.Stunned);
                if (cut)
                {
                    if (channeling) HideCastBar(true);
                    CancelAim(countsAsCast: hasCast);
                    yield break;
                }

                if (channeling && Time.time >= _castReadyAt)
                {
                    channeling = false;
                    HideCastBar(false);
                    if (_releaseQueued) { EndHold(); yield break; }
                }

                if (AimSafetyTimeout > 0f && Time.time - _aimStartedAt >= AimSafetyTimeout)
                {
                    EndHold();
                    yield break;
                }
                yield return null;
            }
        }
        finally
        {
            if (hasCast && CastBreaksOnDamage && OwnerASC != null) OwnerASC.OnTookDamage -= onDamaged;
        }
    }

    // Soltar (IHoldAbility.EndHold, lo pide el dueño al soltar el botón): lanza YA.
    // Idempotente.
    public void EndHold()
    {
        if (!_aiming || OwnerASC == null) return;

        if (OwnerASC.HasTag(EGameplayTag.State_Dead) || OwnerASC.HasTag(EGameplayTag.State_Stunned))
        {
            CancelAim(countsAsCast: HoldCastSeconds > 0f);
            return;
        }

        // Todavía canalizando: no sale hasta que se llene la barra (lo suelta AimWatch).
        if (Time.time < _castReadyAt)
        {
            _releaseQueued = true;
            return;
        }

        _aiming = false;
        StopAimAnimation();

        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        if (pc != null) pc.RotateToAim();

        // El lanzamiento desde la pose: al dueño ya se lo mostró su propia predicción
        // (PredictOwnerReleaseVisuals); a los demás les llega por acá.
        NetworkAbilitySystemComponent netAsc = OwnerASC.GetComponent<NetworkAbilitySystemComponent>();
        if (netAsc != null) netAsc.ServerBroadcastStepAnimationToOthers(this, 0, 1, this);
        else if (pc != null) pc.PlayActionClip(ReleaseClip, 1f, AnimationTriggerName, AnimationID);

        OwnerASC.StartAbilityCoroutine(ShootSequence(fromAim: true));
    }

    // Cortado sin lanzar: no sale nada ni se cobra nada… salvo que estuviera canalizando
    // (sección Canalizar): ahí cuenta como lanzado.
    private void CancelAim(bool countsAsCast = false)
    {
        if (!_aiming) return;
        _aiming = false;
        StopAimAnimation();
        if (countsAsCast) PayCostAndCooldown();
        EndAbility();
    }

    private void StopAimAnimation()
    {
        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        if (pc != null) pc.StopHoldAnimation();

        NetworkAbilitySystemComponent netAsc = OwnerASC.GetComponent<NetworkAbilitySystemComponent>();
        if (netAsc != null)
            netAsc.ServerBroadcastHoldAnimation(this, NetworkAbilitySystemComponent.EHoldAnimationPhase.Stop);
    }

    // El dueño soltó el botón: muestra el lanzamiento en el acto, sin esperar al
    // servidor, y esconde el arma en el mismo momento en que el servidor la va a soltar.
    public override void PredictOwnerReleaseVisuals(PlayerController pc)
    {
        if (pc == null || !AimBeforeThrow) return;

        pc.PlayActionClip(ReleaseClip, 1f, AnimationTriggerName, AnimationID);

        if (!HideWeaponWhileFlying) return;
        ResolveThrowTiming(true, out float releaseDelay, out float backswing);
        pc.PredictWeaponHide(releaseDelay, WaitsForReturn ? float.PositiveInfinity : releaseDelay + backswing);
    }

    // ¿Este Animator tiene ese parámetro, del tipo esperado? Pedirle algo que no tiene
    // no rompe nada, pero llena la consola de warnings.
    private static bool HasAnimatorParameter(Animator anim, string parameterName,
                                             AnimatorControllerParameterType type)
    {
        if (anim == null || string.IsNullOrEmpty(parameterName)) return false;

        foreach (AnimatorControllerParameter p in anim.parameters)
            if (p.type == type && p.name == parameterName) return true;

        return false;
    }

    // Le pide a la capa de red que muestre/oculte el arma del lanzador en TODOS los
    // peers. Sin NetworkASC (un NPC suelto, o pruebas sin red) se aplica local.
    // includeOwner: también al dueño remoto (que normalmente lo anticipa él solo; el bumerán no puede: no sabe cuándo vuelve).
    private void SetOwnerWeaponVisible(bool visible, bool includeOwner = false)
    {
        if (OwnerASC == null) return;

        var netAsc = OwnerASC.GetComponent<NetworkAbilitySystemComponent>();
        if (netAsc != null) { netAsc.ServerSetWeaponVisible(visible, includeOwner); return; }

        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        if (pc != null) pc.SetMainWeaponVisible(visible);
    }
}
