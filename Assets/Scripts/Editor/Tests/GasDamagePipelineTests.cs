using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// ============================================================
// GasDamagePipelineTests — pruebas automáticas del CAMINO DEL DAÑO del GAS (17 de noviembre de 2026, de las
// "Mejoras del GAS")
//
// Cada prueba arma personajes y efectos EN MEMORIA (sin assets ni escena), aplica un golpe o una curación y compara
// el número con lo que dicen las reglas de AbilitySystemComponent.ExecuteInstantEffect / ApplyDefenses:
//   · Daño plano y con escalado del atacante; armadura como porcentaje (Def / (Def + 10)), redondeo hacia abajo y
//     mínimo de 1; vulnerabilidad y resistencia (tope 90 %); escudo (el mágico gasta el doble); inmunidad; inmortal
//     (queda en 1); daño que cura; crítico forzado (×2 sin CritDamage); robo de vida; curación con tope y bonus.
//   · Lo que se cuelga del camino: OnDamageDone (la tabla), el registro del depurador.
//   · Limpiar, purgar e inmunidades por tag.
//
// Cómo correrlas: Window ▸ General ▸ Test Runner ▸ EditMode, o desde el CLI:
//     unity command test run --mode editor --filter GasDamagePipeline
// Si una regla del daño cambia A PROPÓSITO, se cambia su prueba en el mismo commit (y el porqué en el comentario).
// ============================================================
[Category("GAS")]
public class GasDamagePipelineTests
{
    private readonly List<Object> _made = new List<Object>();

    [TearDown]
    public void TearDown()
    {
        foreach (Object o in _made) if (o != null) Object.DestroyImmediate(o);
        _made.Clear();
        AbilitySystemComponent.TraceDamage = false;
        AbilitySystemComponent.DamageContext = null;
    }

    // =========================================================
    // AYUDAS
    // =========================================================

    private AbilitySystemComponent Character(float health, params (EAttributeType type, float value)[] extra)
    {
        var def = ScriptableObject.CreateInstance<AttributeSetDefinition>();
        _made.Add(def);
        def.InitialAttributes = new List<AttributeSetDefinition.BaseAttribute>
        {
            new AttributeSetDefinition.BaseAttribute { Attribute = EAttributeType.Health, BaseValue = health },
        };
        foreach (var (type, value) in extra)
            def.InitialAttributes.Add(new AttributeSetDefinition.BaseAttribute { Attribute = type, BaseValue = value });

        var go = new GameObject("Prueba") { hideFlags = HideFlags.HideAndDontSave };
        _made.Add(go);
        var asc = go.AddComponent<AbilitySystemComponent>();
        asc.CharacterRoleDefinition = def;
        asc.InitializeAttributes(false);
        return asc;
    }

    private GameplayEffect Effect(string name, float duration = 0f, GameplayEffect.EEffectType type = GameplayEffect.EEffectType.Hidden,
                                  params EGameplayTag[] grants)
    {
        var ge = ScriptableObject.CreateInstance<GameplayEffect>();
        _made.Add(ge);
        ge.name = name;
        ge.Duration = duration;
        ge.EffectType = type;
        ge.GrantedTags = new List<EGameplayTag>(grants);
        ge.Modifiers = new List<Modifier>();
        return ge;
    }

    // Un golpe a la vida: 'flat' de daño fijo y/o 'coefficient' × el atributo del atacante.
    private GameplayEffect Hit(float flat, EAttributeType scaleWith = EAttributeType.None, float coefficient = 0f)
    {
        GameplayEffect ge = Effect("Golpe");
        ge.Modifiers.Add(new Modifier
        {
            Attribute = EAttributeType.Health, Type = Modifier.EModificationType.Add, Magnitude = -flat,
            UseAttributeScaling = scaleWith != EAttributeType.None, SourceAttribute = scaleWith, AttributeCoefficient = -coefficient,
        });
        return ge;
    }

    private GameplayEffect Heal(float amount)
    {
        GameplayEffect ge = Effect("Curación");
        ge.Modifiers.Add(new Modifier { Attribute = EAttributeType.Health, Type = Modifier.EModificationType.Add, Magnitude = amount });
        return ge;
    }

    // Le da un tag al personaje con un efecto que dura (el camino normal, como en el juego).
    private void GiveTag(AbilitySystemComponent asc, EGameplayTag tag) =>
        asc.ApplyGameplayEffect(Effect("Tag " + tag, 30f, GameplayEffect.EEffectType.Hidden, tag), asc);

    private static float Health(AbilitySystemComponent asc) => asc.GetAttributeValue(EAttributeType.Health);

    // =========================================================
    // DAÑO BÁSICO
    // =========================================================

    [Test]
    public void DañoPlano_BajaLaVida()
    {
        var victim = Character(100f);
        victim.ApplyGameplayEffect(Hit(10f), Character(100f));
        Assert.AreEqual(90f, Health(victim));
    }

    [Test]
    public void Daño_EscalaConElAtaqueDelAtacante()
    {
        var attacker = Character(100f, (EAttributeType.Attack, 12f));
        var victim = Character(100f);
        victim.ApplyGameplayEffect(Hit(0f, EAttributeType.Attack, 1f), attacker);
        Assert.AreEqual(88f, Health(victim));
    }

    // =========================================================
    // DEFENSAS
    // =========================================================

    [Test]
    public void Armadura_EsUnPorcentaje()
    {
        // Def 10 → 10 / (10 + 10) = 50 % menos: 100 → 50. (Números elegidos para que otra constante se note: con
        // 12 serían 54. Con un golpe de 20, las dos dan 10 y la prueba no vigilaría nada.)
        var victim = Character(200f, (EAttributeType.Def, 10f));
        victim.ApplyGameplayEffect(Hit(100f), Character(100f));
        Assert.AreEqual(150f, Health(victim));
    }

    [Test]
    public void Armadura_NuncaDejaUnGolpeEnCero()
    {
        // Muchísima armadura: el golpe que conecta hace al menos 1.
        var victim = Character(100f, (EAttributeType.Def, 1000f));
        victim.ApplyGameplayEffect(Hit(5f), Character(100f));
        Assert.AreEqual(99f, Health(victim));
    }

    [Test]
    public void VulnerabilidadYResistencia_SeMultiplican()
    {
        // (1 + 0.5) × (1 − 0.2) = 1.2: 10 → 12.
        var victim = Character(100f, (EAttributeType.Vulnerability, 0.5f), (EAttributeType.Resistance, 0.2f));
        victim.ApplyGameplayEffect(Hit(10f), Character(100f));
        Assert.AreEqual(88f, Health(victim));
    }

    [Test]
    public void Resistencia_TieneTopeDe90PorCiento()
    {
        // Resistencia 2 (200 %) se corta en 0.9: 100 → 10, nunca cura ni queda en cero.
        var victim = Character(200f, (EAttributeType.Resistance, 2f));
        victim.ApplyGameplayEffect(Hit(100f), Character(100f));
        Assert.AreEqual(190f, Health(victim));
    }

    [Test]
    public void Daño_SeRedondeaHaciaAbajo()
    {
        // 8 × 1.1 = 8.8 → 8 (a favor de quien recibe).
        var victim = Character(100f, (EAttributeType.Vulnerability, 0.1f));
        victim.ApplyGameplayEffect(Hit(8f), Character(100f));
        Assert.AreEqual(92f, Health(victim));
    }

    // =========================================================
    // ESCUDO
    // =========================================================

    [Test]
    public void Escudo_FrenaElFisicoUnoAUno()
    {
        var victim = Character(100f);
        victim.SetCurrentAttributeValue(EAttributeType.Shield, 5f);
        victim.ApplyGameplayEffect(Hit(12f), Character(100f));
        Assert.AreEqual(0f, victim.GetAttributeValue(EAttributeType.Shield));
        Assert.AreEqual(93f, Health(victim));
    }

    [Test]
    public void Escudo_ElMagicoGastaElDoble()
    {
        // 10 mágico contra 10 de escudo: el escudo frena solo 5 (le cuesta el doble) y entran 5.
        var attacker = Character(100f, (EAttributeType.MagicDamage, 10f));
        var victim = Character(100f);
        victim.SetCurrentAttributeValue(EAttributeType.Shield, 10f);
        victim.ApplyGameplayEffect(Hit(0f, EAttributeType.MagicDamage, 1f), attacker);
        Assert.AreEqual(0f, victim.GetAttributeValue(EAttributeType.Shield));
        Assert.AreEqual(95f, Health(victim));
    }

    // =========================================================
    // ESTADOS QUE CAMBIAN EL GOLPE
    // =========================================================

    [Test]
    public void Inmunidad_NoRecibeDaño()
    {
        var victim = Character(100f);
        GiveTag(victim, EGameplayTag.Status_Immunity);
        victim.ApplyGameplayEffect(Hit(50f), Character(100f));
        Assert.AreEqual(100f, Health(victim));
    }

    [Test]
    public void Inmortal_QuedaEnUno()
    {
        var victim = Character(100f);
        GiveTag(victim, EGameplayTag.Status_Immortal);
        victim.ApplyGameplayEffect(Hit(500f), Character(100f));
        Assert.AreEqual(1f, Health(victim));
    }

    [Test]
    public void DañoQueCura_CuraLoQueIbaAQuitar()
    {
        var victim = Character(100f);
        victim.SetCurrentAttributeValue(EAttributeType.Health, 50f);
        GiveTag(victim, EGameplayTag.Status_DamageToHeal);
        victim.ApplyGameplayEffect(Hit(20f), Character(100f));
        Assert.AreEqual(70f, Health(victim));
    }

    [Test]
    public void CriticoForzado_DuplicaSinCritDamage()
    {
        var victim = Character(100f);
        GiveTag(victim, EGameplayTag.State_AlwaysVisible);
        GameplayEffect hit = Hit(10f);
        hit.CritIfTargetHas = EGameplayTag.State_AlwaysVisible;
        victim.ApplyGameplayEffect(hit, Character(100f));
        Assert.AreEqual(80f, Health(victim));
    }

    // =========================================================
    // CURACIÓN Y ROBO DE VIDA
    // =========================================================

    [Test]
    public void RoboDeVida_CuraAlAtacante()
    {
        // 50 % de robo sobre un golpe de 20: el atacante recupera 10.
        var attacker = Character(100f, (EAttributeType.LifeSteal, 0.5f));
        attacker.SetCurrentAttributeValue(EAttributeType.Health, 50f);
        var victim = Character(100f);
        victim.ApplyGameplayEffect(Hit(20f), attacker);
        Assert.AreEqual(60f, Health(attacker));
        Assert.AreEqual(80f, Health(victim));
    }

    [Test]
    public void Curacion_NoPasaDeLaMaxima()
    {
        var target = Character(100f);
        target.SetCurrentAttributeValue(EAttributeType.Health, 90f);
        target.ApplyGameplayEffect(Heal(20f), target);
        Assert.AreEqual(100f, Health(target));
    }

    [Test]
    public void Curacion_SumaLaCuracionRecibida()
    {
        // +50 % de curación recibida: 20 → 30.
        var target = Character(100f, (EAttributeType.HealingReceived, 0.5f));
        target.SetCurrentAttributeValue(EAttributeType.Health, 50f);
        target.ApplyGameplayEffect(Heal(20f), target);
        Assert.AreEqual(80f, Health(target));
    }

    // =========================================================
    // LO QUE SE CUELGA DEL CAMINO
    // =========================================================

    [Test]
    public void OnDamageDone_AvisaLoQueEntroConEscudoIncluido()
    {
        var attacker = Character(100f);
        var victim = Character(100f);
        victim.SetCurrentAttributeValue(EAttributeType.Shield, 5f);
        float reported = 0f;
        attacker.OnDamageDone += (_, amount) => reported += amount;
        victim.ApplyGameplayEffect(Hit(12f), attacker);
        Assert.AreEqual(12f, reported);   // la tabla cuenta también lo que frenó el escudo
    }

    [Test]
    public void Depurador_AnotaLaCadenaDelGolpe()
    {
        AbilitySystemComponent.TraceDamage = true;
        var victim = Character(100f, (EAttributeType.Def, 10f));
        victim.SetCurrentAttributeValue(EAttributeType.Shield, 3f);
        victim.ApplyGameplayEffect(Hit(40f), Character(100f));
        Assert.AreEqual(1, victim.DebugDamageTraces.Count);
        AbilitySystemComponent.DamageTrace t = victim.DebugDamageTraces[0];
        Assert.AreEqual(40f, t.Base);
        Assert.AreEqual(20f, t.AfterDefenses);
        Assert.AreEqual(3f, t.Shield);
        Assert.AreEqual(17f, t.ToHealth);
        Assert.AreEqual(83f, Health(victim));
    }

    // =========================================================
    // LIMPIAR, PURGAR E INMUNIDADES
    // =========================================================

    [Test]
    public void Limpiar_QuitaLosDebuffsYDejaLosBuffs()
    {
        var target = Character(100f);
        target.ApplyGameplayEffect(Effect("Silencio", 10f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Silenced), null);
        target.ApplyGameplayEffect(Effect("Buff", 10f, GameplayEffect.EEffectType.Buff, EGameplayTag.Status_Invisible), null);
        GameplayEffect cleanse = Effect("Limpiar");
        cleanse.RemoveOnApply = GameplayEffect.ERemoveKind.Debuffs;
        target.ApplyGameplayEffect(cleanse, null);
        Assert.IsFalse(target.HasTag(EGameplayTag.State_Silenced));
        Assert.IsTrue(target.HasTag(EGameplayTag.Status_Invisible));
    }

    [Test]
    public void Purgar_RespetaElTopeYLasPasivasDeClase()
    {
        var target = Character(100f);
        target.ApplyClassPassive(Effect("Pasiva", 9999f, GameplayEffect.EEffectType.Buff, EGameplayTag.Status_HealShield));
        target.ApplyGameplayEffect(Effect("Buff 1", 10f, GameplayEffect.EEffectType.Buff, EGameplayTag.Status_Invisible), null);
        target.ApplyGameplayEffect(Effect("Buff 2", 10f, GameplayEffect.EEffectType.Buff, EGameplayTag.Status_GuaranteedCrit), null);
        GameplayEffect purge = Effect("Purgar");
        purge.RemoveOnApply = GameplayEffect.ERemoveKind.Buffs;
        purge.MaxRemoved = 1;
        target.ApplyGameplayEffect(purge, null);
        Assert.IsTrue(target.HasTag(EGameplayTag.Status_HealShield), "la pasiva de clase no se quita");
        Assert.IsTrue(target.HasTag(EGameplayTag.Status_Invisible), "el más viejo se queda (tope 1)");
        Assert.IsFalse(target.HasTag(EGameplayTag.Status_GuaranteedCrit), "el más nuevo se va");
    }

    [Test]
    public void Inmunidad_PorTag_BloqueaYSeVa()
    {
        var target = Character(100f);
        GameplayEffect silence = Effect("Silencio", 10f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Silenced);
        GameplayEffect immune = Effect("Inmune", 10f, GameplayEffect.EEffectType.Buff);
        immune.GrantsImmunityTo = new List<EGameplayTag> { EGameplayTag.State_Silenced };

        target.ApplyGameplayEffect(silence, null);
        target.ApplyGameplayEffect(immune, null);
        Assert.IsFalse(target.HasTag(EGameplayTag.State_Silenced), "al ponerse, quita el que tenía");

        target.ApplyGameplayEffect(silence, null);
        Assert.IsFalse(target.HasTag(EGameplayTag.State_Silenced), "mientras dura, no entra");

        target.RemoveEffectsByDefinition(immune);
        target.ApplyGameplayEffect(silence, null);
        Assert.IsTrue(target.HasTag(EGameplayTag.State_Silenced), "sin la inmunidad, entra");
    }

    [Test]
    public void Condiciones_NoSiTieneYSoloSiTiene()
    {
        var target = Character(100f);
        GameplayEffect onlyIfStunned = Effect("Solo si aturdido", 5f, GameplayEffect.EEffectType.Debuff, EGameplayTag.Status_Invisible);
        onlyIfStunned.RequiresTargetTags = new List<EGameplayTag> { EGameplayTag.State_Stunned };
        target.ApplyGameplayEffect(onlyIfStunned, null);
        Assert.IsFalse(target.HasTag(EGameplayTag.Status_Invisible));

        GiveTag(target, EGameplayTag.State_Silenced);
        GameplayEffect notIfSilenced = Effect("No si silenciado", 5f, GameplayEffect.EEffectType.Buff, EGameplayTag.State_Rooted);
        notIfSilenced.BlockedIfTargetHas = new List<EGameplayTag> { EGameplayTag.State_Silenced };
        target.ApplyGameplayEffect(notIfSilenced, null);
        Assert.IsFalse(target.HasTag(EGameplayTag.State_Rooted));
    }
}
