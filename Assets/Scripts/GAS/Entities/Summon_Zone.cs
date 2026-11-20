using System.Collections.Generic;
using UnityEngine;

// ============================================================
// Summon_Zone  (la zona de una invocación — 19 de octubre de 2026)
//
// "A los que estén dentro de este radio, cada tanto, aplicarles estos efectos." Estaba repetida
// en el aura del tótem, en la zona de espinas de la trampa y en las áreas continuas; ahora es
// una pieza que se pone al lado de un Entity_Summon (o de una subclase) en el prefab:
//
//   · A quién: aliados (y el dueño) o enemigos; si se quiere, solo jugadores (sin NPCs).
//   · Autor de los efectos: el DUEÑO (escalan con sus stats y la baja es suya: el daño) o la
//     INVOCACIÓN (necesita su propio ASC: el empuje sale desde ella; el aura del tótem).
//   · Potenciada: si el dueño tiene un tag (el Frenesí del Chamán), otros efectos.
//   · Empieza prendida al aparecer, o la prende la invocación cuando quiere (Begin; la trampa
//     de espinas al explotar). Dura lo que la invocación, o sus propios segundos.
//   · El VFX de la zona sale en todas las pantallas mientras está prendida, del tamaño del radio
//     (con VFX_AreaVisual calza exacto). El "prendida" viaja en un SyncVar de la invocación.
//   · Forma: un círculo (el radio, a cualquier altura) o una CAJA apoyada en el piso, girada con
//     la invocación (el Muro de fuego del Mago: 10 m de lado a lado, 2 de largo y 2 de alto; el
//     que salta más alto no se quema). Con caja, el VFX va como hijo del prefab (las llamas de
//     cada casilla) y el de acá se deja vacío.
//   · Crece y avanza (19 de noviembre de 2026, de las "Mejoras del GAS"): el radio crece con el
//     tiempo hasta un tope, y la invocación entera avanza hacia adelante (un tornado, un muro de
//     fuego que avanza), sobre el piso, frenando en las paredes y, si se quiere, terminando ahí.
//     No agrega nada a la red: cada pantalla lo calcula igual desde que la zona se prendió (lo que
//     viaja es el "prendida"); las paredes y el piso son los mismos en todas.
//
// Una zona por invocación. Los efectos se aplican solo en el servidor.
// ============================================================
[RequireComponent(typeof(Entity_Summon))]
public class Summon_Zone : MonoBehaviour
{
    public enum EShape
    {
        [InspectorName("Círculo (el radio)")]           Sphere,
        [InspectorName("Caja (ancho × alto × largo)")]  Box,
    }

    public enum ETargets
    {
        [InspectorName("Aliados (y el dueño)")] Allies,
        [InspectorName("Enemigos")]             Enemies,
    }

    public enum EAuthor
    {
        [InspectorName("El dueño")]       Owner,
        [InspectorName("La invocación")]  Summon,
    }

    [Header("Zona")]
    [Tooltip("Se prende sola al aparecer la invocación. Apagado = la prende la invocación cuando " +
             "quiere (la trampa de espinas, al explotar).")]
    [Label("Empieza prendida")]
    public bool StartActive = true;

    [Tooltip("Cuánto dura prendida. 0 = lo que dure la invocación.")]
    [Label("Dura (s; 0 = lo que la invocación)")]
    public float Seconds = 0f;

    [Tooltip("Círculo: todo lo que esté a 'Radio' metros. Caja: un rectángulo apoyado en el piso, " +
             "girado como la invocación (de lado a lado × alto × hacia adelante).")]
    [Label("Forma")]
    public EShape Shape = EShape.Sphere;

    [Tooltip("Solo con forma Círculo.")]
    [Label("Radio (m)")]
    public float Radius = 5f;

    [Tooltip("Solo con forma Caja: de lado a lado (el eje X de la invocación).")]
    [Label("Caja: ancho de lado a lado (m)")]
    public float BoxWidth = 10f;

    [Tooltip("Solo con forma Caja: desde el piso hacia arriba. El que salta más alto no la toca.")]
    [Label("Caja: alto (m)")]
    public float BoxHeight = 2f;

    [Tooltip("Solo con forma Caja: hacia adelante (el eje Z de la invocación).")]
    [Label("Caja: largo hacia adelante (m)")]
    public float BoxDepth = 2f;

    [Tooltip("Cada cuánto aplica los efectos. El primero, en el acto.")]
    [Label("Cada cuánto (s)")]
    public float TickSeconds = 0.5f;

    [Label("A quién")]
    public ETargets Targets = ETargets.Allies;

    [Tooltip("Solo jugadores y bots: los NPCs no la sienten (las trampas).")]
    [Label("Solo jugadores (sin NPCs)")]
    public bool PlayersOnly = false;

    [Tooltip("Quién figura como autor de los efectos.\n\n" +
             "· El dueño: escalan con sus stats, la baja es suya y le carga la definitiva (el daño).\n" +
             "· La invocación: necesita su propio AbilitySystemComponent. Un empuje sale desde ella.")]
    [Label("Autor de los efectos")]
    public EAuthor Author = EAuthor.Owner;

    [Tooltip("Lo que recibe cada uno dentro del radio, en cada vez.")]
    [Label("Efectos")]
    public List<GameplayEffect> Effects = new List<GameplayEffect>();

    [Header("Potenciada")]
    [Tooltip("Si el DUEÑO tiene este tag, se aplican los efectos potenciados en vez de los normales " +
             "(el tótem con el Chamán enfurecido). None = nunca.")]
    [Label("Si el dueño tiene")]
    public EGameplayTag EmpowerTag = EGameplayTag.None;

    [Label("Efectos potenciados")]
    public List<GameplayEffect> EmpoweredEffects = new List<GameplayEffect>();

    [Header("Visual")]
    [Tooltip("En todas las pantallas mientras la zona está prendida, del tamaño del radio.")]
    [Label("VFX de la zona")]
    public GameObject VFX;

    [Tooltip("Si el VFX no trae VFX_AreaVisual (con él calza solo con el radio): su tamaño por " +
             "cada metro de radio.")]
    [Label("VFX: escala (×)")]
    public float VfxScale = 1f;

    [Label("Capa de personajes")]
    public LayerMask CharacterLayer = 1 << 7;

    [Header("Crece y avanza")]
    [Tooltip("Cuántos metros crece por segundo desde que se prende. Círculo: el radio (el VFX crece con ella si trae " +
             "VFX_AreaVisual). Caja: cada costado (el ancho crece el doble); el \"VFX de la zona\", si tiene, se estira " +
             "de lado a lado, pero los hijos del prefab (las llamas de cada casilla) no. 0 = no crece.")]
    [Label("Crece (m por segundo)")]
    public float GrowPerSecond = 0f;

    [Tooltip("Hasta dónde crece con el tiempo: el radio (Círculo) o el ancho de lado a lado (Caja).")]
    [Label("Tope (m: radio o ancho; 0 = sin tope)")]
    public float MaxRadius = 0f;

    [Tooltip("La invocación entera avanza hacia adelante (hacia donde mira: lejos del que la puso) " +
             "mientras la zona está prendida. 0 = se queda quieta.")]
    [Label("Avanza (m/s)")]
    public float MoveSpeed = 0f;

    [Tooltip("Cuánto recorre antes de frenar.")]
    [Label("Recorre hasta (m; 0 = sin tope)")]
    public float MoveDistance = 0f;

    [Tooltip("Frena al llegar a una pared (de las capas de pared y piso).")]
    [Label("Frena en las paredes")]
    public bool StopsAtWalls = true;

    [Tooltip("Al frenar (pared o recorrido) se termina la zona. Apagado = se queda prendida ahí.")]
    [Label("Se termina al frenar")]
    public bool EndsWhenStopped = false;

    [Tooltip("Las capas que cuentan como pared o piso (el mapa). Al avanzar sigue el piso: sube y baja " +
             "rampas.")]
    [Label("Capas de pared y piso")]
    public LayerMask GroundLayer = 1;

    private Entity_Summon _summon;
    private GameObject    _vfx;
    private float _vfxRadius;   // con qué radio (o ancho, con Caja) se escaló el VFX
    private Vector3 _vfxBaseScale;
    private float _endsAt = float.MaxValue, _nextTick;

    // Crece y avanza: desde cuándo está prendida en ESTA pantalla, cuánto recorrió y si ya frenó.
    private bool    _wasOn;
    private float   _activeSince;
    private float   _travelled;
    private bool    _stopped;

    // El radio de ahora: el del prefab más lo que creció con el tiempo ("Crece", hasta "Radio
    // máximo") más lo que la hicieron crecer (Entity_Summon.GrowZone: la Tormenta de la Evocación
    // crece con cada Descarga que le pega). En todas las pantallas.
    public float CurrentRadius
    {
        get
        {
            float r = Radius;
            if (GrowPerSecond > 0f && _wasOn)
            {
                r += GrowPerSecond * (Time.time - _activeSince);
                if (MaxRadius > 0f) r = Mathf.Min(r, Mathf.Max(Radius, MaxRadius));
            }
            return r + (_summon != null ? _summon.ZoneGrowth : 0f);
        }
    }

    // El ancho de la caja de ahora: crece por los dos costados (20 de noviembre de 2026), hasta el tope.
    public float CurrentBoxWidth
    {
        get
        {
            if (GrowPerSecond <= 0f || !_wasOn) return BoxWidth;
            float w = BoxWidth + 2f * GrowPerSecond * (Time.time - _activeSince);
            return MaxRadius > 0f ? Mathf.Min(w, Mathf.Max(BoxWidth, MaxRadius)) : w;
        }
    }

    // Lo que mide la zona para su VFX: el radio, o el ancho con Caja.
    private float VfxSize => Shape == EShape.Box ? CurrentBoxWidth : CurrentRadius;

    private static readonly Collider[] s_hits = new Collider[64];
    private readonly HashSet<AbilitySystemComponent> _seen = new HashSet<AbilitySystemComponent>();

    private void Awake() => _summon = GetComponent<Entity_Summon>();

    // La prende (servidor). La llama la invocación: sola al aparecer si "Empieza prendida", o
    // cuando decide.
    public void Begin()
    {
        if (_summon == null || !_summon.IsServerInitialized) return;
        _endsAt   = Seconds > 0f ? Time.time + Seconds : float.MaxValue;
        _nextTick = Time.time;
        _summon.SetZoneFlag(true);
    }

    private void Update()
    {
        if (_summon == null) return;
        bool on = _summon.ZoneActive;

        // Crece y avanza: el reloj arranca al prenderse, en cada pantalla.
        if (on && !_wasOn) { _activeSince = Time.time; _travelled = 0f; _stopped = false; }
        _wasOn = on;
        if (on) Advance(Time.deltaTime);

        // Todas las pantallas: el VFX sigue al "prendida" sincronizado.
        if (on && _vfx == null && VFX != null)
        {
            _vfx = Instantiate(VFX, transform.position, transform.rotation, transform);
            _vfxRadius = VfxSize;
            _vfxBaseScale = _vfx.transform.localScale;
            VFX_AreaVisual.Configure(_vfx, CurrentRadius, VfxScale, 0f);
        }
        else if (!on && _vfx != null)
        {
            Destroy(_vfx);
            _vfx = null;
        }
        else if (_vfx != null && Shape == EShape.Box && Mathf.Abs(_vfxRadius - VfxSize) > 0.01f)
        {
            // La caja se ensanchó: el VFX se estira de lado a lado (su X), desde el tamaño con que salió.
            _vfxRadius = VfxSize;
            Vector3 s = _vfxBaseScale;
            s.x *= BoxWidth > 0f ? CurrentBoxWidth / BoxWidth : 1f;
            _vfx.transform.localScale = s;
        }
        else if (_vfx != null && Shape != EShape.Box && Mathf.Abs(_vfxRadius - CurrentRadius) > 0.01f)
        {
            // La zona creció: el VFX crece con ella (solo el tamaño; sigue vivo).
            _vfxRadius = CurrentRadius;
            VFX_AreaVisual area = _vfx.GetComponent<VFX_AreaVisual>();
            if (area != null) area.SetRadius(CurrentRadius);
            else              _vfx.transform.localScale = Vector3.one * (CurrentRadius * VfxScale);
        }

        // Servidor: los efectos.
        if (!on || !_summon.IsServerInitialized) return;
        if (Time.time >= _endsAt) { _endsAt = float.MaxValue; _summon.NotifyZoneEnded(); return; }
        if (Time.time < _nextTick) return;
        _nextTick = Time.time + Mathf.Max(0.05f, TickSeconds);
        Apply();
    }

    private void Apply()
    {
        AbilitySystemComponent owner  = _summon.Summoner;
        AbilitySystemComponent author = Author == EAuthor.Owner ? owner : _summon.OwnASC;
        if (author == null) author = Author == EAuthor.Owner ? _summon.OwnASC : owner;
        // Sin autor solo sigue una invocación que vive sin su dueño (el veneno del zombi): aplica los
        // efectos sin autor (que no escalen con sus stats: daño fijo).
        if (author == null && !_summon.OutlivesOwner) return;

        List<GameplayEffect> effects = EmpowerTag != EGameplayTag.None && owner != null && owner.HasTag(EmpowerTag) &&
                                       EmpoweredEffects != null && EmpoweredEffects.Count > 0
                                       ? EmpoweredEffects : Effects;
        if (effects == null || effects.Count == 0) return;

        _seen.Clear();
        int count = Shape == EShape.Box
            ? Physics.OverlapBoxNonAlloc(BoxCenter, BoxHalfExtents, s_hits, transform.rotation, CharacterLayer,
                                         QueryTriggerInteraction.Collide)
            : Physics.OverlapSphereNonAlloc(transform.position, CurrentRadius, s_hits, CharacterLayer,
                                            QueryTriggerInteraction.Collide);
        for (int i = 0; i < count; i++)
        {
            AbilitySystemComponent target = s_hits[i] != null ? s_hits[i].GetComponentInParent<AbilitySystemComponent>() : null;
            if (target == null || !_seen.Add(target) || target.HasTag(EGameplayTag.State_Dead)) continue;

            Entity_Summon other = target.GetComponent<Entity_Summon>();
            if (other != null && !other.ReceivesZones) continue;

            bool fits = Targets == ETargets.Allies ? _summon.IsAlly(target) : _summon.IsEnemy(target);
            if (!fits) continue;
            if (PlayersOnly && target.GetComponent<PlayerController>() == null) continue;

            foreach (GameplayEffect e in effects)
                if (e != null) target.ApplyGameplayEffect(e, author);
        }
    }

    // Avanza 'dt' segundos: hacia adelante, sobre el piso, hasta su recorrido o una pared. Corre igual en
    // todas las pantallas (el SummonZone_Base no tiene NetworkTransform). Si la invocación tiene uno
    // (Summon_Base), la mueve solo el servidor y lo demás llega por él.
    public void Advance(float dt)
    {
        if (MoveSpeed <= 0f || _stopped || dt <= 0f) return;
        if (_summon != null && !_summon.IsServerInitialized &&
            GetComponent<FishNet.Component.Transforming.NetworkTransform>() != null) return;

        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f) return;
        forward.Normalize();

        float step = MoveSpeed * dt;
        if (MoveDistance > 0f) step = Mathf.Min(step, MoveDistance - _travelled);

        bool blocked = false;
        if (StopsAtWalls)
        {
            // A la altura de la rodilla, desde el frente de la zona: una rampa no es una pared.
            float front = Shape == EShape.Box ? BoxDepth * 0.5f : 0f;
            Vector3 from = transform.position + Vector3.up * 0.75f + forward * front;
            if (Physics.Raycast(from, forward, out RaycastHit wall, step + 0.5f, GroundLayer, QueryTriggerInteraction.Ignore) &&
                wall.normal.y < 0.6f)
            {
                step = Mathf.Max(0f, wall.distance - 0.5f);
                blocked = true;
            }
        }

        Vector3 next = transform.position + forward * step;
        // Sobre el piso: desde un poco arriba, hacia abajo (sube y baja rampas; sin piso, sigue a su altura).
        if (Physics.Raycast(next + Vector3.up * 1.5f, Vector3.down, out RaycastHit ground, 4f, GroundLayer,
                            QueryTriggerInteraction.Ignore))
            next.y = ground.point.y;
        transform.position = next;
        _travelled += step;

        if (blocked || (MoveDistance > 0f && _travelled >= MoveDistance - 0.001f)) Stop();
    }

    // Ya frenó (una pared o su recorrido): no avanza más.
    public bool Stopped => _stopped;

    private void Stop()
    {
        _stopped = true;
        if (EndsWhenStopped && _summon != null && _summon.IsServerInitialized)
        {
            _endsAt = float.MaxValue;
            _summon.NotifyZoneEnded();
        }
    }

    // La caja: apoyada en el piso (el pivote de la invocación) y girada con ella.
    private Vector3 BoxCenter      => transform.position + transform.up * (BoxHeight * 0.5f);
    private Vector3 BoxHalfExtents => new Vector3(CurrentBoxWidth * 0.5f, BoxHeight * 0.5f, BoxDepth * 0.5f);

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Targets == ETargets.Allies ? new Color(0f, 1f, 0.5f, 0.3f) : new Color(1f, 0.3f, 0.2f, 0.3f);
        if (Shape == EShape.Box)
        {
            Gizmos.matrix = Matrix4x4.TRS(BoxCenter, transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, BoxHalfExtents * 2f);
            Gizmos.matrix = Matrix4x4.identity;
            return;
        }
        Gizmos.DrawWireSphere(transform.position, Radius);
        if (GrowPerSecond > 0f && MaxRadius > Radius) Gizmos.DrawWireSphere(transform.position, MaxRadius);
        if (MoveSpeed > 0f)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            Gizmos.DrawLine(transform.position, transform.position + fwd * (MoveDistance > 0f ? MoveDistance : 10f));
        }
    }
}
