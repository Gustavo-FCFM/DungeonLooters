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

    // =========================================================
    // ENCADENADOS ("Al vencer, aplica" / "Si se lo quitan antes, aplica")
    // =========================================================

    // El paso de cada frame, a mano (en las pruebas no corre Update).
    private static void Tick(AbilitySystemComponent asc, float seconds) =>
        typeof(AbilitySystemComponent).GetMethod("ProcessActiveEffects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                                      .Invoke(asc, new object[] { seconds });

    // Una marca de 2 s que al vencer pega 30 y si se la quitan antes pega 7 (números distintos para saber cuál salió).
    private GameplayEffect Mark(out GameplayEffect onExpire, out GameplayEffect onRemoved)
    {
        GameplayEffect mark = Effect("Marca", 2f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Silenced);
        onExpire = Hit(30f);
        onRemoved = Hit(7f);
        mark.OnExpireEffects = new List<GameplayEffect> { onExpire };
        mark.OnRemovedEffects = new List<GameplayEffect> { onRemoved };
        return mark;
    }

    [Test]
    public void Encadenado_AlVencer_SaleSoloAlVencer()
    {
        var target = Character(100f);
        target.ApplyGameplayEffect(Mark(out _, out _), null);
        Tick(target, 1f);
        Assert.AreEqual(100f, Health(target), "todavía no venció");
        Tick(target, 1.5f);
        Assert.IsFalse(target.HasTag(EGameplayTag.State_Silenced));
        Assert.AreEqual(70f, Health(target), "al vencer: 30 (no el 7 de quitarla)");
    }

    [Test]
    public void Encadenado_SiSeLoQuitanAntes_SaleConLimpiezaYConCodigo()
    {
        var target = Character(100f);
        GameplayEffect mark = Mark(out _, out _);
        target.ApplyGameplayEffect(mark, null);
        GameplayEffect cleanse = Effect("Limpiar");
        cleanse.RemoveOnApply = GameplayEffect.ERemoveKind.Debuffs;
        target.ApplyGameplayEffect(cleanse, null);
        Assert.AreEqual(93f, Health(target), "limpiada: 7 (no el 30 de vencer)");

        target.ApplyGameplayEffect(mark, null);
        target.RemoveEffectsWithTag(EGameplayTag.State_Silenced);   // una habilidad que consume la marca
        Assert.AreEqual(86f, Health(target));
    }

    [Test]
    public void Encadenado_NoSaleAlReaplicarNiAlMorirNiAlCambiarDeClase()
    {
        var target = Character(100f);
        GameplayEffect mark = Mark(out _, out _);
        mark.StackingPolicy = GameplayEffect.EStackingType.Override;
        target.ApplyGameplayEffect(mark, null);
        target.ApplyGameplayEffect(mark, null);          // pisarla: reemplazo, no "quitada"
        Assert.AreEqual(100f, Health(target), "pisarla no encadena");

        target.RemoveAllBuffsAndDebuffs();                // lo que hace morir
        Assert.AreEqual(100f, Health(target), "la limpieza de morir no encadena");

        target.ApplyGameplayEffect(mark, null);
        target.RemoveAllActiveEffects();                  // cambiar de clase
        Assert.AreEqual(100f, Health(target), "cambiar de clase no encadena");
    }

    [Test]
    public void Encadenado_LoFirmaQuienPusoElEfecto()
    {
        var attacker = Character(100f, (EAttributeType.Attack, 40f));
        var target = Character(200f);
        GameplayEffect mark = Effect("Marca", 1f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Silenced);
        mark.OnExpireEffects = new List<GameplayEffect> { Hit(0f, EAttributeType.Attack, 1.5f) };
        target.ApplyGameplayEffect(mark, attacker);
        Tick(target, 1.1f);
        Assert.AreEqual(140f, Health(target), "escala con el Ataque del que la puso (40 × 1.5)");
        Assert.AreSame(attacker, target.LastAttacker, "la baja es de quien la puso");
    }

    [Test]
    public void Encadenado_UnCirculoSeCorta()
    {
        var target = Character(1000f);
        GameplayEffect a = Effect("A", 1f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Silenced);
        GameplayEffect b = Effect("B", 1f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Rooted);
        GameplayEffect cleanseA = Effect("Quita A"); cleanseA.RemoveEffectsWithTags = new List<EGameplayTag> { EGameplayTag.State_Silenced };
        GameplayEffect cleanseB = Effect("Quita B"); cleanseB.RemoveEffectsWithTags = new List<EGameplayTag> { EGameplayTag.State_Rooted };
        // Quitar A pone B y lo quita; quitar B pone A y lo quita: sin tope no termina nunca.
        a.OnRemovedEffects = new List<GameplayEffect> { b, cleanseB };
        b.OnRemovedEffects = new List<GameplayEffect> { a, cleanseA };
        target.ApplyGameplayEffect(a, null);
        typeof(AbilitySystemComponent).GetField("s_warnedChainDepth", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                                      .SetValue(null, false);   // el aviso sale una vez por sesión
        UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("se cortó la cadena"));
        target.ApplyGameplayEffect(cleanseA, null);       // si no hay tope, esto no vuelve (o revienta la pila)
        Assert.Pass("la cadena se cortó");
    }

    // =========================================================
    // DURACIÓN INFINITA Y "SE VA SOLO"
    // =========================================================

    // En las pruebas no pasan frames: lo aplicado "en este frame" no se podría ir. Se lo corre a un frame anterior.
    private static void AgeEffects(AbilitySystemComponent asc)
    {
        foreach (ActiveGameplayEffect a in asc.GetActiveEffects()) a.AppliedFrame = -1;
    }

    [Test]
    public void Infinito_NoVenceNiMuestraTiempo_YSeQuita()
    {
        var target = Character(100f);
        GameplayEffect forever = Effect("Para siempre", 0f, GameplayEffect.EEffectType.Buff, EGameplayTag.Status_Invisible);
        forever.Infinite = true;
        forever.OnExpireEffects = new List<GameplayEffect> { Hit(50f) };
        target.ApplyGameplayEffect(forever, null);
        Tick(target, 100000f);
        Assert.IsTrue(target.HasTag(EGameplayTag.Status_Invisible), "no vence");
        Assert.AreEqual(100f, Health(target), "su \"Al vencer\" no sale nunca");
        ActiveGameplayEffect active = target.GetActiveEffects()[0];
        Assert.IsTrue(active.IsInfinite);
        Assert.AreEqual(0f, active.TotalDuration, "total 0 = sin reloj (la barra y la red)");
        target.RemoveEffectsWithTag(EGameplayTag.Status_Invisible);
        Assert.IsFalse(target.HasTag(EGameplayTag.Status_Invisible), "se quita como cualquiera");
    }

    [Test]
    public void SeVaSolo_AlRecibirDaño_DeOtroYNoDeSiMismo()
    {
        var attacker = Character(100f);
        var target = Character(100f);
        GameplayEffect sleep = Effect("Dormido", 10f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Stunned);
        sleep.RemoveOn = GameplayEffect.ERemoveOnEvent.TakeDamage;
        sleep.OnRemovedEffects = new List<GameplayEffect> { Hit(5f) };   // cuenta como "quitado antes"
        target.ApplyGameplayEffect(sleep, attacker);
        AgeEffects(target);

        target.ApplyGameplayEffect(Hit(10f), target);   // de sí mismo: no
        Assert.IsTrue(target.HasTag(EGameplayTag.State_Stunned));
        target.ApplyGameplayEffect(Hit(10f), attacker); // de otro: se despierta, y encadena
        Assert.IsFalse(target.HasTag(EGameplayTag.State_Stunned));
        Assert.AreEqual(75f, Health(target), "10 propio + 10 del golpe + 5 del encadenado");
    }

    [Test]
    public void SeVaSolo_ElGolpeQueLoPoneNoLoSaca()
    {
        var attacker = Character(100f);
        var target = Character(100f);
        GameplayEffect sleep = Effect("Dormido", 10f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Stunned);
        sleep.RemoveOn = GameplayEffect.ERemoveOnEvent.TakeDamage;
        target.ApplyGameplayEffect(sleep, attacker);
        target.ApplyGameplayEffect(Hit(10f), attacker);  // el mismo frame (el mismo golpe pega y duerme)
        Assert.IsTrue(target.HasTag(EGameplayTag.State_Stunned));
    }

    [Test]
    public void SeVaSolo_AlHacerDaño()
    {
        var attacker = Character(100f);
        var victim = Character(100f);
        GameplayEffect stealth = Effect("Sigilo", 10f, GameplayEffect.EEffectType.Buff, EGameplayTag.State_AlwaysVisible);
        stealth.RemoveOn = GameplayEffect.ERemoveOnEvent.DealDamage;
        attacker.ApplyGameplayEffect(stealth, attacker);
        AgeEffects(attacker);
        victim.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.IsFalse(attacker.HasTag(EGameplayTag.State_AlwaysVisible));
    }

    [Test]
    public void SeVaSolo_AlMoverse_ConMargen()
    {
        var target = Character(100f);
        GameplayEffect stance = Effect("Postura", 10f, GameplayEffect.EEffectType.Buff, EGameplayTag.State_Rooted);
        stance.RemoveOn = GameplayEffect.ERemoveOnEvent.Move;
        target.ApplyGameplayEffect(stance, target);
        AgeEffects(target);
        target.transform.position += new Vector3(0.3f, 0f, 0f);
        Tick(target, 0.1f);
        Assert.IsTrue(target.HasTag(EGameplayTag.State_Rooted), "0.3 m: dentro del margen");
        target.transform.position += new Vector3(0.5f, 0f, 0f);
        Tick(target, 0.1f);
        Assert.IsFalse(target.HasTag(EGameplayTag.State_Rooted), "0.8 m: se movió");
    }

    // =========================================================
    // EVENTOS DE JUEGO Y REACCIONES
    // =========================================================

    // Una pasiva: un efecto infinito con una reacción.
    private GameplayEffect Passive(EGameplayEvent when, EReactionTarget to, params GameplayEffect[] apply)
    {
        GameplayEffect passive = Effect("Pasiva", 0f, GameplayEffect.EEffectType.Hidden);
        passive.Infinite = true;
        passive.Reactions = new List<EffectReaction>
        {
            new EffectReaction { When = when, ApplyTo = to, Effects = new List<GameplayEffect>(apply) },
        };
        return passive;
    }

    [Test]
    public void Reaccion_AlHacerDaño_MarcaAlGolpeado()
    {
        var attacker = Character(100f);
        var victim = Character(100f);
        GameplayEffect mark = Effect("Marca", 5f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Silenced);
        attacker.ApplyGameplayEffect(Passive(EGameplayEvent.DamageDealt, EReactionTarget.Other, mark), attacker);

        victim.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.IsTrue(victim.HasTag(EGameplayTag.State_Silenced), "el golpeado queda marcado");
        Assert.IsFalse(attacker.HasTag(EGameplayTag.State_Silenced), "\"al otro\": el que pega no");
    }

    [Test]
    public void Reaccion_LoQueAplicaNoDisparaOtraReaccion()
    {
        var attacker = Character(100f);
        var victim = Character(100f);
        // "Al hacer daño, al otro: 5 más". Sin el freno, esos 5 volverían a disparar la reacción para siempre.
        attacker.ApplyGameplayEffect(Passive(EGameplayEvent.DamageDealt, EReactionTarget.Other, Hit(5f)), attacker);
        victim.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.AreEqual(85f, Health(victim), "10 del golpe + 5 de la reacción, una sola vez");
    }

    [Test]
    public void Reaccion_CondicionYEspera()
    {
        var attacker = Character(1000f);
        var victim = Character(1000f);
        GameplayEffect passive = Passive(EGameplayEvent.DamageDealt, EReactionTarget.Other, Hit(5f));
        passive.Reactions[0].OnlyIfOwnerHas = EGameplayTag.State_AlwaysVisible;   // la versión "potenciada" (un tag que no toca el daño)
        passive.Reactions[0].Cooldown = 5f;
        attacker.ApplyGameplayEffect(passive, attacker);

        victim.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.AreEqual(990f, Health(victim), "sin el tag, no sale");

        GiveTag(attacker, EGameplayTag.State_AlwaysVisible);
        victim.ApplyGameplayEffect(Hit(10f), attacker);
        victim.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.AreEqual(965f, Health(victim), "con el tag sale UNA vez (la segunda está en espera): 10 + 5 + 10");
    }

    [Test]
    public void Reaccion_NoTiene_YSoloSiLlegoALaVida()
    {
        var attacker = Character(1000f);
        var victim = Character(1000f);
        GameplayEffect passive = Passive(EGameplayEvent.DamageDealt, EReactionTarget.Other, Hit(5f));
        passive.Reactions[0].OnlyIfOwnerLacks = EGameplayTag.State_AlwaysVisible;   // la versión "normal"
        passive.Reactions[0].RequiresHealthDamage = true;
        attacker.ApplyGameplayEffect(passive, attacker);

        victim.SetCurrentAttributeValue(EAttributeType.Shield, 50f);
        victim.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.AreEqual(1000f, Health(victim), "el escudo se comió el golpe: no llegó a la vida, no sale");

        victim.SetCurrentAttributeValue(EAttributeType.Shield, 0f);
        victim.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.AreEqual(985f, Health(victim), "llegó a la vida: 10 + 5");

        GiveTag(attacker, EGameplayTag.State_AlwaysVisible);
        victim.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.AreEqual(975f, Health(victim), "con el tag, la \"normal\" no sale");
    }

    [Test]
    public void Reaccion_UnaVezPorGolpe_AunqueAlcanceAVarios()
    {
        var attacker = Character(100f);
        var a = Character(100f);
        var b = Character(100f);
        var c = Character(100f);
        GameplayEffect stack = Effect("Acumulación", 8f, GameplayEffect.EEffectType.Buff, EGameplayTag.State_AlwaysVisible);
        stack.StackingPolicy = GameplayEffect.EStackingType.Stack;
        GameplayEffect passive = Passive(EGameplayEvent.DamageDealt, EReactionTarget.Self, stack);
        passive.Reactions[0].OncePerFrame = true;
        attacker.ApplyGameplayEffect(passive, attacker);

        // Un mismo golpe que alcanza a tres (el mismo frame): una sola acumulación.
        a.ApplyGameplayEffect(Hit(10f), attacker);
        b.ApplyGameplayEffect(Hit(10f), attacker);
        c.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.AreEqual(1, attacker.GetActiveEffects().FindAll(x => x.Definition == stack).Count);
    }

    [Test]
    public void Reaccion_AlMatar_YSeGasta()
    {
        var attacker = Character(100f);
        var victim = Character(10f);
        GameplayEffect buff = Effect("Racha", 10f, GameplayEffect.EEffectType.Buff, EGameplayTag.State_AlwaysVisible);
        GameplayEffect passive = Passive(EGameplayEvent.Kill, EReactionTarget.Self, buff);
        passive.Reactions[0].ConsumesEffect = true;
        attacker.ApplyGameplayEffect(passive, attacker);

        victim.ApplyGameplayEffect(Hit(50f), attacker);
        Assert.IsTrue(attacker.HasTag(EGameplayTag.State_AlwaysVisible), "al matar, el que mata gana el buff");
        Assert.IsFalse(attacker.GetActiveEffects().Exists(a => a.Definition == passive), "y la pasiva se gastó");
    }

    // Una habilidad real (GA_SelfBuff: "al activarse, a mí: 'buff'") que se activa sola con 'when'.
    private GameplayAbility TriggeredSelfBuff(AbilitySystemComponent owner, EGameplayEvent when, GameplayEffect buff)
    {
        var template = ScriptableObject.CreateInstance<GA_SelfBuff>();
        _made.Add(template);
        template.AbilityName = "Se activa sola (prueba)";
        template.TriggerEvent = when;
        template.Effects = new List<AbilityEffect>
        {
            new AbilityEffect { When = EEffectWhen.OnActivate, ApplyTo = EEffectTarget.Self, Effect = buff },
        };
        GameplayAbility granted = owner.GrantAbility(template);
        _made.Add(granted);
        return granted;
    }

    [Test]
    public void SeActivaSola_AlRecibirDañoDeOtro()
    {
        var attacker = Character(100f);
        var owner = Character(100f);
        GameplayEffect buff = Effect("Contra", 5f, GameplayEffect.EEffectType.Buff, EGameplayTag.State_AlwaysVisible);
        TriggeredSelfBuff(owner, EGameplayEvent.DamageTaken, buff);

        owner.ApplyGameplayEffect(Hit(10f), owner);       // daño propio: no es "recibir daño"
        Assert.IsFalse(owner.HasTag(EGameplayTag.State_AlwaysVisible));
        owner.ApplyGameplayEffect(Hit(10f), attacker);    // de otro: se activa sola
        Assert.IsTrue(owner.HasTag(EGameplayTag.State_AlwaysVisible));
    }

    [Test]
    public void SeActivaSola_RespetaSuCondicion()
    {
        var attacker = Character(100f);
        var owner = Character(100f);
        GameplayEffect buff = Effect("Contra", 5f, GameplayEffect.EEffectType.Buff, EGameplayTag.State_AlwaysVisible);
        GameplayAbility ga = TriggeredSelfBuff(owner, EGameplayEvent.DamageTaken, buff);
        ga.TriggerOnlyIfOwnerHas = EGameplayTag.State_Rooted;

        owner.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.IsFalse(owner.HasTag(EGameplayTag.State_AlwaysVisible), "sin el tag, no sale");
        GiveTag(owner, EGameplayTag.State_Rooted);
        owner.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.IsTrue(owner.HasTag(EGameplayTag.State_AlwaysVisible), "con el tag, sale");
    }

    // =========================================================
    // TAREAS: ¿CORTO? (AbilityGuard / EStopWhen)
    // =========================================================

    // Un guardián de una habilidad real otorgada (GA_SelfBuff), desde afuera (Guard es protected).
    private static AbilityGuard GuardFor(GameplayAbility ability, AbilitySystemComponent owner, EStopWhen when) =>
        (AbilityGuard)System.Activator.CreateInstance(typeof(AbilityGuard),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            new object[] { ability, owner, when }, null);

    [Test]
    public void Guardian_MuereAturdidoYBloqueado()
    {
        var owner = Character(100f);
        GameplayAbility ga = TriggeredSelfBuff(owner, EGameplayEvent.None, Effect("Nada", 1f));
        ga.ActivationBlockedTags = new List<EGameplayTag> { EGameplayTag.State_Silenced };

        var onlyDies = GuardFor(ga, owner, EStopWhen.Dies);
        var blocked  = GuardFor(ga, owner, EStopWhen.Blocked);
        var stunned  = GuardFor(ga, owner, EStopWhen.Stunned);
        Assert.IsFalse(onlyDies.Check() || blocked.Check() || stunned.Check(), "nada todavía");

        GiveTag(owner, EGameplayTag.State_Silenced);
        Assert.IsTrue(blocked.Check(), "lo que la bloquea la corta");
        Assert.IsFalse(onlyDies.Check(), "\"solo al morir\" no se corta por un silencio");
        Assert.IsFalse(stunned.Check());

        GiveTag(owner, EGameplayTag.State_Stunned);
        Assert.IsTrue(stunned.Check());
    }

    [Test]
    public void Guardian_InterrupcionYDaño_DesdeQueEmpezo()
    {
        var attacker = Character(100f);
        var owner = Character(100f);
        GameplayAbility ga = TriggeredSelfBuff(owner, EGameplayEvent.None, Effect("Nada", 1f));
        ga.IsInterruptible = true;

        owner.CancelInterruptibleAbilities();             // una interrupción ANTES de empezar no cuenta
        var interrupt = GuardFor(ga, owner, EStopWhen.Interrupted);
        Assert.IsFalse(interrupt.Check());
        owner.CancelInterruptibleAbilities();
        Assert.IsTrue(interrupt.Check(), "otra habilidad la cortó");

        using (var damaged = GuardFor(ga, owner, EStopWhen.Damaged))
        {
            Assert.IsFalse(damaged.Check());
            owner.ApplyGameplayEffect(Hit(10f), attacker);
            Assert.IsTrue(damaged.Check(), "recibió daño de otro");
        }
    }

    // =========================================================
    // TAGS JERÁRQUICOS (GameplayTagTree)
    // =========================================================

    [Test]
    public void Grupo_TieneSiTieneUnHijo_ADosNiveles()
    {
        var target = Character(100f);
        Assert.IsFalse(target.HasTag(EGameplayTag.Group_Control));
        GiveTag(target, EGameplayTag.State_Stunned);
        Assert.IsTrue(target.HasTag(EGameplayTag.Group_HardControl), "aturdido está en el control duro");
        Assert.IsTrue(target.HasTag(EGameplayTag.Group_Control), "y el control duro está en el control");
        Assert.IsFalse(target.HasTag(EGameplayTag.Group_DamageOverTime));
        GiveTag(target, EGameplayTag.Status_Slow);
        Assert.AreEqual(2, target.GetTagCount(EGameplayTag.Group_Control), "cuenta los de todos sus hijos");
        Assert.AreEqual(1, target.GetTagCount(EGameplayTag.Group_HardControl), "la lentitud no es control duro");
    }

    [Test]
    public void Grupo_InmunidadYQuitarPorGrupo()
    {
        var target = Character(100f);
        GameplayEffect poison = Effect("Veneno", 10f, GameplayEffect.EEffectType.Debuff, EGameplayTag.Status_Poison);
        GameplayEffect immune = Effect("Antídoto", 10f, GameplayEffect.EEffectType.Buff);
        immune.GrantsImmunityTo = new List<EGameplayTag> { EGameplayTag.Group_DamageOverTime };
        target.ApplyGameplayEffect(immune, null);
        target.ApplyGameplayEffect(poison, null);
        Assert.IsFalse(target.HasTag(EGameplayTag.Status_Poison), "inmune al grupo = inmune al veneno");

        var other = Character(100f);
        other.ApplyGameplayEffect(Effect("Aturdido", 10f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Stunned), null);
        other.ApplyGameplayEffect(Effect("Lento", 10f, GameplayEffect.EEffectType.Debuff, EGameplayTag.Status_Slow), null);
        other.ApplyGameplayEffect(Effect("Veneno", 10f, GameplayEffect.EEffectType.Debuff, EGameplayTag.Status_Poison), null);
        other.RemoveEffectsWithTag(EGameplayTag.Group_Control);
        Assert.IsFalse(other.HasTag(EGameplayTag.State_Stunned) || other.HasTag(EGameplayTag.Status_Slow), "quitar el grupo quita los hijos");
        Assert.IsTrue(other.HasTag(EGameplayTag.Status_Poison), "y no lo que no es del grupo");

        other.ApplyGameplayEffect(Effect("Enraizado", 10f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Rooted), null);
        other.RemoveEffectsWithTag(EGameplayTag.State_Stunned);
        Assert.IsTrue(other.HasTag(EGameplayTag.State_Rooted), "una hoja solo se quita a sí misma");
    }

    [Test]
    public void Grupo_LaResistenciaAlControlSigueIgual()
    {
        // La resistencia recorta solo el control DURO (los tres de siempre): la lentitud y el desarme, no.
        Assert.IsTrue(Effect("A", 2f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Stunned).CountsAsCrowdControl);
        Assert.IsTrue(Effect("B", 2f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Silenced).CountsAsCrowdControl);
        Assert.IsFalse(Effect("C", 2f, GameplayEffect.EEffectType.Debuff, EGameplayTag.Status_Slow).CountsAsCrowdControl);
        Assert.IsFalse(Effect("D", 2f, GameplayEffect.EEffectType.Debuff, EGameplayTag.State_Disarmed).CountsAsCrowdControl);
    }

    // =========================================================
    // CONTRAATAQUE GENÉRICO (GA_Counterattack)
    // =========================================================

    private GameplayAbility Counter(AbilitySystemComponent owner, float range, float reflect, params GameplayEffect[] onHit)
    {
        var template = ScriptableObject.CreateInstance<GA_Counterattack>();
        _made.Add(template);
        template.AbilityName = "Contraataque (prueba)";
        template.TriggerEvent = EGameplayEvent.DamageTaken;
        template.MaxRange = range;
        template.ReflectFraction = reflect;
        template.Effects = new List<AbilityEffect>();
        foreach (GameplayEffect e in onHit)
            template.Effects.Add(new AbilityEffect { When = EEffectWhen.OnHit, ApplyTo = EEffectTarget.Enemies, Effect = e });
        GameplayAbility granted = owner.GrantAbility(template);
        _made.Add(granted);
        return granted;
    }

    [Test]
    public void Contraataque_LePegaAlQueLePego()
    {
        var attacker = Character(100f);
        var owner = Character(100f);
        Counter(owner, 4f, 0f, Hit(7f));
        owner.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.AreEqual(93f, Health(attacker), "el contraataque le aplicó sus efectos al que pegó");
        Assert.AreEqual(90f, Health(owner));
    }

    [Test]
    public void Contraataque_FueraDeAlcanceNoSale()
    {
        var attacker = Character(100f);
        var owner = Character(100f);
        attacker.transform.position = new Vector3(10f, 0f, 0f);   // un arquero lejos
        Counter(owner, 4f, 0f, Hit(7f));
        owner.ApplyGameplayEffect(Hit(10f), attacker);
        Assert.AreEqual(100f, Health(attacker));
    }

    [Test]
    public void Contraataque_DevuelveParteDelDaño_SinRebotar()
    {
        var attacker = Character(100f);
        var owner = Character(100f);
        Counter(owner, 0f, 0.5f);
        Counter(attacker, 0f, 0.5f);   // el otro también tiene espinas: lo devuelto no dispara otro contraataque
        owner.ApplyGameplayEffect(Hit(20f), attacker);
        Assert.AreEqual(80f, Health(owner), "20 del golpe, y nada más");
        Assert.AreEqual(90f, Health(attacker), "le volvió la mitad: 10");
    }

    // =========================================================
    // PROVOCAR (Status_Taunted → ASC.GetTaunter)
    // =========================================================

    [Test]
    public void Provocar_ElUltimoQueProvocoYQueSigueVivo()
    {
        var npc = Character(100f);
        var tank = Character(100f);
        var other = Character(10f);
        GameplayEffect taunt = Effect("Provocado", 3f, GameplayEffect.EEffectType.Debuff, EGameplayTag.Status_Taunted);
        taunt.StackingPolicy = GameplayEffect.EStackingType.Stack;

        Assert.IsNull(npc.GetTaunter(), "sin provocar");
        npc.ApplyGameplayEffect(taunt, tank);
        Assert.AreSame(tank, npc.GetTaunter());
        Assert.IsTrue(npc.HasTag(EGameplayTag.Group_Control), "provocar es un control (grupo Control)");

        npc.ApplyGameplayEffect(taunt, other);
        Assert.AreSame(other, npc.GetTaunter(), "el más nuevo manda");

        other.ApplyGameplayEffect(Hit(50f), tank);         // el último provocador muere
        Assert.AreSame(tank, npc.GetTaunter(), "un provocador muerto no cuenta: vuelve el anterior");
    }

    // =========================================================
    // GANCHO (GA_Hook) — con física de verdad: colliders en memoria
    // =========================================================

    private GameplayAbility Hook(AbilitySystemComponent owner, params GameplayEffect[] onHit)
    {
        var template = ScriptableObject.CreateInstance<GA_Hook>();
        _made.Add(template);
        template.AbilityName = "Gancho (prueba)";
        template.Mode = GA_Hook.EHookMode.PullTarget;
        template.MaxRange = 20f;
        template.WallLayer = 1;   // Default
        template.Effects = new List<AbilityEffect>();
        foreach (GameplayEffect e in onHit)
            template.Effects.Add(new AbilityEffect { When = EEffectWhen.OnHit, ApplyTo = EEffectTarget.Enemies, Effect = e });
        GameplayAbility granted = owner.GrantAbility(template);
        _made.Add(granted);
        return granted;
    }

    private static void Body(AbilitySystemComponent asc, Vector3 at)
    {
        asc.transform.position = at;
        asc.gameObject.layer = 7;
        var col = asc.gameObject.AddComponent<CapsuleCollider>();
        col.center = Vector3.up; col.height = 2f; col.radius = 0.45f;
    }

    private GameObject Wall(Vector3 at)
    {
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.hideFlags = HideFlags.HideAndDontSave;
        _made.Add(wall);
        wall.transform.position = at;
        wall.transform.localScale = new Vector3(6f, 6f, 0.5f);
        wall.layer = 0;
        return wall;
    }

    [Test]
    public void Gancho_EngachaAlEnemigoDeAdelante()
    {
        var owner = Character(100f);
        var enemy = Character(100f);
        Body(owner, Vector3.zero);
        Body(enemy, new Vector3(0f, 0f, 8f));
        Physics.SyncTransforms();

        GameplayAbility hook = Hook(owner, Hit(7f));
        hook.Activate();
        Assert.AreEqual(93f, Health(enemy), "lo enganchó: sus efectos Al golpear");
        Assert.IsTrue(hook.CommittedThisActivation);
    }

    [Test]
    public void Gancho_UnaParedEnElMedioLoTapa_YSinNadaNoGasta()
    {
        var owner = Character(100f);
        var enemy = Character(100f);
        Body(owner, Vector3.zero);
        Body(enemy, new Vector3(0f, 0f, 8f));
        Wall(new Vector3(0f, 1.4f, 4f));
        Physics.SyncTransforms();

        GameplayAbility hook = Hook(owner, Hit(7f));
        hook.Activate();
        Assert.AreEqual(100f, Health(enemy), "detrás de la pared no lo engancha (engancha la pared)");
        Assert.IsTrue(hook.CommittedThisActivation, "la pared sí se engancha");

        var lonely = Character(100f);
        Body(lonely, new Vector3(50f, 0f, 0f));
        Physics.SyncTransforms();
        GameplayAbility miss = Hook(lonely, Hit(7f));
        miss.Activate();
        Assert.IsFalse(miss.CommittedThisActivation, "sin nada adelante no sale ni gasta");
    }

    // =========================================================
    // RAYO CONTINUO (GA_ChanneledCone con Forma = Rayo) — física de verdad
    // =========================================================

    // Un rayo de un solo golpe (la duración = un tick: sale al activar), hacia el frente del dueño.
    private GameplayAbility Beam(AbilitySystemComponent owner, bool pierces, params GameplayEffect[] onHit)
    {
        var template = ScriptableObject.CreateInstance<GA_ChanneledCone>();
        _made.Add(template);
        template.AbilityName = "Rayo (prueba)";
        template.Shape = GA_ChanneledCone.EChannelShape.Beam;
        template.Range = 20f;
        template.BeamPierces = pierces;
        template.BeamWallLayer = 1;
        template.TargetLayer = 1 << 7;
        template.ChannelDuration = 1f;
        template.TickInterval = 1f;
        template.BlockOtherAbilities = false;   // en las pruebas el tiempo no corre: el canalizado no termina nunca
        template.OriginOffset = new Vector3(0f, 1f, 0f);
        template.Effects = new List<AbilityEffect>();
        foreach (GameplayEffect e in onHit)
            template.Effects.Add(new AbilityEffect { When = EEffectWhen.OnHit, ApplyTo = EEffectTarget.Enemies, Effect = e });
        GameplayAbility granted = owner.GrantAbility(template);
        _made.Add(granted);
        return granted;
    }

    [Test]
    public void Rayo_AlPrimero_OAtraviesa_YLaParedLoCorta()
    {
        var owner = Character(100f);
        var near = Character(100f);
        var far = Character(100f);
        Body(owner, Vector3.zero);
        Body(near, new Vector3(0f, 0f, 4f));
        Body(far, new Vector3(0f, 0f, 8f));
        Physics.SyncTransforms();

        Beam(owner, false, Hit(7f)).Activate();
        Assert.AreEqual(93f, Health(near), "le pega al primero");
        Assert.AreEqual(100f, Health(far), "y se queda ahí");

        Beam(owner, true, Hit(7f)).Activate();
        Assert.AreEqual(86f, Health(near));
        Assert.AreEqual(93f, Health(far), "atravesando, también al segundo");

        Wall(new Vector3(0f, 1f, 6f));
        Physics.SyncTransforms();
        Beam(owner, true, Hit(7f)).Activate();
        Assert.AreEqual(79f, Health(near));
        Assert.AreEqual(93f, Health(far), "la pared lo corta");
    }

    [Test]
    public void Evento_LlegaPorCodigoConSusDatos()
    {
        var attacker = Character(100f);
        var victim = Character(100f);
        var seen = new List<GameplayEventData>();
        attacker.OnGameplayEvent += d => seen.Add(d);
        GameplayEffect crit = Hit(10f);
        crit.CritIfTargetHas = EGameplayTag.State_Silenced;
        GiveTag(victim, EGameplayTag.State_Silenced);
        victim.ApplyGameplayEffect(crit, attacker);

        Assert.IsTrue(seen.Exists(d => d.Event == EGameplayEvent.DamageDealt && d.Other == victim && d.Magnitude > 0f));
        Assert.IsTrue(seen.Exists(d => d.Event == EGameplayEvent.CriticalDealt), "el golpe salió crítico");
    }
    // ---- Proyectil que rebota o vuelve (GC_Projectile, "Rebote y vuelta") ----

    [Test]
    public void Proyectil_RebotaContraLaParedReflejado()
    {
        GameObject wall = Wall(new Vector3(0f, 1f, 5f));   // su cara de adelante está en z = 4.75
        Physics.SyncTransforms();
        Collider col = wall.GetComponent<Collider>();

        Vector3 v = GC_Projectile.BounceVelocity(col, new Vector3(0f, 1f, 4.6f), new Vector3(6f, 0f, 8f));
        Assert.AreEqual(6f, v.x, 0.01f, "lo de costado sigue igual");
        Assert.AreEqual(-8f, v.z, 0.01f, "lo que iba contra la pared se da vuelta");
        Assert.AreEqual(10f, v.magnitude, 0.01f, "con la misma rapidez");

        v = GC_Projectile.BounceVelocity(col, new Vector3(0f, 1f, 4.6f), new Vector3(0f, 0f, 10f));
        Assert.AreEqual(-10f, v.z, 0.01f, "de frente, vuelve por donde vino");
    }

    [Test]
    public void Proyectil_SaltaAlEnemigoMasCercanoQueNoToco_YLaParedLoTapa()
    {
        var owner = Character(100f);
        var hit = Character(100f);
        var near = Character(100f);
        var far = Character(100f);
        Body(owner, Vector3.zero);
        Body(hit, new Vector3(0f, 0f, 8f));
        Body(near, new Vector3(3f, 0f, 8f));
        Body(far, new Vector3(-5f, 0f, 8f));
        Physics.SyncTransforms();

        var none = new HashSet<AbilitySystemComponent> { hit };
        Assert.AreSame(near, GC_Projectile.FindNextEnemy(owner, hit, 8f, none, 1), "el más cercano");
        Assert.IsNull(GC_Projectile.FindNextEnemy(owner, hit, 2f, none, 1), "fuera del alcance, ninguno");

        var touched = new HashSet<AbilitySystemComponent> { hit, near };
        Assert.AreSame(far, GC_Projectile.FindNextEnemy(owner, hit, 8f, touched, 1), "al ya golpeado no vuelve");

        GameObject wall = Wall(new Vector3(-2.5f, 1f, 8f));
        wall.transform.rotation = Quaternion.Euler(0f, 90f, 0f);   // de frente al salto hacia la izquierda
        Physics.SyncTransforms();
        Assert.IsNull(GC_Projectile.FindNextEnemy(owner, hit, 8f, touched, 1), "una pared en el medio lo tapa");
    }
    // ---- Intercambiar posición (GA_SwapPosition) ----

    private GameplayAbility Swap(AbilitySystemComponent owner, GA_SwapPosition.ESwapWith with, params GameplayEffect[] onHit)
    {
        var template = ScriptableObject.CreateInstance<GA_SwapPosition>();
        _made.Add(template);
        template.AbilityName = "Intercambio (prueba)";
        template.SwapWith = with;
        template.MaxRange = 20f;
        template.WallLayer = 1;   // Default
        template.Effects = new List<AbilityEffect>();
        foreach (GameplayEffect e in onHit)
            template.Effects.Add(new AbilityEffect { When = EEffectWhen.OnHit, ApplyTo = EEffectTarget.Enemies, Effect = e });
        GameplayAbility granted = owner.GrantAbility(template);
        _made.Add(granted);
        return granted;
    }

    [Test]
    public void Intercambio_CambiaDeLugarConElEnemigo_YLeAplicaLoSuyo()
    {
        var owner = Character(100f);
        var enemy = Character(100f);
        Body(owner, Vector3.zero);
        Body(enemy, new Vector3(0f, 0f, 8f));
        Physics.SyncTransforms();

        GameplayAbility swap = Swap(owner, GA_SwapPosition.ESwapWith.Enemies, Hit(7f));
        swap.Activate();
        Assert.AreEqual(new Vector3(0f, 0f, 8f), owner.transform.position, "el que la tira, donde estaba el otro");
        Assert.AreEqual(Vector3.zero, enemy.transform.position, "el otro, donde estaba él");
        Assert.AreEqual(93f, Health(enemy), "sus efectos Al golpear");
        Assert.IsTrue(swap.CommittedThisActivation);
    }

    [Test]
    public void Intercambio_NoConImparables_NiDetrasDeUnaPared_NiFueraDeConQuien()
    {
        var owner = Character(100f);
        var enemy = Character(100f);
        Body(owner, Vector3.zero);
        Body(enemy, new Vector3(0f, 0f, 8f));
        Physics.SyncTransforms();

        Swap(owner, GA_SwapPosition.ESwapWith.Allies).Activate();
        Assert.AreEqual(new Vector3(0f, 0f, 8f), enemy.transform.position, "solo aliados: al enemigo no");

        GiveTag(enemy, EGameplayTag.Status_Unstoppable);
        GameplayAbility swap = Swap(owner, GA_SwapPosition.ESwapWith.Enemies);
        swap.Activate();
        Assert.AreEqual(new Vector3(0f, 0f, 8f), enemy.transform.position, "imparable: no se mueve");
        Assert.IsFalse(swap.CommittedThisActivation, "y sin blanco no gasta");

        var other = Character(100f);
        Body(other, new Vector3(0f, 0f, -8f));
        owner.transform.rotation = Quaternion.LookRotation(Vector3.back);
        Wall(new Vector3(0f, 1f, -4f));
        Physics.SyncTransforms();
        Swap(owner, GA_SwapPosition.ESwapWith.Enemies).Activate();
        Assert.AreEqual(new Vector3(0f, 0f, -8f), other.transform.position, "una pared en el medio lo tapa");
    }
    // ---- Zona que avanza (Summon_Zone, "Crece y avanza") ----

    private Summon_Zone MovingZone(float speed, float distance)
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.hideFlags = HideFlags.HideAndDontSave;
        _made.Add(floor);
        floor.transform.position = new Vector3(0f, -0.5f, 0f);   // su cara de arriba en y = 0
        floor.transform.localScale = new Vector3(50f, 1f, 50f);

        var go = new GameObject("Zona (prueba)") { hideFlags = HideFlags.HideAndDontSave };
        _made.Add(go);
        var zone = go.AddComponent<Summon_Zone>();
        zone.MoveSpeed = speed;
        zone.MoveDistance = distance;
        zone.StopsAtWalls = true;
        zone.GroundLayer = 1;
        return zone;
    }

    [Test]
    public void Zona_AvanzaHastaLaPared_YFrena()
    {
        Summon_Zone zone = MovingZone(10f, 0f);
        Wall(new Vector3(0f, 1f, 5f));   // su cara de adelante en z = 4.75
        Physics.SyncTransforms();

        zone.Advance(0.2f);
        Assert.AreEqual(2f, zone.transform.position.z, 0.01f, "10 m/s × 0.2 s");
        Assert.AreEqual(0f, zone.transform.position.y, 0.01f, "sobre el piso");
        zone.Advance(0.2f);
        zone.Advance(0.2f);
        Assert.IsTrue(zone.Stopped, "llegó a la pared");
        Assert.AreEqual(4.25f, zone.transform.position.z, 0.01f, "medio metro antes de la pared");
        zone.Advance(0.2f);
        Assert.AreEqual(4.25f, zone.transform.position.z, 0.01f, "y ahí se queda");
    }

    [Test]
    public void Zona_AvanzaSuRecorrido_YFrena()
    {
        Summon_Zone zone = MovingZone(10f, 3f);
        Physics.SyncTransforms();

        zone.Advance(1f);
        Assert.AreEqual(3f, zone.transform.position.z, 0.01f, "no pasa de su recorrido");
        Assert.IsTrue(zone.Stopped);
    }
    // ---- Revivir (GA_Revive) ----

    // Un aliado (equipo 1) que FindObjectsByType encuentra (sin HideAndDontSave: TearDown lo borra igual).
    private AbilitySystemComponent Teammate(Vector3 at)
    {
        var asc = Character(100f, (EAttributeType.MaxHealth, 100f));
        asc.gameObject.hideFlags = HideFlags.None;
        asc.TeamID = 1;
        asc.transform.position = at;
        return asc;
    }

    private GameplayAbility Revive(AbilitySystemComponent owner, GA_Revive.EReviveMode mode)
    {
        var template = ScriptableObject.CreateInstance<GA_Revive>();
        _made.Add(template);
        template.AbilityName = "Revivir (prueba)";
        template.Mode = mode;
        template.MaxRange = 8f;
        template.HealthFraction = 0.5f;
        GameplayAbility granted = owner.GrantAbility(template);
        _made.Add(granted);
        return granted;
    }

    [Test]
    public void Revivir_EnArea_LevantaALosCuerposCercanosConLaMitad()
    {
        var owner = Teammate(Vector3.zero);
        var near = Teammate(new Vector3(3f, 0f, 0f));
        var far = Teammate(new Vector3(20f, 0f, 0f));
        var enemy = Character(100f, (EAttributeType.MaxHealth, 100f));
        enemy.gameObject.hideFlags = HideFlags.None;
        enemy.TeamID = 2;
        enemy.transform.position = new Vector3(0f, 0f, 3f);
        foreach (var asc in new[] { near, far, enemy }) asc.ApplyGameplayEffect(Hit(500f), owner);
        Assert.IsTrue(near.HasTag(EGameplayTag.State_Dead), "la prueba arranca con ellos muertos");

        GameplayAbility revive = Revive(owner, GA_Revive.EReviveMode.Area);
        revive.Activate();
        Assert.IsFalse(near.HasTag(EGameplayTag.State_Dead), "el de cerca vuelve");
        Assert.AreEqual(50f, Health(near), "con la mitad de su vida máxima");
        Assert.IsTrue(far.HasTag(EGameplayTag.State_Dead), "el de lejos no");
        Assert.IsTrue(enemy.HasTag(EGameplayTag.State_Dead), "un enemigo no");
        Assert.IsTrue(revive.CommittedThisActivation);
    }

    [Test]
    public void Revivir_SinCuerposNoSaleNiGasta()
    {
        var owner = Teammate(Vector3.zero);
        Teammate(new Vector3(3f, 0f, 0f));   // vivo

        GameplayAbility revive = Revive(owner, GA_Revive.EReviveMode.Area);
        Assert.IsFalse(revive.CanActivate(), "nadie a quien revivir");
        revive.Activate();
        Assert.IsFalse(revive.CommittedThisActivation);
    }
    // ---- Vínculo entre dos (GameplayEffect "Vínculo (entre dos)") ----

    private GameplayEffect Link(float damageShare, float healShare, float maxDistance)
    {
        GameplayEffect link = Effect("Vínculo", 30f, GameplayEffect.EEffectType.Buff, EGameplayTag.State_AlwaysVisible);
        link.LinkDamageShare = damageShare;
        link.LinkHealShare = healShare;
        link.LinkMaxDistance = maxDistance;
        return link;
    }

    [Test]
    public void Vinculo_ElQueLoPusoSeLlevaParteDelDano()
    {
        var protector = Character(100f);
        var ally = Character(100f);
        var enemy = Character(100f);
        ally.ApplyGameplayEffect(Link(0.4f, 0f, 0f), protector);

        ally.ApplyGameplayEffect(Hit(50f), enemy);
        Assert.AreEqual(70f, Health(ally), "se queda con el 60 %");
        Assert.AreEqual(80f, Health(protector), "el otro 40 % se lo lleva el protector");

        GiveTag(protector, EGameplayTag.Status_Immunity);
        ally.ApplyGameplayEffect(Hit(10f), enemy);
        Assert.AreEqual(60f, Health(ally), "con el protector inmune no se pasa nada");
        Assert.AreEqual(80f, Health(protector));
    }

    [Test]
    public void Vinculo_ComparteLaCuracion_YSeCortaLejos()
    {
        var linker = Character(100f);
        var ally = Character(100f);
        var enemy = Character(100f);
        linker.ApplyGameplayEffect(Hit(40f), enemy);
        ally.ApplyGameplayEffect(Hit(40f), enemy);
        ally.ApplyGameplayEffect(Link(0f, 0.5f, 10f), linker);

        ally.ApplyGameplayEffect(Heal(20f), ally);
        Assert.AreEqual(80f, Health(ally), "el curado recibe todo");
        Assert.AreEqual(70f, Health(linker), "y al otro le llega la mitad");

        ally.transform.position = new Vector3(20f, 0f, 0f);
        Tick(ally, 0.1f);
        Assert.IsFalse(ally.HasTag(EGameplayTag.State_AlwaysVisible), "lejos, el vínculo se corta");
        ally.ApplyGameplayEffect(Heal(10f), ally);
        Assert.AreEqual(70f, Health(linker), "y ya no comparte");
    }
    // ---- Definitiva global (GA_InstantAoE "Todo el mapa") ----

    private AbilitySystemComponent OnMap(int team, Vector3 at)
    {
        var asc = Character(100f);
        asc.gameObject.hideFlags = HideFlags.None;   // FindObjectsByType no ve los HideAndDontSave (TearDown lo borra igual)
        asc.TeamID = team;
        asc.transform.position = at;
        return asc;
    }

    private GameplayAbility GlobalBlast(AbilitySystemComponent owner, bool global, params GameplayEffect[] onHit)
    {
        var template = ScriptableObject.CreateInstance<GA_InstantAoE>();
        _made.Add(template);
        template.AbilityName = "Definitiva global (prueba)";
        template.Radius = 4f;
        template.Global = global;
        template.Effects = new List<AbilityEffect>();
        foreach (GameplayEffect e in onHit)
            template.Effects.Add(new AbilityEffect { When = EEffectWhen.OnHit, ApplyTo = EEffectTarget.Enemies, Effect = e });
        GameplayAbility granted = owner.GrantAbility(template);
        _made.Add(granted);
        return granted;
    }

    [Test]
    public void DefinitivaGlobal_LeLlegaATodosLosEnemigosDelMapa()
    {
        var owner = OnMap(1, Vector3.zero);
        var ally = OnMap(1, new Vector3(200f, 0f, 0f));
        var far = OnMap(2, new Vector3(0f, 0f, 300f));
        var dead = OnMap(2, new Vector3(50f, 0f, 50f));
        dead.ApplyGameplayEffect(Hit(500f), owner);
        Assert.IsTrue(dead.HasTag(EGameplayTag.State_Dead));

        GlobalBlast(owner, false, Hit(10f)).Activate();
        Assert.AreEqual(100f, Health(far), "sin la casilla, el radio manda: lejos no le llega");

        GlobalBlast(owner, true, Hit(10f)).Activate();
        Assert.AreEqual(90f, Health(far), "con Todo el mapa, a 300 m también");
        Assert.AreEqual(100f, Health(ally), "a los aliados, lo que diga la lista (nada)");
        Assert.AreEqual(100f, Health(owner), "ni al que la usa");
    }
}
