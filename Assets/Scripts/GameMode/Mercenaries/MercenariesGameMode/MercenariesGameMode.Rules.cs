using System;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

// ============================================================
// MercenariesGameMode — LAS CUENTAS (las reglas, sin red ni escena: las prueban las pruebas "Modo_…")
//
// Una parte de MercenariesGameMode (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameMode/Mercenaries/MercenariesGameMode.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class MercenariesGameMode
{

    // =========================================================
    // LAS CUENTAS (25 de noviembre de 2026)
    //
    // Las reglas del modo como funciones estáticas, sin red ni escena: así las prueban
    // las pruebas de editor ("Modo_…" en GasDamagePipelineTests). Lo de arriba solo las
    // llama con los números del Inspector. Un cambio de regla va ACÁ.
    // =========================================================

    // Cuánta experiencia pide subir DESDE 'level' al siguiente. Sin dato, 100 (y nunca
    // menos de 1, para que un 0 en el Inspector no suba todos los niveles de golpe).
    public static float XpNeededForLevel(int level, float[] xpPerLevel)
        => xpPerLevel != null && level >= 1 && level - 1 < xpPerLevel.Length ? Mathf.Max(1f, xpPerLevel[level - 1]) : 100f;

    // La experiencia total para llegar al nivel máximo (la suma de los saltos).
    public static float XpToMaxLevel(float[] xpPerLevel, int maxLevel)
    {
        float total = 0f;
        if (xpPerLevel != null)
            for (int i = 0; i < xpPerLevel.Length && i < maxLevel - 1; i++) total += xpPerLevel[i];
        return Mathf.Max(1f, total);
    }

    // La experiencia pasiva por segundo: la que lleva al nivel máximo en 'minutes' minutos
    // (nunca en menos de 30 s), salvo que el Inspector ponga una fija.
    public static float PassiveXpRate(float[] xpPerLevel, int maxLevel, float minutes, float overridePerSecond)
    {
        if (overridePerSecond > 0f) return overridePerSecond;
        return XpToMaxLevel(xpPerLevel, maxLevel) / Mathf.Max(30f, minutes * 60f);
    }

    // El nivel que corresponde a la experiencia acumulada de un equipo.
    public static int LevelFromXp(float xp, float[] xpPerLevel, int maxLevel)
    {
        int level = 1;
        float remaining = xp;
        if (xpPerLevel != null)
        {
            for (int i = 0; i < xpPerLevel.Length && level < maxLevel; i++)
            {
                float need = Mathf.Max(1f, xpPerLevel[i]);
                if (remaining < need) break;
                remaining -= need;
                level++;
            }
        }
        return Mathf.Min(level, maxLevel);
    }

    // El progreso 0..1 DENTRO del nivel (la barrita del HUD). En el máximo, llena.
    public static float XpProgressWithinLevel(float xp, int level, float[] xpPerLevel, int maxLevel)
    {
        if (level >= maxLevel) return 1f;

        float consumed = 0f;
        for (int i = 0; i < level - 1 && xpPerLevel != null && i < xpPerLevel.Length; i++)
            consumed += Mathf.Max(1f, xpPerLevel[i]);

        return Mathf.Clamp01((xp - consumed) / XpNeededForLevel(level, xpPerLevel));
    }

    // Lo que da una baja: un jugador, lo del modo; un NPC, su propia recompensa si trae
    // (un jefe vale más que un fantasma) o si no, lo del modo.
    public static float KillXp(bool victimIsPlayer, float npcReward, float perPlayer, float perNpc)
    {
        if (victimIsPlayer) return perPlayer;
        return npcReward > 0f ? npcReward : perNpc;
    }

    // ¿Esta entrega gana la partida?
    public static bool HasWon(int score, int pointsToWin) => score >= pointsToWin;

    // Quién va primero en puntos (equipo 1, 2 o 3), o 0 si los de arriba empatan.
    public static int LeaderOf(int[] scores)
    {
        int best = 0, bestScore = -1; bool tie = false;
        for (int i = 0; i < scores.Length; i++)
        {
            if (scores[i] > bestScore) { bestScore = scores[i]; best = i + 1; tie = false; }
            else if (scores[i] == bestScore) tie = true;
        }
        return tie ? 0 : best;
    }

    // Un paso del aviso de "equipo eliminado": avisa UNA vez cuando caen todos y se rearma
    // cuando alguien revive. Un equipo sin nadie no cuenta como eliminado.
    // 'wiped' guarda si ya se avisó; devuelve true si hay que avisar ahora.
    public static bool WipeStep(int members, int alive, ref bool wiped)
    {
        if (members == 0) { wiped = false; return false; }

        bool now = alive == 0;
        bool announce = now && !wiped;
        wiped = now;
        return announce;
    }
}
