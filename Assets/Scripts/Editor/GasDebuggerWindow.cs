using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// ============================================================
// GasDebuggerWindow — el DEPURADOR EN VIVO del GAS (17 de noviembre de 2026, primera de las "Mejoras del GAS")
//
// Mercenarios ▸ GAS ▸ Depurador en vivo. Con el juego corriendo, elige un personaje (lo seleccionado en la jerarquía,
// el jugador local o cualquiera de la lista) y muestra, refrescando solo:
//   · Atributos: el valor base, el actual y los modificadores que lo cambian (suma y multiplicación).
//   · Tags: cuáles tiene y cuántas veces (un tag con dos fuentes cuenta 2).
//   · Efectos activos: tipo, tiempo que les queda, acumulaciones, el próximo tick, quién los puso y con qué habilidad.
//     Los cooldowns son efectos ocultos: se ven con la casilla "Mostrar ocultos".
//   · Habilidades: las otorgadas, con su cooldown.
//   · Últimos golpes recibidos: la CADENA de cada uno ("45 → crítico 68 → defensas 61 → escudo −20 → vida −41"). La
//     anota el ASC solo mientras esta ventana está abierta (AbilitySystemComponent.TraceDamage).
//
// Es solo para mirar: no cambia nada del juego. Los efectos y los golpes viven en el SERVIDOR: con el editor de host
// se ve todo; si el editor es un cliente, solo lo que se sincroniza (avisa arriba).
// ============================================================
public class GasDebuggerWindow : EditorWindow
{
    private enum ETargetMode { Selection, LocalPlayer, Pick }

    private ETargetMode _mode = ETargetMode.Selection;
    private AbilitySystemComponent _picked;
    private Vector2 _scroll;
    private bool _showHidden;
    private bool _foldAttributes = true, _foldTags = true, _foldEffects = true, _foldAbilities = true, _foldHits = true;
    private string _filter = "";

    [MenuItem("Mercenarios/GAS/Depurador en vivo")]
    public static void Open()
    {
        var w = GetWindow<GasDebuggerWindow>("Depurador del GAS");
        w.minSize = new Vector2(380f, 300f);
    }

    private void OnEnable()  => AbilitySystemComponent.TraceDamage = true;
    private void OnDisable() => AbilitySystemComponent.TraceDamage = false;

    // Se refresca solo mientras corre el juego (10 veces por segundo).
    private void OnInspectorUpdate() { if (Application.isPlaying) Repaint(); }

    private void OnSelectionChange() { if (_mode == ETargetMode.Selection) Repaint(); }

    // =========================================================
    // A QUIÉN SE MIRA
    // =========================================================

    private AbilitySystemComponent Target()
    {
        switch (_mode)
        {
            case ETargetMode.LocalPlayer:
                return PlayerController.LocalPlayer != null ? PlayerController.LocalPlayer.GetComponent<AbilitySystemComponent>() : null;
            case ETargetMode.Pick:
                return _picked;
            default:
                GameObject go = Selection.activeGameObject;
                return go != null ? go.GetComponentInParent<AbilitySystemComponent>() : null;
        }
    }

    private static string NameOf(AbilitySystemComponent asc)
    {
        if (asc == null) return "—";
        NetworkAbilitySystemComponent net = asc.GetComponent<NetworkAbilitySystemComponent>();
        string player = net != null ? net.PlayerName : "";
        return string.IsNullOrEmpty(player) ? asc.gameObject.name : $"{player} ({asc.gameObject.name})";
    }

    // =========================================================
    // DIBUJO
    // =========================================================

    private void OnGUI()
    {
        DrawToolbar();

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Entra a Play para ver un personaje en vivo. Mientras esta ventana está abierta, cada golpe " +
                                    "queda anotado con su cadena de daño.", MessageType.Info);
            return;
        }

        AbilitySystemComponent asc = Target();
        if (asc == null)
        {
            EditorGUILayout.HelpBox(_mode == ETargetMode.Selection
                ? "Selecciona en la jerarquía un personaje (o algo que cuelgue de él) que tenga AbilitySystemComponent."
                : "No hay personaje para mirar.", MessageType.Info);
            return;
        }

        DrawHeader(asc);
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        _foldAttributes = Section("Atributos", _foldAttributes, () => DrawAttributes(asc));
        _foldTags       = Section("Tags", _foldTags, () => DrawTags(asc));
        _foldEffects    = Section("Efectos activos", _foldEffects, () => DrawEffects(asc));
        _foldAbilities  = Section("Habilidades y cooldowns", _foldAbilities, () => DrawAbilities(asc));
        _foldHits       = Section("Últimos golpes recibidos", _foldHits, () => DrawHits(asc));
        EditorGUILayout.EndScrollView();
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            _mode = (ETargetMode)GUILayout.Toolbar((int)_mode, new[] { "Selección", "Jugador local", "Elegir" }, EditorStyles.toolbarButton, GUILayout.Width(260f));
            GUILayout.FlexibleSpace();
            _showHidden = GUILayout.Toggle(_showHidden, "Mostrar ocultos", EditorStyles.toolbarButton);
        }

        if (_mode == ETargetMode.Pick && Application.isPlaying)
        {
            var all = FindObjectsByType<AbilitySystemComponent>(FindObjectsSortMode.None)
                      .Where(a => a != null).OrderBy(a => a.TeamID).ThenBy(a => a.gameObject.name).ToList();
            string[] labels = all.Select(a => $"[{a.TeamID}] {NameOf(a)}").ToArray();
            int index = Mathf.Max(0, all.IndexOf(_picked));
            int chosen = EditorGUILayout.Popup("Personaje", index, labels);
            _picked = chosen >= 0 && chosen < all.Count ? all[chosen] : null;
        }
    }

    private void DrawHeader(AbilitySystemComponent asc)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(NameOf(asc), EditorStyles.boldLabel);
            if (GUILayout.Button("Seleccionar", GUILayout.Width(90f))) { Selection.activeGameObject = asc.gameObject; EditorGUIUtility.PingObject(asc.gameObject); }
        }
        EditorGUILayout.LabelField($"Equipo {asc.TeamID}" + (asc.HasTag(EGameplayTag.State_Dead) ? "   ·   MUERTO" : ""));

        NetworkAbilitySystemComponent net = asc.GetComponent<NetworkAbilitySystemComponent>();
        if (net != null && !net.IsServerInitialized)
            EditorGUILayout.HelpBox("Este editor es un CLIENTE: los efectos activos, los cooldowns y los golpes solo existen en el " +
                                    "servidor. Aquí se ve lo sincronizado. Para verlo todo, crea la partida desde el editor.", MessageType.Warning);

        _filter = EditorGUILayout.TextField("Filtrar", _filter);
    }

    private static bool Section(string title, bool open, System.Action body)
    {
        open = EditorGUILayout.BeginFoldoutHeaderGroup(open, title);
        if (open)
        {
            EditorGUI.indentLevel++;
            body();
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4f);
        }
        EditorGUILayout.EndFoldoutHeaderGroup();
        return open;
    }

    private bool Passes(string text) => string.IsNullOrEmpty(_filter) || text.IndexOf(_filter, System.StringComparison.OrdinalIgnoreCase) >= 0;

    // --- Atributos ---

    private void DrawAttributes(AbilitySystemComponent asc)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Atributo", EditorStyles.miniBoldLabel, GUILayout.Width(170f));
            EditorGUILayout.LabelField("Base", EditorStyles.miniBoldLabel, GUILayout.Width(60f));
            EditorGUILayout.LabelField("Actual", EditorStyles.miniBoldLabel, GUILayout.Width(60f));
            EditorGUILayout.LabelField("Modificadores", EditorStyles.miniBoldLabel);
        }
        foreach (var kv in asc.DebugAttributes.OrderBy(k => (int)k.Key))
        {
            string name = ObjectNames.NicifyVariableName(kv.Key.ToString());
            if (!Passes(name)) continue;
            AttributeValue v = kv.Value;
            bool changed = !Mathf.Approximately(v.BaseValue, v.CurrentValue);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(name, GUILayout.Width(170f));
                EditorGUILayout.LabelField(Num(v.BaseValue), GUILayout.Width(60f));
                GUIStyle style = changed ? EditorStyles.boldLabel : EditorStyles.label;
                EditorGUILayout.LabelField(Num(v.CurrentValue), style, GUILayout.Width(60f));
                string mods = "";
                if (!Mathf.Approximately(v.AdditiveModifier, 0f)) mods += (v.AdditiveModifier > 0 ? "+" : "") + Num(v.AdditiveModifier) + "  ";
                if (!Mathf.Approximately(v.MultiplicativeModifier, 1f) && !Mathf.Approximately(v.MultiplicativeModifier, 0f)) mods += "×" + Num(v.MultiplicativeModifier);
                EditorGUILayout.LabelField(mods, EditorStyles.miniLabel);
            }
        }
    }

    // --- Tags ---

    private void DrawTags(AbilitySystemComponent asc)
    {
        var tags = asc.DebugTags.Where(k => k.Value > 0 && Passes(k.Key.ToString())).OrderBy(k => k.Key.ToString()).ToList();
        if (tags.Count == 0) EditorGUILayout.LabelField("(ninguno)", EditorStyles.miniLabel);
        foreach (var kv in tags)
            EditorGUILayout.LabelField(kv.Key + (kv.Value > 1 ? $"   ×{kv.Value}" : ""));

        // Inmunidades: los efectos que dan estos tags no entran (GE ▸ "Mientras dura, inmune a los que dan").
        var immune = asc.DebugImmunities.Where(k => k.Value > 0).Select(k => k.Key.ToString()).OrderBy(n => n).ToList();
        if (immune.Count > 0) EditorGUILayout.LabelField("Inmune a lo que da: " + string.Join(", ", immune), EditorStyles.wordWrappedMiniLabel);
    }

    // --- Efectos activos ---

    private void DrawEffects(AbilitySystemComponent asc)
    {
        List<ActiveGameplayEffect> effects = asc.GetActiveEffects();
        if (effects == null || effects.Count == 0) { EditorGUILayout.LabelField("(ninguno)", EditorStyles.miniLabel); return; }

        // Agrupados por efecto: las acumulaciones son instancias del mismo GE.
        foreach (var group in effects.Where(e => e != null && e.Definition != null).GroupBy(e => e.Definition))
        {
            GameplayEffect ge = group.Key;
            if (ge.EffectType == GameplayEffect.EEffectType.Hidden && !_showHidden) continue;
            if (!Passes(ge.name)) continue;
            ActiveGameplayEffect first = group.OrderByDescending(e => e.DurationRemaining).First();
            int stacks = group.Count();

            string kind = ge.EffectType == GameplayEffect.EEffectType.Buff ? "buff" : ge.EffectType == GameplayEffect.EEffectType.Debuff ? "debuff" : "oculto";
            string source = first.Source is AbilitySystemComponent src ? NameOf(src) : first.Source is Object o && o != null ? o.name : "";
            string ability = first.SourceAbility != null ? first.SourceAbility.DisplayName : "";

            EditorGUILayout.LabelField($"{ge.name}   ({kind}{(stacks > 1 ? $", ×{stacks}" : "")})", EditorStyles.boldLabel);
            Rect r = EditorGUILayout.GetControlRect(false, 16f);
            r = EditorGUI.IndentedRect(r);
            if (first.TotalDuration > 0f)
                EditorGUI.ProgressBar(r, Mathf.Clamp01(first.DurationRemaining / first.TotalDuration), $"{first.DurationRemaining:0.0} / {first.TotalDuration:0.0} s");
            else
                EditorGUI.LabelField(r, first.IsInfinite ? "para siempre (hasta que lo quiten)" : "sin duración (hasta que se quite)");
            string details = "";
            if (ge.Period > 0f) details += $"tick cada {ge.Period:0.##} s (próximo en {first.PeriodRemaining:0.0})   ";
            if (!string.IsNullOrEmpty(source)) details += $"de {source}   ";
            if (!string.IsNullOrEmpty(ability)) details += $"con {ability}   ";
            if (ge.RemoveOn != GameplayEffect.ERemoveOnEvent.Nothing) details += $"se va solo: {ge.RemoveOn}";
            if (details.Length > 0) EditorGUILayout.LabelField(details, EditorStyles.miniLabel);
        }
    }

    // --- Habilidades ---

    private void DrawAbilities(AbilitySystemComponent asc)
    {
        if (asc.GrantedAbilities == null || asc.GrantedAbilities.Count == 0) { EditorGUILayout.LabelField("(ninguna)", EditorStyles.miniLabel); return; }
        foreach (GameplayAbility ga in asc.GrantedAbilities)
        {
            if (ga == null) continue;
            string label = $"{ga.DisplayName}   ({(ga.SourceTemplate != null ? ga.SourceTemplate.name : ga.name)})";
            if (!Passes(label)) continue;
            Rect r = EditorGUILayout.GetControlRect(false, 16f);
            r = EditorGUI.IndentedRect(r);
            Rect labelRect = new Rect(r.x, r.y, r.width * 0.55f, r.height);
            Rect barRect = new Rect(r.x + r.width * 0.56f, r.y, r.width * 0.44f, r.height);
            EditorGUI.LabelField(labelRect, label);
            if (asc.GetCooldownStatus(ga, out float remaining, out float total) && total > 0f)
                EditorGUI.ProgressBar(barRect, 1f - Mathf.Clamp01(remaining / total), $"{remaining:0.0} s");
            else
                EditorGUI.LabelField(barRect, "lista", EditorStyles.miniLabel);
        }
    }

    // --- Últimos golpes ---

    private void DrawHits(AbilitySystemComponent asc)
    {
        List<AbilitySystemComponent.DamageTrace> hits = asc.DebugDamageTraces;
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(hits.Count == 0 ? "(ninguno desde que se abrió la ventana)" : $"{hits.Count} (el más nuevo arriba)", EditorStyles.miniLabel);
            if (hits.Count > 0 && GUILayout.Button("Limpiar", GUILayout.Width(70f))) hits.Clear();
        }
        for (int i = hits.Count - 1; i >= 0; i--)
        {
            AbilitySystemComponent.DamageTrace h = hits[i];
            string who = string.IsNullOrEmpty(h.Attacker) ? "sin autor" : h.Attacker;
            string with = !string.IsNullOrEmpty(h.Ability) ? h.Ability : h.Effect;
            string title = $"hace {Time.time - h.Time:0.0} s   ·   {with} de {who}" + (h.OverTime ? "  (tick)" : "") + (h.Magic ? "  [mágico]" : "  [físico]");
            if (!Passes(title)) continue;
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(Chain(h), EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField($"del que recibió: vulnerabilidad {Num(h.Vulnerability)}, resistencia {Num(h.Resistance)}, armadura {Num(h.Armor)}", EditorStyles.miniLabel);
        }
    }

    // "45 → crítico 68 → bloqueo 68 → defensas 61 → escudo −20 → vida −41"
    private static string Chain(AbilitySystemComponent.DamageTrace h)
    {
        var parts = new List<string> { Num(h.Base) };
        if (!Mathf.Approximately(h.Outgoing, h.Base)) parts.Add((h.Critical ? "crítico " : "bonos ") + Num(h.Outgoing));
        if (!Mathf.Approximately(h.AfterBlock, h.Outgoing)) parts.Add("bloqueo " + Num(h.AfterBlock));
        if (!Mathf.Approximately(h.AfterDefenses, h.AfterBlock)) parts.Add("defensas " + Num(h.AfterDefenses));
        if (h.Shield > 0f) parts.Add("escudo −" + Num(h.Shield));
        parts.Add("vida −" + Num(h.ToHealth));
        return string.Join("  →  ", parts);
    }

    private static string Num(float v) => Mathf.Approximately(v, Mathf.Round(v)) ? Mathf.RoundToInt(v).ToString() : v.ToString("0.##");
}
