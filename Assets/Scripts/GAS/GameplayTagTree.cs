using System.Collections.Generic;

// ============================================================
// GameplayTagTree — los TAGS JERÁRQUICOS (18 de noviembre de 2026, de las "Mejoras del GAS")
//
// EGameplayTag es una lista plana que se guarda por número: no se puede renombrar a "Status.CC.Stun". En vez de eso, los
// GRUPOS son tags más (Group_*, al final del enum) y esta tabla dice quién es hijo de quién. Un grupo puede tener grupos
// adentro (Group_HardControl está dentro de Group_Control).
//
// Dónde vale (todo lo que PREGUNTA por un tag):
//   · AbilitySystemComponent.HasTag / GetTagCount: preguntar por un grupo = por cualquiera de sus hijos.
//   · Lo que busca efectos por el tag que DAN: quitar por tag (RemoveEffectsWithTag, RemoveOneEffectWithTag, "Al
//     aplicarse, quita los que dan"), la inmunidad ("Mientras dura, inmune a los que dan"), ReduceCooldownByTag y
//     GameplayAbility.AnyEffectGrants. Todos pasan por GameplayTagTree.Matches.
// Nadie DA un grupo (el validador lo avisa): los efectos siguen dando las hojas, como siempre.
//
// Un hijo nuevo: una línea en Parents. Un grupo nuevo: un valor AL FINAL de EGameplayTag y sus líneas acá.
// ============================================================
public static class GameplayTagTree
{
    // hijo → padre
    private static readonly Dictionary<EGameplayTag, EGameplayTag> Parents = new Dictionary<EGameplayTag, EGameplayTag>
    {
        // Control
        { EGameplayTag.Group_HardControl,     EGameplayTag.Group_Control },
        { EGameplayTag.State_Stunned,         EGameplayTag.Group_HardControl },
        { EGameplayTag.State_Rooted,          EGameplayTag.Group_HardControl },
        { EGameplayTag.State_Silenced,        EGameplayTag.Group_HardControl },
        { EGameplayTag.State_Disarmed,        EGameplayTag.Group_Control },
        { EGameplayTag.Status_Slow,           EGameplayTag.Group_Control },
        { EGameplayTag.Status_Blinded,        EGameplayTag.Group_Control },

        // Daño con el tiempo
        { EGameplayTag.Status_Poison,         EGameplayTag.Group_DamageOverTime },
        { EGameplayTag.Status_Burning,        EGameplayTag.Group_DamageOverTime },
        { EGameplayTag.Status_Wound,          EGameplayTag.Group_DamageOverTime },

        // Posturas
        { EGameplayTag.Stance_Defensive,      EGameplayTag.Group_Stance },
        { EGameplayTag.Stance_Offensive,      EGameplayTag.Group_Stance },

        // Hechizo del Mago
        { EGameplayTag.Status_SpellFire,      EGameplayTag.Group_Spell },
        { EGameplayTag.Status_SpellIce,       EGameplayTag.Group_Spell },
        { EGameplayTag.Status_SpellEarth,     EGameplayTag.Group_Spell },
        { EGameplayTag.Status_SpellLightning, EGameplayTag.Group_Spell },

        // Compañero activo
        { EGameplayTag.Status_CompanionWolf,  EGameplayTag.Group_Companion },
        { EGameplayTag.Status_CompanionBear,  EGameplayTag.Group_Companion },
        { EGameplayTag.Status_CompanionOwl,   EGameplayTag.Group_Companion },

        // Buffs de los tótems
        { EGameplayTag.Status_Buff_Bear,      EGameplayTag.Group_TotemBuff },
        { EGameplayTag.Status_Buff_Wolf,      EGameplayTag.Group_TotemBuff },
        { EGameplayTag.Status_Buff_Eagle,     EGameplayTag.Group_TotemBuff },
        { EGameplayTag.Status_Buff_Tiger,     EGameplayTag.Group_TotemBuff },

        // Auras del Paladín
        { EGameplayTag.Status_Aura_Protection, EGameplayTag.Group_Aura },
        { EGameplayTag.Status_Aura_Devotion,   EGameplayTag.Group_Aura },
        { EGameplayTag.Status_Aura_Vengeance,  EGameplayTag.Group_Aura },
        { EGameplayTag.Status_Aura_Conqueror,  EGameplayTag.Group_Aura },

        // Cooldowns
        { EGameplayTag.Ability_Cooldown_Global,   EGameplayTag.Group_Cooldown },
        { EGameplayTag.Ability_Cooldown_Melee,    EGameplayTag.Group_Cooldown },
        { EGameplayTag.Ability_Cooldown_Ultimate, EGameplayTag.Group_Cooldown },
        { EGameplayTag.Ability_Cooldown_0,        EGameplayTag.Group_Cooldown },
        { EGameplayTag.Ability_Cooldown_Ranged,   EGameplayTag.Group_Cooldown },
        { EGameplayTag.Ability_Cooldown_Special,  EGameplayTag.Group_Cooldown },
        { EGameplayTag.Ability_Cooldown_Extra,    EGameplayTag.Group_Cooldown },
        { EGameplayTag.Ability_Cooldown_Movement, EGameplayTag.Group_Cooldown },
        { EGameplayTag.Totem_Cooldown_Bear,       EGameplayTag.Group_Cooldown },
        { EGameplayTag.Totem_Cooldown_Wolf,       EGameplayTag.Group_Cooldown },
        { EGameplayTag.Totem_Cooldown_Eagle,      EGameplayTag.Group_Cooldown },
        { EGameplayTag.Totem_Cooldown_Tiger,      EGameplayTag.Group_Cooldown },
        { EGameplayTag.Cooldown_CompanionWolf,    EGameplayTag.Group_Cooldown },
        { EGameplayTag.Cooldown_CompanionBear,    EGameplayTag.Group_Cooldown },
    };

    // Grupo → todas sus hojas y subgrupos (calculado una vez).
    private static Dictionary<EGameplayTag, List<EGameplayTag>> s_descendants;

    // ¿'tag' es 'query' o está adentro de él (a cualquier profundidad)?
    public static bool Matches(EGameplayTag tag, EGameplayTag query)
    {
        if (tag == query) return true;
        if (!IsGroup(query)) return false;
        EGameplayTag current = tag;
        for (int guard = 0; guard < 16 && Parents.TryGetValue(current, out EGameplayTag parent); guard++)
        {
            if (parent == query) return true;
            current = parent;
        }
        return false;
    }

    // ¿Alguno de 'tags' es 'query' o está adentro de él? (los tags que DA un efecto contra lo que se busca)
    public static bool AnyMatches(List<EGameplayTag> tags, EGameplayTag query)
    {
        if (tags == null || query == EGameplayTag.None) return false;
        for (int i = 0; i < tags.Count; i++)
            if (Matches(tags[i], query)) return true;
        return false;
    }

    // ¿Es un grupo (tiene hijos)?
    public static bool IsGroup(EGameplayTag tag) => Descendants.ContainsKey(tag);

    // Lo que hay adentro de un grupo (vacío si no es grupo).
    public static IReadOnlyList<EGameplayTag> DescendantsOf(EGameplayTag group) =>
        Descendants.TryGetValue(group, out List<EGameplayTag> list) ? list : (IReadOnlyList<EGameplayTag>)System.Array.Empty<EGameplayTag>();

    // El padre directo (None si no tiene).
    public static EGameplayTag ParentOf(EGameplayTag tag) => Parents.TryGetValue(tag, out EGameplayTag p) ? p : EGameplayTag.None;

    private static Dictionary<EGameplayTag, List<EGameplayTag>> Descendants
    {
        get
        {
            if (s_descendants != null) return s_descendants;
            var map = new Dictionary<EGameplayTag, List<EGameplayTag>>();
            foreach (KeyValuePair<EGameplayTag, EGameplayTag> kv in Parents)
            {
                EGameplayTag ancestor = kv.Value;
                for (int guard = 0; guard < 16 && ancestor != EGameplayTag.None; guard++)
                {
                    if (!map.TryGetValue(ancestor, out List<EGameplayTag> list)) map[ancestor] = list = new List<EGameplayTag>();
                    if (!list.Contains(kv.Key)) list.Add(kv.Key);
                    ancestor = Parents.TryGetValue(ancestor, out EGameplayTag up) ? up : EGameplayTag.None;
                }
            }
            return s_descendants = map;
        }
    }
}
