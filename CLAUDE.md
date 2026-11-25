# GasTest — contexto del proyecto

Juego: **Mercenaries**, hero shooter en tercera persona (Unity 6000.0.59f2, URP).
Motor de habilidades **propio** (GAS) + red con **FishNet**. Desarrollador único:
Gustavo Pedraza. Todo el código está comentado en español.

---

## Cómo trabajamos

**Los nombres van en INGLÉS.** Prefabs, GameObjects, materiales, escenas, carpetas,
clases y campos: `Objective_GoldBag`, `Base_Team1`, `Mat_ArenaSand`, `SpawnPoint_1`.
Los **comentarios, los `Debug.Log` y los menús de editor siguen en español** — el
proyecto entero está así.

**Lo que ve el jugador va en ESPAÑOL e INGLÉS, por `Loc`** (desde el 8 de octubre de
2026, pedido de Gustavo tras la prueba; antes era solo inglés). Ningún texto visible se
escribe en el código: se pide por clave, `Loc.T("menu.play")`, y la tabla
`Assets/Resources/Localization.csv` (columnas `key;es;en`, UTF-8, se abre con Excel) dice
qué es en cada idioma. Las variables van con nombre: `Loc.T("lobby.team", ("team", 2))`
con `Equipo {team}` en la tabla. Un texto fijo se ata con `LocalizedText` (se reescribe
solo al cambiar de idioma); lo que se recalcula llama a `Loc.T` cada vez y escucha
`Loc.OnLanguageChanged`. **El juego arranca en español**; el idioma se elige en Ajustes
(`GameSettings.Language`). "Mercenaries" no se traduce. Lo que el SERVIDOR le manda a los
clientes no puede ir como texto ya escrito (cada uno lo ve en su idioma): se manda qué es
(un enum, un índice) y cada pantalla lo escribe: el registro de bajas manda el índice de
la clase y qué NPC es (`ENpcLabel`), el desatascarse manda la CLAVE del motivo. **El CONTENIDO no va en la
tabla, va en el propio asset** (desde el 8 de octubre de 2026): el inglés y, al lado, el
español — `ClassName`/`ClassNameEs`, `Description`/`DescriptionEs`, `AbilityName`/
`AbilityNameEs`, `Description`/`DescriptionEs` de cada habilidad (la ventana de ayuda), el `Name`/`NameEs` de cada opción de rueda. La UI lee `DisplayName` /
`DisplayDescription` (`Loc.Pick`), nunca el campo en inglés directo. Así renombrar un asset no
rompe nada. Se cargan todos de una por `DesignDocuments/Traduccion_Contenido.csv` (por GUID) con
`Mercenarios ▸ Idiomas ▸ Importar nombres traducidos` / `Exportar`. Una habilidad o clase
nueva lleva su texto en español.

**El trabajo dentro del editor de Unity lo hace Gustavo.** Armar escenas, crear y
configurar prefabs, cablear referencias en el Inspector, acomodar geometría: eso es
suyo, porque lo está aprendiendo. Lo que se entrega desde acá es **código** — incluidas
las herramientas de editor que él después corre — más las instrucciones de qué tocar.
Cuando el pedido es concreto ("armame la arena", "generá el prefab"), se hace.

**No cablear prefabs por ruta fija** en herramientas de editor: él renombra los assets y
la referencia queda en null sin avisar. Buscarlos por COMPONENTE
(`AssetDatabase.FindAssets("t:Prefab")` + `GetComponent<T>()`), con la ruta solo como
atajo. **Ojo con los nombres repetidos al buscar por nombre:** hay dos `GA_Fireball` (el de
`48toPlay` es el del fantasma); el 22 de octubre de 2026 una herramienta agarró ese y la Bola de
fuego de la Evocación salió sin canalizar. Si un nombre puede repetirse, filtrar también por la
carpeta (o avisar si hay más de uno).

**Para verificar que compila, SIN cerrar el editor.** Unity trae su propio Roslyn, y deja
en `Library/Bee` el archivo con los argumentos exactos que usa para compilar. Se lo puede
invocar directo: tarda segundos y no abre una segunda instancia.

```bash
cd ".../Proyectos Gustavo/GasTest/GasTest"
D="/c/Program Files/Unity/Hub/Editor/6000.0.59f2/Editor/Data"
RSP=$(ls -t Library/Bee/artifacts/*.dag/Assembly-CSharp.rsp | head -1)
sed 's|-out:.*|-out:"Temp/_check.dll"|; s|-refout:.*|-refout:"Temp/_check.ref.dll"|' "$RSP" > /tmp/check.rsp
"$D/NetCoreRuntime/dotnet.exe" "$D/DotNetSdkRoslyn/csc.dll" "@/tmp/check.rsp" -nologo 2>&1 | grep "error CS"
```

Cuatro límites que conviene saber:

- **El `.rsp` lista los archivos fuente uno por uno.** Un `.cs` NUEVO no entra hasta que
  Unity regenere ese archivo — hay que agregarlo a mano a la copia (`echo '"Assets/..."'
  >> /tmp/check.rsp`) o compilar con Unity una vez.
  Y **no termina en salto de línea**: hacer `echo` antes de agregar, si no la primera ruta se
  pega a la línea anterior y ese archivo no compila (sale como "tipo no encontrado").
- **No corre el codegen de FishNet** (el ILPostProcess que genera los RPCs), así que un
  error que solo aparece ahí no lo detecta.
- Es para `Assembly-CSharp`. Para las herramientas de editor, el `.rsp` equivalente es
  `Assembly-CSharp-Editor.rsp`.
- **Con el editor CERRADO no existe `Temp/`**: la salida (`-out` y `-refout`) va a `Library/`, que no se sube.

**Los `.cs` del proyecto están en CRLF** (los `.md` y los `.asset` van mezclados: revisar
con `file`). Un `perl -0pi` con `\n` en el patrón no encuentra nada y no avisa: hay que
escribir `\r\n` (o `\r?\n`) y meter `\r\n` también en el reemplazo. Y al leer un archivo
de reemplazo desde perl, `binmode` sin capa `:utf8` — con la capa, el resto del archivo
sale con las tildes rotas.

**Compilar con Unity en batch** sigue siendo la verificación completa, pero necesita el
editor CERRADO (no entran dos instancias sobre el mismo proyecto):

```bash
"C:/Program Files/Unity/Hub/Editor/6000.0.59f2/Editor/Unity.exe" -batchmode -quit -nographics -projectPath "C:/Users/MDyR/Desktop/Proyectos Gustavo/GasTest/GasTest" -logFile compile.log
```


**Con el editor ABIERTO, el CLI `unity`** (plugin oficial de Unity + paquete `com.unity.pipeline`,
desde el 28 de octubre de 2026): `unity recompile` compila de verdad (con el codegen de
FishNet), `unity command console status / get --level warning --result-only` lee la consola,
`unity command asset find --name X` dice dónde ve Unity un asset. Se usa para VERIFICAR sin
preguntar; correr herramientas, entrar a Play o hacer builds, preguntando antes. La consola
tiene ~150 avisos, todos de FishNet (`Runtime/`): no tocarlos (se pierden al actualizar). Un
aviso de `Assets/Scripts` o `Assets/Prefabs` sí es nuestro.
---

## Mapa del proyecto

La regla, en una línea: **arte en Art, prefabs en Prefabs, datos en Attributes / Effects /
GameplayAbilities, código en Scripts.** Todo va por lo que ES, no por quién lo usa.

| Dónde | Qué |
|---|---|
| `Assets/Art/` | Modelos (`Models/Classes/`: los de cada clase), materiales (`Materials/Arena` es la arena), animaciones (los AOC por clase), VFX, e `Icons/` (`Classes/` y `Abilities/` por clase). |
| `Assets/Audio/` | Los clips: `Sfx/` y `Music/`. La biblioteca que los referencia está en `Resources/AudioLibrary`. |
| `Assets/Prefabs/` | Todos los prefabs: `Player/` (jugador, su cámara y `Classes/`, los modelos de clase), `Enemies/`, `Objective/`, `Projectiles/`, `Summons/`, `UI/`, `Map/` (el sector del cementerio), `Network/` (el `NetworkManager` de las tres escenas y su `NetworkHudCanvas`: venían de las demos de FishNet, borradas el 28 de octubre de 2026). |
| `Assets/Attributes/` | Clases jugables (`Class_*.asset`) y stats (`ASDef_*`), por clase; `Enemies/` y `Summons/` para los NPCs. |
| `Assets/Effects/` | Los `GE_*`, por tipo (Damage, Buffs, CC, Debuffs, Cooldowns). |
| `Assets/GameplayAbilities/` | Los `GA_*` por clase, **con sus scripts `GA_*.cs` al lado** — son contenido. `Enemies/` para las de los NPCs. |
| `Assets/Scripts/GAS/` | El motor de habilidades: ASC, efectos, habilidades base, atributos, tags, registros. `Entities/` son los objetos que las habilidades crean (proyectiles, tótems, barreras). El ASC y `GameplayAbility` están repartidos por tema: el archivo principal (`AbilitySystemComponent.cs`, `GameplayAbility.cs`) y sus partes en `AbilitySystemComponent/` y `GameplayAbility/`. |
| `Assets/Scripts/Network/` | Conexión y red: `NetworkAbilitySystemComponent`, `NetworkGameManager`, `LobbyManager` (la sala de espera), `NetAddress` (las direcciones), `LanDiscovery` (la lista de partidas en la red) y `ConnectionHUD` (el recuadro viejo, solo para `Test_Network`). |
| `Assets/Scripts/Player/` | El jugador y sus cámaras: `PlayerController`, input, animación, `RagdollController`, `UpperBodyAim`, `ThirdPersonOrbitCam`, `SpectatorCamera`, `MenuOrbitCamera`. |
| `Assets/Scripts/UI/` | Toda la UI que no es de un modo: HUD de clase, menú de clases, sala (`UI_LobbyPanel`), menú principal, ajustes, `MercUIFactory`, `InputGlyphs` (la ÚNICA tabla de qué botón se dibuja: Q / RB / R1) y `UICursor` — el único dueño del cursor y del modo de input. |
| `Assets/Scripts/CombatFeel/` | La sensación de los golpes: el panel `CombatFeel` (asset en `Resources`) y un archivo por efecto (parón, sacudida, bordes, daño fantasma, habilidad no disponible, rachas). |
| `Assets/Scripts/Settings/` | `GameSettings` (PlayerPrefs), incluido qué control eligió el jugador y el idioma. |
| `Assets/Scripts/Localization/` | `Loc` (los textos por clave, en español e inglés) y `LocalizedText`. La tabla es `Resources/Localization.csv`. |
| `Assets/Scripts/Audio/` | Sonido: `AudioManager`, `MusicPlayer`, `AudioLibrary` y `SfxCue`. |
| `Assets/Scripts/GameMode/` | Lo que sirve a CUALQUIER modo: `DeathZone`, `FloatingVisual`, `NPC_Target`, `HealthPack`, `TrainingDummy` (el muñeco de práctica). |
| `Assets/Scripts/GameMode/Mercenaries/` | **El modo Mercenarios**, y solo él: el modo, la arena, bases, objetivo, NPCs, bots y su HUD (`UI/`). Ver su `LEEME_ModoMercenarios.md`. Un modo nuevo (FFA) va en su propia carpeta al lado. |
| `Assets/Scripts/Editor/` | Todas las herramientas de editor (menú `Mercenarios ▸ …`, el inspector de habilidades y `ClassModels/`, que importa los modelos de Blender). Tiene que llamarse `Editor` para que Unity no las meta en la build. |
| `Assets/Resources/` | Lo que se carga por nombre: los dos registros de red, el catálogo de enemigos, la biblioteca de audio y la tabla de textos (`Localization.csv`). |
| `Assets/48toPlay/` | Restos de la game jam: los fantasmas y sus stats. Se reusan como NPCs. |
| `Assets/AssetsExtra/` | Packs comprados: FishNet, animaciones de Kevin Iglesias, Medieval Cute Series, Vefects, VFX. |
| `Blender/` | Los modelos de clase hechos en Blender (desde el 10 de noviembre de 2026, empezando por la Paladina): las recetas en Python, el `.blend` y en `Out/<Clase>/` lo que importa Unity. Ver su `LEEME.md`. |
| `DesignDocuments/` | Diseño en `.docx`: arquitectura del GAS, **arquitectura de red** (`Red_Arquitectura.docx`: servidor autoritativo, cómo viaja el ASC, todas las RPC), guía de habilidades, las 8 clases, glosario. |

**Escenas:**
- `Assets/Scenes/Test_Network.unity` — pruebas rápidas de clases y habilidades.
- `Assets/Scenes/Test_Abilities.unity` — **la escena de prueba de habilidades** (desde el 17 de noviembre de 2026): una copia
  de Test_Network con muñecos (`TrainingDummy`) que dicen el DPS, la cadena de cada golpe y los efectos recibidos. Un muñeco
  más es una instancia de `Prefabs/Testing/TrainingDummy` con sus números en el Inspector.
- `Assets/Scenes/Mercenaries_Gamemode.unity` — **el modo de juego**, la arena 3c3c3. Es también la
  pantalla de inicio: el menú principal es un panel sobre la arena, no otra escena.
- `Assets/Scenes/Mercenaries_Graveyard.unity` — **el mapa nuevo** (El Cementerio de los Tres
  Panteones), en greybox. Es una copia de la del modo con otro mapa: trae la sala, la red y
  los menús. Tres instancias de `Prefabs/Map/Graveyard_Sector` giradas 120°: lo que se
  cambie en el prefab cambia en los tres sectores. La generó una herramienta una sola
  vez; desde ahí se edita a mano (Gustavo ya le sacó muros y agrandó el centro). **Es de noche** (desde el 12 de
  noviembre de 2026): la luz ambiente y la niebla son ajustes de la escena (los puso una herramienta de una vez, ya borrada; se cambian en Window ▸ Rendering ▸ Lighting ▸ Environment), y lo
  que se ve lo alumbran la luna y las antorchas del sector (`Prefabs/Map/Torch`; solo las más cercanas a la cámara hacen
  sombra, `TorchShadows`). Alrededor hay una ciudad de decorado (`Graveyard_Sector ▸ City`, sin colliders ni NavMesh). Abajo,
  las catacumbas tienen un anillo y osarios con rampas a la superficie (13 de noviembre de 2026). **El NavMesh se hornea en la
  escena**: cambiar la geometría del sector = volver a hornearlo y guardar la escena. Ver el LEEME del modo.

---

## Reglas técnicas que no se negocian

- **El servidor manda.** Toda la lógica de juego (daño, efectos, puntaje, experiencia)
  corre server-side; los clientes solo dibujan lo que llega sincronizado. Cada
  `GameplayAbility.Activate()` empieza con `if (!IsServer) return;`.
- **Los enums se amplían SOLO al final.** `EGameplayTag` y `EAttributeType` se serializan
  por número en los `.asset`; insertar un valor en el medio corre los índices y rompe
  todas las referencias ya guardadas.
  **Los tags tienen GRUPOS** (desde el 18 de noviembre de 2026, `GameplayTagTree`): los `Group_*` del final del enum no se
  dan, se pregunta por ellos ("¿tiene algún control?", "inmune a todo daño con el tiempo"). Un tag nuevo que sea de un
  grupo va con su línea en la tabla hijo → padre; lo que busque efectos por el tag que dan usa `GameplayTagTree.AnyMatches`,
  no `GrantedTags.Contains`.
  **Cada valor nuevo de `EGameplayTag` / `EAttributeType` lleva `[InspectorName("Carpeta/Nombre")]`** (desde el 18 de noviembre de
  2026): el desplegable del Inspector los agrupa en carpetas por tema o por clase. El nombre visible se cambia libre; el
  ORDEN no (es el número guardado).
- **Los jugadores viven en la capa 7 ("Character")**, no en la 6. Los ataques melee la
  usan como máscara en sus `Physics.OverlapX`.
- **`ASC.ActiveEffects` solo existe poblado en el servidor.** La UI remota lee de los
  diccionarios sincronizados del `NetworkAbilitySystemComponent`, no del ASC local.
- **El transform del jugador es client-authoritative**: teletransportes, saltos y dashes
  los ejecuta el DUEÑO por TargetRpc, nunca el servidor escribiendo el transform. Un bot no
  tiene dueño: cada uno de esos movimientos tiene su ruta en `BotController` (`ServerDash`,
  `ServerLeap`, `ServerStartFlight`, `ServerDive`), y lo que no la tiene se marca `MovesThroughOwner`.
  Un teletransporte va SIEMPRE por `NetworkAbilitySystemComponent.ServerTeleportOwnerTo`, que resuelve los tres casos: el
  dueño, el bot y (desde el 19 de noviembre de 2026) el NPC (`ServerTeleportNpc`, Warp al NavMesh).
- **Los registros de red.** Cada `GA_*` o `GE_*` tiene que estar en
  `GameplayAbilityRegistry` / `GameplayEffectRegistry` (los dos assets de
  `Assets/Resources/`), porque FishNet manda el ÍNDICE dentro de esas listas y no la
  referencia. Si falta uno, su VFX se ve **solo en el host**. **Se actualizan solos** (desde el
  17 de noviembre de 2026, `GasRegistryAutoUpdate`) al crear, duplicar, borrar o renombrar uno;
  a mano, `Mercenarios ▸ Actualizar los registros de red` (`MercRegistryTools`), que las herramientas
  de una vez siguen llamando al terminar. El orden lo decide SOLO `CollectFromProject` de cada
  registro. Un cambio de los registros = todos con el mismo build.

- **Para revisar el GAS hay dos ventanas** (desde el 17 de noviembre de 2026): `Mercenarios ▸ GAS ▸ Revisar el GAS` (el
  validador: registros, tags de bloqueo, íconos, textos, nombres repetidos; correrlo después de crear habilidades o
  efectos) y `Mercenarios ▸ GAS ▸ Depurador en vivo` (en Play: atributos, tags, efectos, cooldowns y la cadena de cada
  golpe). Una regla nueva del proyecto que se pueda revisar mirando los assets va como chequeo del validador
  (`GasValidatorWindow.Scan`). Desde el CLI: `GasValidatorWindow.Report()`.

- **Para medir el balance con números de una partida de verdad: `Mercenarios ▸ GAS ▸ Medir el balance`** (desde el 18 de noviembre
  de 2026): Empezar → jugar → Informe (consola y `Logs/BalanceReport.txt`). Por personaje: lo recibido crudo → tras defensas →
  escudo → vida, lo hecho y de dónde sale cada punto de vida que GANA (también lo que escribe la vida directo: la pila dice quién).
  Lo anota el ASC con `TraceBalance` (parte CombatLog); apagado no cuesta. Antes de opinar "está desbalanceado", medirlo así.

- **En el HOST, un callback de SyncVar del lado cliente (`asServer` false) no escribe en el ASC** (desde el 18 de noviembre de
  2026): el ASC es el mismo objeto que el del servidor y el valor que trae puede ser VIEJO. `OnNetHealthChanged` lo hacía y le
  devolvía la vida del golpe siguiente a cualquiera en el host (la medición del balance lo vio como curación). Un espejo nuevo de
  un atributo va con `!asServer && !IsServerInitialized`.

- **El camino del daño tiene pruebas automáticas** (desde el 17 de noviembre de 2026):
  `Scripts/Editor/Tests/GasDamagePipelineTests.cs` (EditMode, sin assets ni escena). Después de tocar `ExecuteInstantEffect`,
  las defensas, el escudo o los efectos, correr `unity command test run --mode editor --filter GasDamagePipeline`. Si una
  regla cambia A PROPÓSITO, su prueba cambia en el mismo commit. Los números de una prueba nueva se eligen para que una
  regla distinta dé OTRO resultado (con 20 de daño, armadura 10 da lo mismo con dos fórmulas: la prueba no vigilaba nada).
  Desde el 25 de noviembre de 2026 también prueban las cuentas del instinto de los bots ("Bots_…": la esquiva, adelantarse,
  la puntería, superado en número), que son funciones estáticas de `BotController.Instinct` para poder probarlas sin escena.
  Y las reglas del modo ("Modo_…": nivel de la bolsa, barrita, experiencia pasiva, bajas, quién gana, aviso de eliminado;
  sección "LAS CUENTAS", en `MercenariesGameMode.Rules.cs`) y de la sala ("Sala_…": nombre repetido, equipo lleno, cuándo arrancar;
  estáticas de `LobbyManager`). **Un cambio de regla del modo va en esas funciones**, no en el código de red que las llama.

- **Guía sencilla para Gustavo: `Assets/Scripts/LEEME_ScriptsPartidos.md`** (qué es `partial`, las 3 reglas y una tabla
  "si quieres cambiar X, abre Y" por clase). Si se parte otra clase o se agrega una parte, se actualiza también ahí.
- **El ASC y `GameplayAbility` son `partial class` repartidas por tema** (desde el 17 de noviembre de 2026). La cabecera del
  archivo principal (`GAS/AbilitySystemComponent.cs`, `GAS/GameplayAbility.cs`) dice qué tiene cada parte
  (`GAS/AbilitySystemComponent/`, `GAS/GameplayAbility/`). Lo nuevo va en la parte de su tema; un tema nuevo grande, en una
  parte nueva que se anota en esa cabecera. **Un campo nuevo del Inspector va SIEMPRE en el archivo principal**: Unity guarda
  los campos en el orden en que están escritos, y repartidos entre archivos ese orden no está garantizado. **El archivo
  principal se llama EXACTAMENTE como la clase y no se renombra**: con la clase repartida, Unity solo le asocia el componente
  a ese archivo (el del ASC se llamaba "Ability System Component.cs" y, al repartirlo, los prefabs lo vieron como script
  perdido hasta renombrarlo conservando su GUID).
  **Igual quedaron `PlayerController`, `NetworkAbilitySystemComponent` y `BotController`** (25 de noviembre de 2026; tenían
  5 500, 2 700 y 3 000 líneas): en el principal, los campos, eventos y tipos (en el de red, todas las SyncVars); los métodos,
  por sección, en `Player/PlayerController/`, `Network/NetworkAbilitySystemComponent/` y `GameMode/Mercenaries/BotController/`.
  Se movió el texto sin cambiarlo. "Player Controller.cs" pasó a `PlayerController.cs` con su mismo GUID. Las RPC de FishNet
  pueden ir en cualquier parte (el codegen trabaja sobre la clase compilada). Más tarde ese día, igual: `GC_Projectile`
  (`GAS/Entities/GC_Projectile/`) y `UI_LobbyPanel` (`UI/UI_LobbyPanel/`). Y al final del día, las medianas:
  `MercenariesGameMode` y `MercEnemyAI` (`GameMode/Mercenaries/<Clase>/`), `Entity_Companion` y `Entity_ShieldBarrier`
  (`GAS/Entities/<Clase>/`) y `GA_ProjectileShoot` (`GameplayAbilities/GenericsAbilities/GA_ProjectileShoot/`; antes se le
  agregaron los títulos de sección IMPACTO, ACTIVAR, DISPARO y GIZMOS, que no tenía). Son 12 clases partidas.
- **Desde otro script, los componentes de un jugador salen de `pc.AbilitySystem` y `pc.NetAbilitySystem`** (ya guardados
  en `Awake`), no de `pc.GetComponent<…>()`, sobre todo en algo que corre cada frame (25 de noviembre de 2026).

- **Las búsquedas de física van por `PhysicsQuery`, no por `Physics.OverlapSphere` / `OverlapBox` / `OverlapCapsule` /
  `RaycastAll` / `SphereCastAll`** (desde el 25 de noviembre de 2026): esas crean un arreglo nuevo en cada golpe. `PhysicsQuery`
  hace lo mismo sobre un arreglo PRESTADO de una reserva: `using var cols = PhysicsQuery.OverlapSphere(...)` (se recorre igual:
  foreach, `[i]`, `Length`, `Sort`) o, para un solo foreach, `foreach (... in PhysicsQuery.OverlapSphere(...).Once())`. Es una
  reserva y no un arreglo compartido porque un golpe puede disparar otra búsqueda mientras se recorre la primera. El resultado
  no se guarda ni se pasa a otro lado: se devuelve al terminar el bloque. Si llena el arreglo, repite con uno del doble.
- **Para recorrer personajes, las listas de prendidos, no `FindObjectsByType`** (desde el 25 de noviembre de 2026):
  `PlayerController.All` (personas y bots), `AbilitySystemComponent.All` (todo lo que tiene GAS: también NPCs e
  invocaciones) y `Entity_Companion.All`, como ya tenían `HealthPack.All` y los proyectiles. Entran en `OnEnable` y salen en
  `OnDisable`, así que dan lo mismo que el barrido. **Si lo de adentro del foreach puede dañar, matar, crear o destruir**
  (un golpe a todo el mapa, la marca del Cazador), se recorre una copia: `new List<…>(X.All)`; si solo lee, directo. En las
  pruebas de editor Unity no llama `OnEnable`: el ayudante `Character` de las pruebas anota a mano
  (`AbilitySystemComponent.TrackForTests`). `FindFirstObjectByType` para buscar UNA cosa al arrancar (un panel, el
  administrador de partida) sigue bien.
- **Las herramientas de editor "de una vez" se borran cuando ya se corrieron** (el 25 de noviembre de 2026 se fueron las 13
  de "armar": campanas, enemigos, jefe, mapa, orbes, santuarios, biblioteca de audio). Quedan en el historial de git. En el
  menú `Mercenarios` solo queda lo que se usa seguido.
- **El cursor tiene un solo dueño: `UICursor`.** Ningún menú toca `Cursor.lockState` ni
  `PlayerInputProvider.SetUIMode` por su cuenta — los pide con `UICursor.Request(this)` y
  los suelta con `Release`. Si no, gana el último en cerrarse.
- **Cada ventana tiene su PUESTO: `LocalSeat`** (desde el 28 de octubre de 2026, para el showcase: varias
  ventanas en una PC, cada una con su jugador). Con `-pad` / `-kbm` / `-mute` en la build (o los tags de MPPM en
  el editor) decide con qué dispositivos juega la ventana. **Toda copia del asset de input se anota con
  `LocalSeat.Register(copia)`** (hoy: la del jugador, la de los menús y la del espectador), y lo que lee un
  dispositivo por fuera del asset pregunta `LocalSeat.AllowsKeyboardMouse` o usa `LocalSeat.Pad`. **Nada de
  `Gamepad.current`, `Keyboard.current`, `Mouse.current` ni `Input.GetKey` sueltos**: en la PC del showcase un
  control movería las tres ventanas.
- **La animación NO viaja por un `NetworkAnimator`: el jugador no tiene uno.** El que
  tenía estaba inerte (su campo Animator vacío y el Animator en un hijo, así que FishNet
  no lo encontraba y se apagaba solo, sin warning) y se quitó el 25 de septiembre de
  2026. **No volver a ponerlo** "para arreglar" algo: chocaría con lo que ya se manda.
  **Toda** la animación se manda a mano: las de habilidad, golpe, stun y muerte por las
  RPC del `NetworkASC`, y caminar/saltar/caer más el `AimPitch` por las RPC de
  `PlayerController`. Un parámetro nuevo del Animator que tenga que verse en las otras
  pantallas hay que mandarlo; no alcanza con escribirlo.
  Una habilidad con pose sostenida (un canalizado: el molinete, la carga del Golpe final)
  declara `BroadcastsOwnAnimation`: si no, el clip suelto que el servidor manda después de
  `Activate()` pisa la pose en las otras pantallas (el dueño no lo nota: ese RPC lo saltea).
- **Los modificadores `Multiply` se MULTIPLICAN entre sí** (×0.5 y ×0.5 = ×0.25):
  `AttributeValue` guarda la lista y rehace el producto. Hasta el 25 de septiembre de
  2026 se sumaban (×0.5 + ×0.5 = ×0). La velocidad de ataque tiene un tope de
  `MinAttackInterval` = 0.3 s entre ataques.
- **Lo que pasa a cierta hora de la partida es un EVENTO del administrador** (`Prefabs/GameMode/MatchEvents`,
  `GameModeEventManager`, desde el 11 de noviembre de 2026): las rejas, el primer Objetivo, los santuarios, las campanas, los orbes de definitiva y el jefe de mitad de partida. Uno
  nuevo hereda de `MatchEvent` y es un hijo más del prefab; no se agrega otro reloj al modo. Detalle en el LEEME
  del modo ("Los eventos de la partida y los santuarios").

- **Algo del mapa que se golpea pero no es un personaje lleva `StrikeTarget`** (desde el 11 de noviembre de 2026; lo primero es
  la campana de las torres): un ASC neutral más ese componente, y su collider en la capa Character. Las habilidades lo encuentran
  como a un personaje, pero no es enemigo ni aliado de nadie (`ASC.IsEnemyOf` / `IsAllyOf` dan false) y el golpe le AVISA
  (`OnStruck`, en el servidor) en vez de aplicarle efectos: lo resuelve `ApplyHitEffects`, el proyectil, y las habilidades que
  filtran por enemigo antes de golpear llaman a `StrikeIfScenery`. Una habilidad nueva que elija blancos así, también.

- **Los managers de escena reinician su sesión en `OnStartServer`.** Al parar el servidor
  FishNet destruye lo spawneado pero NO toca el estado de los NetworkObjects de escena
  (`LobbyManager`, `NetworkGameManager`, `MercenariesGameMode`): SyncVars, listas,
  puntajes. Desconectar → Iniciar Host en el mismo Play es un camino normal (el menú
  principal lo usa), así que todo estado de partida se limpia al arrancar el servidor.
- **Un NetworkObject de escena creado por código necesita que le asignen su `SceneId`.**
  FishNet se lo da al validar cada objeto de a uno en el editor; creando varios de un
  saque en una herramienta quedan en `SceneId 0`, y para FishNet eso no es un objeto de
  escena: no se spawnea (pasó con los botiquines, aparecía uno solo). Al terminar de
  crearlos hay que llamar `NetworkObject.CreateSceneId(Scene, force, out changed)` por
  reflexión — es lo mismo que corre *Tools ▸ Fish-Networking ▸ Utility ▸ Reserialize
  NetworkObjects*:

  ```csharp
  var m = typeof(NetworkObject).GetMethod("CreateSceneId",
      BindingFlags.Static | BindingFlags.NonPublic, null,
      new[] { typeof(Scene), typeof(bool), typeof(int).MakeByRefType() }, null);
  object[] args = { scene, false, 0 };   // true = rehacer TODOS (escena copiada de otra)
  m.Invoke(null, args);                  // args[2] = cuántos cambió
  ```
- **Una habilidad interrumpible se decide en DOS lados.** El servidor la corta al activar
  otra (`CancelSerial`), pero antes el cliente tiene que dejar leer el botón nuevo mientras
  hay una corriendo (`_attackInterruptible` / `_runningSlot` en
  `PlayerController.HandleAbilityInput`). Arreglar un solo lado hace que marcar
  `IsInterruptible` no cambie nada.
- **La definitiva carga por rol** (`EClassRole` en cada clase; ver
  `PlayerController` → "CARGA DE LA DEFINITIVA"). El daño aguantado y la curación se
  detectan solos en el pipeline de efectos del ASC; **una curación que escribe la vida
  directo** (como el aura del Paladín) tiene que avisar con `NotifyHealedAlly`, o el
  soporte no carga con ella — y con `ASC.ShowCombatNumber(curado, ECombatNumber.Heal)`,
  o no sale su número verde. Lo que pasa por `ExecuteInstantEffect` avisa las dos
  cosas solo. **Curarse a uno mismo no carga**: `OnHealedAlly` es solo para curar a
  OTRO. La pasiva que quiera enterarse de la autocuración (la Bendición del Clérigo)
  escucha `OnHealedSelf`, que va aparte justamente para no tocar la carga.

- **Qué bloquea a cada habilidad lo dice su `ActivationBlockedTags`, por TIPO de acción**
  (desde el 1 de octubre de 2026): `State_Stunned` en todas las que se aprietan;
  `State_Disarmed` en las de ARMA (golpear con ella o lanzarla; el bloqueo con escudo NO);
  `State_Silenced` en las de MAGIA o fantasía; `State_Rooted` en las de MOVIMIENTO. Una
  habilidad de dos tipos lleva los dos tags (cualquiera la bloquea). Las de "salir de
  apuros" (Comida de emergencia, Protección divina) solo llevan el aturdido. Una
  habilidad nueva tiene que llevar los suyos. Nada de esto se resuelve por código.

- **¿Es de mantener? Se pregunta `HoldInput.IsHold(habilidad)`, no `is IHoldAbility`**
  (desde el 1 de octubre de 2026). `GA_ProjectileShoot` implementa la interfaz pero solo
  es un mantenido con su casilla `AimBeforeThrow` (los lanzamientos apuntados). Un
  mantenido nuevo implementa `UsesHoldInput => true`.

- **Canalizar es una sección de cualquier GA, no código por habilidad** (desde el 19 de octubre
  de 2026): `CastMode` (se lanza al terminar / continuo), `CastSeconds`, clips de bucle.
  `ServerActivateAbility` llama a `BeginCast` en vez de `Activate`; cortada (lo que la bloquea,
  morir, otra habilidad si es interrumpible) **cuenta como lanzada**. En un lanzamiento apuntado
  lo resuelve `GA_ProjectileShoot` (mantener canaliza). Las que ya canalizan solas
  (`IChanneledAbility`) y las de rueda no pasan por ahí.

- **Todo lo que una habilidad deja en el mundo es una INVOCACIÓN: hereda de `Entity_Summon` y
  se crea con `Entity_Summon.Spawn(prefab, dueño, punto, giro, máximo)`** (desde el 19-20 de
  octubre de 2026; tótems, trampas, cuchillas, copias y compañeros). La base resuelve el equipo,
  el dueño en red (`SummonerObject`), "Dura", el máximo por dueño, si se va cuando el dueño muere,
  irse si el dueño se va o cambia de clase, su vida (`OnOwnDeath`, en Update) y la visibilidad por
  equipo. Lo que aplica algo a los de cerca cada tanto es una **`Summon_Zone`**, sin código (también la que crece o
  avanza: su sección "Crece y avanza", que cada pantalla calcula sola desde que se prendió, sin red nueva). Una
  subclase sobreescribe `Awake`/`Update` como `override` llamando a `base.` (si no, la base no
  corre). Para apoyarla en el piso: `GroundUnderAim` (nunca arriba de un techo) o `GroundNear`.
  Lo que deja algo que muere (el veneno del zombi hinchado, la sección "Al morir" de `MercEnemyAI`) lleva **"Sigue sin su dueño"**
  (`OutlivesOwner`, desde el 12 de noviembre de 2026): si no, desaparece con él; sin autor, sus efectos tienen que ser de daño fijo.
  No se hace `Instantiate` + `Spawn` a mano. Una nueva sale de una VARIANTE de las plantillas
  `Prefabs/Summons/Summon_Base` (con vida) o `SummonZone_Base` (solo zona), y la pone el genérico
  `GA_SpawnSummon`: los pasos están en la guía de habilidades (7.1).

- **El choque de los proyectiles es un barrido del servidor** (`GC_Projectile.Sweep`, desde el 19
  de octubre de 2026), no `OnTriggerEnter`: el tramo recorrido en cada paso, con la forma del
  proyectil (la caja toma el tamaño del arma) y un radio aparte contra personajes.
  Al que lo tiró se lo ignora entero (lo que cuelga de él y de su `NetworkObject`). Si un
  proyectil se deshace como contra una pared en algo que no es el mapa, avisa en la consola
  ("[Proyectil] … se deshizo contra '…'", `GC_Projectile.WarnIfNotScenery`): es diagnóstico
  del "choca con uno mismo" (29 de octubre de 2026), no lo quites sin encontrarlo.
  Rebotar, saltar entre enemigos y volver al lanzador son la sección "Rebote y vuelta" del GA (desde el 19 de noviembre de
  2026), resuelta en el servidor dentro del mismo barrido: lo que cambie la dirección a mitad del barrido sube `_trajectory`
  (lo que quedaba del tramo viejo ya no cuenta) y no toca `s_found` (se está recorriendo).

- **Lo que hace daño en un área AVISA a los bots: `AreaThreats`** (desde el 20 de noviembre de 2026, el instinto de los bots,
  `BotController.Instinct.cs`). Un bot no ve VFX: la espera de `GA_InstantAoE`, la zona de `GA_ContinuousAoE` y las `Summon_Zone`
  que pegan a enemigos se anotan solas, en el servidor. **Una pieza NUEVA que haga daño en un área** (otra habilidad de zona, un
  peligro del mapa) se anota con `AreaThreats.Add(centro, radio, dueño, segundos)` (y `Remove` si se corta antes) o, si vive y
  decide sola, implementa `IAreaThreat` y se anota con `Register` / `Unregister`: si no, los bots se quedan parados adentro. Lo que
  solo cura o ayuda a los suyos no se anota (`HasEnemyHitEffects`). Del mismo modo miran los proyectiles en vuelo
  (`GC_Projectile.Flying`), los escudos levantados (`Entity_ShieldBarrier.RaisedNow`) y los botiquines (`HealthPack.All`): listas
  estáticas del servidor que se vacían al arrancar (`SubsystemRegistration`). **`BotController` es `partial`** (como el ASC): el
  instinto va en su parte y un campo nuevo del Inspector, en el archivo principal. El componente se agrega al nacer el bot, y
  **sus números salen del molde `Resources/BotSettings`** (desde el 25 de noviembre de 2026): un prefab con solo un
  `BotController` ajustado a mano, que cada bot copia al nacer (`ApplySettings`, por `JsonUtility`). Un campo nuevo del bot
  sale en el molde solo. Por eso `BotController` no lleva `RequireComponent(PlayerController)`. Sin el molde, valen los del código.

- **Qué pesa en la build: `Mercenarios ▸ Build ▸ Qué pesa en la build`** (`MercBuildSizeReport`, desde el 25 de noviembre de
  2026; CLI: `MercBuildSizeReport.Report()`, escribe `Logs/BuildSizeReport.txt`). Mide texturas (comprimidas), mallas y audio
  de las escenas de la build y de toda carpeta `Resources`, y marca lo que entra SOLO por estar en una `Resources` (la demo de
  un pack). GitHub no acepta archivos de más de 100 MB: la B10 ya no entró (su `sharedassets0.assets.resS` pesa 194 MB).

- **Un tag con dos fuentes en el cliente** (el espejo de `NetTags` y algo local) cuenta
  dos veces. `State_Dead` es así: la vida en 0 llega por su SyncVar y el cliente se muere
  solo, y además llega el tag. Por eso `OnNetTagsChanged` le saca TODAS las cuentas cuando
  el servidor lo quita (desde el 29 de octubre de 2026: el cliente reaparecía "muerto" y no
  se movía). Si agregas otro tag que el cliente también ponga solo, haz lo mismo.

- **Qué habilidad hizo cada golpe lo sabe `AbilitySystemComponent.DamageContext`** (desde el 13 de noviembre de 2026, para el
  resumen de muerte): `GameplayAbility` lo pone mientras aplica sus efectos (`ApplyHitEffects`, `ApplyEffectsTo`, también desde
  sus proyectiles) y un efecto con duración lo guarda para sus ticks. Una habilidad que aplique daño con `ApplyGameplayEffect`
  directo sale en el resumen como "Golpe": que pase por `ApplyHitEffects` (o ponga el contexto).

- **El destello de recibir algo no es un VFX de habilidad.** `UI_CombatFlash` pinta un contorno sobre quien recibe daño,
  curación o un golpe que frenó entero el escudo, y sale solo del aviso del número flotante (`ObserversCombatNumber`,
  desde el 4 de noviembre de 2026). Una curación que escribe la vida directo y ya llama a `ShowCombatNumber` lo tiene
  gratis; un GA o un GE no tienen que hacer nada.

- **La sensación de los golpes se maneja desde el asset `Resources/CombatFeel`** (desde el 6 de noviembre de 2026): una casilla
  por efecto y sus números, y cada jugador apaga lo suyo en Ajustes ▸ Combate (se ve si están prendidos los dos: `CombatFeel.HitStopOn`
  y compañía). Los efectos de `Scripts/CombatFeel/` no los llama nadie por su nombre: miran el juego o escuchan los eventos de
  `CombatFeel`, para poder borrarlos sueltos, y uno nuevo va igual. Una habilidad de arma cuerpo a cuerpo pide el parón con
  `StopsOnImpact`. Para probar con el editor: leer `GameSettings` en batch aplica la calidad guardada y ensucia
  `ProjectSettings/QualitySettings.asset`.

- **Cada clase es un personaje: su modelo va en el campo "Modelo" de la clase** (desde el 6 de noviembre de 2026, pedido del líder de
  arte). Tiene que estar hecho sobre el esqueleto del Dummy: `ClassModelSwap` apaga el cuerpo y pone la malla nueva sobre los huesos
  del jugador, buscándolos por NOMBRE, así que animaciones, sockets y ragdoll son los de siempre. Vacío en una subclase = el de su
  clase base (`PlayerController.ResolveClassModel`). Corre en cada pantalla por la clase que ya viaja: no agrega nada a la red. **Con un
  modelo puesto el Animator va en Always Animate:** con Cull Update Transforms deja de mover los huesos porque su cuerpo está apagado y
  se queda en pose T. **FishNet, en el host, prende y apaga los renderers que anotó al nacer el objeto** (el cuerpo del Dummy): por eso
  `ClassModelSwap` escucha `OnHostVisibilityUpdated` y vuelve a apagar el cuerpo (9 de noviembre de 2026: las copias del Ilusionista
  salían con los dos modelos). Si se generan mallas por código, una normal en cero se ve bien en la vista previa pero en juego sale una bola
  blanca: por eso `ClassModelKit.Unit` y no `.normalized`, y un modelo nuevo se prueba en Play. **La Paladina, el Clérigo (leonino) y el Explorador (elfo) salen de Blender** (`Blender/paladin.py`, `cleric.py`, `ranger.py` → `Mercenarios ▸ Modelos de clase ▸ Importar los modelos de Blender`, que arma la malla con los bindposes del jugador y no pasa por FBX; ver `Blender/LEEME.md`): la receta de Unity de la Paladina ya no se usa, y los prefabs `Model_Cleric` y `Model_Ranger` se ponen a mano en el campo "Modelo" de su clase. Los de relleno salieron de
  recetas por clase en `Scripts/Editor/ClassModels` (borradas el 25 de noviembre de 2026 con los modelos ya hechos; están en el historial de git). Con `ModelFinish.Polished`, el alfa de cada color es el pulido, así que una clase que
  se pueda hacer invisible va en `Matte` (`PlayerVisibility` multiplica por ese alfa y el fantasma saldría a parches). Lo que brilla con luz
  propia (los ojos de la Paladina) va en una segunda textura, `ModelAtlas.PaintGlow`, que el kit pone como emisión. **El juego no tiene
  bloom ni tonemapping** (9 de noviembre de 2026: ninguna escena tiene Volume, el perfil por defecto `Settings/DefaultVolumeProfile` los
  tiene en 0 y el único Volume por código es el desenfoque del menú de `MenuOrbitCamera`): una emisión o un VFX se ven como color plano,
  sin resplandor, y con mucha intensidad se vuelven blancos.

- **Dónde va cada VFX.** Lo que dura lo que dura un buff o un debuff va en el
  `TargetVFX` del **GE** (aparece pegado a quien lo recibe, en todas las pantallas, y se
  va cuando el efecto termina; `TargetVFXLoop` repite las partículas de un solo
  disparo). No en la lista de VFX de la habilidad con `EndWithTag`. Los VFX de la
  habilidad van en su lista **`Visuals`** (cada entrada dice cuándo: al lanzar / al
  golpear / en el impacto); en código se disparan con `BroadcastHitVFX(objetivo)` y
  `BroadcastImpactVFX(punto)` — nunca un `Instantiate` a mano ni un campo `HitVFX` nuevo.
  Un GE tiene UN `TargetVFX`: si el buff necesita dos piezas, van juntas en un prefab
  (`VFX_AvengingAngel`, `VFX_FrenzyBuff`). Los `GE_*` se guardan en `Effects/`, nunca al
  lado de la habilidad. El VFX de **curar a un aliado** va en el momento **"Al curar"** de la
  habilidad (desde el 14 de octubre de 2026): sale solo sobre el curado, nunca sobre un enemigo;
  lo dispara `ApplyHitEffects` al aplicar un efecto que sube la vida. Cada GA elige el suyo.

- **Limpiar, purgar, inmunidades y condiciones por tag van en el GE, no en código** (sección "Quitar efectos e inmunidad",
  desde el 17 de noviembre de 2026; lo resuelve `ApplyGameplayEffect` antes que nada). La inmunidad reconoce a un efecto por
  los TAGS que da: un debuff que se quiera poder bloquear o limpiar por tag tiene que dar un tag propio. Nunca se quitan los
  cooldowns, las pasivas de la clase ni lo marcado "No se puede quitar".
  **Lo que pasa cuando un efecto se va también va en el GE** (sección "Al terminar (encadenados)", desde el 18 de noviembre de
  2026): "Al vencer, aplica" y "Si se lo quitan antes, aplica", sobre quien lo tenía y firmados por quien se lo puso.
  `RemoveActiveEffect` recibe POR QUÉ se va (`EEffectEnd`): un código nuevo que quite efectos dice si es "quitado"
  (encadena), "reemplazado" o "limpieza de morir/cambiar de clase" (no encadenan); y si recorre `ActiveEffects`, sube
  `_chainHold` mientras recorre (los encadenados esperan a que termine).
  **Una marca que se detona es un GE** (desde el 19 de noviembre de 2026): "Si se lo quitan antes, explota (GA_InstantAoE)" la
  hace estallar al gastarla (una habilidad con "Gasta los puntos" o una reacción "Y se gasta", que puede ser "Solo si el otro
  es quien lo puso"). Nada de código por habilidad para detonar. "Burla a la muerte (una vez)" es otra casilla del GE (20 de
  noviembre): lo resuelve `SetCurrentAttributeValue` antes de morir; lo de después, en "Si se lo quitan antes".
  **Lo que dura "para siempre" se marca, no se finge** (desde el 18 de noviembre de 2026): casilla "Dura para siempre" (nada
  de 9999 s), y lo que un aura renueva cada medio segundo lleva "Es de aura: no mostrar tiempo". Para saber si un efecto
  tiene duración se pregunta `HasDuration` (no `Duration > 0`) y la duración a aplicar es `EffectiveDuration` (+∞ si es
  infinito; el efecto activo queda con `TotalDuration` 0 = "sin reloj"). Lo que se va solo con un evento va en "Se va
  solo" (`RemoveOn`), no en código de pasiva.
  **Pasar daño o curación entre dos personajes es el "Vínculo (entre dos)" del GE** (desde el 19 de noviembre de 2026): une a
  quien lo tiene con quien se lo puso. Lo resuelve el ASC dentro de `ExecuteInstantEffect` (después de las defensas); un
  código nuevo que reparta daño no se escribe aparte. Su cuerda visible es el "Cuerda (prefab)" del GE (`LinkTether`): el
  servidor sincroniza el otro extremo en `NetworkAbilitySystemComponent.NetLinkPartner` y cada pantalla la dibuja.
  **Una pasiva nueva que reacciona a algo es un GE con "Reacciones"** (desde el 18 de noviembre de 2026, los eventos de
  juego: `GameplayEvents.cs`): un GE infinito con "cuando hago daño / recibo un crítico / mato…, aplica tal efecto a mí o al
  otro". Por código se escucha `ASC.OnGameplayEvent`, no un evento suelto nuevo. Un evento nuevo se agrega AL FINAL de
  `EGameplayEvent` y se manda con `SendGameplayEvent` desde donde pasa. Todo lo que aplique, quite o recorra efectos va
  entre `BeginHold` / `EndHold` (en un finally): así salen después los encadenados y las reacciones.
  **Revivir a un aliado es `GA_Revive`** (desde el 19 de noviembre de 2026; la Resurrección del Clérigo es una subclase vacía):
  los bots y el contorno de los cuerpos la reconocen en cualquier botón. Lo que revive pasa por `ServerResurrect` (cancela la
  reaparición en la base), nunca por `ASC.Revive` directo con red.
  **Una habilidad que sale sola con un evento** (un contraataque) usa la sección "Se activa sola" del GA (desde el 18 de
  noviembre de 2026), no código: la activa el servidor por `ServerActivateTriggered`, que manda la animación también al dueño
  remoto. Nada que la active "a mano" desde el servidor llama a `Activate()` directo.
  **Una rutina de habilidad nueva dice qué la corta con `EStopWhen`** (desde el 18 de noviembre de 2026, las tareas:
  `GameplayAbility.Tasks.cs`): `ShouldStop(...)` o un `Guard(...)` (`guard.Check()` en cada vuelta) y las esperas `WaitSeconds`,
  `WaitUntil` y `Repeat`, en vez de chequear tags y el `CancelSerial` a mano. Con `Damaged`, el guardián se suelta (`using`).
  **Nada de `WaitForSeconds` en una rutina de habilidad** (desde el 20 de noviembre de 2026 no queda ninguno): no corta a mitad.
  Solo lo llevan lo que no es una rutina que se corte (VFX con retraso, recarga de cargas, el proyectil, la zona desplegada).

- **Los efectos de una habilidad van en su lista `Effects`** (desde el 7 de octubre de
  2026): cada entrada dice cuándo (al golpear, al primer golpe, al activarse, al matar),
  a quién (enemigos, aliados, el lanzador, todos) y una condición de tag opcional. El
  código los aplica con `ApplyHitEffects(objetivo, primerGolpe)`; "al activarse" lo hace
  `CommitAbility`. Nada de campos sueltos `DamageEffect` / `AdditionalEffects` nuevos.
  Los campos nuevos de un GA (y de un GE, una clase, un ASDef, un perfil de pared o un componente) llevan
  **`[Label("...")]`** con su nombre en español: el Inspector se ve en español (desde el 13 de
  octubre de 2026) y sin `[Label]` el campo sale en inglés. **El nombre dice la unidad** (desde
  el 15 de octubre de 2026, etapa 6 de la revisión): `(m)`, `(s)`, `(m/s)`, `(°)`, `(px)`, `(×)` para
  multiplicadores, `(0 a 1)` para fracciones (los de juego van así, nunca "%" si el valor es 0.3),
  `(%)` solo si de verdad se escribe 30, `(vida)` / `(exp)` para puntos, y `(más = más rápido)`
  en los suavizados sin unidad. Un 0 especial va en el nombre: `Alcance máximo (m; 0 = sin tope)`. Un enum nuevo que se elige en el
  Inspector lleva `[InspectorName("...")]` en cada valor. Los campos nuevos de un GA llevan **`[Section]`** (con las constantes de
  `AbilitySection`) y, si dependen de otro, **`[ShowIf]`**; si tiene forma, que
  sobreescriba `DrawGizmos` y `DrawSceneHandles` (manijas con `AbilityHandles`).
  **Lo que gasta acumulaciones** (las de Artes marciales del Monje: cada una da
  `Status_MartialArts`) va por la sección **Acumulaciones** del GA (`StacksTag` +
  `ConsumeStacks`) y el "Con las acumulaciones" de cada entrada de efectos — nada de código
  por habilidad. La regla de Gustavo: sin acumulaciones, lo que escala no se aplica.
  **Cambiar el tipo de un campo ya cargado en assets** (uno suelto a lista, etc.): dejar
  el viejo como `[SerializeField, HideInInspector] private` con su mismo nombre y pasarlo
  en `OnUpgradeLegacyData` — así los assets no pierden lo cargado.

- **La UI del jugador vive en el prefab `Player Camera`** (desde el 14 de octubre de 2026,
  pedido de Gustavo): las barras de canalizar y de carga, los avisos de combate, la viñeta,
  la rueda, los compañeros, la curva de los lanzamientos, el material de la silueta y el
  marcador del piso de las habilidades apuntadas (`GroundTargetIndicator`: zona, franja y la
  casilla del muro como molde; desde el 21 de octubre de 2026, al jugar se suelta de la cámara
  para no temblar al girarla) están
  ARMADOS ahí (con un MOLDE apagado si algo se repite), y cada script los tiene por
  referencia: solo lee el juego y los anima. **Nada de crear canvas ni objetos de UI por
  código "encima".** Lo que se ve quieto se cambia en las piezas mismas; lo que cambia en
  juego, con campos en unidades absolutas y que digan cuáles (px del HUD, s, %), nunca
  relativos raros. El canvas del HUD es de **800×450 en "Expand"** (desde el 13 de noviembre de 2026; antes 800×600 con match en
  ancho, que en 16:9 da lo mismo): lo pensado para un canvas de 1920 va × 800/1920. Una pieza nueva se pone con una herramienta de una vez y
  después se edita a mano. Lo que es de la PARTIDA y no del personaje (registro de bajas,
  pantalla de muerte, números flotantes, marcador) va al **prefab de partida `Prefabs/UI/Match_UI`**
  (uno por escena; desde el 14 de octubre de 2026: registro de bajas, pantalla de muerte, números,
  marcador, avisos de partida, marcadores del Objetivo, el espectador y, desde el 4 de noviembre de 2026, la tabla con
  KDA, la ventana de ayuda y `CombatFeel`). Los menús (inicio, conexión,
  Ajustes, la sala y la pausa, más `MenuInput`) están en el **prefab de menús `Prefabs/UI/Menus_UI`**: cada fila de Ajustes lleva un
  `UI_SettingRow` y cada lugar de la sala un `UI_LobbySlot`. **El color de un botón de los menús va en el
  Button ("Normal Color" y los demás estados), con su imagen BLANCA** (`ButtonTint`, desde el 26 de octubre de 2026):
  así el elegido con el control se ve del mismo color, más oscuro (nunca más pálido: Gustavo no quiere colores deslavados). Pintar la imagen rompe los estados; desde código, `ButtonTint.Paint`.
  Además, con el control, lo elegido lo rodea un marco blanco, UNO para todos los menús (`Menus_UI ▸ SelectionFrame`, desde el 28 de octubre de 2026).
  **Una pantalla de menú que no cambia mientras está abierta se enlaza por filas al abrirse** (`MenuNavigation.LinkRows`, como el teclado y
  los Ajustes): la navegación automática de Unity elige entre TODOS los botones de la escena, también los de la pantalla de atrás, y el
  stick se trababa. Para las que cambian solas (la sala), `MenuInput` no deja que el stick salga de la pantalla de arriba.
  Los canvas de los dos prefabs son de
  1920×1080 en **"Expand"** (desde el 13 de noviembre de 2026): el diseño de 1920×1080 cabe entero en cualquier pantalla y lo que
  sobra queda a los lados (ultra ancha) o arriba y abajo (4:3). **Para que se adapte**, cada pieza va anclada a su esquina o
  borde, y una pantalla acomodada a mano va en un recuadro CENTRADO de tamaño fijo (con el fondo aparte, estirado): así está la
  ayuda (`HelpScreen ▸ Panel ▸ Content`). Nada acomodado a mano sobre un panel estirado a toda la pantalla. Están en la capa UI: si tapan la vista de escena, se apaga esa capa en el
  desplegable Layers del editor (no cambia el juego). Un `Graphic` propio necesita `[RequireComponent(typeof(CanvasRenderer))]`:
  sin él no se dibuja y no avisa. La **barra de vida de arriba de la cabeza** es UN prefab para todos,
  **`Prefabs/UI/Nameplate`** (`UI_Nameplate`: piezas y colores; desde el 19 de octubre de 2026): cada
  personaje lleva una instancia a su altura y `UI_WorldHealthbar` la encuentra sola. Un personaje o
  invocación nueva con vida lleva una instancia de ese prefab, nunca una barra armada aparte. De lejos la barra se agranda para mantener
  su tamaño en pantalla (`UI_Nameplate`: "Tamaño fijo en pantalla desde (m)" y "Agrandar hasta (×)", 18 de noviembre de 2026).

Los detalles finos del GAS (pipeline de daño, acumulación de efectos, animaciones de
combo) están en `DesignDocuments/GAS_Arquitectura.docx`.

---

## Conexión para jugar con amigos

Túnel UDP de **playit.gg** al puerto **7770** — sin VPN y sin abrir puertos en el router.
En la misma red (el showcase, 3 PCs con un router propio) no hace falta: se conectan a la IP
local del host, que la sala y la pausa muestran.

**La conexión es una pantalla del prefab de menús** (`UI_ConnectPanel`, desde el 26 de octubre
de 2026, para el showcase): Jugar → Crear partida, o la dirección (IP, o dirección:puerto la de
playit) y Conectar. **Cualquier build puede crear la partida** (se fue el candado de "solo el
editor o `-host`"). Lo que se abría con ESC es el **menú de pausa** (`UI_PauseMenu`, ESC o
Start): Continuar, Ajustes, Desatascarse, Salir. Los menús van por el Input System nuevo y
responden al control: quién recibe "volver" y Start y dónde está la selección lo decide
`MenuInput` (cada pantalla es un `IMenuScreen`). El recuadro viejo (`ConnectionHUD`, IMGUI)
queda solo en `Test_Network`, que no tiene el prefab de menús.

**La lista de partidas** (etapa 2 del showcase): la pantalla de conexión busca sola las partidas
abiertas en esa PC y en la red (`LanDiscovery`, UDP en el puerto **7771**, aparte del de la
partida) y las muestra como botones; la PC con la partida la anuncia. Por internet sigue la
dirección de playit escrita a mano. **Con control, A sobre un campo de texto abre el teclado en
pantalla** (`UI_OnScreenKeyboard`: X borra, Y espacio, Start acepta, B cancela); por eso los
campos ya no se ponen a escribir solos al elegirlos (`shouldActivateOnSelect` apagado).

**Tres ventanas en una PC** (etapa 3 del showcase): cada build de Windows sale con un `Showcase.bat` al lado
(`MercShowcaseBuildBat`) que abre tres ventanas, una por monitor: la 1 con teclado y mouse (`-kbm`), la 2 y
la 3 con `-pad -mute`. Una ventana `-pad` pide "Aprieta A en tu control" (`UI_PadClaim`) y se queda con el
control que aprieta A mientras ella tiene el foco; desde ahí lo lee aunque no tenga el foco (el Input System
está en Ignore Focus). Ver `LocalSeat`. Al lado sale también **`Controles.bat`** (desde el 20 de noviembre de
2026): X ventanas con X controles, y si se quiere una más con teclado y mouse; pregunta cuántas al abrirlo (o
`Controles.bat 4 s`), las reparte entre los monitores y, si son más que monitores, las abre en ventana de 960×540.
Para jugar en la misma PC o en la misma red no hace falta playit: la lista de partidas las encuentra (o `127.0.0.1`).

**Si la build falla con "Una directiva de Control de aplicaciones bloqueó este archivo"** (`Bee.NativeProgramSupport.dll`):
es Smart App Control de Windows bloqueando una DLL sin firma del propio Unity (20 de noviembre de 2026, en la PC de
trabajo). No es del proyecto: se apaga en Seguridad de Windows ▸ Control de aplicaciones y navegador (no se puede
volver a prender sin reinstalar Windows), o se hace la build en otra máquina.

---

## Git

Repo: `.../Proyectos Gustavo/GasTest/GasTest` (la carpeta INTERNA; la de afuera no es el
repo). Remoto: `https://github.com/Gustavo-FCFM/GasTest.git`, rama `main`.

**Al empezar a trabajar en una máquina, mirar arriba de `PENDIENTES.md`:** si hay una
sección "★ EN LA MÁQUINA DE CASA" (u otra máquina) con casillas sin marcar, son acciones
de una sola vez para esa máquina. Hacerlas primero y confirmarlas.

Al cambiar de máquina, sincronizar con **fetch + reset**, no con `pull`:

```bash
git fetch origin && git checkout main && git reset --hard origin/main
```

(La máquina de casa tuvo historia divergente en julio de 2026; un `pull` normal recrea
ese merge conflictivo. Lo descartado quedó en la rama `backup-merge-2026-07-15`.)
