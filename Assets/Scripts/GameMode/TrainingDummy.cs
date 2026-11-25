using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

// ============================================================
// TrainingDummy — muñeco de práctica para probar habilidades (17 de noviembre de 2026, de las "Mejoras del GAS")
//
// Vive en la escena Test_Abilities (y sirve en cualquier otra). Es un personaje con su ASC que NUNCA muere (queda
// en 1, Status_Immortal) y que, sobre la cabeza, dice:
//   · su vida y su escudo;
//   · el DAÑO POR SEGUNDO de los últimos segundos ("Ventana del DPS") y el total del combate con su duración;
//   · la CURACIÓN POR SEGUNDO si alguien lo cura (el muñeco aliado sirve para probar soportes);
//   · el último golpe con su cadena (habilidad, crítico, lo que se comió el escudo, lo que llegó a la vida);
//   · los efectos que tiene encima, con el tiempo que les queda y sus acumulaciones.
// Si pasa un rato sin recibir nada, se REINICIA: vuelve a su vida y su escudo, y empieza un combate nuevo (el
// resumen del anterior queda a la vista hasta el próximo golpe).
//
// Las variantes (blindado, resistente, con escudo, aliado herido) son el mismo prefab con otros números: armadura y
// resistencia se le suman con un efecto oculto propio al empezar.
//
// No es de red: lo calcula quien tiene la lógica (el host). En un cliente se ve, pero sin números.
// ============================================================
[RequireComponent(typeof(AbilitySystemComponent))]
public class TrainingDummy : MonoBehaviour
{
    [Header("Qué muñeco es")]
    [Label("Nombre")]
    public string Title = "Muñeco";

    [Tooltip("Armadura que se le suma (10 = la mitad del daño físico).")]
    [Label("Armadura extra")]
    public float Armor = 0f;

    [Tooltip("Resistencia que se le suma (0.5 = recibe la mitad de todo). Tope del juego: 0.9.")]
    [Label("Resistencia extra (0 a 0.9)")]
    [Range(0f, 0.9f)] public float Resistance = 0f;

    [Tooltip("Escudo con el que arranca y al que vuelve al reiniciarse. 0 = sin escudo.")]
    [Label("Escudo (vida extra)")]
    public float Shield = 0f;

    [Tooltip("Del equipo del jugador local: para probar curaciones y buffs a aliados. Si no, es enemigo de todos.")]
    [Label("Aliado del jugador")]
    public bool AllyOfLocalPlayer = false;

    [Tooltip("La vida a la que vuelve al reiniciarse, como fracción de la máxima. El aliado herido arranca a la mitad.")]
    [Label("Vida al reiniciarse (0 a 1)")]
    [Range(0.05f, 1f)] public float RestHealth = 1f;

    [Header("Números")]
    [Tooltip("Si pasa esto sin recibir daño ni curación, vuelve a su vida y empieza un combate nuevo.")]
    [Label("Se reinicia tras (s)")]
    public float ResetAfter = 4f;

    [Tooltip("El DPS cuenta lo de estos últimos segundos.")]
    [Label("Ventana del DPS (s)")]
    public float DpsWindow = 5f;

    [Header("Piezas")]
    [Tooltip("Lo que gira para mirar a la cámara (el cartel).")]
    [Label("Cartel")]
    public Transform Panel;

    [Label("Título")]
    public TMP_Text TitleText;

    [Label("Números")]
    public TMP_Text StatsText;

    [Label("Efectos")]
    public TMP_Text EffectsText;

    private const int EnemyOfEveryone = 4;   // el equipo de los monstruos: enemigo de los tres

    private AbilitySystemComponent _asc;
    private readonly List<(float time, float damage, float heal)> _samples = new List<(float, float, float)>();
    private float _combatStart = -1f, _lastActivity = -1f, _totalDamage, _totalHeal;
    private string _lastCombat = "";
    private float _lastHealth;
    private bool _resetting;
    private Camera _cam;
    private float _nextText;

    private static int _alive;   // mientras haya muñecos, el ASC anota la cadena de cada golpe

    private void Awake() => _asc = GetComponent<AbilitySystemComponent>();

    private void OnEnable()
    {
        _alive++;
        AbilitySystemComponent.TraceDamage = true;
    }

    private void OnDisable()
    {
        if (--_alive <= 0) { _alive = 0; AbilitySystemComponent.TraceDamage = false; }
    }

    private void Start()
    {
        _asc.TeamID = EnemyOfEveryone;
        _asc.OnDamageEndured += OnDamage;

        // Nunca muere (queda en 1) y lleva su armadura y resistencia: un efecto oculto propio, como cualquier otro.
        GameplayEffect setup = ScriptableObject.CreateInstance<GameplayEffect>();
        setup.name = "Muñeco (inmortal)";
        setup.EffectType = GameplayEffect.EEffectType.Hidden;
        setup.Infinite = true;
        setup.CannotBeRemoved = true;
        setup.GrantedTags = new List<EGameplayTag> { EGameplayTag.Status_Immortal };
        setup.Modifiers = new List<Modifier>();
        if (Armor != 0f) setup.Modifiers.Add(new Modifier { Attribute = EAttributeType.Def, Type = Modifier.EModificationType.Add, Magnitude = Armor });
        if (Resistance != 0f) setup.Modifiers.Add(new Modifier { Attribute = EAttributeType.Resistance, Type = Modifier.EModificationType.Add, Magnitude = Resistance });
        _asc.ApplyGameplayEffect(setup, _asc);

        ResetDummy();
        _lastCombat = "";
    }

    private void OnDestroy()
    {
        if (_asc != null) _asc.OnDamageEndured -= OnDamage;
    }

    // =========================================================
    // LO QUE RECIBE
    // =========================================================

    private void OnDamage(float amount)
    {
        if (amount <= 0f) return;
        StartCombatIfNeeded();
        _samples.Add((Time.time, amount, 0f));
        _totalDamage += amount;
        _lastActivity = Time.time;
    }

    private void StartCombatIfNeeded()
    {
        if (_combatStart >= 0f) return;
        _combatStart = Time.time;
        _totalDamage = _totalHeal = 0f;
        _samples.Clear();
    }

    private void Update()
    {
        if (_asc == null) return;

        // El aliado sigue al equipo del jugador local (por si cambia de equipo en la sala).
        if (AllyOfLocalPlayer && PlayerController.LocalPlayer != null)
        {
            var net = PlayerController.LocalPlayer.NetAbilitySystem;
            int team = net != null ? net.NetTeamID : PlayerController.LocalPlayer.AbilitySystem.TeamID;
            if (team > 0) _asc.TeamID = team;
        }

        // La curación: lo que sube la vida y no fue el reinicio.
        float health = _asc.GetAttributeValue(EAttributeType.Health);
        if (!_resetting && health > _lastHealth + 0.01f)
        {
            StartCombatIfNeeded();
            float healed = health - _lastHealth;
            _samples.Add((Time.time, 0f, healed));
            _totalHeal += healed;
            _lastActivity = Time.time;
        }
        _lastHealth = health;
        _resetting = false;

        // Se reinicia si pasó un rato sin nada.
        if (_combatStart >= 0f && Time.time - _lastActivity > ResetAfter)
        {
            _lastCombat = Summary("Último combate", _lastActivity - _combatStart);
            _combatStart = -1f;
            ResetDummy();
        }

        _samples.RemoveAll(s => Time.time - s.time > DpsWindow);
        if (Time.time >= _nextText) { _nextText = Time.time + 0.2f; WriteTexts(); }
    }

    private void ResetDummy()
    {
        float max = _asc.GetAttributeValue(EAttributeType.MaxHealth);
        _asc.SetCurrentAttributeValue(EAttributeType.Health, Mathf.Max(1f, max * RestHealth));
        _asc.SetCurrentAttributeValue(EAttributeType.Shield, Shield);
        _lastHealth = _asc.GetAttributeValue(EAttributeType.Health);
        _resetting = true;
        _asc.DebugDamageTraces.Clear();
    }

    // =========================================================
    // EL CARTEL
    // =========================================================

    private void LateUpdate()
    {
        if (Panel == null) return;
        if (_cam == null || !_cam.isActiveAndEnabled) _cam = Camera.main;
        if (_cam != null) Panel.rotation = Quaternion.LookRotation(Panel.position - _cam.transform.position);
    }

    private void WriteTexts()
    {
        if (TitleText != null) TitleText.text = Title + (AllyOfLocalPlayer ? "  <size=70%>(aliado)</size>" : "");

        if (StatsText != null)
        {
            var sb = new StringBuilder();
            float hp = _asc.GetAttributeValue(EAttributeType.Health), max = _asc.GetAttributeValue(EAttributeType.MaxHealth);
            float shield = _asc.GetAttributeValue(EAttributeType.Shield);
            sb.Append($"Vida {hp:0} / {max:0}");
            if (shield > 0f || Shield > 0f) sb.Append($"   Escudo {shield:0}");
            sb.AppendLine();

            if (_combatStart >= 0f)
            {
                float window = Mathf.Min(DpsWindow, Mathf.Max(0.5f, Time.time - _combatStart));
                float dps = 0f, hps = 0f;
                foreach (var s in _samples) { dps += s.damage; hps += s.heal; }
                sb.AppendLine($"<b>DPS {dps / window:0.0}</b>" + (hps > 0f ? $"   <color=#7CFC8A>HPS {hps / window:0.0}</color>" : ""));
                sb.AppendLine(Summary("Combate", Time.time - _combatStart));
            }
            else if (!string.IsNullOrEmpty(_lastCombat)) sb.AppendLine(_lastCombat);
            else sb.AppendLine("<color=#AAAAAA>Pégale para medir</color>");

            List<AbilitySystemComponent.DamageTrace> traces = _asc.DebugDamageTraces;
            if (traces.Count > 0)
            {
                AbilitySystemComponent.DamageTrace t = traces[traces.Count - 1];
                string with = !string.IsNullOrEmpty(t.Ability) ? t.Ability : t.Effect;
                sb.Append($"Último: {with} {t.Base:0}");
                if (t.Critical) sb.Append(" <color=#FFD54A>(crítico)</color>");
                if (!Mathf.Approximately(t.AfterDefenses, t.Outgoing)) sb.Append($" → defensas {t.AfterDefenses:0}");
                if (t.Shield > 0f) sb.Append($" → escudo −{t.Shield:0}");
                sb.Append($" → vida −{t.ToHealth:0}");
            }
            StatsText.text = sb.ToString();
        }

        if (EffectsText != null)
        {
            var groups = new Dictionary<GameplayEffect, (int stacks, float left)>();
            foreach (ActiveGameplayEffect e in _asc.GetActiveEffects())
            {
                if (e?.Definition == null || e.Definition.EffectType == GameplayEffect.EEffectType.Hidden) continue;
                groups.TryGetValue(e.Definition, out var g);
                groups[e.Definition] = (g.stacks + 1, Mathf.Max(g.left, e.DurationRemaining));
            }
            var sb = new StringBuilder();
            foreach (var kv in groups)
            {
                string color = kv.Key.EffectType == GameplayEffect.EEffectType.Buff ? "#7CFC8A" : "#FF7A7A";
                sb.AppendLine($"<color={color}>{kv.Key.name}</color> {(float.IsPositiveInfinity(kv.Value.left) ? "∞" : kv.Value.left.ToString("0.0") + " s")}" + (kv.Value.stacks > 1 ? $" ×{kv.Value.stacks}" : ""));
            }
            EffectsText.text = sb.ToString();
        }
    }

    private string Summary(string label, float seconds)
    {
        seconds = Mathf.Max(0.1f, seconds);
        string s = $"{label}: {_totalDamage:0} de daño en {seconds:0.0} s ({_totalDamage / seconds:0.0}/s)";
        if (_totalHeal > 0f) s += $", {_totalHeal:0} de curación";
        return s;
    }
}
