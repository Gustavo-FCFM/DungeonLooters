using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// ============================================================
// GasValidatorWindow — el VALIDADOR DE ASSETS del GAS (17 de noviembre de 2026, segunda de las "Mejoras del GAS")
//
// Mercenarios ▸ GAS ▸ Revisar el GAS. Recorre TODAS las habilidades (GA), efectos (GE) y clases del proyecto y
// lista lo que está mal, en tres niveles:
//   · ERRORES (algo se rompe): un GA/GE fuera de los registros de red (su VFX se ve solo en el host y su índice no
//     viaja), registros con huecos o repetidos, efectos con ticks pero sin duración (nunca tickean), modificadores sin
//     atributo, clases sin stats o con un hueco en el kit.
//   · AVISOS (se ve o se comporta mal): habilidades de un kit que el aturdido no bloquea (la regla: State_Stunned en
//     todas las que se aprietan), buffs/debuffs visibles sin ícono, entradas de efecto vacías, nombres repetidos (el
//     caso de los dos GA_Fireball), textos sin español.
//   · SUGERENCIAS (reglas del proyecto, deducidas de cómo está hecha la habilidad; pueden tener excepciones): las de
//     movimiento sin State_Rooted, las que escalan con daño mágico sin State_Silenced, las que escalan con ataque sin
//     State_Disarmed.
// Un clic en una línea selecciona el asset. "Actualizar los registros" corre la herramienta de siempre.
// No cambia nada por su cuenta. Para la consola o el CLI: GasValidatorWindow.Report().
// ============================================================
public class GasValidatorWindow : EditorWindow
{
    public enum ESeverity { Error, Warning, Suggestion }

    public struct Issue
    {
        public ESeverity Severity;
        public string Rule;      // de qué se trata (agrupa)
        public string Message;   // qué pasa con este asset
        public Object Asset;
    }

    private List<Issue> _issues = new List<Issue>();
    private Vector2 _scroll;
    private readonly Dictionary<string, bool> _folds = new Dictionary<string, bool>();
    private bool _showSuggestions;
    private double _scannedAt;

    [MenuItem("Mercenarios/GAS/Revisar el GAS")]
    public static void Open()
    {
        var w = GetWindow<GasValidatorWindow>("Revisar el GAS");
        w.minSize = new Vector2(420f, 300f);
        w.Rescan();
    }

    private void Rescan()
    {
        _issues = Scan();
        _scannedAt = EditorApplication.timeSinceStartup;
    }

    // Resumen en texto (para la consola o el CLI).
    public static string Report()
    {
        List<Issue> issues = Scan();
        var sb = new StringBuilder($"[Revisar el GAS] {Count(issues, ESeverity.Error)} errores, {Count(issues, ESeverity.Warning)} avisos, " +
                                   $"{Count(issues, ESeverity.Suggestion)} sugerencias.\n");
        foreach (var group in issues.GroupBy(i => (i.Severity, i.Rule)).OrderBy(g => g.Key.Severity))
        {
            sb.AppendLine($"{Label(group.Key.Severity)} · {group.Key.Rule} ({group.Count()})");
            foreach (Issue i in group.Take(40)) sb.AppendLine("   " + i.Message);
            if (group.Count() > 40) sb.AppendLine($"   … y {group.Count() - 40} más");
        }
        return sb.ToString();
    }

    private static int Count(List<Issue> issues, ESeverity s) => issues.Count(i => i.Severity == s);
    private static string Label(ESeverity s) => s == ESeverity.Error ? "ERROR" : s == ESeverity.Warning ? "AVISO" : "SUGERENCIA";

    // =========================================================
    // LA REVISIÓN
    // =========================================================

    public static List<Issue> Scan()
    {
        var issues = new List<Issue>();
        void Add(ESeverity s, string rule, string msg, Object asset) => issues.Add(new Issue { Severity = s, Rule = rule, Message = msg, Asset = asset });

        List<GameplayAbility> abilities = LoadAll<GameplayAbility>();
        List<GameplayEffect> effects = LoadAll<GameplayEffect>();
        List<CharacterClassDefinition> classes = LoadAll<CharacterClassDefinition>();

        // --- Registros de red ---
        GameplayAbilityRegistry gaReg = LoadAll<GameplayAbilityRegistry>().FirstOrDefault(r => AssetDatabase.GetAssetPath(r).Contains("/Resources/"));
        GameplayEffectRegistry geReg = LoadAll<GameplayEffectRegistry>().FirstOrDefault(r => AssetDatabase.GetAssetPath(r).Contains("/Resources/"));
        const string notInRegistry = "Fuera del registro de red (su VFX se ve solo en el host; su índice no viaja)";
        if (gaReg == null) Add(ESeverity.Error, "Registros", "No hay GameplayAbilityRegistry en una carpeta Resources.", null);
        else
        {
            var set = new HashSet<GameplayAbility>(gaReg.Abilities.Where(a => a != null));
            foreach (GameplayAbility ga in abilities) if (!set.Contains(ga)) Add(ESeverity.Error, notInRegistry, ga.name, ga);
            CheckRegistryList(gaReg.Abilities, gaReg, "habilidades", Add);
        }
        if (geReg == null) Add(ESeverity.Error, "Registros", "No hay GameplayEffectRegistry en una carpeta Resources.", null);
        else
        {
            var set = new HashSet<GameplayEffect>(geReg.Effects.Where(e => e != null));
            foreach (GameplayEffect ge in effects) if (!set.Contains(ge)) Add(ESeverity.Error, notInRegistry, ge.name, ge);
            CheckRegistryList(geReg.Effects, geReg, "efectos", Add);
        }

        // --- Efectos ---
        var grantedByAnyone = new HashSet<EGameplayTag>(effects.Where(e => e.GrantedTags != null).SelectMany(e => e.GrantedTags));
        foreach (GameplayEffect ge in effects)
        {
            if (ge.Period > 0f && !ge.HasDuration)
                Add(ESeverity.Error, "Efecto con ticks pero sin duración (nunca tickea)", $"{ge.name}: cada {ge.Period} s, duración {ge.Duration}", ge);

            if (ge.Modifiers != null)
                for (int i = 0; i < ge.Modifiers.Count; i++)
                {
                    Modifier m = ge.Modifiers[i];
                    if (m == null) continue;
                    if (m.Attribute == EAttributeType.None)
                        Add(ESeverity.Error, "Modificador sin atributo", $"{ge.name}: modificador {i + 1}", ge);
                    else if (m.UseAttributeScaling && m.SourceAttribute == EAttributeType.None)
                    {
                        // Con coeficiente 0 no suma nada: la casilla quedó prendida sin uso. Con coeficiente, el escalado se pierde.
                        if (Mathf.Approximately(m.AttributeCoefficient, 0f))
                            Add(ESeverity.Warning, "Casilla \"escalar con atributo\" prendida sin atributo (no suma nada)", $"{ge.name}: modificador {i + 1} ({m.Attribute})", ge);
                        else
                            Add(ESeverity.Error, "Escala con un atributo, pero no dice cuál", $"{ge.name}: modificador {i + 1} ({m.Attribute})", ge);
                    }
                }

            // Quitar e inmunidad: un tag que ningún efecto da no quita ni bloquea nada; una inmunidad sin duración no dura.
            foreach (EGameplayTag tag in (ge.RemoveEffectsWithTags ?? new List<EGameplayTag>()).Concat(ge.GrantsImmunityTo ?? new List<EGameplayTag>()))
                if (tag != EGameplayTag.None && !grantedByAnyone.Contains(tag))
                    Add(ESeverity.Warning, "Quita o da inmunidad a un tag que ningún efecto da (no hace nada)", $"{ge.name}: {tag}", ge);
            if (ge.GrantsImmunityTo != null && ge.GrantsImmunityTo.Count > 0 && !ge.HasDuration)
                Add(ESeverity.Warning, "Da inmunidad pero es instantáneo (la inmunidad no dura)", ge.name, ge);

            // Encadenados ("Al terminar"): solo tienen sentido con duración; una entrada vacía no hace nada; uno que se
            // vuelve a poner a sí mismo al vencer no termina nunca (puede ser a propósito: un aura que se renueva).
            int chains = (ge.OnExpireEffects?.Count ?? 0) + (ge.OnRemovedEffects?.Count ?? 0);
            if (chains > 0 && !ge.HasDuration)
                Add(ESeverity.Warning, "Tiene encadenados pero es instantáneo (nunca vence ni se quita: no salen)", ge.name, ge);
            foreach (GameplayEffect next in (ge.OnExpireEffects ?? new List<GameplayEffect>()).Concat(ge.OnRemovedEffects ?? new List<GameplayEffect>()))
                if (next == null) { Add(ESeverity.Warning, "Entrada vacía en \"Al vencer\" / \"Si se lo quitan antes\"", ge.name, ge); break; }
            if (ge.RemoveOn != GameplayEffect.ERemoveOnEvent.Nothing && !ge.HasDuration)
                Add(ESeverity.Warning, "\"Se va solo\" en un efecto instantáneo (no queda nada que quitar)", ge.name, ge);
            if (ge.Infinite && ge.OnExpireEffects != null && ge.OnExpireEffects.Count > 0)
                Add(ESeverity.Warning, "Es infinito: su \"Al vencer\" no sale nunca", ge.name, ge);
            // Los que fingen ser infinitos con un número enorme (antes del 18 de noviembre de 2026 no había casilla).
            if (!ge.Infinite && ge.Duration >= 900f)
                Add(ESeverity.Suggestion, "Dura 15 minutos o más: ¿es \"Dura para siempre\"?", $"{ge.name}: {ge.Duration:0} s", ge);
            // Reacciones (eventos de juego): solo mientras dura; cada una con evento y algo que aplicar; "al otro" con un
            // evento que no tiene otro no hace nada.
            if (ge.Reactions != null && ge.Reactions.Count > 0)
            {
                if (!ge.HasDuration)
                    Add(ESeverity.Warning, "Tiene reacciones pero es instantáneo (las reacciones son mientras dura)", ge.name, ge);
                for (int i = 0; i < ge.Reactions.Count; i++)
                {
                    EffectReaction x = ge.Reactions[i];
                    if (x == null) continue;
                    if (x.When == EGameplayEvent.None || x.Effects == null || !x.Effects.Exists(e => e != null))
                        Add(ESeverity.Warning, "Reacción sin evento o sin efectos (no hace nada)", $"{ge.name}: reacción {i + 1}", ge);
                    else if (x.When == EGameplayEvent.AbilityUsed && x.ApplyTo == EReactionTarget.Other)
                        Add(ESeverity.Warning, "Reacción \"al usar una habilidad\" que aplica \"al otro\" (no hay otro)", $"{ge.name}: reacción {i + 1}", ge);
                }
            }
            if (ge.OnExpireEffects != null && ge.OnExpireEffects.Contains(ge))
                Add(ESeverity.Suggestion, "Al vencer se vuelve a poner a sí mismo: no termina nunca (¿a propósito?)", ge.name, ge);

            bool visible = ge.EffectType != GameplayEffect.EEffectType.Hidden && ge.HasDuration;
            if (visible && ge.Icon == null)
                Add(ESeverity.Warning, "Buff o debuff visible sin ícono (sale un cuadro vacío en la barra)", ge.name, ge);
        }

        // --- Habilidades ---
        // Las que se APRIETAN: las que están en el kit de una clase en un botón (no la pasiva).
        var kitAbilities = new HashSet<GameplayAbility>();
        // Solo las clases jugables (Assets/Attributes): las de la jam (48toPlay) no se juegan.
        foreach (CharacterClassDefinition c in classes.Where(c => AssetDatabase.GetAssetPath(c).StartsWith("Assets/Attributes/")))
            if (c.Abilities != null)
                foreach (var a in c.Abilities)
                    if (a.Ability != null && a.InputSlot != EAbilityInput.Passive && a.InputSlot != EAbilityInput.None) kitAbilities.Add(a.Ability);

        foreach (GameplayAbility ga in abilities)
        {
            if (ga.Effects != null)
                for (int i = 0; i < ga.Effects.Count; i++)
                    if (ga.Effects[i].Effect == null)
                        Add(ESeverity.Warning, "Entrada de efecto vacía en la lista Effects", $"{ga.name}: entrada {i + 1}", ga);

            if (!kitAbilities.Contains(ga)) continue;
            var blocked = ga.ActivationBlockedTags ?? new List<EGameplayTag>();

            if (!blocked.Contains(EGameplayTag.State_Stunned))
                Add(ESeverity.Warning, "Habilidad de un kit que el aturdido no bloquea (falta State_Stunned)", ga.name, ga);

            if (string.IsNullOrWhiteSpace(ga.AbilityNameEs) || string.IsNullOrWhiteSpace(ga.DescriptionEs))
                Add(ESeverity.Warning, "Habilidad de un kit sin nombre o descripción en español", ga.name, ga);

            // Sugerencias por cómo está hecha (pueden tener excepciones: el escudo no es de arma, etc.).
            if (IsMovement(ga) && !blocked.Contains(EGameplayTag.State_Rooted))
                Add(ESeverity.Suggestion, "De movimiento sin State_Rooted (el enraizado no la frena)", ga.name, ga);
            if (ScalesWith(ga, EAttributeType.MagicDamage) && !blocked.Contains(EGameplayTag.State_Silenced))
                Add(ESeverity.Suggestion, "Escala con daño mágico sin State_Silenced (el silencio no la frena)", ga.name, ga);
            if (ScalesWith(ga, EAttributeType.Attack) && !blocked.Contains(EGameplayTag.State_Disarmed) && !(ga is GA_ShieldBlock) && !IsMovement(ga))
                Add(ESeverity.Suggestion, "Escala con ataque sin State_Disarmed (el desarme no la frena)", ga.name, ga);
        }

        // --- Clases ---
        foreach (CharacterClassDefinition c in classes)
        {
            if (c.BaseAttributes == null) Add(ESeverity.Error, "Clase sin stats (ASDef)", c.name, c);
            if (c.Abilities != null)
                for (int i = 0; i < c.Abilities.Count; i++)
                    if (c.Abilities[i].Ability == null)
                        Add(ESeverity.Error, "Clase con un hueco en el kit (botón sin habilidad)", $"{c.name}: {c.Abilities[i].InputSlot}", c);
            if (c.ClassIcon == null) Add(ESeverity.Warning, "Clase sin ícono", c.name, c);
            if (string.IsNullOrWhiteSpace(c.ClassNameEs) || string.IsNullOrWhiteSpace(c.DescriptionEs))
                Add(ESeverity.Warning, "Clase sin nombre o descripción en español", c.name, c);
        }

        // --- Nombres repetidos (el caso de los dos GA_Fireball: una herramienta que busca por nombre agarra el que no es) ---
        foreach (var group in abilities.Cast<Object>().Concat(effects).GroupBy(o => o.name).Where(g => g.Count() > 1))
            foreach (Object o in group)
                Add(ESeverity.Warning, "Nombre repetido (una herramienta que busque por nombre puede agarrar otro)", AssetDatabase.GetAssetPath(o), o);

        return issues;
    }

    private static void CheckRegistryList<T>(List<T> list, Object registry, string what, System.Action<ESeverity, string, string, Object> add) where T : Object
    {
        int empty = list.Count(x => x == null);
        if (empty > 0) add(ESeverity.Error, "Registros", $"El registro de {what} tiene {empty} entradas vacías (corre la herramienta de los registros).", registry);
        foreach (var dup in list.Where(x => x != null).GroupBy(x => x).Where(g => g.Count() > 1))
            add(ESeverity.Error, "Registros", $"El registro de {what} tiene repetido {dup.Key.name}.", registry);
    }

    private static bool IsMovement(GameplayAbility ga) =>
        ga is ILeapAbility || ga is GA_Dash || ga is GA_Teleport || ga is GA_Flight || ga is GA_RushAttack;

    // ¿Algún efecto de daño de esta habilidad escala con ese atributo?
    private static bool ScalesWith(GameplayAbility ga, EAttributeType attribute)
    {
        if (ga.Effects == null) return false;
        foreach (var e in ga.Effects)
            if (e.Effect != null && e.Effect.Modifiers != null)
                foreach (Modifier m in e.Effect.Modifiers)
                    if (m != null && m.UseAttributeScaling && m.SourceAttribute == attribute && m.Attribute == EAttributeType.Health) return true;
        return false;
    }

    private static List<T> LoadAll<T>() where T : Object =>
        AssetDatabase.FindAssets("t:" + typeof(T).Name)
                     .Select(g => AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(g)))
                     .Where(x => x != null).Distinct().ToList();

    // =========================================================
    // LA VENTANA
    // =========================================================

    private void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("Revisar de nuevo", EditorStyles.toolbarButton, GUILayout.Width(120f))) Rescan();
            if (GUILayout.Button("Actualizar los registros", EditorStyles.toolbarButton, GUILayout.Width(150f)))
            {
                MercRegistryTools.RefreshNetworkRegistries();
                Rescan();
            }
            GUILayout.FlexibleSpace();
            _showSuggestions = GUILayout.Toggle(_showSuggestions, "Mostrar sugerencias", EditorStyles.toolbarButton);
        }

        int errors = Count(_issues, ESeverity.Error), warnings = Count(_issues, ESeverity.Warning), suggestions = Count(_issues, ESeverity.Suggestion);
        if (errors == 0 && warnings == 0)
            EditorGUILayout.HelpBox($"Sin errores ni avisos. ({suggestions} sugerencias)", MessageType.Info);
        else
            EditorGUILayout.HelpBox($"{errors} errores · {warnings} avisos · {suggestions} sugerencias", errors > 0 ? MessageType.Error : MessageType.Warning);

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        foreach (var group in _issues.GroupBy(i => (i.Severity, i.Rule)).OrderBy(g => g.Key.Severity).ThenBy(g => g.Key.Rule))
        {
            if (group.Key.Severity == ESeverity.Suggestion && !_showSuggestions) continue;
            string key = group.Key.Severity + group.Key.Rule;
            if (!_folds.ContainsKey(key)) _folds[key] = group.Key.Severity == ESeverity.Error;
            string icon = group.Key.Severity == ESeverity.Error ? "✖" : group.Key.Severity == ESeverity.Warning ? "▲" : "•";
            _folds[key] = EditorGUILayout.BeginFoldoutHeaderGroup(_folds[key], $"{icon} {group.Key.Rule}  ({group.Count()})");
            if (_folds[key])
                foreach (Issue i in group)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(16f);
                        if (GUILayout.Button(i.Message, EditorStyles.linkLabel) && i.Asset != null)
                        {
                            Selection.activeObject = i.Asset;
                            EditorGUIUtility.PingObject(i.Asset);
                        }
                    }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }
        EditorGUILayout.EndScrollView();
    }

    // Al volver a la ventana después de editar assets, que la lista no quede vieja.
    private void OnFocus()
    {
        if (EditorApplication.timeSinceStartup - _scannedAt > 2.0) Rescan();
    }
}
