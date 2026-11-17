using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — LOS VFX
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // VFX
    // =========================================================

    // Reproduce la secuencia configurada en VisualsSequence (con sus
    // delays/offsets/escalas). Público para que
    // NetworkAbilitySystemComponent pueda arrancarla en cada peer por
    // igual (ver ServerPlayAbilityVisualsSequence) — el resultado sale
    // idéntico en todos porque solo depende de OwnerASC.transform/tags,
    // que ya están sincronizados.
    public System.Collections.IEnumerator PlayVisualsSequence() => PlayVisualsSequence(OwnerASC);

    // Overload con dueño explícito. El peer OBSERVADOR resuelve esta habilidad
    // como el asset-template compartido (vía GameplayAbilityRegistry), que no
    // tiene OwnerASC propio, así que le pasa su ASC acá. Se lo toma por
    // parámetro (en vez de mutar el campo OwnerASC del template compartido)
    // porque la corutina se extiende varios frames y dos jugadores podrían
    // correr la misma secuencia a la vez.
    public System.Collections.IEnumerator PlayVisualsSequence(AbilitySystemComponent owner)
    {
        if (owner == null) yield break;

        float mult = 1f;
        float spd  = owner.GetAttributeValue(EAttributeType.AtkSpeed);
        if (spd > 0) mult = 1f / spd;

        if (Visuals == null) yield break;

        foreach (var v in Visuals)
        {
            // Solo las de "al lanzar": las de golpe e impacto las dispara la habilidad
            // cuando pasa eso (ver BroadcastHitVFX / BroadcastImpactVFX).
            if (v.When != EVisualWhen.OnCast || v.VFXPrefab == null) continue;

            if (v.Delay > 0)
                yield return new WaitForSeconds(v.Delay / mult);

            Vector3    pos = owner.transform.position + owner.transform.TransformDirection(v.Offset);
            Quaternion rot = owner.transform.rotation * Quaternion.Euler(v.RotationOffset);
            GameObject vfx = v.Attach
                ? Instantiate(v.VFXPrefab, pos, rot, owner.transform)
                : Instantiate(v.VFXPrefab, pos, rot);

            vfx.transform.localScale = (v.Scale != Vector3.zero) ? v.Scale : Vector3.one;

            if (v.EndWithTag != EGameplayTag.None || v.EndWhenAttributeDepleted)
                owner.StartAbilityCoroutine(DestroyVfxWhenExpired(owner, vfx, v.EndWithTag,
                                                                 v.EndWhenAttributeDepleted,
                                                                 v.DepletedAttribute));
            else if (v.DestroyTime > 0)
                Destroy(vfx, v.DestroyTime);
        }
    }

    // Mantiene vivo un VFX de la secuencia hasta que se cumpla su condición de fin: que
    // el dueño pierda un TAG, que se le agote un ATRIBUTO, o lo que pase primero si
    // están configuradas las dos.
    //
    // El caso que motivó lo del atributo es el escudo del Frenzy: el aura del buff y la
    // burbuja del escudo son dos entradas de la MISMA secuencia, pero el escudo se
    // consume con los golpes mientras el buff sigue corriendo. Atado solo al tag, la
    // burbuja quedaba puesta sin nada que absorber.
    private System.Collections.IEnumerator DestroyVfxWhenExpired(
        AbilitySystemComponent owner, GameObject vfx,
        EGameplayTag tag, bool watchAttribute, EAttributeType attribute)
    {
        bool watchTag = tag != EGameplayTag.None;

        // Primero esperamos a que la condición se CUMPLA. En el servidor/host el efecto
        // se aplica en el mismo frame, pero en un cliente remoto el tag llega por
        // NetTags y el atributo por su SyncVar — los dos asincrónicos. Sin esta espera,
        // el bucle de abajo vería "sin tag / escudo en cero" y destruiría el VFX al
        // instante en los observadores (el aura parpadeaba y desaparecía).
        const float startTimeout = 1f;
        float elapsed = 0f;
        while (elapsed < startTimeout && owner != null && vfx != null
               && !VisualShouldLive(owner, watchTag, tag, watchAttribute, attribute))
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        // Ahora sí: vive mientras se sostenga la condición.
        while (owner != null && vfx != null
               && VisualShouldLive(owner, watchTag, tag, watchAttribute, attribute))
            yield return null;

        if (vfx != null) Destroy(vfx);
    }

    // ¿Sigue en pie lo que mantiene vivo al VFX? Con las dos condiciones activas tienen
    // que cumplirse AMBAS: el VFX muere en cuanto falla la primera.
    private static bool VisualShouldLive(AbilitySystemComponent owner,
                                         bool watchTag, EGameplayTag tag,
                                         bool watchAttribute, EAttributeType attribute)
    {
        if (watchTag       && !owner.HasTag(tag))                       return false;
        if (watchAttribute && owner.GetAttributeValue(attribute) <= 0f) return false;
        return true;
    }

    // ¿Hay alguna entrada de VFX para este momento?
    public bool HasVisuals(EVisualWhen when)
    {
        if (Visuals == null) return false;
        foreach (AbilityVisual v in Visuals)
            if (v.VFXPrefab != null && v.When == when) return true;
        return false;
    }

    private bool HasImpactSound => ImpactSound != null && ImpactSound.Clips != null && ImpactSound.Clips.Length > 0;

    // ¿Tiene algo que mostrar o hacer sonar en el punto de impacto? (el proyectil no manda
    // nada por red si no).
    public bool HasImpactFeedback => HasVisuals(EVisualWhen.OnImpact) || HasImpactSound;

    // SERVIDOR: los VFX "al golpear" sobre 'target' (y el sonido de impacto), en todas las
    // pantallas. Se manda al personaje y no una posición: así un VFX pegado lo sigue.
    // withSound = false cuando el mismo golpe ya suena por su impacto (el proyectil).
    public void BroadcastHitVFX(AbilitySystemComponent target, bool withSound = true)
    {
        if (target == null) return;
        if (!HasVisuals(EVisualWhen.OnHit) && !(withSound && HasImpactSound)) return;

        NetworkAbilitySystemComponent netAsc = OwnerASC != null
            ? OwnerASC.GetComponent<NetworkAbilitySystemComponent>() : null;

        if (netAsc != null) netAsc.ServerPlayAbilityVFXOn(this, target, withSound);
        else                PlayImpactVFXOn(target);   // sin red (escena de pruebas suelta)
    }

    // SERVIDOR: los VFX "al curar" sobre el aliado curado, en todas las pantallas. Lo llama
    // ApplyHitEffects solo; sin entradas "al curar" no manda nada.
    public void BroadcastHealVFX(AbilitySystemComponent target)
    {
        if (target == null || !HasVisuals(EVisualWhen.OnHeal)) return;

        NetworkAbilitySystemComponent netAsc = OwnerASC != null
            ? OwnerASC.GetComponent<NetworkAbilitySystemComponent>() : null;

        if (netAsc != null) netAsc.ServerPlayAbilityVFXOn(this, target, false, EVisualWhen.OnHeal);
        else                SpawnVisuals(EVisualWhen.OnHeal, OwnerASC, target, target.transform.position);
    }

    // SERVIDOR: los VFX "en el impacto" en un punto del mundo (y el sonido), en todas las
    // pantallas: donde estalla un área, donde aterriza un salto, donde choca un tiro.
    public void BroadcastImpactVFX(Vector3 point)
    {
        if (!HasVisuals(EVisualWhen.OnImpact) && !HasImpactSound) return;

        NetworkAbilitySystemComponent netAsc = OwnerASC != null
            ? OwnerASC.GetComponent<NetworkAbilitySystemComponent>() : null;

        if (netAsc != null) netAsc.ServerPlayAbilityVFX(this, point);
        else                PlayImpactVFX(point);
    }

    // RAYOS: tramos de un punto a otro (from[i] → to[i]) que se dibujan en todas las pantallas
    // (la Descarga eléctrica del Mago y sus rebotes). Cada peer los dibuja con su copia de la
    // habilidad (PlayBeams), como los VFX de impacto.
    public void BroadcastBeams(Vector3[] from, Vector3[] to)
    {
        if (from == null || to == null || from.Length == 0 || from.Length != to.Length) return;

        NetworkAbilitySystemComponent netAsc = OwnerASC != null
            ? OwnerASC.GetComponent<NetworkAbilitySystemComponent>() : null;

        if (netAsc != null) netAsc.ServerPlayBeams(this, from, to);
        else                PlayBeams(from, to);
    }

    // Dibuja los tramos en ESTA pantalla. Por defecto nada: la habilidad que tiene rayos lo
    // sobreescribe (GA_ChainLightning).
    public virtual void PlayBeams(Vector3[] from, Vector3[] to) { }

    // Los VFX "en el impacto" en un punto. Lo corre CADA peer con su propia copia de la
    // habilidad (NetworkAbilitySystemComponent.ServerPlayAbilityVFX resuelve esta MISMA
    // habilidad en cada cliente), así no hace falta sincronizar el GameObject del VFX.
    // Virtual por si una habilidad necesita algo que la lista no cubre.
    public virtual void PlayImpactVFX(Vector3 position)
        => SpawnVisuals(EVisualWhen.OnImpact, OwnerASC, null, position);

    // Los VFX "al golpear" sobre un PERSONAJE: lo que llega por
    // NetworkAbilitySystemComponent.ServerPlayAbilityVFXOn().
    public virtual void PlayImpactVFXOn(AbilitySystemComponent target)
    {
        if (target != null) SpawnVisuals(EVisualWhen.OnHit, OwnerASC, target, target.transform.position);
    }

    // Los mismos, con un dueño puntual. El peer OBSERVADOR resuelve esta habilidad como el
    // asset-template compartido (vía GameplayAbilityRegistry), que no tiene OwnerASC propio;
    // los VFX lo necesitan para parentarse al jugador o para esperar su Delay. El swap es
    // sincrónico —instancian y retornan en el mismo frame, y Unity es single-thread—, así
    // que restaurar OwnerASC al final deja el template intacto para cualquier otro jugador.
    public void PlayImpactVFXFor(AbilitySystemComponent owner, Vector3 position)
    {
        AbilitySystemComponent prev = OwnerASC;
        OwnerASC = owner;
        PlayImpactVFX(position);
        OwnerASC = prev;
    }

    // when: "al golpear" (lo de siempre) o "al curar".
    public void PlayImpactVFXOnFor(AbilitySystemComponent owner, AbilitySystemComponent target,
                                   EVisualWhen when = EVisualWhen.OnHit)
    {
        AbilitySystemComponent prev = OwnerASC;
        OwnerASC = owner;
        if (when == EVisualWhen.OnHit)  PlayImpactVFXOn(target);
        else if (target != null)        SpawnVisuals(when, owner, target, target.transform.position);
        OwnerASC = prev;
    }

    // Los VFX "al golpear" (o "al curar") en una POSICIÓN: el respaldo para un objetivo sin
    // NetworkObject (no se lo puede mandar por red), que en las demás pantallas sale donde estaba.
    public void PlayHitVFXAtFor(AbilitySystemComponent owner, Vector3 position,
                                EVisualWhen when = EVisualWhen.OnHit)
        => SpawnVisuals(when, owner, null, position);

    // ---------------------------------------------------------
    // Lo que cada habilidad le aporta a sus VFX de impacto
    // ---------------------------------------------------------

    // Radio del área, para las entradas con "Calzar con el área". 0 = no tiene área.
    public virtual float VisualAreaRadius => 0f;

    // Cuánto dura un VFX de golpe o impacto con DestroyTime en 0: lo que dura la habilidad
    // (un área, su duración). Por defecto, 2 s.
    protected virtual float VisualDefaultLifetime => 2f;

    // A quién se pega un VFX de impacto con "Lo sigue": al lanzador, SOLO si el área lo
    // sigue (una zona que se mueve con él). null = queda fijo donde apareció.
    protected virtual Transform VisualImpactParent(AbilitySystemComponent owner) => null;

    // Instancia las entradas de un momento. target != null = sobre un personaje (al golpear).
    private void SpawnVisuals(EVisualWhen when, AbilitySystemComponent owner,
                              AbilitySystemComponent target, Vector3 point)
    {
        if (Visuals == null) return;

        foreach (AbilityVisual v in Visuals)
        {
            if (v.When != when || v.VFXPrefab == null) continue;

            // Con espera, la corutina corre en el ASC del dueño (el template del observador
            // no tiene dónde correrla). Sin dueño no hay espera: sale ya.
            if (v.Delay > 0f && owner != null)
                owner.StartAbilityCoroutine(SpawnVisualDelayed(v, owner, target, point));
            else
                SpawnVisual(v, owner, target, point);
        }
    }

    private IEnumerator SpawnVisualDelayed(AbilityVisual v, AbilitySystemComponent owner,
                                           AbilitySystemComponent target, Vector3 point)
    {
        yield return new WaitForSeconds(v.Delay);
        // El objetivo pudo desaparecer en la espera: sale donde estaba.
        if (target == null) SpawnVisual(v, owner, null, point);
        else                SpawnVisual(v, owner, target, target.transform.position);
    }

    private void SpawnVisual(AbilityVisual v, AbilitySystemComponent owner,
                             AbilitySystemComponent target, Vector3 point)
    {
        // Con la rotación del PREFAB, no cero: muchos VFX de los packs acuestan el círculo
        // girando su raíz −90° en X (Zone of Truth), y con Quaternion.identity quedaban parados.
        Quaternion rot = Quaternion.Euler(v.RotationOffset) * v.VFXPrefab.transform.rotation;
        GameObject vfx = Instantiate(v.VFXPrefab, point + v.Offset, rot);

        Transform parent = null;
        if (v.Attach)
            parent = target != null ? target.transform : (v.When == EVisualWhen.OnImpact ? VisualImpactParent(owner) : null);
        if (parent != null) vfx.transform.SetParent(parent, true);

        if (v.Scale != Vector3.zero) vfx.transform.localScale = v.Scale;

        float lifetime = v.DestroyTime > 0f ? v.DestroyTime : VisualDefaultLifetime;
        float radius   = VisualAreaRadius;

        // Con VFX_AreaVisual el círculo calza EXACTO con el radio y se desvanece al
        // terminar; si no, cae al multiplicador a ojo.
        if (v.MatchAreaSize && radius > 0f)
            VFX_AreaVisual.Configure(vfx, radius, v.AreaSizeMultiplier > 0f ? v.AreaSizeMultiplier : 1f, lifetime);
        else
            Destroy(vfx, lifetime);
    }
}
