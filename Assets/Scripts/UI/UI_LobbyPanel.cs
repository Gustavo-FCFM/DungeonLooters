using System.Collections;
using System.Collections.Generic;
using FishNet;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// ============================================================
// UI_LobbyPanel
//
// La sala de espera entera en un solo panel: IP, nombre, los tres equipos con sus
// lugares, la franja de espectadores, el selector de clase y los botones de Confirmar
// y Start.
//
// VIVE EN EL PREFAB DE MENÚS (Menus_UI ▸ Lobby), desde el 14 de octubre de 2026: el panel,
// la IP, el nombre, las cabeceras de las columnas, los lugares (cada uno con su
// UI_LobbySlot), la línea de estado, los botones y el selector de clase están armados ahí y
// se acomodan a mano. Lo que depende de la LISTA de clases (las pestañas por rol y los
// íconos de la grilla) se arma al empezar copiando dos MOLDES del selector
// ("TabTemplate" y "ClassIconTemplate"): su tamaño y su altura son los del molde. Este
// componente vive en un objeto siempre prendido (el servidor lo consulta para saber con qué
// clase aparece cada bot); lo que se prende y se apaga es su canvas.
//
// CÓMO FUNCIONA EL FLUJO:
//   1. Entrás → te anotás con tu nombre y quedás de ESPECTADOR.
//   2. Te unís a un equipo tocando un lugar libre → dejás de ser espectador.
//   3. Elegís clase tocando tu propio ícono → se despliega la grilla de clases.
//   4. Confirmar te marca LISTO (tu nombre se pone verde). Volver a tocarlo lo apaga.
//   5. El HOST aprieta Start y arranca la partida para todos.
//   Salir (arriba a la izquierda) vuelve al menú principal; siendo host, cierra la sala.
//
// EL PERSONAJE NO APARECE AL CONFIRMAR, sino cuando la partida arranca. Si no, el que
// confirma primero se queda dando vueltas por el mapa mientras los demás todavía
// eligen — que es justo lo que pasaba antes.
//
// CON CONTROL (26 de octubre de 2026): es una pantalla de MenuInput. Arranca con algo elegido
// (ver DefaultSelection: unirte a un equipo, elegir tu clase, Confirmar, Start) y, cuando el
// botón elegido desaparece (al unirte, el "Unirte" se apaga), el control pasa al siguiente
// paso. B cierra el selector de clase; Start o ESC abren la pausa.
//
// AUTORIDAD: este panel solo PIDE (ServerSubmit / ServerSetReady / ServerRequestStart).
// Quién está en qué equipo, si un nombre se repite y si un equipo está lleno lo decide
// el servidor en LobbyManager; acá solo se dibuja lo que la sala sincronizada dice.
// ============================================================
// ESTÁ REPARTIDA POR TEMA (partial class, desde el 25 de noviembre de 2026). En este archivo: los campos del
// Inspector, el ciclo y la pantalla de menú (IMenuScreen); lo demás, en UI/UI_LobbyPanel/:
//   · Requests lo que el panel le pide a la sala (equipo, clase, bots, empezar)
//   · Draw     dibujar la sala: lugares, columnas, espectadores, clases
//   · Wiring   conectar las piezas del prefab al empezar
public partial class UI_LobbyPanel : MonoBehaviour, IMenuScreen
{
    [Header("Clases elegibles")]
    [Tooltip("Las mismas que MainBaseClasses del Player. El ícono de cada una sale de su " +
             "ClassIcon, y el índice que se manda a la sala es la posición en esta lista.")]
    [Label("Clases elegibles")]
    public CharacterClassDefinition[] SelectableClasses;

    [Header("Piezas")]
    [Tooltip("El canvas de la sala: se prende con la conexión y se apaga al arrancar la partida.")]
    [Label("Canvas de la sala")]
    public GameObject LobbyCanvas;

    [Tooltip("El panel (lo que contiene todo; su escala agranda la sala entera).")]
    [Label("Panel")]
    public RectTransform Panel;

    [Label("IP del host")]
    public TMP_Text IpText;

    [Label("Campo del nombre")]
    public TMP_InputField NameInput;

    [Tooltip("Solo lo ve el host.")]
    [Label("Botón Start")]
    public Button StartButton;

    [Label("Texto de Start")]
    public TMP_Text StartLabel;

    [Label("Botón Confirmar")]
    public Button ConfirmButton;

    [Label("Texto de Confirmar")]
    public TMP_Text ConfirmLabel;

    [Tooltip("Salir de la sala al menú principal. Siendo host, cierra la sala para todos (el botón lo dice).")]
    [Label("Botón Salir")]
    public Button LeaveButton;

    [Label("Texto de Salir")]
    public TMP_Text LeaveLabel;

    [Tooltip("Siendo host, Salir se aprieta dos veces: la primera pregunta \"¿Cerrar para todos?\". Lo que espera la segunda antes de volver a decir \"Cerrar sala\".")]
    [Label("Tiempo para confirmar Salir (s)")]
    public float LeaveConfirmSeconds = 4f;

    [Tooltip("La línea sobre Confirmar: qué falta para arrancar, o qué te falta a vos.")]
    [Label("Línea de estado")]
    public TMP_Text StatusText;

    [Tooltip("El texto de la cabecera de cada equipo (\"Equipo 1\"...), en orden.")]
    [Label("Cabeceras de los equipos")]
    public TMP_Text[] TeamHeaders = new TMP_Text[LobbyManager.TeamCount];

    [Header("Piezas: selector de clase")]
    [Tooltip("El recuadro del selector. Su fondo es un botón que lo cierra.")]
    [Label("Selector de clase")]
    public RectTransform ClassPicker;

    [Label("Botón de cerrar (el fondo)")]
    public Button PickerCloseButton;

    [Tooltip("MOLDE de una pestaña de rol (apagado): un botón con un texto. Su altura en el selector es la de las pestañas.")]
    [Label("Molde de pestaña")]
    public Button TabTemplate;

    [Tooltip("MOLDE de un ícono de clase (apagado): un botón con su Image. Su tamaño y su altura en el selector son los de la grilla.")]
    [Label("Molde de ícono de clase")]
    public Button ClassIconTemplate;

    [Tooltip("El nombre de la clase que está bajo el mouse.")]
    [Label("Nombre de la clase")]
    public TMP_Text ClassNameLabel;

    [Tooltip("La descripción de la clase seleccionada.")]
    [Label("Descripción de la clase")]
    public TMP_Text ClassDescLabel;

    [Tooltip("Las barras de vida, velocidad, velocidad de ataque y armadura de la clase elegida (13 de noviembre de " +
             "2026). Las mismas filas que las tarjetas del menú de clases (UI_ClassStatRow).")]
    [Label("Estadísticas de la clase")]
    public UI_ClassStatRow[] ClassStatRows;

    [Label("Botón Confirmar clase")]
    public Button PickerConfirm;

    [Header("Selector: separaciones (px del panel)")]
    [Label("Entre pestañas (px)")]
    public float TabGap = 8f;

    [Label("Entre íconos (px)")]
    public float IconGap = 12f;

    [Label("Ícono bajo el stick (×)")]
    public float PickerHoverScale = 1.15f;

    [Tooltip("Margen a los costados de la grilla: el selector se ensancha si las clases de un rol no entran.")]
    [Label("Margen de la grilla (px)")]
    public float PickerMargin = 32f;

    [Header("Colores (los que cambian en juego)")]
    [Tooltip("Las pestañas que no están abiertas se mezclan con este color.")]
    [Label("Color del panel")]
    public Color PanelColor   = new Color(0.09f, 0.09f, 0.11f, 0.97f);
    [Tooltip("El ícono de un lugar sin clase elegida.")]
    [Label("Color del lugar")]
    public Color SlotColor    = new Color(0.20f, 0.24f, 0.32f, 1f);
    [Label("Color: listo")]
    public Color ReadyColor   = new Color(0.55f, 0.95f, 0.55f, 1f);
    [Label("Color: pendiente")]
    public Color PendingColor = new Color(0.86f, 0.88f, 0.92f, 1f);
    [Tooltip("Color del botón de Start y de Confirmar cuando TODAVÍA no corresponde " +
             "apretarlos. Verde cuando sí.")]
    [Label("Color: inactivo")]
    public Color IdleColor    = new Color(0.17f, 0.24f, 0.40f, 1f);

    [Tooltip("Fondo de una fila OCUPADA: el recuadro detrás del ícono y el nombre. Las " +
             "filas libres no lo llevan — ahí el fondo lo pone el propio botón de Unirte.")]
    [Label("Color de fila")]
    public Color RowColor     = new Color(0.16f, 0.19f, 0.26f, 1f);

    [Tooltip("Fondo de TU propia fila. Sirve para encontrarte de un vistazo cuando la " +
             "sala está llena.")]
    [Label("Color de tu fila")]
    public Color OwnRowColor  = new Color(0.22f, 0.29f, 0.42f, 1f);

    [Tooltip("Color del nombre de los BOTS. Distinto del de un jugador listo a propósito: " +
             "en una captura tiene que verse de un vistazo quién es persona y quién no.")]
    [Label("Color de los bots")]
    public Color BotColor     = new Color(0.72f, 0.66f, 0.95f, 1f);

    // --- armados al empezar ---
    private bool  _built;
    private Image[] _classIcons;
    private int   _pickerSelection = -1;

    // Los grupos por rol del selector: qué clases van en cada uno (índices de
    // SelectableClasses), la pestaña de cada grupo, y cuál está abierto.
    private readonly List<EClassRole> _roleGroups = new List<EClassRole>();
    private readonly Dictionary<EClassRole, List<int>> _roleMembers = new Dictionary<EClassRole, List<int>>();
    private readonly List<Image> _roleTabs = new List<Image>();
    private int _openGroup;

    private readonly List<UI_LobbySlot>[] _teamSlots = new List<UI_LobbySlot>[LobbyManager.TeamCount];
    private readonly List<UI_LobbySlot>   _spectatorSlots = new List<UI_LobbySlot>();

    private bool  _spawnRequested;
    private bool  _registered;      // ya me anoté en la sala (aunque sea de espectador)
    private bool  _visible;         // el estado que el panel CREE tener; ver Update
    private float _redrawIn;        // segundos hasta el próximo redibujado de reloj
    private readonly LeaveConfirm _leaveConfirm = new LeaveConfirm();

    private void Start()
    {
        Build();
        Refresh();
    }

    // Cambiar de idioma (en Ajustes) redibuja lo que se calcula en Refresh; los textos fijos
    // están atados con LocalizedText y se reescriben solos.
    private void OnEnable()
    {
        LobbyManager.OnLobbyChanged += Refresh;
        Loc.OnLanguageChanged      += Refresh;
    }

    private void OnDisable()
    {
        LobbyManager.OnLobbyChanged -= Refresh;
        Loc.OnLanguageChanged      -= Refresh;
        MenuInput.Pop(this);
        UICursor.Release(this);
    }

    // =========================================================
    // CICLO
    // =========================================================

    private void Update()
    {
        LobbyManager lobby = LobbyManager.Instance;

        // La sala se muestra SOLO cuando ya hay conexión: IsLobbyReady se prende en
        // OnStartClient, o sea al iniciar el host o al conectarse como cliente. Antes
        // aparecía sobre el menú vacío, encima del recuadro de conexión.
        //
        // Y se va cuando arranca la partida: ahí ya estás en el mapa con personaje y HUD.
        bool show = lobby != null && lobby.IsLobbyReady && !lobby.MatchStarted;

        // Al desconectarse se vuelve al estado inicial. Si no, reconectarse te deja sin
        // anotar en la sala (y sin personaje cuando arranque la siguiente partida).
        if (lobby == null || !lobby.IsLobbyReady)
        {
            _registered     = false;
            _spawnRequested = false;
        }

        // Vuelta a la sala al terminar una partida: hay que poder volver a PEDIR
        // personaje cuando el host arranque la siguiente. Sin esto, la bandera se queda
        // en true de la partida anterior y el segundo Start te deja de espectador.
        if (lobby != null && !lobby.MatchStarted) _spawnRequested = false;

        if (LobbyCanvas != null && _visible != show)
        {
            _visible = show;
            LobbyCanvas.SetActive(show);
            if (show) MenuInput.Push(this); else MenuInput.Pop(this);
            _leaveConfirm.Cancel();
        }

        // El cursor tiene que estar libre para escribir y clickear en la sala. Se pide
        // por UICursor y no a mano: el recuadro de red también lo pide con ESC, y el
        // último en soltarlo no puede llevarse el de los demás.
        //
        // Va FUERA del if de visibilidad —o sea, cada frame— a propósito. Antes se
        // pedía y se soltaba solo en el FLANCO, y ese suelto ocurre cuando arranca la
        // partida, justo cuando el jugador todavía no existe: Apply() no tenía a quién
        // avisarle y el pedido quedaba anotado en el aire. Request y Release son
        // idempotentes (si el conjunto no cambia no hacen nada), así que repetirlos sale
        // gratis y el estado converge solo en vez de depender de un flanco.
        if (show) UICursor.Request(this);
        else      UICursor.Release(this);

        if (lobby == null) return;

        // Anotarse apenas la sala responde: entrar te deja de ESPECTADOR, sin tener que
        // tocar nada. Recién al elegir equipo dejás de serlo.
        if (!_registered && lobby.IsLobbyReady)
        {
            _registered = true;
            if (!lobby.TryGetLocalEntry(out _)) Submit(0, -1, spectator: true);
        }

        // El personaje se pide RECIÉN cuando arranca la partida, no al confirmar (ver
        // la cabecera). El espectador no pide nada: mira desde la cámara del lobby.
        if (lobby.MatchStarted && !_spawnRequested)
        {
            _spawnRequested = true;
            RequestSpawnIfPlaying(lobby);
        }

        // La sala avisa sola cuando cambia (OnLobbyChanged), así que acá alcanza con un
        // repaso lento para lo que NO viaja por la lista: la IP y quién es host.
        if (!show) return;
        _redrawIn -= Time.unscaledDeltaTime;
        if (_leaveConfirm.Changed(LeaveButton != null ? LeaveButton.gameObject : null)) _redrawIn = 0f;
        if (_redrawIn <= 0f)
        {
            _redrawIn = 0.5f;
            Refresh();
        }
    }

    private void RequestSpawnIfPlaying(LobbyManager lobby)
    {
        if (!lobby.TryGetLocalEntry(out LobbyEntry me)) return;
        if (me.Spectator || me.Team <= 0) return;

        // Va por BROADCAST y no por ServerRpc: un RPC necesita un NetworkObject ya
        // inicializado del lado del que llama, y el personaje todavía no existe.
        InstanceFinder.ClientManager.Broadcast(new SpawnRequestBroadcast
        {
            PlayerName = me.PlayerName,
            TeamID     = me.Team,
        });

        StartCoroutine(EquipClassWhenSpawned(me.ClassIndex));
    }

    // El personaje tarda un momento en llegar (viaje de red + spawn). Cuando aparece,
    // el DUEÑO equipa la clase por el camino normal, que ya se sincroniza a todos.
    private IEnumerator EquipClassWhenSpawned(int classIndex)
    {
        float timeout = 10f;
        while (PlayerController.LocalPlayer == null && timeout > 0f)
        {
            timeout -= Time.deltaTime;
            yield return null;
        }

        CharacterClassDefinition cls = ClassAt(classIndex);
        if (PlayerController.LocalPlayer != null && cls != null)
            PlayerController.LocalPlayer.EquipCharacterClass(cls);
    }

    // Sobre QUIÉN va a actuar el selector de clase cuando se elija: 0 = yo, negativo = ese
    // bot. Se decide al abrirlo, no al elegir, porque la fila puede cambiar mientras está
    // abierto (alguien se conecta y se corren los lugares).
    private int _pickerTarget;

    // =========================================================
    // IMenuScreen
    // =========================================================

    public int MenuLayer => 0;

    private bool PickerOpen => ClassPicker != null && ClassPicker.gameObject.activeInHierarchy;

    // Con control, a dónde va la selección según lo que te falta: unirte a un equipo, elegir
    // tu clase, Confirmar y, siendo host y ya listo, Start. Con el selector abierto, la clase
    // que estás mirando (o la primera de la pestaña).
    public GameObject DefaultSelection
    {
        get
        {
            if (PickerOpen)
            {
                if (_classIcons != null)
                {
                    if (_pickerSelection >= 0 && _pickerSelection < _classIcons.Length && Usable(_classIcons[_pickerSelection]))
                        return _classIcons[_pickerSelection].gameObject;
                    foreach (Image icon in _classIcons)
                        if (Usable(icon)) return icon.gameObject;
                }
                return PickerConfirm != null ? PickerConfirm.gameObject : null;
            }

            LobbyManager lobby = LobbyManager.Instance;
            if (lobby != null && lobby.TryGetLocalEntry(out LobbyEntry me) && !me.Spectator && me.Team > 0)
            {
                if (me.ClassIndex < 0)
                {
                    UI_LobbySlot mine = SlotOf(me.ClientId);
                    if (mine != null && Usable(mine.IconButton)) return mine.IconButton.gameObject;
                }
                if (me.Ready && LobbyManager.LocalIsHost && Usable(StartButton)) return StartButton.gameObject;
                if (Usable(ConfirmButton)) return ConfirmButton.gameObject;
            }

            // De espectador: el primer "Unirte" libre.
            foreach (List<UI_LobbySlot> team in _teamSlots)
            {
                if (team == null) continue;
                foreach (UI_LobbySlot slot in team)
                    if (slot != null && Usable(slot.JoinButton)) return slot.JoinButton.gameObject;
            }

            return ConfirmButton != null ? ConfirmButton.gameObject : null;
        }
    }

    public Transform SelectionRoot =>
        PickerOpen ? ClassPicker : LobbyCanvas != null ? LobbyCanvas.transform : transform;

    // B cierra el selector de clase; en la sala no hay a dónde volver (se sale por la pausa).
    public void OnBack()
    {
        if (PickerOpen) ShowClassPicker(false);
    }

    public void OnPause() => UI_PauseMenu.Get()?.Open();

    private UI_LobbySlot SlotOf(int entryId)
    {
        foreach (List<UI_LobbySlot> team in _teamSlots)
        {
            if (team == null) continue;
            foreach (UI_LobbySlot slot in team)
                if (slot != null && slot.EntryId == entryId) return slot;
        }
        return null;
    }

    // Con control, la selección sigue al paso que toca: si estaba en el paso anterior (Unirte,
    // tu ícono, Confirmar) o se apagó, pasa al siguiente. Si la llevaste a otro lado (el "+" de
    // un bot), se queda ahí.
    private GameObject _lastStep;

    // El "+" que se acaba de apretar. Al entrar el bot ese "+" se apaga (el lugar ya está
    // ocupado) y la selección se perdía y caía en Confirmar: para meter varios bots había que
    // volver hasta los "+" cada vez. Ahora pasa al "+" siguiente del mismo equipo, o al "x" del
    // bot si el equipo se llenó (28 de octubre de 2026, prueba con control).
    private GameObject _pressedAddBot;

    private void FollowStep()
    {
        if (!MenuInput.UsingPad || PickerOpen || !ReferenceEquals(MenuInput.Top, this))
        {
            _lastStep = null;
            _pressedAddBot = null;
            return;
        }

        GameObject next = DefaultSelection;
        GameObject current = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;

        if (_pressedAddBot != null)
        {
            if (!_pressedAddBot.activeInHierarchy)
            {
                GameObject near = NearAfterBot(_pressedAddBot);
                _pressedAddBot = null;
                if (near != null)
                {
                    MenuInput.Select(near);
                    _lastStep = next;
                    return;
                }
            }
            else if (current != _pressedAddBot) _pressedAddBot = null;   // se fue a otro lado
        }

        bool onLastStep = current == null || !current.activeInHierarchy || current == _lastStep;
        if (next != _lastStep && onLastStep) MenuInput.Select(next);
        _lastStep = next;
    }

    // Después de meter un bot con ese "+": el próximo "+" libre de su equipo, o si se llenó,
    // el "x" del último bot del equipo.
    private GameObject NearAfterBot(GameObject pressed)
    {
        foreach (List<UI_LobbySlot> team in _teamSlots)
        {
            if (team == null) continue;
            bool mine = false;
            foreach (UI_LobbySlot slot in team)
                if (slot != null && slot.AddBotButton != null && slot.AddBotButton.gameObject == pressed) mine = true;
            if (!mine) continue;

            foreach (UI_LobbySlot slot in team)
                if (slot != null && Usable(slot.AddBotButton)) return slot.AddBotButton.gameObject;
            for (int s = team.Count - 1; s >= 0; s--)
                if (team[s] != null && Usable(team[s].RemoveButton)) return team[s].RemoveButton.gameObject;
        }
        return null;
    }

    // Prendido y se puede apretar.
    private static bool Usable(Component c)
    {
        if (c == null || !c.gameObject.activeInHierarchy) return false;
        Selectable s = c as Selectable;
        if (s == null) s = c.GetComponent<Selectable>();
        return s == null || s.IsInteractable();
    }
}
