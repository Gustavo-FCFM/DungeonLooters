using System.Collections;
using System.Collections.Generic;
using FishNet;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// ============================================================
// UI_LobbyPanel — CONECTAR LAS PIEZAS (una vez, al empezar)
//
// Una parte de UI_LobbyPanel (partial class). La clase, sus campos del Inspector y sus
// eventos están en UI/UI_LobbyPanel.cs; ahí está también la lista de las partes.
// Repartida el 25 de noviembre de 2026 sin cambiar el código: es el mismo que estaba en el archivo principal.
// ============================================================
public partial class UI_LobbyPanel
{

    // =========================================================
    // CONECTAR LAS PIEZAS (una vez, al empezar)
    // =========================================================

    private void Build()
    {
        if (_built) return;
        _built = true;

        // Nace APAGADO: la sala no existe hasta que hay conexion, y hasta entonces lo
        // unico que va en pantalla es el recuadro para hostear o conectarse.
        if (LobbyCanvas != null) LobbyCanvas.SetActive(false);

        if (StartButton != null)   { StartButton.onClick.AddListener(AudioManager.Click);   StartButton.onClick.AddListener(RequestStart); }
        if (ConfirmButton != null) { ConfirmButton.onClick.AddListener(AudioManager.Click); ConfirmButton.onClick.AddListener(ToggleReady); }
        if (LeaveButton != null)   { LeaveButton.onClick.AddListener(AudioManager.Click);   LeaveButton.onClick.AddListener(Leave); }

        if (NameInput != null)
        {
            NameInput.text = Loc.T("lobby.default_name");
            // Al TERMINAR de escribir, no en cada tecla: si no, cada letra sería un pedido
            // al servidor y un rebote por "nombre repetido" a mitad de palabra.
            NameInput.onEndEdit.AddListener(OnNameChanged);
            UI_OnScreenKeyboard.SetTitle(NameInput, "keyboard.name");   // con control se escribe con el teclado en pantalla
        }

        // Los lugares, por columna y en el orden de la jerarquía.
        for (int t = 0; t < _teamSlots.Length; t++) _teamSlots[t] = new List<UI_LobbySlot>();
        Transform root = Panel != null ? Panel : transform;
        foreach (UI_LobbySlot slot in root.GetComponentsInChildren<UI_LobbySlot>(true))
        {
            if (slot.Team <= 0) { _spectatorSlots.Add(slot); WireSlot(slot); continue; }
            if (slot.Team <= _teamSlots.Length) { _teamSlots[slot.Team - 1].Add(slot); WireSlot(slot); }
        }

        BuildClassPicker();
    }

    // Los botones de un lugar. Se registran UNA vez; sobre quién actúan lo dice EntryId,
    // que se reescribe en cada redibujado.
    private void WireSlot(UI_LobbySlot slot)
    {
        int team = slot.Team;
        if (slot.JoinButton != null)
        {
            slot.JoinButton.onClick.AddListener(AudioManager.Click);
            slot.JoinButton.onClick.AddListener(() => JoinTeam(team));
        }
        if (slot.IconButton != null)   slot.IconButton.onClick.AddListener(() => ShowClassPickerFor(slot.EntryId));
        if (slot.AddBotButton != null)
        {
            slot.AddBotButton.onClick.AddListener(AudioManager.Click);
            slot.AddBotButton.onClick.AddListener(() => { _pressedAddBot = slot.AddBotButton.gameObject; AddBot(team); });
        }
        if (slot.RemoveButton != null) { slot.RemoveButton.onClick.AddListener(AudioManager.Click); slot.RemoveButton.onClick.AddListener(() => RemoveBot(slot.EntryId)); }
    }

    // La franja de espectadores no tiene cupo: si entran más de los armados, se copia el
    // último lugar y se pone debajo, a la misma distancia que hay entre los dos primeros.
    private void EnsureSpectatorRows(int needed)
    {
        if (_spectatorSlots.Count == 0) return;

        while (_spectatorSlots.Count < needed)
        {
            UI_LobbySlot last = _spectatorSlots[_spectatorSlots.Count - 1];
            RectTransform lastRt = (RectTransform)last.transform;

            Vector2 step = _spectatorSlots.Count >= 2
                ? lastRt.anchoredPosition - ((RectTransform)_spectatorSlots[_spectatorSlots.Count - 2].transform).anchoredPosition
                : new Vector2(0f, -(lastRt.sizeDelta.y + 8f));

            UI_LobbySlot copy = Instantiate(last, last.transform.parent);
            copy.name = $"Slot_Spec_{_spectatorSlots.Count}";
            ((RectTransform)copy.transform).anchoredPosition = lastRt.anchoredPosition + step;
            _spectatorSlots.Add(copy);
            WireSlot(copy);
        }
    }

    // La grilla de clases: íconos arriba, y debajo el nombre y la descripción de la que
    // estás mirando.
    //
    // ELEGIR NO ES CONFIRMAR. Tocar un ícono solo la SELECCIONA: se resalta y aparece su
    // descripción, y recién el botón Confirm la aplica.
    //
    // El nombre sigue al MOUSE (pasar por encima alcanza, sin tocar nada) y la
    // descripción sigue a la SELECCIÓN.
    //
    // AGRUPADAS POR ROL (8 de octubre, pedido de Gustavo). Arriba, una pestaña por rol
    // —Tanks, Damage, Supports, y Other si alguna clase no tiene— y debajo solo las clases
    // del rol abierto. Una clase base no tiene rol: va con el de sus subclases
    // (CharacterClassDefinition.DisplayRole). El índice que se manda a la sala sigue siendo
    // la posición en SelectableClasses: agrupar solo cambia dónde se DIBUJA cada ícono.
    //
    // Las pestañas y los íconos se copian de sus MOLDES: la altura de la fila y el tamaño
    // son los del molde; acá solo se reparten a lo ancho.
    private void BuildClassPicker()
    {
        if (ClassPicker == null) return;

        if (PickerCloseButton != null) PickerCloseButton.onClick.AddListener(() => ShowClassPicker(false));
        if (PickerConfirm != null)
        {
            PickerConfirm.onClick.AddListener(AudioManager.Click);
            PickerConfirm.onClick.AddListener(ConfirmClassSelection);
        }

        int count = SelectableClasses != null ? SelectableClasses.Length : 0;

        // Los grupos, en el orden de los menús; solo los que tienen alguna clase.
        _roleGroups.Clear();
        _roleMembers.Clear();
        _roleTabs.Clear();
        for (int i = 0; i < count; i++)
        {
            EClassRole role = RoleOf(i);
            if (!_roleMembers.TryGetValue(role, out List<int> members))
                _roleMembers[role] = members = new List<int>();
            members.Add(i);
        }
        foreach (EClassRole role in ClassRoleStyle.MenuOrder)
            if (_roleMembers.ContainsKey(role)) _roleGroups.Add(role);

        int widest = 1;
        foreach (List<int> members in _roleMembers.Values) widest = Mathf.Max(widest, members.Count);

        RectTransform tabRt  = TabTemplate != null ? (RectTransform)TabTemplate.transform : null;
        RectTransform iconRt = ClassIconTemplate != null ? (RectTransform)ClassIconTemplate.transform : null;
        float tabW      = tabRt != null ? tabRt.sizeDelta.x : 150f;
        float iconSize  = iconRt != null ? iconRt.sizeDelta.x : 84f;
        float step      = iconSize + IconGap;
        float tabsWidth = _roleGroups.Count * tabW + Mathf.Max(0, _roleGroups.Count - 1) * TabGap;

        // El ancho es el del grupo MÁS grande, para que el recuadro no cambie al cambiar de
        // pestaña. Nunca más angosto que como está armado.
        float needed = Mathf.Max(widest * step, tabsWidth) + PickerMargin;
        if (needed > ClassPicker.sizeDelta.x) ClassPicker.sizeDelta = new Vector2(needed, ClassPicker.sizeDelta.y);

        // Las pestañas de rol, del color de su rol.
        if (TabTemplate != null)
        {
            TabTemplate.gameObject.SetActive(false);
            for (int g = 0; g < _roleGroups.Count; g++)
            {
                int capturedGroup = g;
                EClassRole role = _roleGroups[g];
                int memberCount = _roleMembers[role].Count;

                Button tab = Instantiate(TabTemplate, TabTemplate.transform.parent);
                tab.name = $"Tab_{role}";
                tab.gameObject.SetActive(true);
                RectTransform rt = (RectTransform)tab.transform;
                rt.anchoredPosition = new Vector2(-tabsWidth * 0.5f + tabW * 0.5f + g * (tabW + TabGap), tabRt.anchoredPosition.y);

                Image bg = tab.targetGraphic as Image;
                ButtonTint.Paint(bg, ClassRoleStyle.Color(role));

                TMP_Text label = tab.GetComponentInChildren<TMP_Text>(true);
                if (label != null) LocalizedText.Bind(label, () => $"{ClassRoleStyle.GroupName(role)} ({memberCount})");

                tab.onClick.AddListener(AudioManager.Click);
                tab.onClick.AddListener(() => OpenRoleGroup(capturedGroup));
                _roleTabs.Add(bg);
            }
        }

        // Los íconos: cada uno se centra dentro de SU grupo (solo se ve el grupo abierto).
        _classIcons = new Image[count];
        if (ClassIconTemplate != null)
        {
            ClassIconTemplate.gameObject.SetActive(false);
            for (int i = 0; i < count; i++)
            {
                int captured = i;
                CharacterClassDefinition cls = SelectableClasses[i];

                List<int> group = _roleMembers[RoleOf(i)];
                int slot = group.IndexOf(i);

                Button b = Instantiate(ClassIconTemplate, ClassIconTemplate.transform.parent);
                b.name = $"Class_{i}";
                b.gameObject.SetActive(true);
                RectTransform rt = (RectTransform)b.transform;
                rt.anchoredPosition = new Vector2((slot - (group.Count - 1) * 0.5f) * step, iconRt.anchoredPosition.y);

                Image icon = b.targetGraphic as Image;
                if (icon == null) icon = b.GetComponent<Image>();
                if (icon != null)
                {
                    icon.sprite = cls != null ? cls.ClassIcon : null;
                    icon.preserveAspect = true;
                }
                _classIcons[i] = icon;

                b.onClick.AddListener(() => SelectClassInPicker(captured));

                // Pasar el mouse por encima muestra el nombre, sin elegir nada.
                EventTrigger trigger = b.GetComponent<EventTrigger>();
                if (trigger == null) trigger = b.gameObject.AddComponent<EventTrigger>();
                AddTriggerEntry(trigger, EventTriggerType.PointerEnter, () => ShowClassName(captured));
                AddTriggerEntry(trigger, EventTriggerType.PointerExit,  () => ShowClassName(_pickerSelection));

                // Con control, lo mismo al pasar por encima con el stick, y el ícono se agranda
                // para que se vea dónde estás parado (el mouse ya lo muestra con el puntero).
                AddTriggerEntry(trigger, EventTriggerType.Select,   () => { ShowClassName(captured); rt.localScale = Vector3.one * PickerHoverScale; });
                AddTriggerEntry(trigger, EventTriggerType.Deselect, () => { ShowClassName(_pickerSelection); rt.localScale = Vector3.one; });
            }
        }

        ClassPicker.gameObject.SetActive(false);
    }

    private static void AddTriggerEntry(EventTrigger trigger, EventTriggerType type, System.Action action)
    {
        EventTrigger.Entry entry = new EventTrigger.Entry { eventID = type };
        entry.callback.AddListener(_ => action());
        trigger.triggers.Add(entry);
    }

    // El rol con el que se agrupa la clase en esa posición de SelectableClasses.
    private EClassRole RoleOf(int index)
    {
        CharacterClassDefinition cls = ClassAt(index);
        return cls != null ? cls.DisplayRole : EClassRole.None;
    }

    // Abre la pestaña de un rol: muestra solo sus clases y resalta la pestaña (las otras
    // quedan apagadas, mezcladas con el fondo).
    private void OpenRoleGroup(int group)
    {
        if (group < 0 || group >= _roleGroups.Count) return;
        EClassRole open = _roleGroups[group];
        _openGroup = group;

        if (_classIcons != null)
            for (int i = 0; i < _classIcons.Length; i++)
                if (_classIcons[i] != null) _classIcons[i].gameObject.SetActive(RoleOf(i) == open);

        for (int g = 0; g < _roleTabs.Count; g++)
        {
            if (_roleTabs[g] == null) continue;
            Color c = ClassRoleStyle.Color(_roleGroups[g]);
            ButtonTint.Paint(_roleTabs[g], g == group ? c : Color.Lerp(c, PanelColor, 0.7f));
        }

        WirePickerNavigation();
    }

    // Con control, el selector se recorre SOLO por dentro: las pestañas arriba, los íconos del
    // grupo abierto en el medio y Confirmar abajo. Con la navegación automática el stick bajaba
    // del ícono a los "Unirse" de la sala que quedan detrás, MenuInput lo devolvía al ícono y
    // nunca se llegaba a Confirmar (28 de octubre de 2026, prueba con control).
    private void WirePickerNavigation()
    {
        if (_roleGroups.Count == 0) return;

        var icons = new List<Button>();
        foreach (int i in _roleMembers[_roleGroups[_openGroup]])
        {
            Button b = _classIcons != null && _classIcons[i] != null ? _classIcons[i].GetComponent<Button>() : null;
            if (b != null) icons.Add(b);
        }

        var tabs = new List<Button>();
        foreach (Image tab in _roleTabs)
        {
            Button b = tab != null ? tab.GetComponent<Button>() : null;
            if (b != null) tabs.Add(b);
        }

        Button selectedIcon = _pickerSelection >= 0 && _classIcons != null && _classIcons[_pickerSelection] != null &&
                              _classIcons[_pickerSelection].gameObject.activeSelf
            ? _classIcons[_pickerSelection].GetComponent<Button>() : null;
        Button firstIcon = selectedIcon != null ? selectedIcon : icons.Count > 0 ? icons[0] : null;
        Button openTab   = _openGroup < tabs.Count ? tabs[_openGroup] : null;

        for (int t = 0; t < tabs.Count; t++)
            SetNavigation(tabs[t], up: null, down: firstIcon,
                          left: t > 0 ? tabs[t - 1] : null, right: t < tabs.Count - 1 ? tabs[t + 1] : null);

        for (int i = 0; i < icons.Count; i++)
            SetNavigation(icons[i], up: openTab, down: PickerConfirm,
                          left: i > 0 ? icons[i - 1] : null, right: i < icons.Count - 1 ? icons[i + 1] : null);

        if (PickerConfirm != null)
            SetNavigation(PickerConfirm, up: firstIcon, down: null, left: null, right: null);
    }

    private static void SetNavigation(Selectable s, Selectable up, Selectable down, Selectable left, Selectable right)
    {
        if (s == null) return;
        s.navigation = new Navigation
        {
            mode = Navigation.Mode.Explicit,
            selectOnUp = up, selectOnDown = down, selectOnLeft = left, selectOnRight = right,
        };
    }

    // Solo mira: escribe el nombre de esa clase (o lo limpia con índice inválido).
    private void ShowClassName(int index)
    {
        CharacterClassDefinition cls = ClassAt(index);
        if (ClassNameLabel != null) ClassNameLabel.text = cls != null ? cls.DisplayName : "";
    }

    // Selecciona sin aplicar: resalta el ícono y muestra su descripción.
    private void SelectClassInPicker(int index)
    {
        _pickerSelection = index;
        ShowClassName(index);

        CharacterClassDefinition cls = ClassAt(index);
        if (ClassDescLabel != null) ClassDescLabel.text = cls != null ? cls.DisplayDescription : "";
        if (ClassStatRows != null)
            foreach (UI_ClassStatRow row in ClassStatRows)
            {
                if (row == null) continue;
                if (cls != null) row.Show(cls);
                else row.gameObject.SetActive(false);
            }

        // El resaltado es el propio ícono, más brillante. Los demás quedan apagados, así
        // se ve de un vistazo cuál está elegida.
        if (_classIcons != null)
            for (int i = 0; i < _classIcons.Length; i++)
                if (_classIcons[i] != null)
                    ButtonTint.Paint(_classIcons[i], i == index ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f));

        if (PickerConfirm != null) PickerConfirm.interactable = cls != null;
        WirePickerNavigation();   // Confirmar sube a la clase elegida
    }

    // Recién acá se aplica de verdad.
    private void ConfirmClassSelection()
    {
        if (ClassAt(_pickerSelection) == null) return;
        ChooseClass(_pickerSelection);
    }

    private void ShowClassPicker(bool show)
    {
        if (ClassPicker == null) return;

        // Al abrirlo, arrancar en la clase que ya tenés puesta: así el selector muestra
        // de entrada qué elegiste y se puede confirmar sin volver a tocar el ícono.
        if (show)
        {
            int current = -1;
            LobbyManager lobby = LobbyManager.Instance;
            if (lobby != null && !LobbyManager.IsBot(_pickerTarget)
                && lobby.TryGetLocalEntry(out LobbyEntry me)) current = me.ClassIndex;

            // Se abre en la pestaña del rol de esa clase (o en la primera, sin clase).
            int group = ClassAt(current) != null ? _roleGroups.IndexOf(RoleOf(current)) : -1;
            OpenRoleGroup(group >= 0 ? group : 0);

            if (ClassAt(current) != null) SelectClassInPicker(current);
            else
            {
                _pickerSelection = -1;
                ShowClassName(-1);
                if (ClassDescLabel != null) ClassDescLabel.text = "";
                if (PickerConfirm != null) PickerConfirm.interactable = false;
                if (_classIcons != null)
                    foreach (Image icon in _classIcons)
                        ButtonTint.Paint(icon, Color.white);
            }
        }

        ClassPicker.gameObject.SetActive(show);

        // Con control: al abrirlo, el ícono de la clase que tenías (o el primero); al cerrarlo,
        // lo que toque en la sala (tu ícono, Confirmar). Con el mouse no hace falta.
        if (MenuInput.UsingPad) MenuInput.Select(DefaultSelection);
    }
}
