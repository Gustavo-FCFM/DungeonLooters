using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — LAS CARGAS
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
//
// Cómo corren las cargas de cualquier habilidad. El campo "Cargas" (MaxCharges) está en
// GameplayAbility.cs.
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // CARGAS — cómo corren (el campo "Cargas", MaxCharges, está en GameplayAbility.cs)
    // =========================================================

    // IChargedAbility: la UI lo lee para mostrar el contador junto al ícono (solo si es > 1).
    public int MaxChargeCount => MaxCharges;

    // Cargas disponibles en el SERVIDOR. -1 = sin inicializar (se toma como lleno).
    // NonSerialized: estado de runtime por instancia otorgada, no se guarda en el asset.
    [System.NonSerialized] private int  _charges = -1;
    [System.NonSerialized] private bool _recharging;

    // True si esta habilidad usa el sistema de cargas.
    protected bool UsesCharges => MaxCharges > 1;

    // Cargas que quedan ahora mismo (inicializa perezosamente al máximo).
    protected int ChargesRemaining
    {
        get
        {
            if (_charges < 0) _charges = Mathf.Max(1, MaxCharges);
            return _charges;
        }
    }

    // Gasta una carga y arranca la recarga si hace falta. Devuelve true si con esto se
    // agotó la última (o si la habilidad no usa cargas), que es cuando corresponde
    // aplicar el cooldown. La llama CommitAbility.
    private bool ConsumeCharge()
    {
        if (!UsesCharges) return true;   // sin cargas: cooldown en cada uso, como siempre

        int remaining = ChargesRemaining;
        if (remaining > 0) _charges = remaining - 1;

        ReportCharges();
        StartRecharge();

        return _charges <= 0;
    }

    // Devuelve una carga (sin pasarse del máximo) y deja la habilidad usable ya mismo.
    // La usa el dash para su reembolso cuando un enemigo atravesado muere.
    protected void RefundCharge()
    {
        if (!UsesCharges) return;

        if (ChargesRemaining < MaxCharges) _charges = ChargesRemaining + 1;

        ClearCooldownTag();
        ReportCharges();
        StartRecharge();
    }

    // Baja a 0 el cooldown vigente de esta habilidad, para que vuelva a estar
    // disponible al instante en vez de esperar a que el GE expire.
    protected void ClearCooldownTag()
    {
        if (OwnerASC == null || CooldownEffect == null) return;
        if (CooldownEffect.GrantedTags == null || CooldownEffect.GrantedTags.Count == 0) return;

        OwnerASC.ReduceCooldownByTag(CooldownEffect.GrantedTags[0], 99999f);
    }

    // Publica las cargas para que la UI del dueño las muestre. Solo tiene sentido en
    // habilidades con cargas: si no, ensuciaría NetCharges con una entrada por cada
    // habilidad del juego.
    private void ReportCharges()
    {
        if (!UsesCharges || OwnerASC == null) return;

        NetworkAbilitySystemComponent netAsc = OwnerASC.GetComponent<NetworkAbilitySystemComponent>();
        if (netAsc != null) netAsc.ServerReportCharges(this, _charges);
    }

    private void StartRecharge()
    {
        if (!UsesCharges || _recharging || OwnerASC == null) return;
        OwnerASC.StartAbilityCoroutine(RechargeRoutine());
    }

    // Devuelve 1 carga cada "cooldown" hasta llenar MaxCharges.
    private System.Collections.IEnumerator RechargeRoutine()
    {
        _recharging = true;

        while (ChargesRemaining < MaxCharges)
        {
            float cd = ResolveCooldownDuration();
            if (cd <= 0f) cd = 1f; // salvaguarda si la habilidad no tiene cooldown configurado

            yield return new WaitForSeconds(cd);

            if (ChargesRemaining < MaxCharges)
            {
                _charges = ChargesRemaining + 1;
                ReportCharges();
                ClearCooldownTag();
            }
        }

        _recharging = false;
    }

    // Deja la habilidad como recién otorgada: cargas llenas y cooldown en cero. La
    // llama el ASC al revivir, para todas menos la definitiva (ver IsUltimate). La
    // recarga en curso, si la hay, se corta sola: RechargeRoutine ve las cargas llenas
    // en su próxima vuelta y termina.
    public void ResetForRespawn()
    {
        if (UsesCharges)
        {
            _charges = MaxCharges;
            ReportCharges();
        }
        ClearCooldownTag();
    }

    // Apaga el sistema de cargas en ESTA instancia. La usan los combos y el TagSwitch
    // al clonar un paso/variante: ese clon es solo la ejecución, su ciclo de vida
    // (costo, cooldown y por lo tanto también las cargas) es del padre. Sin esto, un
    // paso cuyo asset tuviera cargas las gastaría por su cuenta y terminaría
    // bloqueándose en silencio, igual que pasaba con el cooldown.
    public void DisableCharges()
    {
        MaxCharges = 1;
        _charges   = -1;
    }
}
