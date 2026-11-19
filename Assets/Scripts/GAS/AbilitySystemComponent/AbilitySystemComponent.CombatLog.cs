using UnityEngine;
using System.Collections.Generic;
using System;

// ============================================================
// AbilitySystemComponent — EL REGISTRO DEL COMBATE
//
// Una parte de AbilitySystemComponent (partial class). La clase, sus campos del Inspector y sus
// eventos están en GAS/AbilitySystemComponent.cs; ahí está también la lista de las partes.
//
// Quién le pegó y quién lo curó (las asistencias del KDA), el daño recibido (el resumen de
// muerte) y la cadena de cada golpe (el depurador en vivo). Todo en el servidor.
// ============================================================
public partial class AbilitySystemComponent
{
    // =========================================================
    // QUIÉN LE PEGÓ Y QUIÉN LO CURÓ (las asistencias del KDA)
    // =========================================================

    // Quién le pegó y quién lo curó, y cuándo (solo en el servidor): de acá salen las
    // asistencias del KDA (PlayerController.ServerCountKda). Se olvida al revivir.
    private readonly Dictionary<AbilitySystemComponent, float> _lastHitBy    = new Dictionary<AbilitySystemComponent, float>();

    private readonly Dictionary<AbilitySystemComponent, float> _lastHealedBy = new Dictionary<AbilitySystemComponent, float>();

    // A quién se le anota lo que hizo una invocación (tótem, compañero, copia): a su dueño. Lo demás, a sí mismo.
    private static AbilitySystemComponent CreditOf(AbilitySystemComponent source)
    {
        Entity_Summon summon = source != null ? source.GetComponent<Entity_Summon>() : null;
        return summon != null && summon.Summoner != null ? summon.Summoner : source;
    }

    // Los que le pegaron (o lo curaron) en los últimos segundos pedidos van a `into`.

    public void CollectRecentHitters(float seconds, HashSet<AbilitySystemComponent> into) => CollectRecent(_lastHitBy, seconds, into);
    public void CollectRecentHealers(float seconds, HashSet<AbilitySystemComponent> into) => CollectRecent(_lastHealedBy, seconds, into);

    private static void CollectRecent(Dictionary<AbilitySystemComponent, float> from, float seconds,
                                      HashSet<AbilitySystemComponent> into)
    {
        foreach (KeyValuePair<AbilitySystemComponent, float> entry in from)
            if (entry.Key != null && Time.time - entry.Value <= seconds) into.Add(entry.Key);
    }

    // =========================================================
    // EL DAÑO RECIBIDO, para el resumen de muerte (13 de noviembre de 2026)
    // =========================================================
    //
    // Cada golpe que le baja vida (o escudo) a este personaje queda anotado unos segundos: quién (el dueño, si fue
    // una invocación), con qué habilidad, cuánto y si fue con el tiempo (un veneno). Al morir, PlayerController arma el
    // resumen con lo de los últimos segundos. Solo en el servidor.
    //
    // QUÉ HABILIDAD: DamageContext es la habilidad que está aplicando efectos EN ESTE MOMENTO (la pone
    // GameplayAbility al aplicar los suyos, también los de sus proyectiles). Un efecto con duración la guarda
    // (ActiveGameplayEffect.SourceAbility) para sus ticks. Lo que no pasa por una habilidad queda sin ella.
    public static GameplayAbility DamageContext;

    public struct DamageTakenRecord
    {
        public float Time;
        public AbilitySystemComponent Attacker;   // null = el entorno (una zona sin autor)
        public GameplayAbility Ability;           // null = no se sabe (un golpe suelto, una zona)
        public float Amount;
        public bool OverTime;
    }

    private readonly List<DamageTakenRecord> _damageTaken = new List<DamageTakenRecord>();
    private const float DamageTakenKeptSeconds = 15f;

    private void RecordDamageTaken(AbilitySystemComponent attacker, float amount, bool overTime)
    {
        float now = Time.time;
        _damageTaken.RemoveAll(r => now - r.Time > DamageTakenKeptSeconds);
        if (_damageTaken.Count >= 128) _damageTaken.RemoveAt(0);
        _damageTaken.Add(new DamageTakenRecord
        {
            Time = now, Attacker = attacker, Ability = DamageContext, Amount = amount, OverTime = overTime,
        });
    }

    // Lo recibido en los últimos 'seconds' segundos, del más viejo al más nuevo.
    public void CollectRecentDamage(float seconds, List<DamageTakenRecord> into)
    {
        float now = Time.time;
        foreach (DamageTakenRecord r in _damageTaken)
            if (now - r.Time <= seconds) into.Add(r);
    }

    public void ClearDamageTaken() => _damageTaken.Clear();

    // =========================================================
    // PARA EL DEPURADOR EN VIVO (Mercenarios ▸ GAS ▸ Depurador en vivo, 17 de noviembre de 2026)
    // =========================================================
    //
    // Solo lectura: la ventana mira atributos y tags sin poder cambiarlos. Y, mientras está abierta (TraceDamage), cada
    // golpe que recibe un personaje deja anotada su CADENA: el número base, con crítico y bonos, lo que frenó un
    // bloqueo, lo que dejaron las defensas (vulnerabilidad, resistencia, armadura), lo que se comió el escudo y lo
    // que llegó a la vida. Con la ventana cerrada no se anota nada: en el juego no cuesta.
    public static bool TraceDamage;

    public IReadOnlyDictionary<EAttributeType, AttributeValue> DebugAttributes => Attributes;
    public IReadOnlyDictionary<EGameplayTag, int> DebugTags => GameplayTags;

    public struct DamageTrace
    {
        public float Time;
        public string Attacker, Ability, Effect;
        public bool OverTime, Critical, Magic;
        public float Base;          // el número del efecto, antes de crítico y bonos del atacante
        public float Outgoing;      // con crítico y bonos del atacante
        public float AfterBlock;    // después de un bloqueo direccional (escudo de frente)
        public float AfterDefenses; // después de vulnerabilidad, resistencia, redondeo y armadura
        public float Shield;        // lo que se comió el escudo
        public float ToHealth;      // lo que llegó a la vida
        public float Vulnerability, Resistance, Armor;   // los del que recibió, en ese momento
    }

    public readonly List<DamageTrace> DebugDamageTraces = new List<DamageTrace>();
    private const int MaxTraces = 12;

    private void RecordTrace(GameplayEffect effect, AbilitySystemComponent source, bool overTime, bool critical, bool magic,
                             float baseDamage, float outgoing, float afterBlock, float afterDefenses, float shield, float toHealth)
    {
        if (DebugDamageTraces.Count >= MaxTraces) DebugDamageTraces.RemoveAt(0);
        DebugDamageTraces.Add(new DamageTrace
        {
            Time = Time.time,
            Attacker = source == null ? "" : source.gameObject.name,
            Ability = DamageContext != null ? DamageContext.DisplayName : "",
            Effect = effect != null ? effect.name : "",
            OverTime = overTime, Critical = critical, Magic = magic,
            Base = baseDamage, Outgoing = outgoing, AfterBlock = afterBlock, AfterDefenses = afterDefenses,
            Shield = shield, ToHealth = toHealth,
            Vulnerability = GetAttributeValue(EAttributeType.Vulnerability),
            Resistance = GetAttributeValue(EAttributeType.Resistance),
            Armor = GetAttributeValue(EAttributeType.Def),
        });
    }

    // =========================================================
    // LA MEDICIÓN DEL BALANCE (Mercenarios ▸ GAS ▸ Medir el balance, 18 de noviembre de 2026)
    // =========================================================
    //
    // Para responder "¿la armadura funciona?" y "¿de dónde sale tanta curación?" con números de una partida
    // de verdad: mientras está prendida (TraceBalance), cada personaje suma lo que recibe (crudo, después de
    // las defensas, lo que se comió el escudo y lo que llegó a la vida) y cada punto de vida que GANA, con
    // quién lo causó. La curación se mide donde se escribe la vida (SetCurrentAttributeValue), así cuenta
    // TODO, también lo que escribe la vida directo (la sala, el aura del Paladín, las pasivas): el nombre lo
    // da la habilidad en curso o, si no hay, el método que la escribió. Apagada no cuesta nada. Solo servidor.
    public static bool TraceBalance;

    public class BalanceTally
    {
        public int   Hits;
        public float Raw;            // lo que traía el golpe, con crítico y bonos del atacante
        public float AfterDefenses;  // después de bloqueo, vulnerabilidad, resistencia y armadura
        public float Shield;         // lo que se comió el escudo
        public float ToHealth;       // lo que llegó a la vida
        public float Dealt;          // lo que ESTE le hizo a otros (a la vida y al escudo)
        public float Healed;         // vida ganada, total
        public readonly Dictionary<string, float> HealedBy = new Dictionary<string, float>();
        public float StartedAt;
        // Cómo era al último registro (el personaje puede ya no existir al pedir el informe: fin de partida).
        public string Name, ClassName;
        public int    Team;
        public float  MaxHealth, Armor, Resistance;
    }

    public static readonly Dictionary<AbilitySystemComponent, BalanceTally> BalanceTallies =
        new Dictionary<AbilitySystemComponent, BalanceTally>();

    private BalanceTally Tally()
    {
        if (!BalanceTallies.TryGetValue(this, out BalanceTally t))
            BalanceTallies[this] = t = new BalanceTally { StartedAt = Time.time };
        t.Name       = gameObject.name;
        t.ClassName  = CurrentClass != null ? CurrentClass.ClassName : "-";
        t.Team       = TeamID;
        t.MaxHealth  = GetAttributeValue(EAttributeType.MaxHealth);
        t.Armor      = GetAttributeValue(EAttributeType.Def);
        t.Resistance = GetAttributeValue(EAttributeType.Resistance);
        return t;
    }

    private void RecordBalanceHit(AbilitySystemComponent source, float raw, float afterDefenses, float shield, float toHealth)
    {
        BalanceTally t = Tally();
        t.Hits++;
        t.Raw += raw;
        t.AfterDefenses += afterDefenses;
        t.Shield += shield;
        t.ToHealth += toHealth;
        if (source != null && !ReferenceEquals(source, this)) CreditOf(source).Tally().Dealt += shield + toHealth;
    }

    private void RecordBalanceHeal(float amount)
    {
        if (amount <= 0f) return;
        BalanceTally t = Tally();
        t.Healed += amount;
        string by = HealSourceName();
        t.HealedBy.TryGetValue(by, out float sum);
        t.HealedBy[by] = sum + amount;
    }

    // Quién escribió la vida, mirando la pila: el robo de vida; un efecto (con la habilidad que lo puso); o el
    // método de afuera del ASC que la escribió directo (el aura del Paladín, la sala, una pasiva), con la habilidad
    // que lo provocó si había una en curso. "El aura del Paladín por el Castigo" es su curación al pegar.
    private static GameplayEffect BalanceEffect;

    private static string HealSourceName()
    {
        string by = DamageContext != null ? $" (por {DamageContext.DisplayName})" : "";
        var stack = new System.Diagnostics.StackTrace(2, false);
        bool lifeSteal = false, instant = false;
        for (int i = 0; i < stack.FrameCount; i++)
        {
            System.Reflection.MethodBase m = stack.GetFrame(i).GetMethod();
            Type type = m?.DeclaringType;
            if (type == null) continue;
            if (type == typeof(AbilitySystemComponent))
            {
                if (m.Name == nameof(HandleLifeSteal)) lifeSteal = true;
                if (m.Name == nameof(ExecuteInstantEffect)) instant = true;
                continue;
            }
            if (lifeSteal) return "Robo de vida" + by;
            if (instant || typeof(GameplayAbility).IsAssignableFrom(type))
                return (BalanceEffect != null ? BalanceEffect.name : "efecto") + by;
            // Las lambdas y corrutinas quedan en clases anidadas (<>c, <Rutina>d__): el nombre es el de la de afuera.
            while (type.DeclaringType != null && type.Name.Contains("<")) type = type.DeclaringType;
            return $"{type.Name}.{m.Name}{by}";
        }
        if (lifeSteal) return "Robo de vida" + by;
        return (instant && BalanceEffect != null ? BalanceEffect.name + " (con el tiempo)" : "?") + by;
    }
}
