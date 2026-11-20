using UnityEngine;

// ============================================================
// LinkTether  (la cuerda visible de un vínculo — 20 de noviembre de 2026)
//
// Une con su LineRenderer a quien tiene un efecto con vínculo (GameplayEffect "Vínculo (entre dos)") y a quien se lo
// puso, en todas las pantallas, mientras el vínculo dura. La pone y la saca NetworkAbilitySystemComponent (NetLinkPartner):
// el servidor sincroniza QUIÉN es el otro extremo, y cada pantalla la dibuja siguiendo a los dos cada frame.
//
// Va en el prefab de la cuerda (el "Cuerda" del GE, por ejemplo VFX_LinkTether): un LineRenderer en espacio de mundo. Si
// el prefab no la trae, se agrega sola. Se borra sola si uno de los dos extremos desaparece.
// ============================================================
[RequireComponent(typeof(LineRenderer))]
public class LinkTether : MonoBehaviour
{
    [Tooltip("A qué altura sobre los pies de cada uno se ata la cuerda.")]
    [Label("Altura (m)")]
    public float Height = 1.2f;

    [Tooltip("Cuántos puntos tiene la línea (más = la comba se ve más suave).")]
    [Label("Puntos")]
    [Min(2)] public int Points = 12;

    [Tooltip("Cuánto cuelga la cuerda en el medio, por cada metro de largo. 0 = recta.")]
    [Label("Comba (m por metro)")]
    public float Sag = 0.04f;

    private Transform    _from, _to;
    private LineRenderer _line;

    // Crea la cuerda de 'prefab' entre 'from' y 'to'.
    public static LinkTether Create(GameObject prefab, Transform from, Transform to)
    {
        if (prefab == null || from == null || to == null) return null;
        GameObject go = Instantiate(prefab);
        LinkTether tether = go.GetComponent<LinkTether>();
        if (tether == null) tether = go.AddComponent<LinkTether>();
        tether._from = from;
        tether._to   = to;
        tether.LateUpdate();
        return tether;
    }

    private void Awake()
    {
        _line = GetComponent<LineRenderer>();
        _line.useWorldSpace = true;
    }

    private void LateUpdate()
    {
        if (_from == null || _to == null) { Destroy(gameObject); return; }

        Vector3 a = _from.position + Vector3.up * Height;
        Vector3 b = _to.position + Vector3.up * Height;
        float sag = Vector3.Distance(a, b) * Sag;

        int n = Mathf.Max(2, Points);
        if (_line.positionCount != n) _line.positionCount = n;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)(n - 1);
            _line.SetPosition(i, Vector3.Lerp(a, b, t) + Vector3.down * (sag * 4f * t * (1f - t)));
        }
    }
}
