using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — CUÁNDO PEGA
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
//
// Los frames de golpe del clip (Animation Events), la velocidad a la que se reproduce y la
// rutina que llama al golpe en cada uno (cortable, para el ataque básico interrumpible).
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // CUÁNDO PEGA: los frames de golpe del clip y la velocidad de la animación
    // =========================================================

    // Nombre del Animation Event que marca el FRAME DE IMPACTO dentro de un clip.
    //
    // Es una CONSTANTE y no un campo del inspector a propósito: el evento tiene que
    // llamar a un método que exista en el receptor (PlayerController.AnimationEvent_HitFrame,
    // vía PlayerAnimationEvents). Con cualquier otro nombre, Unity no encontraría el
    // receptor y llenaría la consola de "AnimationEvent has no receiver!". O sea que
    // poder editarlo por habilidad era una libertad falsa.
    //
    // Cómo se usa: en el clip (ventana Animation, o el importer del FBX → Events) se
    // agrega un evento con esta función en el frame donde el arma conecta; el servidor
    // lee ese timestamp del asset y resuelve el golpe ahí, sin calcular tiempos a mano.
    // Varios eventos en el mismo clip = golpe escalonado (barridos que 'recorren').
    // Sin eventos (o sin clip), se usa el delay fijo de la habilidad.
    public const string HitFrameEventName = "AnimationEvent_HitFrame";

    // Velocidad de animación forzada desde afuera (0 = sin forzar, se calcula sola).
    // La usan los combos: el ritmo de ataque lo conoce el PADRE, así que se lo imponen
    // a cada paso — que por sí solo devolvería velocidad natural, porque no tiene
    // UseAttackSpeedAsCooldown ni cooldown propio. Ver ResolveAnimationSpeed.
    // NonSerialized: estado de runtime por instancia, igual que SourceTemplate.
    [System.NonSerialized] public float AnimationSpeedOverride;

    // Velocidad a la que hay que reproducir la animación de esta habilidad.
    //
    // El clip se ESTIRA/COMPRIME para durar lo que el ritmo de ataque SOLO cuando
    // UseAttackSpeedAsCooldown está activo — que es precisamente lo que define a un
    // ataque básico: su cooldown ES cada cuánto podés volver a pegar, así que el swing
    // tiene que entrar justo en ese hueco (clip de 2s con 0.8s de ritmo → 2.5x).
    //
    // En cualquier otra habilidad el cooldown NO es un ritmo sino una espera, y estirar
    // el clip para llenarlo sería absurdo (un ult con 60s de cooldown quedaría a cámara
    // lenta). Esas reproducen su clip a velocidad natural.
    //
    // El MISMO valor lo usan el Animator (para reproducir) y HitTimingRoutine (para
    // programar el golpe), así el daño siempre cae en el frame que se ve.
    public float ResolveAnimationSpeed()
    {
        // Velocidad impuesta desde afuera: la fijan los combos en cada paso, porque el
        // ritmo lo conoce el PADRE (es él quien tiene UseAttackSpeedAsCooldown) y no el
        // paso suelto, que por sí solo devolvería velocidad natural. Ver
        // GA_ComboSequence/GA_AlternatingCombo.
        if (AnimationSpeedOverride > 0f) return AnimationSpeedOverride;

        if (AnimationClip != null && UseAttackSpeedAsCooldown && AnimationClip.length > 0.001f)
        {
            float target = ResolveCooldownDuration();
            // Clamp: un ritmo minúsculo no debe dar una animación ilegible.
            if (target > 0f) return Mathf.Clamp(AnimationClip.length / target, 0.1f, 10f);
        }

        // Con clip pero sin ritmo de ataque: velocidad natural del clip.
        if (AnimationClip != null) return 1f;

        // Esquema viejo (sin clip): el multiplicador global de siempre.
        float atkSpeed = OwnerASC != null ? OwnerASC.GetAttributeValue(EAttributeType.AtkSpeed) : 0f;
        return atkSpeed > 0f ? 1f / atkSpeed : 1f;
    }

    // Caché de los tiempos leídos (AnimationClip.events aloca un array en cada
    // acceso). Se invalida solo si cambia el clip — los pasos de combo pueden
    // pisarlo (ver ComboStep.AnimationClipOverride).
    [System.NonSerialized] private AnimationClip _hitTimesClip;
    [System.NonSerialized] private List<float>   _hitTimesCache;
    // El aviso de "al clip le faltan los eventos de impacto" se da una sola vez.
    [System.NonSerialized] private bool _warnedNoHitFrames;

    // Momentos (en segundos DENTRO del clip) en los que este ataque conecta, leídos
    // de los Animation Events del AnimationClip. Lista vacía = el clip no los define
    // y hay que caer al delay fijo de la habilidad.
    //
    // Esto es lo que permite que el daño caiga exactamente cuando el arma golpea sin
    // configurar ningún número: el tiempo sale de la animación. Y como lo lee el
    // SERVIDOR desde el asset, no depende de que ningún cliente reporte nada (los
    // Animation Events reales corren en cada cliente y no serían confiables).
    public List<float> GetHitFrameTimes()
    {
        if (_hitTimesClip == AnimationClip && _hitTimesCache != null) return _hitTimesCache;

        _hitTimesClip  = AnimationClip;
        _hitTimesCache = new List<float>();

        if (AnimationClip != null)
        {
            foreach (AnimationEvent evt in AnimationClip.events)
                if (evt.functionName == HitFrameEventName) _hitTimesCache.Add(evt.time);

            _hitTimesCache.Sort();
        }
        return _hitTimesCache;
    }

    // True si el último HitTimingRoutine se cortó por una interrupción. Quien lo llame
    // tiene que mirarlo y salir sin EndAbility (ver GA_ConeAttack).
    [System.NonSerialized] protected bool WasCancelled;

    // Corre el TIMING de un ataque y llama a 'onHit' en cada momento de impacto. Los
    // tiempos salen de los Animation Events del clip, escalados por la velocidad a la
    // que se reproduce (ver ResolveAnimationSpeed): así el golpe cae siempre en el
    // frame que se ve, aunque el swing se comprima por el ritmo de ataque.
    //
    // A 'onHit' se le pasa un conjunto COMPARTIDO de ya golpeados: un swing con
    // varios frames de impacto (un barrido escalonado) le pega UNA sola vez a cada
    // enemigo, aunque siga dentro del área en el siguiente test.

    protected IEnumerator HitTimingRoutine(System.Action<HashSet<AbilitySystemComponent>> onHit)
    {
        WasCancelled = false;
        int cancelSerial = OwnerASC != null ? OwnerASC.CancelSerial : 0;

        // La MISMA velocidad a la que se reproduce el clip: si el swing se comprime
        // para entrar en el ritmo de ataque, el golpe se adelanta en igual proporción.
        float speedMultiplier = ResolveAnimationSpeed();
        if (speedMultiplier <= 0f) speedMultiplier = 1f;

        HashSet<AbilitySystemComponent> alreadyHit = new HashSet<AbilitySystemComponent>();
        List<float> hitTimes = GetHitFrameTimes();

        // Sin eventos en el clip no hay con qué sincronizar: el golpe sale de una. Se
        // avisa una vez, porque casi siempre significa que al clip le falta el evento.
        if (hitTimes.Count == 0)
        {
            if (!_warnedNoHitFrames)
            {
                _warnedNoHitFrames = true;
                Debug.LogWarning($"[{AbilityName}] Su clip no tiene eventos '{HitFrameEventName}', " +
                                 $"así que el golpe se resuelve al instante (sin sincronizar con la " +
                                 $"animación). Agregá el evento en el frame de impacto del clip.");
            }
            onHit?.Invoke(alreadyHit);
            yield break;
        }

        // Con eventos: un test por cada uno, esperando lo que falte hasta ese momento
        // del clip (los tiempos son absolutos dentro del clip, por eso el descuento).
        float elapsed = 0f;
        foreach (float time in hitTimes)
        {
            float wait = (time - elapsed) / speedMultiplier;
            if (wait > 0f)   // corta apenas lo interrumpen (abajo lo nota), no al próximo impacto
                yield return WaitUntil(() => IsInterruptible && OwnerASC != null && OwnerASC.CancelSerial != cancelSerial, null, wait);
            elapsed = time;

            if (IsInterruptible && OwnerASC != null && OwnerASC.CancelSerial != cancelSerial)
            {
                WasCancelled = true;
                yield break;
            }

            onHit?.Invoke(alreadyHit);
        }
    }
}
