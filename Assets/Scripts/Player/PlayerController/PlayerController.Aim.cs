using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — HUD, APUNTADO Y UTILIDADES
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    // =========================================================
    // HUD
    // =========================================================

    // Conecta el UI_PlayerHUD de la escena a este personaje (solo si sos
    // el dueño). Si el HUD todavía no existe (carga de escena en curso),
    // reintenta unos frames en vez de fallar directamente.
    private void UpdateHUD()
    {
        if (!base.IsOwner) return;

        UI_PlayerHUD hud = FindFirstObjectByType<UI_PlayerHUD>();
        if (hud != null)
        {
            hud.InitializeHUD(this);
            return;
        }

        // Cuando el jugador se spawnea, OnStartClient/EquipCharacterClass
        // corren en el MISMO frame en que FishNet instancia el objeto. Si
        // el UI_PlayerHUD de la escena todavía no terminó su Awake/Start
        // (o está en otra escena que aún está cargando), FindFirstObjectByType
        // devuelve null y el HUD se quedaría vacío para siempre porque
        // EquipCharacterClass no se vuelve a llamar — por eso reintentamos.
        StartCoroutine(RetryUpdateHUD());
    }

    // Reintenta encontrar el UI_PlayerHUD durante unos frames (ver
    // UpdateHUD) antes de rendirse.
    private System.Collections.IEnumerator RetryUpdateHUD()
    {
        const int   maxAttempts = 30;   // ~0.5s a 60fps
        int         attempts    = 0;

        while (attempts < maxAttempts)
        {
            yield return null;

            UI_PlayerHUD hud = FindFirstObjectByType<UI_PlayerHUD>();
            if (hud != null)
            {
                hud.InitializeHUD(this);
                yield break;
            }

            attempts++;
        }

        Debug.LogWarning("[PlayerController] No se encontró UI_PlayerHUD tras varios intentos.");
    }

    public void LockAim(Vector3 point, float seconds)
    {
        _lockedAimPoint = point;
        _aimLockedUntil = Time.time + Mathf.Max(0.1f, seconds);
    }

    public void UnlockAim() => _aimLockedUntil = 0f;

    public Vector3 GetAimPoint(float maxRange = 100f)
    {
        if (Time.time < _aimLockedUntil) return _lockedAimPoint;
        if (!IsOwner) return NetworkAimPoint;

        Camera cam = MainCamera;
        if (cam == null)
            return transform.position + transform.forward * 10f;

        Ray          ray  = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
        RaycastHit[] hits = Physics.RaycastAll(ray, maxRange);
        float   bestDist  = float.MaxValue;
        Vector3 bestPoint = ray.GetPoint(maxRange);

        // NADA DE LO QUE ESTÉ ENTRE LA CÁMARA Y VOS CUENTA.
        //
        // La cámara va varios metros atrás, así que el rayo atraviesa todo ese tramo
        // antes de llegar al personaje. Un compañero parado justo detrás tuyo —cosa de
        // todos los partidos en un 3c3c3— o una pared contra la que la cámara se apoya
        // daban un punto de mira DETRÁS del jugador: el personaje giraba al revés y lo
        // que lanzabas salía para atrás o de costado.
        //
        // El corte es la distancia a la que queda el propio personaje sobre el rayo.
        float ignoreCloserThan = Vector3.Dot(transform.position + Vector3.up * EyeHeight - ray.origin,
                                             ray.direction);

        foreach (var h in hits)
        {
            if (!h.collider.isTrigger
                && h.collider.transform.root != transform.root
                && h.distance >= ignoreCloserThan
                && h.distance < bestDist)
            {
                bestDist  = h.distance;
                bestPoint = h.point;
            }
        }
        return bestPoint;
    }

    // Hacia dónde lanzar algo que sale de 'muzzle' (la mano, el cañón) para que vaya a
    // donde apunta la retícula.
    //
    // NO ES (puntoDeMira - muzzle) A SECAS, y ese era el problema de los lanzamientos de
    // cerca: la retícula sale de la cámara, que está atrás y al hombro, mientras que el
    // proyectil nace en la mano. Las dos líneas convergen bien de lejos, pero cuanto más
    // cerca está el objetivo más se abren — a dos metros, apuntar al pecho de alguien
    // hacía salir el hacha veinte grados al costado. Se veía torcido aunque pegara.
    //
    // El arreglo es el de siempre en un juego en tercera persona: no se apunta al punto
    // crudo sino a un punto sobre el MISMO rayo pero a una distancia mínima. Así la
    // dirección desde la mano queda casi paralela al rayo de la retícula, que es lo que
    // el jugador espera ver, y el proyectil igual pasa por donde apuntó.
    public Vector3 GetLaunchDirection(Vector3 muzzle, float minConvergeDistance = 4f)
    {
        Vector3 aimOrigin = GetAimOrigin();
        Vector3 aimPoint  = GetAimPoint();

        Vector3 rayDir = aimPoint - aimOrigin;
        if (rayDir.sqrMagnitude < 0.0001f) return transform.forward;
        rayDir.Normalize();

        // La distancia que importa es la que hay del PUNTO A LA MANO, no a la cámara: la
        // cámara está cinco metros atrás, así que un enemigo pegado al personaje ya
        // estaría "lejos" para ella y no se corregiría nada.
        //
        // Si el punto quedó más cerca que el mínimo, se lo empuja por el mismo rayo
        // hasta esa distancia. El proyectil sigue yendo por la línea de la retícula, y
        // como el objetivo está SOBRE esa línea, le pega igual.
        float toMuzzle = Vector3.Distance(aimPoint, muzzle);
        if (toMuzzle < minConvergeDistance)
            aimPoint += rayDir * (minConvergeDistance - toMuzzle);

        Vector3 dir = aimPoint - muzzle;
        return dir.sqrMagnitude > 0.0001f ? dir.normalized : transform.forward;
    }

    // Desde dónde sale el rayo de la mira: la cámara del dueño. En el servidor y en los
    // observadores no hay cámara que valga, así que se usa el que el dueño mandó con el
    // pedido. Mismo patrón que GetAimPoint.
    public Vector3 GetAimOrigin()
    {
        if (IsOwner)
        {
            Camera cam = MainCamera;
            if (cam != null) return cam.transform.position;
        }
        else if (NetworkAimOrigin != Vector3.zero)
        {
            return NetworkAimOrigin;
        }

        // Sin origen recibido —un BOT, que no tiene cámara ni manda pedidos, o una
        // activación server-side— se usan los ojos del personaje. Es lo más parecido a
        // una cámara que tiene, y sin este corte el origen sería el (0,0,0) del mundo y
        // la selección apuntaría a cualquier lado.
        return transform.position + Vector3.up * EyeHeight;
    }

    // Dirección de movimiento del dueño (WASD/stick) en espacio de mundo, plano
    // horizontal y normalizada; Vector3.zero si está quieto. En el dueño se lee
    // en vivo del input; en el servidor/observadores (IsOwner falso) no hay input
    // que leer, así que se usa el último valor que el dueño envió
    // (NetworkMoveDirection) — mismo patrón que GetAimPoint. La usa la esquiva
    // direccional del dash (GA_Dash).
    public Vector3 GetMoveDirection()
    {
        if (!IsOwner) return NetworkMoveDirection;
        if (_input == null || !_input.IsReady) return Vector3.zero;
        Vector2 mv = _input.MoveValue;
        return GetWASDInputVector(mv.x, mv.y);
    }

    // Rota al personaje para mirar hacia GetAimPoint().
    public void RotateToAim() => RotateToAim(GetAimPoint());

    // Rota al personaje para mirar hacia un punto específico (sin
    // inclinar en el eje vertical).
    public void RotateToAim(Vector3 aimPoint)
    {
        // EL CUERPO SE ALINEA CON LA CAMARA, NO CON EL PUNTO APUNTADO.
        //
        // Son cosas distintas y confundirlas era el bug: la camara va sobre el hombro,
        // asi que el rayo de la mirilla no sale del cuerpo. La direccion jugador→punto
        // se abre hacia el lado del hombro, y se abre MAS cuanto mas cerca este lo
        // apuntado (a dos metros el angulo es enorme; a cincuenta, despreciable). Por
        // eso el modelo se torcia al atacar, y por eso a veces se notaba y a veces no.
        //
        // Lo que el jugador espera es lo que ya hace FaceCameraForward cada frame:
        // mirar hacia donde mira la camara. Aca solo lo hacemos de golpe, sin el Slerp,
        // para que el cuerpo este en su sitio en el frame del golpe aunque acabes de
        // girar la camara.
        //
        // La INCLINACION del ataque (apuntar arriba o abajo) no se pierde: eso lo
        // resuelve GameplayAbility.ResolveAttackDirection con el punto de mira, aparte
        // del yaw del cuerpo.
        Camera cam = MainCamera;
        if (IsOwner && cam != null)
        {
            Vector3 camFwd = cam.transform.forward;
            camFwd.y = 0f;

            if (camFwd.sqrMagnitude > 0.0001f)
            {
                transform.rotation = Quaternion.LookRotation(camFwd.normalized);
                return;
            }
        }

        // Copias que NO son el dueño (el servidor con un dueño remoto): ahi no hay una
        // camara local que consultar, asi que se cae al punto de mira sincronizado. El
        // desfase del hombro sigue existiendo en ese camino, pero no se ve: el transform
        // es client-authoritative, y la rotacion buena que manda el dueno lo pisa.
        Vector3 dir = aimPoint - transform.position;
        dir.y = 0f;

        // Menos de medio metro en horizontal = mira casi vertical. Ahi no se gira: el
        // cuerpo se queda como esta. Girar con ese dato seria inventar una direccion.
        if (dir.sqrMagnitude < 0.25f) return;

        transform.rotation = Quaternion.LookRotation(dir.normalized);
    }
}
