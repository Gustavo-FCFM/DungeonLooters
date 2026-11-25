using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — GIRO DEL MODELO (molinete)
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    // Arranca (o corta, con 0) el giro del modelo sobre su eje vertical.
    public void SetModelSpin(float degreesPerSecond)
    {
        _modelSpinSpeed = degreesPerSecond;

        // Al cortar, el modelo vuelve a mirar como la raíz: si lo dejáramos en un
        // ángulo cualquiera, el personaje quedaría torcido respecto de su propia mira
        // hasta el próximo giro.
        if (Mathf.Approximately(degreesPerSecond, 0f))
        {
            _modelSpinAngle = 0f;
            if (characterAnimator != null)
                characterAnimator.transform.localRotation = Quaternion.identity;
        }
    }

    private void TickTrajectoryPreview()
    {
        GA_ProjectileShoot shot = _holdAbility != null
            ? _holdAbility.ProjectileToPreview(Time.time - HoldStartedAt)
            : null;

        if (shot == null)
        {
            if (_trajectory != null) _trajectory.Hide();
            return;
        }

        if (_trajectory == null && !_trajectorySearched && _ownerCamera != null)
        {
            _trajectorySearched = true;
            _trajectory = _ownerCamera.GetComponentInChildren<TrajectoryPreview>(true);
        }
        if (_trajectory == null) return;

        Vector3 start = transform.TransformPoint(shot.SpawnOffset);
        Vector3 dir   = GetLaunchDirection(start, shot.MinConvergeDistance);
        _trajectory.Show(start, dir * shot.LaunchForce, shot.ProjectileUsesGravity, shot.StraightFlightTime,
                         shot.FlightSeconds, transform.root, shot.WallBounces);
    }

    // Va en LateUpdate y no en Update: el Animator escribe las poses en Update, así que
    // rotar antes lo pisaría el propio animator en el mismo frame.
    private void LateUpdate()
    {
        TickFootsteps();
        TickStunAnimation();
        TickStanceVisuals();

        // Mantener un lanzamiento acerca la cámara a la mira (solo el dueño tiene cámara).
        if (_orbitCam != null)
        {
            _orbitCam.Aiming = _holdAbility != null && _holdAbility.AimsCameraWhileHeld;
            _orbitCam.ZoomFov = _holdAbility != null ? _holdAbility.HeldCameraZoom(Time.time - HoldStartedAt) : 1f;
        }

        if (IsOwner) TickTrajectoryPreview();

        // Rotación del MODELO: la inclinación en una pared y el giro del molinete, juntos.
        TickModelLean();
        if (characterAnimator == null) return;

        bool spinning = !Mathf.Approximately(_modelSpinSpeed, 0f);
        bool leaning  = _leanUpLocal != Vector3.up;
        if (!spinning && !leaning && !_modelLeaned) return;   // derecho y quieto: no se toca

        if (spinning) _modelSpinAngle += _modelSpinSpeed * Time.deltaTime;
        characterAnimator.transform.localRotation =
            Quaternion.FromToRotation(Vector3.up, _leanUpLocal) * Quaternion.Euler(0f, _modelSpinAngle, 0f);

        // Un frame más después de enderezarse, para dejarlo exactamente derecho.
        _modelLeaned = leaning;
    }

    // Anima un CANALIZADO (una habilidad que se sostiene un rato: el molinete del
    // bárbaro) reusando las ranuras del mantenido: inicio → bucle → final.
    //
    // Reusa esas ranuras y NO la interfaz IHoldAbility a propósito. Los estados del
    // Animator ya existen y hacen exactamente esta forma, pero implementar la interfaz
    // haría que el resto del sistema tratara la habilidad como un mantenido de botón
    // (IsHoldingAbility, la excepción de FaceCameraForward, el botón de ultimate), que
    // no es lo que un canalizado por tiempo necesita.
    //
    // Sin guard de dueño: la usan por igual el dueño y la réplica a observadores.
    public void ApplyChannelAnimation(AnimationClip start, AnimationClip loop, AnimationClip end)
    {
        if (characterAnimator == null || loop == null) return;

        if (!HasHoldSlots)
        {
            if (!_warnedNoHoldSlots)
            {
                _warnedNoHoldSlots = true;
                Debug.LogWarning($"[PlayerController] Hay una habilidad canalizada con clips, pero el " +
                                 $"controller no tiene la ranura del bucle ('{HoldLoopSlotName}'). " +
                                 $"La habilidad funciona igual, pero sin animación sostenida.");
            }
            return;
        }

        SetHoldClips(start, loop, end);

        // El bool ANTES del trigger, por lo mismo que en ApplyHoldAnimation: si se
        // seteara después, el Animator podría evaluar la salida del bucle con el valor
        // viejo y salirse en el primer frame.
        if (!string.IsNullOrEmpty(HoldingParam)) characterAnimator.SetBool(HoldingParam, true);

        characterAnimator.SetInteger("ActionID", HoldStateID);
        if (!string.IsNullOrEmpty(HoldTrigger)) characterAnimator.SetTrigger(HoldTrigger);
    }

    // Corta el canalizado: baja el bool y para el giro. La transición bucle → final la
    // resuelve el Animator solo.
    public void ApplyStopChannelAnimation()
    {
        SetModelSpin(0f);
        ApplyStopHoldAnimation();
    }

    public GameObject GetCurrentMainWeapon() => currentMainWeapon;

    public void PredictWeaponHide(float hideAt, float showAt)
    {
        if (_weaponHideRoutine != null) StopCoroutine(_weaponHideRoutine);
        _weaponHideRoutine = StartCoroutine(WeaponHideRoutine(hideAt, showAt));
    }

    // Corta la anticipación y devuelve el arma a la mano ya mismo. La llama el dueño al
    // arrancar otra habilidad: si cortó el lanzamiento ANTES de soltar, el arma nunca
    // tenía que esconderse; si lo cortó DESPUÉS, tiene que volver ahora y no cuando
    // terminaba un remate que ya no va a correr.
    public void CancelWeaponHide()
    {
        if (_weaponHideRoutine == null) return;

        StopCoroutine(_weaponHideRoutine);
        _weaponHideRoutine = null;
        SetMainWeaponVisible(true);
    }

    private System.Collections.IEnumerator WeaponHideRoutine(float hideAt, float showAt)
    {
        if (hideAt > 0f) yield return new WaitForSeconds(hideAt);
        SetMainWeaponVisible(false);

        float rest = showAt - hideAt;
        if (rest > 0f) yield return new WaitForSeconds(rest);
        SetMainWeaponVisible(true);

        _weaponHideRoutine = null;
    }

    public void SetMainWeaponVisible(bool visible)
    {
        if (currentMainWeapon == null) return;

        foreach (Renderer r in currentMainWeapon.GetComponentsInChildren<Renderer>(true))
        {
            // La estela se maneja sola por AnimationEvents: si la tocáramos acá,
            // volveríamos a prenderla en mitad de un swing que ya la había apagado.
            if (r is TrailRenderer) continue;
            r.enabled = visible;
        }
    }
}
