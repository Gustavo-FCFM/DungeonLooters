using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet;
using FishNet.Object;

// ============================================================
// GA_ProjectileShoot
//
// Dispara un proyectil físico (GC_Projectile) hacia el punto de mira
// del dueño. Esta habilidad solo se encarga de crear/lanzar el
// proyectil — toda la lógica de impacto (daño, VFX) vive en
// GC_Projectile, que recibe los datos necesarios en Initialize().
//
// APUNTAR ANTES DE LANZAR (casilla AimBeforeThrow, para los lanzamientos de arma):
// apretar levanta el arma y la deja atrás (AimHoldClip), la cámara del dueño se acerca a
// la mira y el modelo se le esconde (ThirdPersonOrbitCam); soltar lanza YA: se reproduce
// el lanzamiento desde esa pose (AimReleaseClip, sin el impulso hacia atrás) y el
// proyectil sale en el momento en que el clip suelta. Es un mantenido (IHoldAbility)
// SOLO con la casilla puesta: los demás disparos (castigos, el arco del combo del
// Clérigo) siguen saliendo al apretar.
// ============================================================
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 25 de noviembre de 2026). En este archivo: TODOS los campos
// del Inspector y Activate; lo demás, en GameplayAbilities/GenericsAbilities/GA_ProjectileShoot/:
//   · Aim      apuntar antes de lanzar: mantener, soltar, cancelar y el arma en la mano
//   · Shoot    el disparo: la secuencia, cuándo suelta, crear y lanzar el proyectil (y los ecos)
//   · Impact   lo que pasa al chocar: la explosión, la zona y los efectos "al primer golpe"
//   · Gizmos   la trayectoria dibujada en el editor
//   · Legacy   datos viejos → formato nuevo (al cargar el asset)
[CreateAssetMenu(fileName = "GA_ProjectileShoot", menuName = "GAS/Generics/Projectile Shoot")]
public partial class GA_ProjectileShoot : GameplayAbility, IHoldAbility
{
    [Section("Proyectil")]
    [Tooltip("El prefab que vuela (con GC_Projectile, NetworkObject y Rigidbody). Al chocar le " +
             "aplica la lista de efectos de ESTA habilidad a quien toque.")]
    [Label("Prefab del proyectil")]
    public GameObject ProjectilePrefab;
    [Tooltip("Velocidad de salida del proyectil.")]
    [Label("Velocidad (m/s)")]
    public float      LaunchForce  = 20f;
    [Tooltip("Punto de salida, en espacio local del dueño (X = derecha, Y = arriba, Z = adelante): la mano.")]
    [Label("Punto de salida (m)")]
    public Vector3    SpawnOffset  = new Vector3(0.5f, 1.5f, 1.0f);
    [Tooltip("Si el proyectil gira sobre sí mismo mientras vuela (solo estético).")]
    [Label("Girar en el aire")]
    public bool       AddSpin      = true;

    [Tooltip("Esconde el arma que el personaje tiene en la mano mientras el proyectil vuela.\n\n" +
             "Hace falta en los LANZAMIENTOS: el rig de Kevin Iglesias anima el hueso de prop " +
             "(B-handProp) como si el arma se soltara, asi que sin esto se ve volar el arma real " +
             "del jugador ADEMAS del proyectil. Se apaga al soltar y se vuelve a prender al " +
             "terminar la habilidad.\n\n" +
             "Apagado (lo normal) para magos y cualquier disparo que no suelte el arma.")]
    [Label("Esconder el arma mientras vuela")]
    public bool       HideWeaponWhileFlying = false;

    [Tooltip("Alcance del proyectil expresado en SEGUNDOS de vuelo (con velocidad constante, " +
             "alcance = LifeTime × LaunchForce). Pasado ese tiempo se despawnea solo aunque no haya " +
             "chocado con nada. 0 = usar el default del prefab (5s).\n\n" +
             "Para una estela/rayo que avanza lento y no cae, ponele al Rigidbody del prefab " +
             "Use Gravity en false y bajá LaunchForce.")]
    [Label("Tiempo de vuelo (s)")]
    public float LifeTime = 0f;

    [Tooltip("Segundos que vuela DERECHO hacia la mira, sin caer; después la gravedad lo baja " +
             "(las flechas del Explorador: van exactas a la mira hasta su alcance, = esto × " +
             "velocidad, y de ahí caen). 0 = cae desde que sale (si el prefab usa gravedad).")]
    [Label("Vuelo recto (s)")]
    public float StraightFlightTime = 0f;

    [Tooltip("Distancia mínima a la que se considera que la retícula y el proyectil se " +
             "juntan. La retícula sale de la cámara y el proyectil de la mano: apuntando " +
             "a algo muy cerca, esas dos líneas se abren y el tiro sale visiblemente " +
             "torcido. Con esto se apunta a un punto del MISMO rayo pero a esta " +
             "distancia, así el tiro sale derecho y igual pasa por donde apuntaste. " +
             "Subirlo endereza más el tiro de cerca; bajarlo lo hace más literal.")]
    [Label("Convergencia mínima (m)")]
    public float MinConvergeDistance = 4f;

    [Tooltip("Se deshace al tocar al PRIMER enemigo: le pega a uno solo (los Misiles arcanos del " +
             "Mago). Apagado (lo de siempre) = lo atraviesa y sigue, y le pega a cada enemigo que " +
             "cruce, una vez a cada uno. A los aliados los atraviesa igual.")]
    [Label("Se deshace en el primer enemigo")]
    public bool StopAtFirstEnemy = false;

    [Tooltip("Cuántos proyectiles salen por ataque, uno tras otro, cada uno hacia donde se esté " +
             "mirando en ese momento (los Misiles arcanos del Mago: 3). Cada uno pega con la lista de " +
             "efectos completa; el cooldown se cobra una vez (con el primero) y la carga de la " +
             "definitiva se reparte entre todos. Los corta otra habilidad, un aturdido o morir. " +
             "1 = uno solo, lo de siempre.")]
    [Label("Ráfaga (proyectiles)")]
    [Min(1)]
    public int BurstCount = 1;

    [ShowIf(nameof(BurstCount), 1, true)]
    [Tooltip("Segundos entre un proyectil de la ráfaga y el siguiente (no cambia con el ritmo de " +
             "ataque). Más alto = más fácil de esquivar.")]
    [Label("Entre proyectiles (s)")]
    public float BurstInterval = 0.2f;

    // REBOTE Y VUELTA (19 de noviembre de 2026, de las "Mejoras del GAS"). Lo resuelve el proyectil
    // (GC_Projectile, en el servidor) leyendo estos campos. Todo en 0 / apagado = lo de siempre.
    [Section("Rebote y vuelta")]
    [Tooltip("Cuántas veces rebota contra una pared o el piso en vez de deshacerse (sale reflejado, " +
             "con la misma velocidad). Al acabarse los rebotes, el próximo choque lo deshace (o lo hace " +
             "volver, si \"Vuelve al chocar\"). 0 = se deshace al primer choque, lo de siempre.")]
    [Label("Rebota en paredes (veces)")]
    [Min(0)]
    public int WallBounces = 0;

    [Tooltip("Cuántas veces SALTA de un enemigo al siguiente: al pegarle a uno, sale derecho hacia el " +
             "enemigo más cercano que todavía no tocó (a la vista y dentro del alcance de abajo). Cada " +
             "uno recibe la lista de efectos completa. Sin a quién saltar, sigue de largo (o se deshace, " +
             "con \"Se deshace en el primer enemigo\"). 0 = no salta.")]
    [Label("Salta entre enemigos (veces)")]
    [Min(0)]
    public int EnemyBounces = 0;

    [ShowIf(nameof(EnemyBounces), 0, true)]
    [Tooltip("Hasta dónde busca al próximo enemigo para saltar, desde el que acaba de golpear.")]
    [Label("Alcance del salto (m)")]
    public float EnemyBounceRange = 8f;

    [Tooltip("Vuelve al que lo tiró, como un bumerán: al llegar a su distancia máxima da la vuelta y " +
             "lo persigue hasta llegarle (ahí desaparece). A la vuelta atraviesa las paredes (no se " +
             "pierde detrás de una) y no rebota.")]
    [Label("Vuelve al lanzador")]
    public bool Returns = false;

    [ShowIf(nameof(Returns))]
    [Tooltip("A qué distancia del que lo tiró da la vuelta.")]
    [Label("Da la vuelta a (m)")]
    public float ReturnDistance = 12f;

    [ShowIf(nameof(Returns))]
    [Tooltip("Al chocar con algo que lo desharía (una pared sin rebotes, o el primer enemigo con \"Se " +
             "deshace en el primer enemigo\") da la vuelta en vez de deshacerse. Apagado = ese choque " +
             "lo deshace igual.")]
    [Label("Vuelve al chocar")]
    public bool ReturnsOnHit = true;

    [ShowIf(nameof(Returns))]
    [Tooltip("A la vuelta les vuelve a pegar a los que ya golpeó a la ida (cada uno, una vez más).")]
    [Label("Pega otra vez a la vuelta")]
    public bool HitsAgainOnReturn = true;

    [Section("Al chocar")]
    [Tooltip("Una explosión donde choca (contra un enemigo, una pared o un escudo): un GA_InstantAoE " +
             "que se reusa tal cual —su radio, sus efectos y su VFX—, sin su costo, su cooldown ni su " +
             "animación (la Bola de fuego del Mago). La lista de efectos de ESTA habilidad sigue yendo " +
             "al que toca. Vacío = nada.")]
    [Label("Explosión (GA_InstantAoE)")]
    public GA_InstantAoE ImpactExplosion;

    [Tooltip("Una zona que queda en el piso donde choca: un GA_ContinuousAoE tal cual (la zona de " +
             "fuego del dragón del Monje). Vacío = nada.")]
    [Label("Zona que deja (GA_ContinuousAoE)")]
    public GA_ContinuousAoE ImpactZone;

    [Section("Potenciado (al cruzar un muro)")]
    [Tooltip("La explosión que suelta si en el camino CRUZÓ algo que potencia proyectiles (un " +
             "Summon_ProjectileBooster: el Muro de fuego del mismo Mago). La Gran bola de fuego de la " +
             "Evocación: más grande, más daño, más quemadura. Vacío = no se potencia.")]
    [Label("Explosión potenciada (GA_InstantAoE)")]
    public GA_InstantAoE BoostedExplosion;

    [ShowIf(nameof(BoostedExplosion))]
    [Tooltip("Cuánto crece el proyectil a la vista (y su choque) al potenciarse.")]
    [Label("Tamaño potenciado (×)")]
    public float BoostedScale = 1.5f;

    // ¿Se potencia al cruzar un muro? Lo pregunta el proyectil (GC_Projectile) en el servidor.
    public bool CanBeBoosted => BoostedExplosion != null;

    // Las copias con este dueño de la explosión y la zona, guardadas entre lanzamientos.
    [System.NonSerialized] private GA_InstantAoE    _impactExplosion;
    [System.NonSerialized] private GA_InstantAoE    _boostedExplosion;
    [System.NonSerialized] private GA_ContinuousAoE _impactZone;

    [Section(AbilitySection.Timing)]
    [Tooltip("Espera entre activar la habilidad y que el proyectil realmente salga, si el clip " +
             "no trae el evento AnimationEvent_HitFrame (con el evento, sale en ese frame).")]
    [Label("Espera al lanzar (s)")]
    public float SpawnDelay = 0.4f;

    [Section(AbilitySection.Effects)]
    [Tooltip("Segundos mínimos antes de volver a aplicarle los efectos 'al primer golpe' al MISMO " +
             "enemigo (el primero que toca cada proyectil). Sin esto, un aturdido de 1 s con " +
             "ataques cada 0.8 s lo deja aturdido para siempre. 0 = sin límite.")]
    [Label("Espera del primer golpe por objetivo (s)")]
    public float FirstHitCooldownPerTarget = 3f;

    [Section("Apuntar (mantener y soltar)")]
    [Tooltip("Mantener el botón apunta (el arma atrás, la cámara cerca de la mira, el modelo " +
             "oculto para el que apunta) y SOLTAR lanza al instante. Apagado = sale al apretar, " +
             "como siempre (los castigos, el arco del combo del Clérigo).")]
    [Label("Apuntar antes de lanzar")]
    public bool AimBeforeThrow = false;

    [ShowIf(nameof(AimBeforeThrow))]
    [Tooltip("Pose de apuntar, en bucle, mientras se mantiene (ej. HumanM@ThrowWeapon01_R - Hold).")]
    [Label("Clip: apuntando (bucle)")]
    public AnimationClip AimHoldClip;

    [ShowIf(nameof(AimBeforeThrow))]
    [Tooltip("El lanzamiento DESDE la pose de apuntar, sin el impulso hacia atrás: se reproduce " +
             "al soltar. Si trae el evento AnimationEvent_HitFrame, el proyectil sale ahí. " +
             "Vacío = se usa el AnimationClip entero.")]
    [Label("Clip: soltar")]
    public AnimationClip AimReleaseClip;

    [ShowIf(nameof(AimBeforeThrow))]
    [Tooltip("Si el clip de soltar no trae el evento: segundos desde que se suelta hasta que " +
             "sale el proyectil.")]
    [Label("Espera al soltar (s)")]
    public float AimReleaseDelay = 0.1f;

    [ShowIf(nameof(AimBeforeThrow))]
    [Tooltip("Corte de seguridad: si el aviso de soltar nunca llega, lanza a los tantos segundos.")]
    [Label("Corte de seguridad (s)")]
    public float AimSafetyTimeout = 30f;

    [Tooltip("Mientras se apunta (Apuntar antes de lanzar), o mientras se tensa el arco si es una " +
             "fase de un ataque cargado, el que apunta ve la curva que va a hacer el proyectil y un " +
             "anillo donde va a chocar. Solo en su pantalla.")]
    [Label("Curva para el que apunta")]
    public bool ShowTrajectoryWhileAiming = true;

    // =========================================================
    // ACTIVAR
    // =========================================================

    // Donde choca es un punto: acepta VFX de impacto.
    public override bool SupportsVisualTiming(EVisualWhen when) => true;

    [System.NonSerialized] private bool _aiming;
    [System.NonSerialized] private float _aimStartedAt;

    // CANALIZANDO MIENTRAS APUNTA (sección Canalizar; el Haz guía): desde cuándo se puede
    // soltar, y si ya soltó antes de tiempo (sale apenas se llena). Solo servidor.
    [System.NonSerialized] private float _castReadyAt;
    [System.NonSerialized] private bool  _releaseQueued;

    // IHoldAbility: solo con la casilla puesta se maneja como mantenido.
    public bool UsesHoldInput => AimBeforeThrow;
    public bool IsHolding     => _aiming;
    // Sin pose de apuntar propia, la de canalizar (el Haz guía no tiene una de apuntar).
    public AnimationClip HoldLoopClip   => AimBeforeThrow ? (AimHoldClip != null ? AimHoldClip : CastLoopAnimation) : null;
    public AnimationClip HoldStartClip  => null;
    public AnimationClip HoldEndClip    => null;   // al soltar va el clip de soltar, por la ranura de acción
    public AnimationClip HoldImpactClip => null;

    public override bool AimsCameraWhileHeld => AimBeforeThrow;

    // La curva mientras se apunta (ver TrajectoryPreview).
    public override GA_ProjectileShoot ProjectileToPreview(float heldSeconds)
        => AimBeforeThrow && ShowTrajectoryWhileAiming ? this : null;

    // Cuánto vuela antes de desaparecer: su "Tiempo de vuelo", o el del proyectil si está en 0.
    public float FlightSeconds => LifeTime > 0f ? LifeTime : GC_Projectile.DefaultLifeTime;

    // El clip que se ve al soltar (y del que salen los tiempos del lanzamiento apuntado).
    public AnimationClip ReleaseClip => AimReleaseClip != null ? AimReleaseClip : AnimationClip;

    // Para el RPC de animación: el paso 1 es soltar después de apuntar.
    public override AnimationClip GetStepAnimationClip(int sequenceIndex, int stepIndex)
        => stepIndex == 1 ? ReleaseClip : AnimationClip;

    // Valida, reproduce la animación de disparo (con o sin PlayerController)
    // y arranca la secuencia de disparo.
    public override void Activate()
    {
        if (!IsServer) return;   // ← NUEVO
        if (!CanActivate()) return;

        if (AimBeforeThrow)
        {
            StartAiming();
            return;
        }

        // OJO: acá NO se cobra. El cooldown empieza cuando el hacha SALE de la mano, no
        // cuando arranca la animación — ver ShootSequence.

        if (OwnerASC != null)
        {
            PlayerController pc = OwnerASC.GetComponent<PlayerController>();
            if (pc != null)
            {
                pc.RotateToAim();   // mirar hacia donde se lanza, igual que los ataques melee
                pc.PlayAnimation(this);
            }
            else
            {
                // Sin PlayerController (un NPC): se le habla directo al Animator. Los
                // parámetros se chequean antes porque el Animator de un NPC no tiene por
                // qué tener el esquema del jugador — el del fantasma, por ejemplo, no
                // tiene "ActionID". Sin este guard, Unity escupe un warning por CADA
                // disparo y termina tapando la consola.
                Animator anim = OwnerASC.GetComponent<Animator>();
                if (anim == null) anim = OwnerASC.GetComponentInChildren<Animator>();
                if (anim != null)
                {
                    if (HasAnimatorParameter(anim, "ActionID", AnimatorControllerParameterType.Int))
                        anim.SetInteger("ActionID", AnimationID);

                    if (HasAnimatorParameter(anim, AnimationTriggerName, AnimatorControllerParameterType.Trigger))
                        anim.SetTrigger(AnimationTriggerName);
                }
            }

            OwnerASC.StartAbilityCoroutine(ShootSequence());

            // Arrancó de verdad, aunque cobre recién al soltar: sin esto el servidor no
            // les manda la animación a los demás (ver StartedThisActivation).
            StartedThisActivation = true;
        }
    }
    [System.NonSerialized] private int _projectilesOut;

    // =========================================================
    // DATOS VIEJOS (solo para pasarlos a las listas; ver GameplayAbility.UpgradeLegacyData)
    // =========================================================

    [SerializeField, HideInInspector] private GameplayEffect InstantDamageEffect;
    [SerializeField, HideInInspector] private GameplayEffect DurationEffect;
    [SerializeField, HideInInspector] private List<GameplayEffect> AdditionalEffects;
    [UnityEngine.Serialization.FormerlySerializedAs("AllyEffects")]
    [SerializeField, HideInInspector] private List<GameplayEffect> TargetEffects;
    [SerializeField, HideInInspector] private List<GameplayEffect> FirstHitEffects;
    [SerializeField, HideInInspector] private GameObject ImpactVFX;
}
