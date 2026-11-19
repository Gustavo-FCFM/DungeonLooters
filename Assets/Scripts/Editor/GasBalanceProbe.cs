using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// ============================================================
// GasBalanceProbe
//
// La medición del balance (18 de noviembre de 2026, tras la partida 3c3c3 de bots en la que "el que
// entregaba se curaba demasiado" y "la armadura parecía no hacer nada"). En Play:
//   1. Mercenarios ▸ GAS ▸ Medir el balance ▸ Empezar: borra lo anotado y prende la medición.
//   2. Jugar (o mirar a los bots) un rato.
//   3. Mercenarios ▸ GAS ▸ Medir el balance ▸ Informe: escribe en la consola, por personaje, cuánto
//      recibió (crudo → después de las defensas → escudo → vida), cuánto hizo y DE DÓNDE sacó cada
//      punto de vida que ganó. También lo deja en Logs/BalanceReport.txt.
// Lo anota el ASC (AbilitySystemComponent.TraceBalance, en su parte CombatLog). Desde el CLI:
// GasBalanceProbe.Start() y GasBalanceProbe.Report().
// ============================================================
public static class GasBalanceProbe
{
    [MenuItem("Mercenarios/GAS/Medir el balance/Empezar")]
    public static void StartMenu() => Debug.Log(Start());

    [MenuItem("Mercenarios/GAS/Medir el balance/Informe")]
    public static void ReportMenu() => Debug.Log(Report());

    [MenuItem("Mercenarios/GAS/Medir el balance/Terminar")]
    public static void StopMenu()
    {
        AbilitySystemComponent.TraceBalance = false;
        Debug.Log("[Balance] Medición apagada (lo anotado sigue ahí hasta el próximo Empezar).");
    }

    public static string Start()
    {
        AbilitySystemComponent.BalanceTallies.Clear();
        AbilitySystemComponent.TraceBalance = true;
        _startedAt = Time.time;
        return "[Balance] Midiendo desde ahora. Al terminar: Mercenarios ▸ GAS ▸ Medir el balance ▸ Informe.";
    }

    private static float _startedAt;

    public static string Report()
    {
        var sb = new StringBuilder();
        float seconds = Time.time - _startedAt;
        sb.AppendLine($"[Balance] Informe de {seconds:0} s de juego" +
                      (AbilitySystemComponent.TraceBalance ? "" : " (la medición está apagada)") + ":");

        // Lo de los NPCs (equipo 4) va aparte y sumado: son muchos y chicos.
        // Los datos de cada uno son los del último registro: el personaje puede ya no existir (fin de partida).
        var rows = AbilitySystemComponent.BalanceTallies.Values
            .Where(t => t.Team != 4 && t.ClassName != "-")
            .OrderBy(t => t.Team)
            .ThenByDescending(t => t.ToHealth + t.Healed);

        foreach (AbilitySystemComponent.BalanceTally t in rows)
        {
            if (t.Hits == 0 && t.Healed <= 0f && t.Dealt <= 0f) continue;

            float armor = t.Armor;
            float mitigated = t.Raw > 0f ? 1f - t.AfterDefenses / t.Raw : 0f;

            sb.AppendLine($"· {t.Name} [{t.ClassName}, equipo {t.Team}] vida máx {t.MaxHealth:0}, " +
                          $"armadura {armor:0.#} (frena {armor / (armor + 10f):P0} del físico), resistencia {t.Resistance:P0}");
            sb.AppendLine($"    recibió {t.Hits} golpes: {t.Raw:0} crudo → {t.AfterDefenses:0} tras defensas ({mitigated:P0} frenado) " +
                          $"→ {t.Shield:0} al escudo, {t.ToHealth:0} a la vida");
            sb.AppendLine($"    hizo {t.Dealt:0} de daño (vida + escudo)");
            if (t.Healed > 0f)
            {
                sb.AppendLine($"    se curó {t.Healed:0} ({(t.ToHealth > 0f ? t.Healed / t.ToHealth : 0f):P0} de lo que le llegó a la vida):");
                foreach (KeyValuePair<string, float> h in t.HealedBy.OrderByDescending(h => h.Value))
                    sb.AppendLine($"        {h.Value,6:0}  {h.Key}");
            }
        }

        // Las invocaciones (copias, tótems, compañeros: sin clase) también, sumadas.
        var summons = AbilitySystemComponent.BalanceTallies.Values.Where(t => t.Team != 4 && t.ClassName == "-").ToList();
        if (summons.Count > 0)
            sb.AppendLine($"· Invocaciones ({summons.Count}): recibieron {summons.Sum(t => t.ToHealth):0} a la vida, hicieron {summons.Sum(t => t.Dealt):0}, " +
                          $"se curaron {summons.Sum(t => t.Healed):0}");
        var npcs = AbilitySystemComponent.BalanceTallies.Values.Where(t => t.Team == 4).ToList();
        if (npcs.Count > 0)
            sb.AppendLine($"· NPCs ({npcs.Count}): recibieron {npcs.Sum(t => t.ToHealth):0} a la vida, hicieron {npcs.Sum(t => t.Dealt):0}, " +
                          $"se curaron {npcs.Sum(t => t.Healed):0}");

        string text = sb.ToString();
        try
        {
            System.IO.Directory.CreateDirectory("Logs");
            System.IO.File.WriteAllText("Logs/BalanceReport.txt", text);
        }
        catch (System.Exception e) { Debug.LogWarning($"[Balance] No pude guardar Logs/BalanceReport.txt: {e.Message}"); }
        return text;
    }
}
