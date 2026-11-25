using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet;
using FishNet.Object;

// ============================================================
// GA_ProjectileShoot — EL DISPARO (la secuencia, el momento de soltar y el lanzamiento)
//
// Una parte de GA_ProjectileShoot (partial class). La clase, sus campos del Inspector y sus
// eventos están en GameplayAbilities/GenericsAbilities/GA_ProjectileShoot.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class GA_ProjectileShoot
{

    // =========================================================
    // DISPARO (la secuencia, el momento de soltar y el lanzamiento)
    // =========================================================

    // Espera SpawnDelay (ajustado por velocidad de ataque), suelta el
    // proyectil, espera el remate de la animación, y termina.
    // SE PUEDE CORTAR A MITAD, si el asset trae IsInterruptible. Hay dos momentos y
    // significan cosas distintas:
    //
    //   · ANTES de soltar  → el proyectil no sale. El cooldown ya pagado no se devuelve:
    //     cancelaste, y cancelar cuesta. Es la finta.
    //   · DESPUÉS de soltar → el proyectil ya está en el aire y se queda. Lo único que
    //     se saltea es el remate de la animación, así se puede encadenar otra habilidad
    //     de inmediato en vez de esperar a que el brazo vuelva.
    //
    // En los dos casos se sale SIN EndAbility: el "fin" lo manda la habilidad nueva, que
    // es la que pasa a mandar (ver GameplayAbility.IsInterruptible).
    private IEnumerator ShootSequence(bool fromAim = false)
    {
        ResolveThrowTiming(fromAim, out float releaseDelay, out float backswing);

        int cancelSerial = OwnerASC != null ? OwnerASC.CancelSerial : 0;

        // Los guardianes (las tareas, 20 de noviembre de 2026): la interrupción (otra habilidad, si es interrumpible) corta
        // el amago y el remate; la ráfaga la cortan además morir y el aturdido. Los dos recuerdan el cancelSerial de AHORA.
        AbilityGuard interrupt = Guard(EStopWhen.Interrupted);
        AbilityGuard burst     = Guard(EStopWhen.Interrupted | EStopWhen.Dies | EStopWhen.Stunned);

        if (releaseDelay > 0f) yield return WaitSeconds(releaseDelay, interrupt);

        if (Cancelled(cancelSerial))
        {
            // Nunca se escondió el arma (eso pasa al soltar), así que no hay nada que
            // devolver: solo no sale el proyectil. Y tampoco se cobra nada — la finta
            // sale gratis en cooldown, ver abajo por qué eso no se puede abusar.
            yield break;
        }

        // EL COOLDOWN EMPIEZA ACÁ, no al apretar el botón.
        //
        // Cortar el lanzamiento antes de tiempo te dejaba igual sin habilidad por varios
        // segundos: pagabas el precio de un hacha que nunca salió. Cobrando al soltar, lo
        // que se paga es el hacha, no la intención.
        //
        // POR QUÉ NO SE PUEDE ABUSAR: para cancelar hay que activar OTRA habilidad, y esa
        // tiene su propio costo. No existe un botón de "cancelar" suelto. Y repetir el
        // botón del lanzamiento no reinicia nada, porque una habilidad no se corta a sí
        // misma (ver PlayerController.CheckAbilityButton). Lo peor que se puede hacer es
        // amagar gastando el cooldown de otra cosa, que es un mal negocio.
        //
        // EFECTO EN EL BALANCE: entre dos hachas ahora pasa (tiempo de soltar + cooldown)
        // en vez de solo el cooldown. Son unos 0,7 s más en el hacha del Bárbaro. Si se
        // siente lenta, se baja su CooldownDuration.
        CommitAbility();

        SpawnProjectile();

        // El arma real desaparece justo al soltar: de ahi en mas lo unico que vuela es
        // el proyectil (ver HideWeaponWhileFlying).
        if (HideWeaponWhileFlying) SetOwnerWeaponVisible(false);

        // RÁFAGA (BurstCount): los demás, de a uno. El remate corre desde el primero, así que
        // la ráfaga no alarga el ataque si entra en él.
        float waited = 0f;
        for (int shot = 1; shot < BurstCount; shot++)
        {
            float started = Time.time;
            yield return WaitSeconds(shot * BurstInterval - waited, burst);
            waited += Time.time - started;
            if (burst.Stopped)
            {
                ShowWeaponAfterThrow();
                if (!Cancelled(cancelSerial)) EndAbility();
                yield break;
            }
            SpawnProjectile();
        }

        // El remate, cortable: si otra habilidad lo interrumpe, el arma vuelve YA a la mano (la rutina que la devolvía
        // no va a terminar) y el fin lo manda la nueva.
        if (backswing > waited) yield return WaitSeconds(backswing - waited, interrupt);
        if (interrupt.Stopped)
        {
            ShowWeaponAfterThrow();
            yield break;
        }

        ShowWeaponAfterThrow();

        EndAbility();
    }

    private bool Cancelled(int cancelSerial)
        => IsInterruptible && OwnerASC != null && OwnerASC.CancelSerial != cancelSerial;

    // EL ARMA VUELVE CON EL BUMERÁN (20 de noviembre de 2026). Con "Esconder el arma mientras vuela" y "Vuelve al
    // lanzador", el arma no reaparece al terminar el remate sino cuando el último proyectil de este lanzamiento se va
    // (vuelve a la mano, choca o vence: GC_Projectile avisa con OnThrownProjectileGone).
    private bool WaitsForReturn => HideWeaponWhileFlying && Returns && !IsEcho;

    // Al terminar (o cortar) el lanzamiento: el arma vuelve a la mano, salvo que haya un bumerán en el aire.
    private void ShowWeaponAfterThrow()
    {
        if (!HideWeaponWhileFlying) return;
        if (WaitsForReturn && _projectilesOut > 0) return;
        SetOwnerWeaponVisible(true);
    }

    // Lo llama el proyectil (servidor) al irse. Con el último afuera, el arma vuelve, también en la pantalla del dueño.
    public void OnThrownProjectileGone()
    {
        if (!WaitsForReturn || _projectilesOut <= 0) return;
        if (--_projectilesOut == 0) SetOwnerWeaponVisible(true, includeOwner: true);
    }

    // El dueño hace la MISMA cuenta de su lado y esconde el arma cuando su propia
    // animacion suelta, sin esperar el aviso del servidor — que le llega dos viajes
    // tarde y le dejaba el hacha en la mano mientras el proyectil ya volaba.
    public override void PredictOwnerVisuals(PlayerController pc)
    {
        if (pc == null || !HideWeaponWhileFlying) return;

        ResolveThrowTiming(false, out float releaseDelay, out float backswing);
        pc.PredictWeaponHide(releaseDelay, WaitsForReturn ? float.PositiveInfinity : releaseDelay + backswing);
    }

    // Cuando sale el proyectil (releaseDelay) y cuanto dura el remate hasta devolver el
    // arma a la mano (backswing). Lo calculan por igual el servidor y el dueño: los dos
    // datos salen del asset y del ritmo de ataque, que estan de los dos lados.
    //
    // fromAim: el lanzamiento soltado después de apuntar. Los tiempos salen del clip de
    // soltar (que ya arranca con el arma atrás), a su velocidad natural.
    private void ResolveThrowTiming(bool fromAim, out float releaseDelay, out float backswing)
    {
        if (fromAim)
        {
            AnimationClip clip = ReleaseClip;
            releaseDelay = AimReleaseDelay;
            if (clip != null)
                foreach (AnimationEvent evt in clip.events)
                    if (evt.functionName == HitFrameEventName) { releaseDelay = evt.time; break; }

            backswing = clip != null ? Mathf.Max(0.1f, clip.length - releaseDelay) : 0.4f;
            return;
        }

        float speedMultiplier = 1f;
        float atkSpeedStat = OwnerASC != null ? OwnerASC.GetAttributeValue(EAttributeType.AtkSpeed) : 0f;
        if (atkSpeedStat > 0) speedMultiplier = 1f / atkSpeedStat;

        // EL MOMENTO DE SOLTAR SALE DEL PROPIO CLIP, no de un numero a mano.
        //
        // Es el mismo mecanismo que ya usan los ataques melee: el evento
        // AnimationEvent_HitFrame marca en la animacion el frame exacto en el que la
        // mano suelta, y el servidor lo lee del asset (no depende de que ningun
        // cliente reporte nada). Asi el proyectil nace EXACTAMENTE en el frame en que
        // el arma sale de la mano, y como el arma real se esconde en ese mismo frame
        // —sin ningun yield en el medio— el relevo es invisible: lo que se ve es una
        // sola hacha que pasa de la mano al aire.
        //
        // Ademas se escala con ResolveAnimationSpeed, asi que si el ritmo de ataque
        // comprime el clip el disparo se adelanta en la misma proporcion y NUNCA se
        // desfasa. Un SpawnDelay fijo se desincroniza en cuanto cambia la velocidad.
        //
        // Sin eventos en el clip se cae a SpawnDelay, el comportamiento de siempre.
        List<float> releaseTimes = GetHitFrameTimes();

        float animSpeed = ResolveAnimationSpeed();
        if (animSpeed <= 0f) animSpeed = 1f;

        // Sin eventos, SpawnDelay se escala con la velocidad impuesta si la hay (un paso
        // de combo o de un cargado con AnimationSpeedOverride).
        releaseDelay = releaseTimes.Count > 0
            ? releaseTimes[0] / animSpeed
            : SpawnDelay / (AnimationSpeedOverride > 0f ? AnimationSpeedOverride : speedMultiplier);

        float backswingTime = 0.5f;
        backswing = backswingTime / speedMultiplier;

        // Con el arma escondida hay que esperar a que el CLIP TERMINE antes de
        // devolverla. El hueso de prop se va con el lanzamiento y regresa solo a la
        // mano dentro de la propia animacion: si la prendemos a mitad de ese regreso,
        // se ve el arma "volviendo" por el aire hasta la mano — el gesto raro del
        // final. Al esperar al final del clip, reaparece recien cuando el hueso ya
        // esta de vuelta en su lugar y el cambio no se nota.
        //
        // La espera solo se extiende cuando el arma esta escondida; el resto de los
        // disparos (magos, etc.) conservan el remate de siempre.
        if (HideWeaponWhileFlying && AnimationClip != null)
            backswing = Mathf.Max(backswing, (AnimationClip.length / animSpeed) - releaseDelay);
    }

    // Dispara el proyectil desde la mano del dueño hacia su punto de mira (y sus ecos, si tiene).
    private void SpawnProjectile()
    {
        // Sin prefab no hay nada que disparar. Sin este guard, Instantiate(null) tira
        // una excepción que corta la corutina y deja al jugador trabado en "atacando"
        // hasta que salta el watchdog — un fallo de configuración difícil de leer.
        if (ProjectilePrefab == null)
        {
            Debug.LogWarning($"[{AbilityName}] no tiene ProjectilePrefab asignado: no se dispara nada.");
            return;
        }

        Vector3 spawnPos = OwnerASC.transform.TransformPoint(SpawnOffset);

        // La dirección la resuelve el PlayerController: no es (mira - mano) a secas,
        // porque de cerca eso sale torcido (ver GetLaunchDirection).
        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        Vector3 launchDirection = pc != null ? pc.GetLaunchDirection(spawnPos, MinConvergeDistance)
                                             : OwnerASC.transform.forward;

        // En ráfaga, la carga de la definitiva se reparte: el ataque entero carga lo mismo.
        LaunchProjectile(spawnPos, launchDirection, UltimateChargeAmount / Mathf.Max(1, BurstCount));

        // Los ecos (la Proyección arcana): el mismo proyectil desde cada aliado proyectado.
        FireEchoes<GA_ProjectileShoot>((echo, from) => echo.SpawnEchoProjectile(from));
    }

    // La copia de un eco: sale del pecho de 'from' hacia donde apunta el dueño, sin cargar la
    // definitiva. Corre en la instancia del eco (IsEcho, con su DamageScale).
    private void SpawnEchoProjectile(AbilitySystemComponent from)
    {
        if (ProjectilePrefab == null || from == null) return;

        PlayerController pc = OwnerASC.GetComponent<PlayerController>();
        Vector3 origin = from.transform.position + Vector3.up * SpawnOffset.y;
        Vector3 aim    = pc != null ? pc.GetAimPoint() : OwnerASC.transform.position + OwnerASC.transform.forward * 30f;
        Vector3 dir    = aim - origin;
        if (dir.sqrMagnitude < 0.01f) dir = OwnerASC.transform.forward;
        dir.Normalize();

        LaunchProjectile(origin + dir * Mathf.Max(0.3f, SpawnOffset.z), dir, 0f);
    }

    // Instancia el proyectil en 'spawnPos', lo spawnea en red, lo inicializa con esta habilidad y
    // lo lanza en 'launchDirection'.
    private void LaunchProjectile(Vector3 spawnPos, Vector3 launchDirection, float ultCharge)
    {
        Quaternion spawnRot = Quaternion.LookRotation(launchDirection);
        GameObject newProjectile = Instantiate(ProjectilePrefab, spawnPos, spawnRot);

        // Esto corre en el servidor. Un Instantiate() normal solo crea el
        // objeto en ESTE proceso — nunca llega a los clientes (por eso el
        // proyectil nunca se veía para el dueño remoto). Hay que spawnearlo
        // en red igual que cualquier otro NetworkObject.
        NetworkObject projectileNob = newProjectile.GetComponent<NetworkObject>();
        if (projectileNob != null)
            InstanceFinder.ServerManager.Spawn(projectileNob);
        else
            Debug.LogWarning("[GA_ProjectileShoot] El ProjectilePrefab no tiene NetworkObject — no se va a replicar a los clientes.");

        GC_Projectile projectileScript = newProjectile.GetComponent<GC_Projectile>();
        if (projectileScript != null)
        {
            // El swap visual (cubo -> arma real) lo resuelve GC_Projectile
            // internamente en CADA peer a partir de quién disparó (ver
            // _shooterNob ahí) — llamarlo acá de nuevo solo pasaría en el
            // servidor y duplicaría el arma clonada en esa copia.
            //
            // Le pasamos 'this': al impactar (siempre en el servidor), el proyectil le
            // aplica la lista de efectos de esta habilidad a quien toque
            // (ApplyHitEffects) y le pide a NetworkASC los VFX de impacto en todos los
            // peers. Si la lista no tiene nada para los aliados, el proyectil los sigue
            // ignorando como siempre; si tiene, se lo aplica a los que atraviese (la
            // estela del Castigo divino, que cura al pasar).
            projectileScript.Initialize(OwnerASC, ultCharge, this, LifeTime);
            if (WaitsForReturn) _projectilesOut++;   // el arma espera a que vuelva (ver OnThrownProjectileGone)
        }

        Rigidbody rb = newProjectile.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = launchDirection * LaunchForce;
            if (StraightFlightTime > 0f && rb.useGravity && projectileScript != null)
                projectileScript.HoldGravity(StraightFlightTime);
            if (AddSpin) rb.AddRelativeTorque(Vector3.right * 1000f);
        }
    }

    // ¿El proyectil cae? Lo decide el Rigidbody del prefab (por defecto, sí). Lo usan el
    // gizmo de abajo y la trayectoria que ve el jugador al tensar el arco (TrajectoryPreview).
    public bool ProjectileUsesGravity
    {
        get
        {
            if (ProjectilePrefab == null) return true;
            Rigidbody rb = ProjectilePrefab.GetComponent<Rigidbody>();
            return rb == null || rb.useGravity;
        }
    }
}
