using UnityEngine;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;

// ============================================================
// PlayerController — CICLO DE VIDA DE RED Y UPDATE
//
// Una parte de PlayerController (partial class). La clase, sus campos del Inspector y sus
// eventos están en Player/PlayerController.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class PlayerController
{

    // =========================================================
    // CICLO DE VIDA DE RED
    // =========================================================

    // Cachea las referencias a los demás componentes del mismo GameObject.
    // La lista de los jugadores prendidos (ver All en el archivo principal).
    void OnEnable()  { if (!s_all.Contains(this)) s_all.Add(this); }
    void OnDisable() => s_all.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetAllList() => s_all.Clear();

    void Awake()
    {
        ASC                = GetComponent<AbilitySystemComponent>();
        NetASC             = GetComponent<NetworkAbilitySystemComponent>();
        characterController = GetComponent<CharacterController>();
        _input             = GetComponent<PlayerInputProvider>();

        // Los clips de ataque disparan Animation Events (AnimationEvent_EnableTrail,
        // AnimationEvent_DisableTrail, AnimationEvent_HitFrame) sobre el GameObject
        // que tiene el Animator — el modelo del personaje, NO esta raíz. Unity
        // busca el método receptor solo en los componentes de ESE GameObject, así
        // que necesita un PlayerAnimationEvents ahí (que reenvía a este script).
        // Lo agregamos por código para que funcione aunque no esté puesto a mano
        // en el prefab del modelo: si faltaba, la consola tiraba
        // "AnimationEvent 'AnimationEvent_EnableTrail' has no receiver!" y el
        // trail del arma nunca se activaba.
        if (characterAnimator != null &&
            characterAnimator.GetComponent<PlayerAnimationEvents>() == null)
        {
            characterAnimator.gameObject.AddComponent<PlayerAnimationEvents>();
        }
    }

    // Equipa la clase inicial (a todos los peers, para que la visual esté
    // bien en todos lados), y configura las cosas exclusivas del dueño
    // local: cámara propia y bloqueo de cursor.
    public override void OnStartClient()
    {
        base.OnStartClient();
        spawnPosition = transform.position;

        // Recibir los cambios de clase en runtime (evolución a subclase) para
        // actualizar los visuales/arma en los observadores. Faltaba este
        // suscribir (solo estaba el -= en OnStopClient), por eso los demás
        // jugadores nunca veían el cambio de arma del otro al evolucionar.
        _netClassIndex.OnChange += OnNetClassIndexChanged;
        _netRespawnClassIndex.OnChange += OnNetRespawnClassIndexChanged;

        // Seguro anti-errores: si el clon nace sin clase, forzamos la
        // primera clase disponible.
        if (CurrentClassDef == null && MainBaseClasses != null && MainBaseClasses.Length > 0)
        {
            CurrentClassDef = MainBaseClasses[0];
        }

        // Equipamos la clase a TODOS (dueños y clones) para que la visual
        // esté bien en todos lados.
        if (CurrentClassDef != null) EquipCharacterClass(CurrentClassDef);
        if (ASC != null)
        {
            ASC.OnDeath += HandlePlayerDeath;

            // Carga de la definitiva por rol. Los dos eventos solo se disparan en el
            // servidor (ahí corre el pipeline de daño), así que en las demás copias
            // quedan suscriptos sin hacer nada.
            ASC.OnDamageEndured += OnDamageEnduredForRole;
            ASC.OnHealedAlly    += OnHealedAllyForRole;

            // Las columnas de daño y curación de la tabla (también solo en el servidor).
            ASC.OnDamageDone += ServerCountDamage;
            ASC.OnHealedAlly += ServerCountHealingAlly;
            ASC.OnHealedSelf += ServerCountHealing;
            ASC.OnLifeStolen += ServerCountHealing;
        }

        // Cosas exclusivas del dueño local
        if (base.IsOwner)
        {
            LocalPlayer = this;

            // Si este personaje lo creó el desatascarse, nace con la clase de antes.
            TryEquipRespawnClass();

            // Habilitar el input del Input System nuevo solo para el dueño local.
            if (_input != null) _input.InitializeForOwner();

            // La cámara de la sala se apaga mientras juegas: la tuya la reemplaza. Se busca por su
            // componente (MenuOrbitCamera) y, si no hay, por la etiqueta. Camara_Lobby perdió la
            // etiqueta MainCamera el 2 de septiembre de 2026 y desde entonces Camera.main daba null
            // acá: la de la sala quedaba prendida debajo de la tuya toda la partida (el mapa se
            // dibujaba dos veces) y, desde que tiene su AudioListener, habría dos oídos.
            GameObject lobbyCam = FindLobbyCamera();
            if (lobbyCam != null)
            {
                _lobbyCamera = lobbyCam;
                _lobbyCamera.SetActive(false);
            }

            if (CameraPrefab != null)
            {
                GameObject camObj = Instantiate(CameraPrefab);
                _ownerCamera = camObj;
                ThirdPersonOrbitCam cam = camObj.GetComponent<ThirdPersonOrbitCam>();
                _orbitCam = cam;
                if (cam != null)
                {
                    cam.Target = this.transform;
                    // El cursor lo arbitra UICursor: si hay un menu abierto (la sala, el
                    // recuadro de red con ESC) no se lo traga el juego.
                    UICursor.Apply();
                }

                // Menú ÚNICO de clases (UI_ClassMenu): sirve tanto para elegir la
                // clase base (tecla C) como para evolucionar a una subclase (tecla V),
                // sobre el mismo panel. Vive en ESTE prefab de cámara, que se
                // instancia acá — DESPUÉS de EquipCharacterClass/UpdateHUD. Por eso se
                // engancha acá y no en UpdateHUD: allá la cámara todavía no existía.
                // GetComponentInChildren(true) lo encuentra aunque su panel arranque
                // inactivo.
                _classMenu = camObj.GetComponentInChildren<UI_ClassMenu>(true);
                if (_classMenu != null) _classMenu.InitializeMenu(this);

                // La retícula (crosshair) vive en el Canvas del prefab "Player
                // Camera" (componente Reticle sobre un objeto de UI). Como esta
                // cámara solo se instancia para el dueño local, la retícula solo
                // la ve su dueño — no hace falta crearla por código acá.
            }
        }
        else
        {
            // Destroy() falla siempre acá: PlayerController tiene
            // [RequireComponent(typeof(CharacterController))], así que Unity
            // bloquea la destrucción ("Can't remove CharacterController
            // because PlayerController depends on it") y el componente se
            // queda vivo en todos los fantasmas remotos. Nada llama a
            // .Move() en una copia que no es dueña (guard de IsOwner en
            // Update()), así que NO hace falta desactivarlo.
            //
            // IMPORTANTE: no desactivar el CharacterController acá.
            // Es el único collider del jugador, y las habilidades de daño
            // lo usan como hitbox vía Physics.OverlapSphere/OverlapBox en
            // el servidor. En Host, el cliente del host trata a los jugadores
            // remotos como "no dueños" — si acá lo desactivábamos, esa misma
            // instancia (la que usa el servidor, porque host = server+client
            // en un solo proceso) quedaba sin collider, y el host nunca
            // detectaba ni dañaba a los demás jugadores.
        }
    }

    // Se desuscribe de eventos al despawnear (evita callbacks sobre un
    // objeto ya destruido).
    public override void OnStopClient()
    {
        base.OnStopClient();
        _netClassIndex.OnChange -= OnNetClassIndexChanged;
        _netRespawnClassIndex.OnChange -= OnNetRespawnClassIndexChanged;
        if (ASC != null) ASC.OnDeath -= HandlePlayerDeath;
        if (LocalPlayer == this) LocalPlayer = null;
        if (base.IsOwner && _input != null) _input.ShutdownOwner();

        // La camara propia se destruye con el jugador. Sin esto quedaba viva al
        // desconectarse o al respawnear, y como trae AudioListener terminabas con dos en
        // la escena (la suya y la del lobby, que vuelve a habilitarse) — Unity avisa
        // "There are 2 audio listeners in the scene" y el sonido queda indefinido.
        if (_ownerCamera != null)
        {
            // Apagarla ANTES de destruirla: Destroy es diferido al fin del frame, y si
            // se prende la del lobby en el medio quedan dos AudioListener vivos.
            _ownerCamera.SetActive(false);
            Destroy(_ownerCamera);
            _ownerCamera = null;
        }

        // Y devolver la camara de la escena, o al desconectarse te quedas sin ninguna:
        // pantalla negra con el recuadro de red flotando encima.
        if (_lobbyCamera != null)
        {
            _lobbyCamera.SetActive(true);
            _lobbyCamera = null;
        }
    }

    // =========================================================
    // UNITY UPDATE — solo el dueño procesa input y movimiento
    // =========================================================

    void Update()
    {
        // Lo primero, porque corre en las copias que NO son del dueño: son las únicas
        // que reciben la locomoción en vez de calcularla (ver "LOCOMOCIÓN EN RED").
        if (!DrivesOwnAnimator) TickRemoteAnimations();

        // El servidor anota hace cuánto que este personaje no se mueve: el desatascarse
        // solo se acepta si llevas un rato quieto (ver esa sección).
        if (IsServerInitialized) TrackStillness();
        if (IsServerInitialized) ServerTickUltimatePercent();

        if (!IsOwner) return;

        // Un canalizado que apunta mientras dura le pidió la mira (ver "MIRA EN VIVO").
        TickAimStream();

        // El input del dueño todavía no está listo (se habilita en OnStartClient).
        if (_input == null || !_input.IsReady) return;

        // Menú modal abierto (ej. selección de clase inicial): el jugador no se
        // mueve ni activa habilidades hasta cerrarlo (elegir una tarjeta). La
        // gravedad sí sigue corriendo: si no, abrir el menú en pleno salto lo dejaba
        // colgado en el aire hasta elegir.
        if (_inputLocked)
        {
            SettleWithoutInput();
            return;
        }

        // SUBCLASES (acción "Cheat", rebindeada a la tecla V): abre el menú con las
        // subclases de la clase que tenés puesta ahora. Al elegir, conserva el
        // progreso — es una evolución, no un cambio de clase.
        //
        // Esta acción antes era el cheat de subir al nivel máximo, que existía solo
        // para forzar la aparición de este mismo menú; ahora se abre directo.
        if (_input.Cheat.WasPressedThisFrame() && _classMenu != null)
        {
            _classMenu.OpenSubclasses();
        }

        // CLASES BASE (acción ChangeClass: tecla C / D-pad arriba). Al elegir, la
        // clase nueva arranca en nivel 1. Ambos menús son modales (bloquean el input
        // vía SetInputLocked), así que _inputLocked de arriba ya evita reabrirlos
        // encima de sí mismos.
        if (_input.ChangeClass.WasPressedThisFrame() && _classMenu != null)
        {
            _classMenu.OpenBaseClasses();
        }

        // Watchdog: si isAttacking quedó trabado (una habilidad no reseteó el
        // estado — su corutina se interrumpió, no llegó el aviso de fin, etc.),
        // lo liberamos tras un máximo razonable para no dejar al jugador sin
        // poder actuar. El menú radial queda excluido (mantiene isAttacking a
        // propósito hasta que soltás el botón).
        if (isAttacking && !IsAimingAbility && Time.time - _attackStartTime > MaxAttackSeconds)
        {
            Debug.LogWarning("[PlayerController] isAttacking quedó trabado — reseteando (watchdog).");
            FinishAttack();
        }

        if (ASC.HasTag(EGameplayTag.State_Dead))
        {
            if (_wallActive) StopWall();
            if (_input.Ability3.WasPressedThisFrame() && AbilityR != null && AbilityR.CanActivate())
                RequestAbility(EAbilityInput.Action3);
            return;
        }

        // Recién revivido: State_Dead ya se fue, pero el aviso que saca el ragdoll y prende de nuevo
        // el controlador (ObserversHandleRevive) llega unos cuadros después, también en el host. Sin
        // esto, cada uno de esos cuadros tiraba "CharacterController.Move called on inactive controller".
        if (!characterController.enabled) return;

        UpdateFlightState();

        // Repelido / atraído: el empuje corre aunque esté aturdido o enraizado; si no lo
        // está, conserva parte del control (knockbackSteerControl). Las habilidades siguen
        // leyéndose (si no está aturdido) para no perderse el soltar de un mantenido, como
        // el escudo.
        if (TickKnockback())
        {
            if (!ASC.HasTag(EGameplayTag.State_Stunned)) HandleAbilityInput();
            UpdateAnimations();
            return;
        }

        if (ASC.HasTag(EGameplayTag.State_Stunned))
        {
            if (_rushActive) StopRush(false);   // el servidor también la corta
            if (_wallActive) StopWall();        // aturdido, se cae de la pared
            if (IsFlying || _flightFalling) SlowFall();
            return;
        }

        // Embestida: avanza sola; el input solo la tuerce o la corta (ver TickRush).
        if (TickRush())
        {
            UpdateAnimations();
            return;
        }

        // Enganchado a una pared: se mueve distinto, pero las habilidades se siguen usando
        // (las de movimiento lo despegan al arrancar). Ver "MOVIMIENTO EN PAREDES".
        if (TickWall())
        {
            HandleAbilityInput();
            UpdateGroundTargetIndicator();
            UpdateAnimations();
            return;
        }

        HandleMovementInput();
        HandleAbilityInput();
        UpdateGroundTargetIndicator();

        // UpdateAnimations solo en el dueño — los observadores remotos ven
        // las animaciones sincronizadas por las ObserversRpc del NetworkASC.
        UpdateAnimations();
    }
}
