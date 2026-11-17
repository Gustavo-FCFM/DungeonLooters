using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// ============================================================
// GameplayAbility — A QUIÉN Y HACIA DÓNDE
//
// Una parte de GameplayAbility (partial class). La clase y TODOS sus campos del Inspector están
// en GAS/GameplayAbility.cs; ahí está también la lista de las partes.
//
// La dirección del golpe (puntería vertical), el alcance cuerpo a cuerpo, la afiliación y
// el objetivo apuntado (FindBestTargetInAim).
// ============================================================
public abstract partial class GameplayAbility
{
    // =========================================================
    // DIRECCIÓN DEL GOLPE (puntería vertical)
    // =========================================================

    // Altura desde la que se mide la inclinación. El pivote del personaje está en los
    // pies y la cámara a la altura de la cabeza: midiendo desde el piso, apuntar al
    // horizonte daría una inclinación hacia arriba que no existe.
    private const float AimOriginHeight = 1.4f;

    // Hacia dónde sale el golpe. Sin puntería vertical es el frente del cuerpo, plano —
    // el comportamiento de siempre.
    //
    // EL INTERRUPTOR NO VIVE ACÁ: lo declara cada habilidad que de verdad lo usa (hoy
    // GA_ConeAttack y GA_LineAttack) y se pasa por parámetro. Es la misma decisión que
    // se tomó con AllyEffects: un campo en la clase base aparece en el Inspector de las
    // 78 habilidades del juego, incluidas las zonas, los buffs y los dashes, donde no
    // hace absolutamente nada — y un campo que no hace nada es peor que no tenerlo,
    // porque invita a prenderlo y esperar un resultado.
    //
    // CÓMO SE COMBINAN LAS DOS FUENTES, y por qué:
    //   · El GIRO sale del cuerpo, que gira en vivo hacia la cámara. Por eso el defensor
    //     puede leer el swing y esquivarlo, y el atacante corregir sobre la marcha.
    //   · La INCLINACIÓN sale del punto de mira, que el dueño mandó AL ACTIVAR y queda
    //     congelado (el servidor no tiene la cámara del jugador).
    //
    // Usar el punto de mira entero para las dos cosas sería un retroceso: el ataque
    // saldría hacia donde apuntabas al apretar y dejaría de acompañar el giro, que es
    // justo lo que hoy funciona bien.
    protected Vector3 ResolveAttackDirection(bool useVerticalAim)
    {
        Vector3 bodyForward = OwnerASC != null ? OwnerASC.transform.forward : Vector3.forward;
        bodyForward.y = 0f;

        if (bodyForward.sqrMagnitude < 0.0001f) return Vector3.forward;
        bodyForward.Normalize();

        if (!useVerticalAim || OwnerASC == null) return bodyForward;

        // Los NPCs no tienen cámara: se quedan con el golpe plano.
        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        if (pc == null) return bodyForward;

        // Se lee NetworkAimPoint y no GetAimPoint() a propósito: en el host, GetAimPoint
        // haría un raycast de cámara EN VIVO y en los clientes remotos devolvería el
        // punto congelado. Serían dos reglas distintas según quién hostea. Este campo lo
        // escribe el servidor al activar, igual para todos.
        Vector3 aimPoint = pc.NetworkAimPoint;
        if (aimPoint == Vector3.zero) return bodyForward;   // nunca se envió

        Vector3 origin = OwnerASC.transform.position + Vector3.up * AimOriginHeight;
        Vector3 toAim  = aimPoint - origin;

        float horizontal = new Vector2(toAim.x, toAim.z).magnitude;
        if (horizontal < 0.01f) return bodyForward;         // apuntando a los pies

        // La inclinación de la mira, aplicada al frente del cuerpo.
        float pitch = Mathf.Atan2(toAim.y, horizontal);
        return bodyForward * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch);
    }

    // Multiplicador del alcance de los golpes cuerpo a cuerpo (conos y líneas): 1 +
    // MeleeRangeBonus del dueño. El Avatar del Guardián lo sube al crecer.
    protected float MeleeReach
        => OwnerASC != null ? Mathf.Max(0.1f, 1f + OwnerASC.GetAttributeValue(EAttributeType.MeleeRangeBonus)) : 1f;

    // =========================================================
    // AFILIACIÓN — atajos hacia AbilitySystemComponent.IsEnemyOf/IsAllyOf
    // usando al dueño de esta habilidad como referencia
    // =========================================================

    // Para las habilidades que eligen a quién pegarle ANTES de ApplyHitEffects (filtrando por enemigo):
    // si lo encontrado es algo del mapa que se golpea (la campana, StrikeTarget), le avisa y devuelve true
    // para que la búsqueda lo saltee. Sin carga de definitiva ni VFX.
    protected bool StrikeIfScenery(AbilitySystemComponent target)
    {
        if (target == null || target.Strikeable == null) return false;
        if (OwnerASC != null) target.Strikeable.Strike(OwnerASC, this);
        return true;
    }

    protected bool IsEnemy(AbilitySystemComponent target)
        => OwnerASC != null && OwnerASC.IsEnemyOf(target);

    protected bool IsAlly(AbilitySystemComponent target, bool includeSelf = true)
        => OwnerASC != null && OwnerASC.IsAllyOf(target, includeSelf);

    // =========================================================
    // SELECCIÓN DE OBJETIVO APUNTADO
    // =========================================================

    // A quién busca FindBestTargetInAim.
    protected enum ETargetAffiliation { Enemies, Allies }

    // Devuelve el personaje MÁS CENTRADO en la mira del dueño, dentro de 'maxRange' y
    // de un cono de 'selectionAngle' grados. null si no hay ninguno válido.
    //
    // Vive acá porque es la misma búsqueda para todas las habilidades de objetivo
    // único, cambiando solo a quién apuntan: el Golpe mortal del Pícaro (enemigos, para
    // aparecer detrás), la Intercepción heroica del Paladín (aliados, para aparecer
    // delante) y las de seleccionar-y-aplicar (GA_Target). Estaba copiada casi
    // igual en cada una.
    //
    // En el servidor, GetAimPoint() usa el NetworkAimPoint que el dueño mandó junto con
    // el input (ver NetworkASC.ServerActivateAbility), así que la selección se resuelve
    // con la mira REAL del jugador y no con hacia dónde apunta su cuerpo.
    //
    // El filtro de física sale de TargetLayer; la afiliación se resuelve en código.
    protected AbilitySystemComponent FindBestTargetInAim(float maxRange, float selectionAngle,
                                                        ETargetAffiliation affiliation,
                                                        bool includeSelf = false,
                                                        bool allowDead   = false,
                                                        System.Func<AbilitySystemComponent, bool> filter = null)
    {
        if (OwnerASC == null) return null;

        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        Vector3 origin = OwnerASC.transform.position;

        // EL RAYO DE LA RETÍCULA, no la línea desde los pies del personaje.
        //
        // Antes se medía el ángulo desde el PERSONAJE hasta cada candidato, contra la
        // dirección personaje→punto de mira. Pero la cámara está detrás y al hombro:
        // esas dos líneas no son la misma, y cuanto más cerca está el objetivo más se
        // abren. El resultado era que apuntar a un enemigo del fondo elegía al que
        // tenías pegado — "siempre el más cercano", aunque no estuviera en la retícula.
        //
        // Midiendo desde la CÁMARA a lo largo de su rayo, "el más centrado" quiere decir
        // lo que el jugador ve. El alcance se sigue midiendo desde el personaje (el
        // OverlapSphere de abajo), que es lo correcto para una habilidad.
        //
        // El origen viaja con el pedido de activación (PlayerController.NetworkAimOrigin),
        // así que el servidor resuelve con el mismo rayo que vio el dueño.
        Vector3 aimOrigin = pc != null ? pc.GetAimOrigin() : origin + Vector3.up * 1.6f;

        // El punto de mira se pide LEJOS a propósito. Con el alcance de la habilidad, un
        // rayo de 12 metros desde una cámara que ya está 5 detrás del personaje se queda
        // corto, y si no choca contra nada devuelve un punto casi encima del jugador:
        // la dirección salía de ahí y era pura ruleta.
        Vector3 aimPoint = pc != null ? pc.GetAimPoint(AimRayLength)
                                      : origin + OwnerASC.transform.forward * maxRange;

        Vector3 aimDir = aimPoint - aimOrigin;
        if (aimDir.sqrMagnitude < 0.0001f) aimDir = OwnerASC.transform.forward;
        aimDir.Normalize();

        // TOLERANCIA DE RED. El dueño eligió al objetivo en SU pantalla y el servidor lo busca
        // con las posiciones que él tiene, que llegan con atraso: contra alguien que se mueve,
        // a 3 m (el Desarme) el servidor no encontraba a nadie, rechazaba la activación y el
        // dueño veía el golpe pero el efecto nunca entraba. Solo para un dueño REMOTO: el
        // host y los bots ven las mismas posiciones que el servidor.
        if (pc != null && pc.IsServerInitialized && !pc.IsOwner && pc.Owner != null && pc.Owner.IsValid)
        {
            maxRange      += ServerRangeGrace;
            selectionAngle = Mathf.Min(selectionAngle + ServerAngleGrace, 89f);
        }

        Collider[] cols = Physics.OverlapSphere(origin, maxRange, TargetLayer);
        AbilitySystemComponent best = null;
        // Umbral de "está dentro del cono": comparar cosenos evita un Acos por candidato.
        float bestAlign = Mathf.Cos(selectionAngle * Mathf.Deg2Rad);

        foreach (var c in cols)
        {
            AbilitySystemComponent asc = c.GetComponentInParent<AbilitySystemComponent>();
            if (asc == null) continue;
            if (!includeSelf && ReferenceEquals(asc, OwnerASC)) continue;
            if (!allowDead && asc.HasTag(EGameplayTag.State_Dead)) continue;

            bool valid = affiliation == ETargetAffiliation.Allies
                ? IsAlly(asc, includeSelf)
                : IsEnemy(asc);
            if (!valid) continue;
            if (filter != null && !filter(asc)) continue;   // ej. la Presa: solo jugadores

            // Se apunta al TORSO, no a los pies: el transform de un personaje está en el
            // suelo, y desde una cámara que mira un poco hacia abajo los pies de alguien
            // cercano quedan bastante más lejos de la retícula que su cuerpo.
            Vector3 toTarget = asc.transform.position + Vector3.up * TargetCenterHeight - aimOrigin;
            if (toTarget.sqrMagnitude < 0.0001f) continue;

            float align = Vector3.Dot(aimDir, toTarget.normalized);
            if (align > bestAlign) { bestAlign = align; best = asc; } // el más centrado
        }
        return best;
    }

    // Largo del rayo con el que se resuelve la DIRECCIÓN de la mira. No limita el
    // alcance de nada: solo sirve para que el punto quede lo bastante lejos como para
    // que la dirección sea estable.
    private const float AimRayLength = 200f;

    // Cuánto más alcance y ángulo acepta el servidor que el dueño remoto (ver la tolerancia
    // de red en FindBestTargetInAim).
    private const float ServerRangeGrace = 1.5f;
    private const float ServerAngleGrace = 10f;

    // A qué altura del transform está el centro de un personaje. Los modelos del
    // proyecto tienen el pivote en los pies y miden algo menos de dos metros.
    private const float TargetCenterHeight = 1f;
}
