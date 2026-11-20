using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — LAS TAREAS (18 de noviembre de 2026, de las "Mejoras del GAS")
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
//
// Las piezas que se repetían en cada corrutina de habilidad, escritas una vez:
//   · ¿CORTO? Cada rutina preguntaba a su manera "¿murió?", "¿murió o está aturdido?", "¿lo que la bloquea?", "¿la
//     interrumpió otra habilidad?", "¿recibió daño?". Ahora se dice QUÉ la corta con EStopWhen:
//       - ShouldStop(qué): la pregunta directa, para lo que se mira en el momento (murió, aturdido, enraizado, bloqueado).
//       - Guard(qué): un AbilityGuard para lo que hay que recordar desde que empezó (la interrupción del ataque básico,
//         "recibió daño"). Se pregunta guard.Check() en cada vuelta; una vez que dice "corto", sigue diciéndolo.
//   · ESPERAR: WaitSeconds, WaitUntil y Repeat, que ya traen el guardián: si algo corta, la espera termina antes (y
//     guard.Stopped lo dice).
// Una habilidad NUEVA usa esto en vez de sus propios chequeos. Las viejas se fueron pasando donde el corte era EXACTAMENTE
// el mismo (ver el historial), y el 20 de noviembre de 2026 (pedido de Gustavo) también sus WaitForSeconds: ahora cortan
// A MITAD de la espera. Quedan con WaitForSeconds solo lo que no es una rutina de habilidad que se corte: los VFX con
// retraso, la recarga de las cargas, el proyectil, la zona ya desplegada y las pasivas que corren solas.
// ============================================================

// Lo que corta una rutina de habilidad (se combinan con |).
[System.Flags]
public enum EStopWhen
{
    Nothing     = 0,
    Dies        = 1 << 0,   // el dueño murió (o ya no está)
    Stunned     = 1 << 1,   // State_Stunned
    Rooted      = 1 << 2,   // State_Rooted
    Blocked     = 1 << 3,   // cualquier tag de su ActivationBlockedTags (lo que impide lanzarla la corta a mitad)
    Interrupted = 1 << 4,   // otra habilidad cortó las interrumpibles (solo si IsInterruptible; ver CancelSerial)
    Damaged     = 1 << 5,   // recibió daño de otro desde que empezó (OnTookDamage)

    // Lo de casi siempre: morir, lo que la bloquea y la interrupción del básico.
    Default     = Dies | Blocked | Interrupted,
}

// El guardián de UNA rutina: recuerda desde que empezó lo que hay que recordar (el contador de interrupción, si recibió
// daño). Con Damaged se suscribe a OnTookDamage: Dispose() (o un using) lo suelta.
public sealed class AbilityGuard : System.IDisposable
{
    private readonly GameplayAbility _ability;
    private readonly AbilitySystemComponent _asc;
    private readonly EStopWhen _when;
    private readonly int _cancelSerial;
    private bool _damaged;
    private System.Action<AbilitySystemComponent> _onDamaged;

    // Ya cortó (queda así).
    public bool Stopped { get; private set; }

    internal AbilityGuard(GameplayAbility ability, AbilitySystemComponent asc, EStopWhen when)
    {
        _ability = ability;
        _asc = asc;
        _when = when;
        _cancelSerial = asc != null ? asc.CancelSerial : 0;
        if ((when & EStopWhen.Damaged) != 0 && asc != null)
        {
            _onDamaged = _ => _damaged = true;
            asc.OnTookDamage += _onDamaged;
        }
    }

    // ¿Corto? Lo mira ahora; si ya cortó, sigue en true.
    public bool Check()
    {
        if (Stopped) return true;
        if (_asc == null) return Stopped = (_when & EStopWhen.Dies) != 0;
        if (GameplayAbility.StateStops(_ability, _asc, _when)) return Stopped = true;
        if ((_when & EStopWhen.Interrupted) != 0 && _ability != null && _ability.IsInterruptible &&
            _asc.CancelSerial != _cancelSerial) return Stopped = true;
        if ((_when & EStopWhen.Damaged) != 0 && _damaged) return Stopped = true;
        return false;
    }

    public void Dispose()
    {
        if (_onDamaged != null && _asc != null) _asc.OnTookDamage -= _onDamaged;
        _onDamaged = null;
    }
}

public abstract partial class GameplayAbility
{
    // =========================================================
    // ¿CORTO?
    // =========================================================

    // La pregunta directa, para lo que se mira en el momento (Dies, Stunned, Rooted, Blocked). Interrupted y Damaged
    // necesitan recordar desde el principio: para esos, Guard(...).
    protected bool ShouldStop(EStopWhen when)
    {
        if (OwnerASC == null) return (when & EStopWhen.Dies) != 0;
        return StateStops(this, OwnerASC, when);
    }

    // Un guardián para una rutina que empieza ahora. Con Damaged, soltarlo al terminar (using o Dispose en un finally).
    protected AbilityGuard Guard(EStopWhen when = EStopWhen.Default) => new AbilityGuard(this, OwnerASC, when);

    // Lo que se mira en el momento: murió, aturdido, enraizado, lo que la bloquea.
    internal static bool StateStops(GameplayAbility ability, AbilitySystemComponent asc, EStopWhen when)
    {
        if ((when & EStopWhen.Dies) != 0 && asc.HasTag(EGameplayTag.State_Dead)) return true;
        if ((when & EStopWhen.Stunned) != 0 && asc.HasTag(EGameplayTag.State_Stunned)) return true;
        if ((when & EStopWhen.Rooted) != 0 && asc.HasTag(EGameplayTag.State_Rooted)) return true;
        if ((when & EStopWhen.Blocked) != 0 && ability != null && ability.ActivationBlockedTags != null)
            foreach (EGameplayTag tag in ability.ActivationBlockedTags)
                if (asc.HasTag(tag)) return true;
        return false;
    }

    // =========================================================
    // ESPERAR (con el guardián: si algo corta, la espera termina antes y guard.Stopped lo dice)
    // =========================================================

    // Espera 'seconds' (tiempo de juego), o hasta que el guardián corte. guard null = no se corta.
    protected IEnumerator WaitSeconds(float seconds, AbilityGuard guard)
    {
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            if (guard != null && guard.Check()) yield break;
            yield return null;
            elapsed += Time.deltaTime;
        }
    }

    // Espera hasta que 'done' diga que sí (soltó el botón, llegó, chocó...), hasta que el guardián corte, o hasta
    // 'timeout' segundos (0 = sin tope).
    protected IEnumerator WaitUntil(System.Func<bool> done, AbilityGuard guard, float timeout = 0f)
    {
        float elapsed = 0f;
        while (done == null || !done())
        {
            if (guard != null && guard.Check()) yield break;
            if (timeout > 0f && elapsed >= timeout) yield break;
            yield return null;
            elapsed += Time.deltaTime;
        }
    }

    // Llama a 'tick' (con el número de vuelta, desde 0) cada 'interval' segundos durante 'duration', o hasta que el
    // guardián corte. La primera vuelta es en el acto. Para lo que hace efecto "cada tanto mientras dura".
    protected IEnumerator Repeat(float interval, float duration, System.Action<int> tick, AbilityGuard guard)
    {
        interval = Mathf.Max(0.05f, interval);
        float elapsed = 0f, next = 0f;
        int i = 0;
        while (elapsed <= duration)
        {
            if (guard != null && guard.Check()) yield break;
            if (elapsed >= next) { tick?.Invoke(i++); next += interval; }
            yield return null;
            elapsed += Time.deltaTime;
        }
    }
}
