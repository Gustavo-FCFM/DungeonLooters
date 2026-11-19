using UnityEngine;
using System.Collections.Generic;

// ============================================================
// GA_Counterattack  (genérico — "devolver el golpe"; 19 de noviembre de 2026, de las "Mejoras del GAS")
//
// Le pega al que te pegó. Se usa de dos formas:
//   · SOLA (lo normal): con la sección "Se activa sola" en un evento — "Al bloquear un golpe" (un contraataque con
//     escudo), "Al recibir daño" (espinas), "Al recibir un crítico"... El objetivo es el OTRO del evento (el que te
//     pegó), si es enemigo, está vivo y está a su alcance. Si no, no sale y no gasta nada. Puede ir sin botón (None).
//   · CON SU BOTÓN: al enemigo más centrado en la mira, dentro del alcance.
//
// QUÉ LE HACE: las entradas "Al golpear" de su lista de efectos (un daño, un aturdimiento, un empuje). Y, si salió con un
// evento de daño, puede DEVOLVER parte de lo que recibió ("Devuelve del daño recibido"): ese número, como daño de este
// personaje (pasa por sus críticos y por las defensas del atacante).
//
// Su cooldown es la "espera" entre contraataques. Mejor instantánea: si sale sola no espera a que termine lo que
// estabas haciendo (ver GameplayAbility "Se activa sola").
// ============================================================
[CreateAssetMenu(fileName = "GA_Counterattack", menuName = "GAS/Generics/Counterattack")]
public class GA_Counterattack : TargetImpactAbility
{
    [Section(AbilitySection.Targeting)]
    [Tooltip("Hasta dónde llega el contraataque. Si el que te pegó está más lejos (un arquero), no sale. 0 = sin tope " +
             "(espinas que le llegan a cualquiera).")]
    [Label("Alcance (m; 0 = sin tope)")]
    public float MaxRange = 4f;

    [Section(AbilitySection.Targeting)]
    [Tooltip("Solo con su botón: ángulo máximo (grados) entre la mira y el enemigo para elegirlo.")]
    [Label("Ángulo de selección con botón (°)")]
    public float SelectionAngle = 45f;

    [Section(AbilitySection.Effects)]
    [Tooltip("Si salió sola con un evento de daño (recibir daño, bloquear, recibir un crítico): le devuelve esta parte " +
             "de lo que recibió (o de lo que bloqueó), como daño propio: 0.5 = la mitad, 1 = todo. 0 = no devuelve nada (solo sus efectos).")]
    [Label("Devuelve del daño recibido (×; 0 = nada)")]
    [Range(0f, 3f)] public float ReflectFraction = 0f;

    [System.NonSerialized] private GameplayEffect _reflect;

    // =========================================================
    // ACTIVACIÓN
    // =========================================================

    // Sin a quién pegarle no se activa (ni gasta, ni anima).
    public override bool CanActivate() => base.CanActivate() && ResolveTarget() != null;

    public override bool CanPredictActivation() => ResolveTarget() != null;

    public override void Activate()
    {
        if (!IsServer) return;
        if (!CanActivate()) return;

        AbilitySystemComponent target = ResolveTarget();
        if (target == null) { EndAbility(); return; }

        CommitAbility();
        if (StrikeIfScenery(target)) { EndAbility(); return; }

        ApplyHitEffects(target, firstHit: true);

        float reflected = WasTriggered ? TriggeredBy.Magnitude * ReflectFraction : 0f;
        if (reflected > 0f) Reflect(target, reflected);

        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        if (pc != null)
        {
            // Mira al que le pegó (en el host y en los bots; un jugador remoto gira él mismo con su mira).
            pc.RotateToAim(target.transform.position + Vector3.up);
            pc.PlayAnimation(this);
        }

        PlayImpactVFXOnTarget(target);
        EndAbility();
    }

    // Activada sola: el OTRO del evento, si sirve. Con botón: el enemigo de la mira.
    private AbilitySystemComponent ResolveTarget()
    {
        if (OwnerASC == null) return null;

        if (WasTriggered)
        {
            AbilitySystemComponent other = TriggeredBy.Other;
            if (other == null || other == OwnerASC || other.HasTag(EGameplayTag.State_Dead)) return null;
            if (!IsEnemy(other) && other.Strikeable == null) return null;
            if (MaxRange > 0f && (other.transform.position - OwnerASC.transform.position).sqrMagnitude > MaxRange * MaxRange)
                return null;
            return other;
        }

        return FindBestTargetInAim(MaxRange > 0f ? MaxRange : 30f, SelectionAngle, ETargetAffiliation.Enemies);
    }

    // El daño devuelto: un efecto instantáneo propio con el número de ahora, firmado por el dueño y a nombre de esta
    // habilidad (el resumen de muerte dice "Contraataque").
    private void Reflect(AbilitySystemComponent target, float amount)
    {
        if (_reflect == null)
        {
            _reflect = CreateInstance<GameplayEffect>();
            _reflect.name = "Daño devuelto";
            _reflect.hideFlags = HideFlags.HideAndDontSave;
            _reflect.GrantedTags = new List<EGameplayTag>();
            _reflect.Modifiers = new List<Modifier>();
        }
        _reflect.Modifiers.Clear();
        _reflect.Modifiers.Add(new Modifier
        {
            Attribute = EAttributeType.Health, Type = Modifier.EModificationType.Add, Magnitude = -amount,
        });

        GameplayAbility previous = AbilitySystemComponent.DamageContext;
        AbilitySystemComponent.DamageContext = this;
        try { target.ApplyGameplayEffect(_reflect, OwnerASC); }
        finally { AbilitySystemComponent.DamageContext = previous; }
    }

    private void OnDestroy()
    {
        if (_reflect != null) DestroyImmediate(_reflect);
    }

    // =========================================================
    // GIZMOS
    // =========================================================

    public override void DrawGizmos(Transform origin)
        => DrawSelectionGizmo(origin, MaxRange > 0f ? MaxRange : 30f, SelectionAngle, new Color(1f, 0.5f, 0.3f, 0.9f));

#if UNITY_EDITOR
    public override void DrawSceneHandles(Transform origin)
        => AbilityHandles.Selection(this, origin, ref MaxRange, ref SelectionAngle);
#endif
}
