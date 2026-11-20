using System.Collections.Generic;
using UnityEngine;

// ============================================================
// AreaThreats  (las zonas de daño que se pueden esquivar — 20 de noviembre de 2026)
//
// Un jugador VE la zona de fuego en el piso, el círculo de la explosión que se está cargando o el veneno del zombi, y
// se sale. Un bot no ve VFX: lo que hace daño en un área tiene que AVISAR que existe. Las piezas que lo hacen lo anotan
// acá, en el servidor (que es donde piensan los bots):
//   · GA_InstantAoE con espera inicial: el círculo de lo que va a caer, mientras se carga (lo que se esquiva de verdad).
//   · GA_ContinuousAoE: la zona mientras dura (y, si tiene espera, desde que se anuncia).
//   · Summon_Zone que pega a los enemigos: mientras está prendida (las trampas, el Muro de fuego, el veneno).
// Lo que solo cura o ayuda a los suyos no se anota (GameplayAbility.HasEnemyHitEffects). Lo consulta BotController
// (ver BotController.Instinct: "SALIR DE LAS ZONAS").
// ============================================================

// Una amenaza que vive mientras su dueño exista y decida (una Summon_Zone): se anota con Register y dice sola si está
// prendida y a quién le pega.
public interface IAreaThreat
{
    // ¿'point' está adentro (con 'margin' de más) y le hace daño a 'who'? Si sí: hacia dónde salir (plano, normalizado;
    // cero si da igual) y cuánto caminar para quedar afuera.
    bool Contains(AbilitySystemComponent who, Vector3 point, float margin, out Vector3 escapeDir, out float exitDistance);
}

public static class AreaThreats
{
    // Un círculo con fecha de vencimiento (lo que anotan las habilidades): sigue a 'Follow' si lo tiene.
    private struct Timed
    {
        public int Id;
        public Vector3 Center;
        public Transform Follow;
        public float Radius;
        public AbilitySystemComponent Owner;
        public float Until;
    }

    // Diferencia de altura a partir de la cual una zona no cuenta (las catacumbas, debajo de la superficie).
    private const float MaxHeightGap = 2.5f;

    private static readonly List<Timed>       s_timed   = new List<Timed>();
    private static readonly List<IAreaThreat> s_sources = new List<IAreaThreat>();
    private static int s_nextId = 1;

    // Sin recarga de dominio (Enter Play Mode rápido) lo estático sobrevive entre partidas: se vacía al arrancar.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_timed.Clear();
        s_sources.Clear();
        s_nextId = 1;
    }

    // Anota un círculo de daño de 'owner' por 'seconds' (con 'follow', el centro va con ese transform: un área que sigue
    // al dueño). Devuelve con qué borrarlo antes (Remove) si se corta. 0 = no se anotó.
    public static int Add(Vector3 center, float radius, AbilitySystemComponent owner, float seconds, Transform follow = null)
    {
        if (radius <= 0f || seconds <= 0f) return 0;
        int id = s_nextId++;
        s_timed.Add(new Timed { Id = id, Center = center, Follow = follow, Radius = radius, Owner = owner,
                                Until = Time.time + seconds });
        return id;
    }

    public static void Remove(int id)
    {
        if (id == 0) return;
        for (int i = s_timed.Count - 1; i >= 0; i--)
            if (s_timed[i].Id == id) { s_timed.RemoveAt(i); return; }
    }

    public static void Register(IAreaThreat source)
    {
        if (source != null && !s_sources.Contains(source)) s_sources.Add(source);
    }

    public static void Unregister(IAreaThreat source) => s_sources.Remove(source);

    // ¿'point' está adentro de algo que le hace daño a 'who'? Devuelve la que más le cuesta dejar: hacia dónde salir
    // (plano; cero si da igual) y cuánto caminar. 'margin' agranda las zonas (salir con un paso de más).
    public static bool Find(Vector3 point, AbilitySystemComponent who, float margin,
                            out Vector3 escapeDir, out float exitDistance)
    {
        escapeDir = Vector3.zero;
        exitDistance = 0f;
        bool found = false;
        float now = Time.time;

        for (int i = s_timed.Count - 1; i >= 0; i--)
        {
            Timed t = s_timed[i];
            if (now > t.Until) { s_timed.RemoveAt(i); continue; }
            if (!Hostile(t.Owner, who)) continue;

            Vector3 center = t.Follow != null ? t.Follow.position : t.Center;
            if (Mathf.Abs(point.y - center.y) > MaxHeightGap) continue;

            Vector3 flat = point - center;
            flat.y = 0f;
            float dist = flat.magnitude;
            float exit = t.Radius + margin - dist;
            if (exit <= 0f || exit <= exitDistance) continue;

            found = true;
            exitDistance = exit;
            escapeDir = dist > 0.05f ? flat / dist : Vector3.zero;
        }

        for (int i = s_sources.Count - 1; i >= 0; i--)
        {
            IAreaThreat s = s_sources[i];
            if (s == null || (s is Object o && o == null)) { s_sources.RemoveAt(i); continue; }
            if (!s.Contains(who, point, margin, out Vector3 dir, out float exit) || exit <= exitDistance) continue;

            found = true;
            exitDistance = exit;
            escapeDir = dir;
        }

        return found;
    }

    // ¿'point' es peligroso para 'who'? (lo mismo, sin la salida)
    public static bool IsDangerous(Vector3 point, AbilitySystemComponent who, float margin = 0f)
        => Find(point, who, margin, out _, out _);

    // ¿Una zona de 'owner' le hace daño a 'who'? Sin dueño (salió de la partida): a cualquiera.
    private static bool Hostile(AbilitySystemComponent owner, AbilitySystemComponent who)
    {
        if (who == null) return false;
        if (owner == null) return true;
        if (ReferenceEquals(owner, who)) return false;
        return owner.IsEnemyOf(who);
    }
}
